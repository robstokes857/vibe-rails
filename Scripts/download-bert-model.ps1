param(
    [string]$GithubRepo = "robstokes857/vibe-rails",
    [string]$Ref = "main",
    [switch]$Force
)

# Downloads and extracts the BertV2 model assets used by semantic search.
$ErrorActionPreference = "Stop"

$modelsDir = Join-Path $HOME ".vibe_rails\models\bertv2"
$modelPath = Join-Path $modelsDir "model.onnx"
$vocabPath = Join-Path $modelsDir "vocab.txt"
$baseUrl = "https://raw.githubusercontent.com/$GithubRepo/$Ref/VibeRails/Models/BertV2"

if (-not (Test-Path $modelsDir)) {
    New-Item -ItemType Directory -Path $modelsDir -Force | Out-Null
}

if ($Force) {
    Remove-Item $modelPath -Force -ErrorAction SilentlyContinue
    Remove-Item $vocabPath -Force -ErrorAction SilentlyContinue
}

if ((Test-Path $modelPath) -and (Test-Path $vocabPath)) {
    Write-Host "BertV2 model assets already exist, skipping."
    Write-Host ""
    Write-Host "Done. BertV2 model files are in: $modelsDir"
    exit 0
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "vibe_rails_bertv2_$(Get-Random)"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    if (-not (Test-Path $vocabPath)) {
        $vocabDownloadPath = Join-Path $tempDir "vocab.txt"
        Write-Host "Downloading BertV2 vocab..."
        Invoke-WebRequest -Uri "$baseUrl/vocab.txt" -OutFile $vocabDownloadPath -UseBasicParsing
        Copy-Item -Path $vocabDownloadPath -Destination $vocabPath -Force
        Write-Host "  -> $vocabPath"
    } else {
        Write-Host "vocab.txt already exists, skipping."
    }

    if (-not (Test-Path $modelPath)) {
        $zipPath = Join-Path $tempDir "model.onnx.zip"
        $extractDir = Join-Path $tempDir "extract"
        New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

        Write-Host "Downloading BertV2 model archive..."
        Invoke-WebRequest -Uri "$baseUrl/model.onnx.zip" -OutFile $zipPath -UseBasicParsing

        Write-Host "Extracting BertV2 model..."
        Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

        $extractedModelPath = Join-Path $extractDir "model.onnx"
        if (-not (Test-Path $extractedModelPath)) {
            throw "BertV2 model archive did not contain model.onnx"
        }

        Copy-Item -Path $extractedModelPath -Destination $modelPath -Force
        Write-Host "  -> $modelPath"
    } else {
        Write-Host "model.onnx already exists, skipping."
    }
} finally {
    if (Test-Path $tempDir) {
        Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "Done. BertV2 model files are in: $modelsDir"
