# Compile and invoke an inert signer; never sign or install an application.
param([Parameter(Mandatory)][string]$Compiler)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$script = Join-Path $repoRoot 'deploy/build-installer.ps1'
$tokens = $null
$errors = $null
$syntax = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join [Environment]::NewLine) }
$helper = $syntax.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-InnoSignToolCommand'
}, $false)
if ($null -eq $helper) { throw 'Signing command helper was not found.' }
. ([scriptblock]::Create($helper.Extent.Text))

$fixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('viberails-signing-' + [Guid]::NewGuid().ToString('N') + ' $q space')))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $fixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid signing fixture path.' }
try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    $signer = Join-Path $fixture 'signer with spaces.ps1'
    @'
param([Parameter(Mandatory)][string]$Path)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'received-path.txt'), $Path)
'@ | Set-Content -LiteralPath $signer -Encoding utf8NoBOM
    $source = Join-Path $fixture 'signing.iss'
    @'
[Setup]
AppName=Signing command regression
AppVersion=1.0.0
DefaultDirName={tmp}\signing-command-regression
CreateAppDir=no
Uninstallable=no
SignedUninstaller={#SignedUninstaller}
OutputDir={#OutputDirectory}
OutputBaseFilename=signing-probe
SignTool=argvprobe
'@ | Set-Content -LiteralPath $source -Encoding utf8NoBOM
    $command = Get-InnoSignToolCommand -PowerShellPath (Get-Command pwsh -CommandType Application).Source -SigningScript $signer
    foreach ($case in @(
        @{ SignedUninstaller = 'no'; File = 'signing-probe.exe'; ExitCode = 0 },
        @{ SignedUninstaller = 'yes'; File = 'uninst.e32.tmp'; ExitCode = 2 }
    )) {
        $marker = Join-Path $fixture 'received-path.txt'
        if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }
        $PSNativeCommandUseErrorActionPreference = $false
        $compilerOutput = @(& $Compiler '/Qp' "/DOutputDirectory=$fixture" "/DSignedUninstaller=$($case.SignedUninstaller)" ('/Sargvprobe=' + $command) $source 2>&1)
        $compilerExitCode = $LASTEXITCODE
        if (-not (Test-Path -LiteralPath $marker)) {
            throw "Signer was not invoked ($compilerExitCode): $($compilerOutput -join [Environment]::NewLine)"
        }
        $received = [IO.File]::ReadAllText($marker)
        $expected = Join-Path $fixture $case.File
        if ($received -ne $expected) { throw "Signer received '$received', expected '$expected'." }
        if ($compilerExitCode -ne $case.ExitCode) {
            throw "Unexpected compiler result ($compilerExitCode): $($compilerOutput -join [Environment]::NewLine)"
        }
        # An inert signer must not satisfy the signed-uninstaller requirement.
        if ($case.SignedUninstaller -eq 'yes' -and ($compilerOutput -join "`n") -notlike '*file does not have a digital signature*') {
            throw 'Inno did not reject the unsigned uninstaller.'
        }
    }
    $global:LASTEXITCODE = 0
    Write-Output 'Passed: ISCC invokes the signer with space and dollar paths as intact arguments. No installation or real signing was run.'
} finally {
    if (Test-Path -LiteralPath $fixture) {
        # Resolved and checked inside the system temp directory before creation.
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
