param(
    # The Hugging Face revision of cross-encoder/ms-marco-TinyBERT-L2-v2 that VibeRails ships in
    # VibeRails/Models/Reranker. Update it together with the hashes below;
    # ReleaseSupplyChainTests checks the hashes against the checked-in files.
    [string]$Revision = "81d1926f67cb8eee2c2be17ca9f793c7c3bd20cc",
    [switch]$Force
)

# Downloads the search re-ranking model (VIBE-258) from its upstream source. The license is
# Apache-2.0; release packages carry it in Models/Reranker/LICENSE.txt.
# Manual fallback only: release packages bundle these files.
$ErrorActionPreference = "Stop"

$modelsDir = Join-Path $HOME ".vibe_rails\models\reranker"
$baseUrl = "https://huggingface.co/cross-encoder/ms-marco-TinyBERT-L2-v2/resolve/$Revision"

# The vocabulary goes first: the application turns re-ranking on once model.onnx exists.
$assets = [ordered]@{
    "vocab.txt"  = @{ Url = "$baseUrl/vocab.txt"; Sha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3" }
    "model.onnx" = @{ Url = "$baseUrl/onnx/model.onnx"; Sha256 = "0eac39ee56a3edf98d0beee17fa1bb368a5a1d8c8671b5afd6dd71677f8d8496" }
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

if (-not (Test-Path $modelsDir)) {
    New-Item -ItemType Directory -Path $modelsDir -Force | Out-Null
}

$tempDir = Join-Path $modelsDir ('.download-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    foreach ($name in $assets.Keys) {
        $asset = $assets[$name]
        $installedPath = Join-Path $modelsDir $name
        if (-not $Force -and (Test-Path -LiteralPath $installedPath) -and (Get-Sha256 $installedPath) -eq $asset.Sha256) {
            Write-Host "$name already exists, skipping."
            continue
        }

        $downloadPath = Join-Path $tempDir $name
        Write-Host "Downloading re-ranker $name..."
        Invoke-WebRequest -Uri $asset.Url -OutFile $downloadPath -UseBasicParsing
        $actual = Get-Sha256 $downloadPath
        if ($actual -ne $asset.Sha256) {
            throw "SHA-256 mismatch for $name from $($asset.Url): expected $($asset.Sha256), got $actual. Nothing was installed from it."
        }
        Move-Item -LiteralPath $downloadPath -Destination $installedPath -Force
        Write-Host "  -> $installedPath"
    }
} finally {
    if (Test-Path $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "Done. Re-ranker model files are in: $modelsDir"
