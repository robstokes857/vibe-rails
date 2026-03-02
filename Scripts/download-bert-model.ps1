# Downloads the all-MiniLM-L6-v2 ONNX model files used by BERT input capture.
$ErrorActionPreference = "Stop"

$modelsDir = Join-Path $HOME ".vibe_rails\models\bert"
if (-not (Test-Path $modelsDir)) {
    New-Item -ItemType Directory -Path $modelsDir -Force | Out-Null
}

$baseUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main"

$vocabPath = Join-Path $modelsDir "vocab.txt"
if (-not (Test-Path $vocabPath)) {
    Write-Host "Downloading vocab.txt..."
    Invoke-WebRequest -Uri "$baseUrl/vocab.txt" -OutFile $vocabPath
    Write-Host "  -> $vocabPath"
} else {
    Write-Host "vocab.txt already exists, skipping."
}

$modelPath = Join-Path $modelsDir "model.onnx"
if (Test-Path $modelPath) {
    $sizeMb = (Get-Item $modelPath).Length / 1MB
    if ($sizeMb -ge 100) {
        Write-Host "Existing model.onnx is $([math]::Round($sizeMb, 1))MB, replacing with MiniLM..."
        Remove-Item $modelPath -Force
    }
}

if (-not (Test-Path $modelPath)) {
    Write-Host "Downloading model.onnx (~90MB)..."
    Invoke-WebRequest -Uri "$baseUrl/onnx/model.onnx" -OutFile $modelPath
    Write-Host "  -> $modelPath"
} else {
    Write-Host "model.onnx already exists, skipping."
}

Write-Host ""
Write-Host "Done. BERT model files are in: $modelsDir"
