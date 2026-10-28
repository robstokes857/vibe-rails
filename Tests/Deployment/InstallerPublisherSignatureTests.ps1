# Run: pwsh -NoProfile -File Tests/Deployment/InstallerPublisherSignatureTests.ps1
# Windows only (Get-AuthenticodeSignature). Runs under Windows PowerShell 5.1 too, which is
# what `irm ... | iex` uses on a stock machine. The accepting case is the release itself:
# release.yml runs the same function on the vb.exe it has just signed.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$installScript = Join-Path $repoRoot 'Scripts/install.ps1'
$parseErrors = $null
$tokens = $null
$syntax = [System.Management.Automation.Language.Parser]::ParseFile($installScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors -join [Environment]::NewLine) }
$check = $syntax.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-PublisherSignature'
}, $false)
if ($null -eq $check) { throw 'Assert-PublisherSignature was not found in Scripts/install.ps1.' }
. ([scriptblock]::Create($check.Extent.Text))

function Assert-Rejected {
    param([string]$Executable, [string]$ExpectedMessage)
    $rejected = $false
    try { Assert-PublisherSignature -Executable $Executable }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw }
        if ($_.Exception.Message -notlike '*The existing installation was not changed.*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Accepted $Executable." }
}

$fixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('viberails-publisher-' + [Guid]::NewGuid().ToString('N'))))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $fixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture path.' }

try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    $unsigned = Join-Path $fixture 'vb.exe'
    [IO.File]::WriteAllText($unsigned, 'Inert installer regression fixture; never execute')
    Assert-Rejected $unsigned 'does not have a valid signature'

    # The running PowerShell host is validly signed by Microsoft, so only the publisher
    # check can reject it.
    $hostExecutable = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    if ((Get-AuthenticodeSignature -LiteralPath $hostExecutable).Status -ne 'Valid') {
        throw "The PowerShell host $hostExecutable is not validly signed; the publisher case cannot run."
    }
    Assert-Rejected $hostExecutable 'not the VibeRails publisher'

    Write-Output 'Passed: unsigned and foreign-publisher binaries are rejected. No installation was run.'
} finally {
    if (Test-Path -LiteralPath $fixture) {
        # fixture is resolved and verified inside the system temp directory above.
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
