#!/usr/bin/env bash
# Downloads the search re-ranking model (VIBE-258) from its upstream source. The license is
# Apache-2.0; release packages carry it in Models/Reranker/LICENSE.txt.
# Manual fallback only: release packages bundle these files.
set -euo pipefail

# The Hugging Face revision of cross-encoder/ms-marco-TinyBERT-L2-v2 that VibeRails ships in
# VibeRails/Models/Reranker. Update it together with the hashes below;
# ReleaseSupplyChainTests checks the hashes against the checked-in files.
MODEL_REVISION="${MODEL_REVISION:-81d1926f67cb8eee2c2be17ca9f793c7c3bd20cc}"
FORCE_DOWNLOAD="${FORCE_DOWNLOAD:-0}"

MODELS_DIR="$HOME/.vibe_rails/models/reranker"
BASE_URL="https://huggingface.co/cross-encoder/ms-marco-TinyBERT-L2-v2/resolve/$MODEL_REVISION"

VOCAB_SHA256="07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"
MODEL_SHA256="0eac39ee56a3edf98d0beee17fa1bb368a5a1d8c8671b5afd6dd71677f8d8496"

download_to_file() {
    local url="$1"
    local output="$2"

    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$url" -o "$output"
        return
    fi

    if command -v wget >/dev/null 2>&1; then
        wget -q -O "$output" "$url"
        return
    fi

    echo "Error: Neither 'curl' nor 'wget' is installed." >&2
    exit 1
}

sha256_file() {
    local file="$1"

    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$file" | awk '{print tolower($1)}'
        return
    fi

    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$file" | awk '{print tolower($1)}'
        return
    fi

    echo "Error: Neither 'sha256sum' nor 'shasum' is installed." >&2
    exit 1
}

mkdir -p "$MODELS_DIR"
TEMP_DIR=$(mktemp -d "$MODELS_DIR/.download.XXXXXX")
trap 'rm -rf "$TEMP_DIR"' EXIT

# Usage: install_asset <file> <url> <sha256>
install_asset() {
    local name="$1"
    local url="$2"
    local expected="$3"
    local installed="$MODELS_DIR/$name"
    local actual

    if [ "$FORCE_DOWNLOAD" != "1" ] && [ -f "$installed" ] && [ "$(sha256_file "$installed")" = "$expected" ]; then
        echo "$name already exists, skipping."
        return
    fi

    echo "Downloading re-ranker $name..."
    download_to_file "$url" "$TEMP_DIR/$name"
    actual="$(sha256_file "$TEMP_DIR/$name")"
    if [ "$actual" != "$expected" ]; then
        echo "Error: SHA-256 mismatch for $name from $url: expected $expected, got $actual. Nothing was installed from it." >&2
        exit 1
    fi
    mv -f "$TEMP_DIR/$name" "$installed"
    echo "  -> $installed"
}

# The vocabulary goes first: the application turns re-ranking on once model.onnx exists.
install_asset vocab.txt "$BASE_URL/vocab.txt" "$VOCAB_SHA256"
install_asset model.onnx "$BASE_URL/onnx/model.onnx" "$MODEL_SHA256"

echo ""
echo "Done. Re-ranker model files are in: $MODELS_DIR"
