param(
    [string]$GithubRepo = "robstokes857/vibe-rails",
    # The commit that last changed VibeRails/Models/BertV2. Update it together with the
    # hashes below; ReleaseSupplyChainTests checks the hashes against the checked-in files.
    [string]$Ref = "d0422f76545206eb9181dd546ff20d0b2ebd669c",
    [switch]$Force
)

# Downloads and extracts the BertV2 model assets used by semantic search.
# Manual fallback only: release packages bundle these files.
$ErrorActionPreference = "Stop"

$modelsDir = Join-Path $HOME ".vibe_rails\models\bertv2"
$modelPath = Join-Path $modelsDir "model.onnx"
$vocabPath = Join-Path $modelsDir "vocab.txt"
$baseUrl = "https://raw.githubusercontent.com/$GithubRepo/$Ref/VibeRails/Models/BertV2"

# SHA-256 of the git blobs (vocab.txt is LF in the blob even where a checkout uses CRLF).
$expectedSha256 = @{
    "vocab.txt"      = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"
    "model.onnx.zip" = "bf820f09f11cff84a7995f158acf279e6803791e15c5de4439318fd133fbc476"
}

function Assert-DownloadHash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    if ($actual -ne $expectedSha256[$Name]) {
        throw "SHA-256 mismatch for $Name from ${baseUrl}: expected $($expectedSha256[$Name]), got $actual. Nothing was installed from it."
    }
}

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
        Assert-DownloadHash -Path $vocabDownloadPath -Name "vocab.txt"
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
        Assert-DownloadHash -Path $zipPath -Name "model.onnx.zip"

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
