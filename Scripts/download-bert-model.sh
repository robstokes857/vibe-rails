#!/usr/bin/env bash
# Downloads and extracts the BertV2 model assets used by semantic search.
# Manual fallback only: release packages bundle these files.
set -euo pipefail

GITHUB_REPO="${GITHUB_REPO:-robstokes857/vibe-rails}"
# The commit that last changed VibeRails/Models/BertV2. Update it together with the
# hashes below; ReleaseSupplyChainTests checks the hashes against the checked-in files.
MODEL_REF="${MODEL_REF:-d0422f76545206eb9181dd546ff20d0b2ebd669c}"
FORCE_DOWNLOAD="${FORCE_DOWNLOAD:-0}"

MODELS_DIR="$HOME/.vibe_rails/models/bertv2"
MODEL_PATH="$MODELS_DIR/model.onnx"
VOCAB_PATH="$MODELS_DIR/vocab.txt"
BASE_URL="https://raw.githubusercontent.com/$GITHUB_REPO/$MODEL_REF/VibeRails/Models/BertV2"

# SHA-256 of the git blobs (vocab.txt is LF in the blob even where a checkout uses CRLF).
VOCAB_SHA256="07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"
MODEL_GZ_SHA256="25e54c5059641222151bc3f4bd5c523ae60057e6296a23fb90317b8ef5522698"

require_cmd() {
    local cmd="$1"
    if ! command -v "$cmd" >/dev/null 2>&1; then
        echo "Error: Required command '$cmd' not found." >&2
        exit 1
    fi
}

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

assert_download_hash() {
    local file="$1"
    local name="$2"
    local expected="$3"
    local actual

    actual="$(sha256_file "$file")"
    if [ "$actual" != "$expected" ]; then
        echo "Error: SHA-256 mismatch for $name from $BASE_URL: expected $expected, got $actual. Nothing was installed from it." >&2
        exit 1
    fi
}

mkdir -p "$MODELS_DIR"

if [ "$FORCE_DOWNLOAD" = "1" ]; then
    rm -f "$MODEL_PATH" "$VOCAB_PATH"
fi

if [ -f "$MODEL_PATH" ] && [ -f "$VOCAB_PATH" ]; then
    echo "BertV2 model assets already exist, skipping."
    echo ""
    echo "Done. BertV2 model files are in: $MODELS_DIR"
    exit 0
fi

TEMP_DIR=$(mktemp -d)
trap 'rm -rf "$TEMP_DIR"' EXIT

if [ ! -f "$VOCAB_PATH" ]; then
    echo "Downloading BertV2 vocab..."
    download_to_file "$BASE_URL/vocab.txt" "$TEMP_DIR/vocab.txt"
    assert_download_hash "$TEMP_DIR/vocab.txt" "vocab.txt" "$VOCAB_SHA256"
    cp "$TEMP_DIR/vocab.txt" "$VOCAB_PATH"
    echo "  -> $VOCAB_PATH"
else
    echo "vocab.txt already exists, skipping."
fi

if [ ! -f "$MODEL_PATH" ]; then
    require_cmd gzip
    echo "Downloading BertV2 model archive..."
    download_to_file "$BASE_URL/model.onnx.gz" "$TEMP_DIR/model.onnx.gz"
    assert_download_hash "$TEMP_DIR/model.onnx.gz" "model.onnx.gz" "$MODEL_GZ_SHA256"

    echo "Extracting BertV2 model..."
    gzip -dc "$TEMP_DIR/model.onnx.gz" > "$TEMP_DIR/model.onnx"
    mv "$TEMP_DIR/model.onnx" "$MODEL_PATH"
    echo "  -> $MODEL_PATH"
else
    echo "model.onnx already exists, skipping."
fi

echo ""
echo "Done. BertV2 model files are in: $MODELS_DIR"
