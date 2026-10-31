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
    "model.onnx.gz" = "25e54c5059641222151bc3f4bd5c523ae60057e6296a23fb90317b8ef5522698"
    "model.onnx" = "828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35"
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

function Test-InstalledAsset([string]$Path, [string]$Name) {
    return (-not $Force) -and (Test-Path -LiteralPath $Path) -and
        ((Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() -eq $expectedSha256[$Name])
}

if ((Test-InstalledAsset $modelPath 'model.onnx') -and (Test-InstalledAsset $vocabPath 'vocab.txt')) {
    Write-Host "BertV2 model assets already exist, skipping."
    Write-Host ""
    Write-Host "Done. BertV2 model files are in: $modelsDir"
    exit 0
}

$tempDir = Join-Path $modelsDir ('.download-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    if (-not (Test-InstalledAsset $vocabPath 'vocab.txt')) {
        $vocabDownloadPath = Join-Path $tempDir "vocab.txt"
        Write-Host "Downloading BertV2 vocab..."
        Invoke-WebRequest -Uri "$baseUrl/vocab.txt" -OutFile $vocabDownloadPath -UseBasicParsing
        Assert-DownloadHash -Path $vocabDownloadPath -Name "vocab.txt"
        Copy-Item -Path $vocabDownloadPath -Destination $vocabPath -Force
        Write-Host "  -> $vocabPath"
    } else {
        Write-Host "vocab.txt already exists, skipping."
    }

    if (-not (Test-InstalledAsset $modelPath 'model.onnx')) {
        $archivePath = Join-Path $tempDir "model.onnx.gz"

        Write-Host "Downloading BertV2 model archive..."
        Invoke-WebRequest -Uri "$baseUrl/model.onnx.gz" -OutFile $archivePath -UseBasicParsing
        Assert-DownloadHash -Path $archivePath -Name "model.onnx.gz"

        Write-Host "Extracting BertV2 model..."
        $extractedModelPath = Join-Path $tempDir "model.onnx"
        $source = [IO.File]::OpenRead($archivePath)
        try {
            $gzip = [IO.Compression.GZipStream]::new($source, [IO.Compression.CompressionMode]::Decompress)
            try {
                $destination = [IO.File]::Create($extractedModelPath)
                try { $gzip.CopyTo($destination) } finally { $destination.Dispose() }
            } finally { $gzip.Dispose() }
        } finally { $source.Dispose() }
        Assert-DownloadHash -Path $extractedModelPath -Name "model.onnx"
        Move-Item -LiteralPath $extractedModelPath -Destination $modelPath -Force
        Write-Host "  -> $modelPath"
    } else {
        Write-Host "model.onnx already exists, skipping."
    }
} finally {
    if (Test-Path $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "Done. BertV2 model files are in: $modelsDir"
