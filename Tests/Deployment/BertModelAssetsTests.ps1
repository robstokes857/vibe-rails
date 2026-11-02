# Isolated installer function tests; never execute installation or touch real user assets.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'Scripts/install.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join "`n") }
$function = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Install-BundledModelAssets'
}, $false)
. ([scriptblock]::Create($function.Extent.Text))
$fixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('bert-assets-' + [Guid]::NewGuid().ToString('N'))))
if (-not $fixture.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture path' }
function Write-Payload([string]$Name) {
    $dir = Join-Path $fixture "Models/$Name"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    [IO.File]::WriteAllText((Join-Path $dir 'vocab.txt'), "expected $Name vocabulary")
    $stream = [IO.File]::Create((Join-Path $dir 'model.onnx.gz'))
    $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Compress)
    try { $gzip.Write([Text.Encoding]::UTF8.GetBytes("expected $Name model bytes")) } finally { $gzip.Dispose(); $stream.Dispose() }
}
function Assert-Assets([string]$Name) {
    $runtime = Join-Path $fixture ('models/' + $Name.ToLowerInvariant())
    if ([IO.File]::ReadAllText((Join-Path $runtime 'model.onnx')) -ne "expected $Name model bytes") { throw "Wrong $Name model bytes" }
    if ([IO.File]::ReadAllText((Join-Path $runtime 'vocab.txt')) -ne "expected $Name vocabulary") { throw "Wrong $Name vocabulary bytes" }
    foreach ($ext in @('gz', 'zip', 'br')) {
        if (Test-Path -LiteralPath (Join-Path $fixture "Models/$Name/model.onnx.$ext")) { throw "Unused $Name $ext archive retained" }
    }
}
try {
    # An older release without the re-ranker installs; a missing required model fails.
    Install-BundledModelAssets -RootDir $fixture -Name Reranker -Optional
    if (Test-Path -LiteralPath (Join-Path $fixture 'models/reranker')) { throw 'Skipped model created a runtime directory' }
    $failed = $false
    try { Install-BundledModelAssets -RootDir $fixture -Name BertV2 } catch { $failed = $true }
    if (-not $failed) { throw 'Missing required model was accepted' }

    foreach ($name in @('BertV2', 'Reranker')) {
        Write-Payload $name
        Install-BundledModelAssets -RootDir $fixture -Name $name -Optional:($name -eq 'Reranker')
        Assert-Assets $name
        Write-Payload $name
        $model = Join-Path $fixture ('models/' + $name.ToLowerInvariant() + '/model.onnx')
        [IO.File]::SetLastWriteTimeUtc($model, [datetime]'2001-01-01')
        Install-BundledModelAssets -RootDir $fixture -Name $name
        Assert-Assets $name
        if ([IO.File]::GetLastWriteTimeUtc($model) -ne [datetime]'2001-01-01') { throw "Unchanged $name model was replaced" }
        [IO.File]::WriteAllText($model, 'obsolete')
        [IO.File]::WriteAllText((Join-Path $fixture ('models/' + $name.ToLowerInvariant() + '/vocab.txt')), 'obsolete')
        # The release overlay replaces the bundled vocabulary first. On Windows
        # Models and models are the same directory, so this is also the runtime file.
        Write-Payload $name
        foreach ($ext in @('zip', 'br')) { [IO.File]::WriteAllText((Join-Path $fixture "Models/$name/model.onnx.$ext"), 'unused') }
        Install-BundledModelAssets -RootDir $fixture -Name $name
        Assert-Assets $name
        Write-Payload $name
        [IO.File]::WriteAllText((Join-Path $fixture "Models/$name/model.onnx.gz"), 'broken archive')
        $failed = $false
        try { Install-BundledModelAssets -RootDir $fixture -Name $name -Optional } catch { $failed = $true }
        if (-not $failed) { throw "Broken $name archive was accepted" }
        if ([IO.File]::ReadAllText($model) -ne "expected $name model bytes") { throw "Failed extraction damaged installed $name model" }
    }
    # Each model keeps its own runtime folder.
    if ([IO.File]::ReadAllText((Join-Path $fixture 'models/bertv2/model.onnx')) -ne 'expected BertV2 model bytes') { throw 'Re-ranker install changed the BertV2 model' }
    Write-Output 'Passed: BertV2 and Reranker fresh install, unchanged assets, stale model/vocabulary replacement, archive cleanup, failed extraction and optional skip.'
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
