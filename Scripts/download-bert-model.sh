#!/usr/bin/env bash
# Downloads and extracts the BertV2 model assets used by semantic search.
set -euo pipefail

GITHUB_REPO="${GITHUB_REPO:-robstokes857/vibe-rails}"
MODEL_REF="${MODEL_REF:-main}"
FORCE_DOWNLOAD="${FORCE_DOWNLOAD:-0}"

MODELS_DIR="$HOME/.vibe_rails/models/bertv2"
MODEL_PATH="$MODELS_DIR/model.onnx"
VOCAB_PATH="$MODELS_DIR/vocab.txt"
BASE_URL="https://raw.githubusercontent.com/$GITHUB_REPO/$MODEL_REF/VibeRails/Models/BertV2"

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
    cp "$TEMP_DIR/vocab.txt" "$VOCAB_PATH"
    echo "  -> $VOCAB_PATH"
else
    echo "vocab.txt already exists, skipping."
fi

if [ ! -f "$MODEL_PATH" ]; then
    require_cmd gzip
    echo "Downloading BertV2 model archive..."
    download_to_file "$BASE_URL/model.onnx.gz" "$TEMP_DIR/model.onnx.gz"

    echo "Extracting BertV2 model..."
    gzip -dc "$TEMP_DIR/model.onnx.gz" > "$TEMP_DIR/model.onnx"
    mv "$TEMP_DIR/model.onnx" "$MODEL_PATH"
    echo "  -> $MODEL_PATH"
else
    echo "model.onnx already exists, skipping."
fi

echo ""
echo "Done. BertV2 model files are in: $MODELS_DIR"
