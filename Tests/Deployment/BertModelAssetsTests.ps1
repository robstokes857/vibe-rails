# Isolated installer function tests; never execute installation or touch real user assets.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'Scripts/install.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join "`n") }
$function = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Install-BertV2ModelAssets'
}, $false)
. ([scriptblock]::Create($function.Extent.Text))
$fixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('bert-assets-' + [Guid]::NewGuid().ToString('N'))))
if (-not $fixture.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture path' }
function Write-Payload {
    $dir = Join-Path $fixture 'Models/BertV2'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    [IO.File]::WriteAllText((Join-Path $dir 'vocab.txt'), 'expected vocabulary')
    $stream = [IO.File]::Create((Join-Path $dir 'model.onnx.gz'))
    $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Compress)
    try { $gzip.Write([Text.Encoding]::UTF8.GetBytes('expected model bytes')) } finally { $gzip.Dispose(); $stream.Dispose() }
}
function Assert-Assets {
    if ([IO.File]::ReadAllText((Join-Path $fixture 'models/bertv2/model.onnx')) -ne 'expected model bytes') { throw 'Wrong model bytes' }
    if ([IO.File]::ReadAllText((Join-Path $fixture 'models/bertv2/vocab.txt')) -ne 'expected vocabulary') { throw 'Wrong vocabulary bytes' }
    foreach ($ext in @('gz', 'zip', 'br')) {
        if (Test-Path -LiteralPath (Join-Path $fixture "Models/BertV2/model.onnx.$ext")) { throw "Unused $ext archive retained" }
    }
}
try {
    Write-Payload
    Install-BertV2ModelAssets -RootDir $fixture
    Assert-Assets
    Write-Payload
    $model = Join-Path $fixture 'models/bertv2/model.onnx'
    [IO.File]::SetLastWriteTimeUtc($model, [datetime]'2001-01-01')
    Install-BertV2ModelAssets -RootDir $fixture
    Assert-Assets
    if ([IO.File]::GetLastWriteTimeUtc($model) -ne [datetime]'2001-01-01') { throw 'Unchanged model was replaced' }
    [IO.File]::WriteAllText($model, 'obsolete')
    [IO.File]::WriteAllText((Join-Path $fixture 'models/bertv2/vocab.txt'), 'obsolete')
    # The release overlay replaces the bundled vocabulary first. On Windows
    # Models and models are the same directory, so this is also the runtime file.
    Write-Payload
    foreach ($ext in @('zip', 'br')) { [IO.File]::WriteAllText((Join-Path $fixture "Models/BertV2/model.onnx.$ext"), 'unused') }
    Install-BertV2ModelAssets -RootDir $fixture
    Assert-Assets
    Write-Payload
    [IO.File]::WriteAllText((Join-Path $fixture 'Models/BertV2/model.onnx.gz'), 'broken archive')
    $failed = $false
    try { Install-BertV2ModelAssets -RootDir $fixture } catch { $failed = $true }
    if (-not $failed) { throw 'Broken archive was accepted' }
    if ([IO.File]::ReadAllText($model) -ne 'expected model bytes') { throw 'Failed extraction damaged installed model' }
    Write-Output 'Passed: fresh install, unchanged assets, stale model/vocabulary replacement, archive cleanup and failed extraction.'
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
