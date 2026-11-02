# Run: pwsh -NoProfile -File Tests/Deployment/LocalDeployPayloadTests.ps1
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$deployScript = Join-Path $repoRoot 'deploy/local_deploy.ps1'
$parseErrors = $null
$tokens = $null
$syntax = [System.Management.Automation.Language.Parser]::ParseFile($deployScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors -join [Environment]::NewLine) }

# Load ONLY the validator. Never execute the deployment script's build, process-stop or copy code.
$validator = $syntax.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-DeployPayload'
}, $false)
if ($null -eq $validator) { throw 'Deploy payload validator was not found.' }
. ([scriptblock]::Create($validator.Extent.Text))

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('viberails-deploy-payload-' + [Guid]::NewGuid().ToString('N'))
$fixturePath = [IO.Path]::GetFullPath($fixture)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $fixturePath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture path.' }
try {
    foreach ($relative in @('vb.exe', 'appsettings.json', 'wwwroot/index.html', 'Models/BertV2/model.onnx.gz',
        'Models/BertV2/vocab.txt', 'Models/Reranker/model.onnx.gz', 'Models/Reranker/vocab.txt',
        'Models/Reranker/LICENSE.txt', 'scripts/pre-commit-hook.sh', 'scripts/commit-msg-hook.sh',
        'onnxruntime.dll', 'e_sqlite3.dll', 'vec0.dll')) {
        $file = Join-Path $fixturePath $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)) | Out-Null
        [IO.File]::WriteAllText($file, 'Publish fixture')
    }
    Assert-DeployPayload -RootDir $fixturePath

    # A local deploy turns on search re-ranking, so its publish output must carry the model.
    $reranker = Join-Path $fixturePath 'Models/Reranker/model.onnx.gz'
    Remove-Item -LiteralPath $reranker -Force
    $rejected = $false
    try { Assert-DeployPayload -RootDir $fixturePath }
    catch {
        if ($_.Exception.Message -notlike '*Models\Reranker\model.onnx.gz*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Payload without the re-ranker model was accepted.' }
    [IO.File]::WriteAllText($reranker, 'Publish fixture')

    # Model extraction reuses install.ps1's function. Load it the way the deploy does.
    $RepoRoot = $repoRoot
    $loader = $syntax.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-InstallerModelFunction'
    }, $false)
    if ($null -eq $loader) { throw 'Installer model function loader was not found.' }
    . ([scriptblock]::Create($loader.Extent.Text))
    . (Get-InstallerModelFunction)
    if (-not (Get-Command Install-BundledModelAssets -CommandType Function -ErrorAction SilentlyContinue)) {
        throw 'Install-BundledModelAssets was not loaded from Scripts/install.ps1.'
    }
    foreach ($name in @('BertV2', 'Reranker')) {
        if ((Get-Content -LiteralPath $deployScript -Raw) -notmatch "Install-BundledModelAssets -RootDir \`$InstallDir -Name $name\b") {
            throw "local_deploy.ps1 does not install the $name model."
        }
    }
    $protectedFiles = @('state.db', 'STATE.DB-WAL', 'state.db-shm', 'state.db-journal',
        'board.db', 'BOARD.DB', 'Board.Db-WaL', 'board.db-shm', 'BOARD.DB-JOURNAL', 'board.db.backup')
    foreach ($name in $protectedFiles) {
        $file = Join-Path $fixturePath $name
        [IO.File]::WriteAllText($file, 'Must not overwrite user data')
        $rejected = $false
        try { Assert-DeployPayload -RootDir $fixturePath }
        catch {
            if ($_.Exception.Message -notlike '*protected user-data path*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "Payload containing '$name' was accepted." }
        Remove-Item -LiteralPath $file -Force
    }
    Assert-DeployPayload -RootDir $fixturePath
    Write-Output "Passed: clean payload accepted; payload without the re-ranker and all $($protectedFiles.Count) database payloads rejected; installer model function loaded. No deployment was run."
} finally {
    if (Test-Path -LiteralPath $fixturePath) {
        # fixturePath was resolved and verified inside the system temp directory before creation.
        Remove-Item -LiteralPath $fixturePath -Recurse -Force
    }
}
