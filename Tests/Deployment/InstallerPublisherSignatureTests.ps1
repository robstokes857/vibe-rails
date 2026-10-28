# Run: pwsh -NoProfile -File Tests/Deployment/InstallerPublisherSignatureTests.ps1
# Windows only (Authenticode and file catalogs). Runs under Windows PowerShell 5.1 too, which
# is what `irm ... | iex` uses on a stock machine. The accepting signature case is the release
# itself: release.yml runs these functions on the vb.exe and catalog it has just signed.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$installScript = Join-Path $repoRoot 'Scripts/install.ps1'
$parseErrors = $null
$tokens = $null
$syntax = [System.Management.Automation.Language.Parser]::ParseFile($installScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors -join [Environment]::NewLine) }
foreach ($functionName in @('Assert-PublisherSignature', 'Assert-PayloadMatchesCatalog')) {
    $function = $syntax.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName
    }, $false)
    if ($null -eq $function) { throw "$functionName was not found in Scripts/install.ps1." }
    . ([scriptblock]::Create($function.Extent.Text))
}

function Assert-Rejected {
    param([string]$Case, [scriptblock]$Check, [string]$ExpectedMessage)
    $rejected = $false
    try { & $Check }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw }
        if ($_.Exception.Message -notlike '*The existing installation was not changed.*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Accepted: $Case." }
}

$fixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('viberails-publisher-' + [Guid]::NewGuid().ToString('N'))))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $fixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture path.' }

try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    $unsigned = Join-Path $fixture 'vb.exe'
    [IO.File]::WriteAllText($unsigned, 'Inert installer regression fixture; never execute')
    Assert-Rejected 'unsigned vb.exe' { Assert-PublisherSignature -Path $unsigned } 'does not have a valid signature'

    # The running PowerShell host is validly signed by Microsoft, so only the publisher
    # check can reject it.
    $hostExecutable = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    if ((Get-AuthenticodeSignature -LiteralPath $hostExecutable).Status -ne 'Valid') {
        throw "The PowerShell host $hostExecutable is not validly signed; the publisher case cannot run."
    }
    Assert-Rejected 'foreign publisher' { Assert-PublisherSignature -Path $hostExecutable } 'not the VibeRails publisher'

    # A payload that keeps a genuine signed vb.exe (the host stands in for it) beside inert
    # native, hook and web files, catalogued the way release.yml does it.
    $payload = Join-Path $fixture 'payload'
    foreach ($directory in @('scripts', 'wwwroot')) {
        [IO.Directory]::CreateDirectory((Join-Path $payload $directory)) | Out-Null
    }
    Copy-Item -LiteralPath $hostExecutable -Destination (Join-Path $payload 'vb.exe')
    $dll = Join-Path $payload 'e_sqlite3.dll'
    $hook = Join-Path $payload 'scripts/pre-commit-hook.sh'
    $page = Join-Path $payload 'wwwroot/index.html'
    foreach ($file in @($dll, $hook, $page)) {
        [IO.File]::WriteAllText($file, 'Inert installer regression fixture; never execute')
    }
    $catalog = Join-Path $fixture 'vb-win-x64.cat'
    New-FileCatalog -Path $payload -CatalogFilePath $catalog -CatalogVersion 2.0 | Out-Null

    # Test-FileCatalog reports an unsigned catalog as Valid, which is why install.ps1 checks
    # the catalog's publisher first.
    Assert-Rejected 'unsigned catalog' { Assert-PublisherSignature -Path $catalog } 'does not have a valid signature'
    Assert-PayloadMatchesCatalog -PayloadDir $payload -CatalogPath $catalog

    [IO.File]::WriteAllText($dll, 'Substituted native library; never execute')
    Assert-Rejected 'altered DLL beside a genuine vb.exe' { Assert-PayloadMatchesCatalog -PayloadDir $payload -CatalogPath $catalog } 'does not match its signed file catalog'
    [IO.File]::WriteAllText($dll, 'Inert installer regression fixture; never execute')

    $extra = Join-Path $payload 'scripts/post-checkout-hook.sh'
    [IO.File]::WriteAllText($extra, 'Added hook; never execute')
    Assert-Rejected 'extra hook' { Assert-PayloadMatchesCatalog -PayloadDir $payload -CatalogPath $catalog } 'does not match its signed file catalog'
    Remove-Item -LiteralPath $extra

    Remove-Item -LiteralPath $page
    Assert-Rejected 'missing web asset' { Assert-PayloadMatchesCatalog -PayloadDir $payload -CatalogPath $catalog } 'does not match its signed file catalog'

    Write-Output 'Passed: unsigned and foreign-publisher files, an unsigned catalog, and altered, extra or missing payload files are rejected. No installation was run.'
} finally {
    if (Test-Path -LiteralPath $fixture) {
        # fixture is resolved and verified inside the system temp directory above.
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
