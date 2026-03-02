#!/bin/bash
# Downloads the all-MiniLM-L6-v2 ONNX model files used by BERT input capture.
set -e

MODELS_DIR="$HOME/.vibe_rails/models/bert"
mkdir -p "$MODELS_DIR"

BASE_URL="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main"

if [ ! -f "$MODELS_DIR/vocab.txt" ]; then
    echo "Downloading vocab.txt..."
    curl -L -o "$MODELS_DIR/vocab.txt" "$BASE_URL/vocab.txt"
    echo "  -> $MODELS_DIR/vocab.txt"
else
    echo "vocab.txt already exists, skipping."
fi

if [ -f "$MODELS_DIR/model.onnx" ]; then
    SIZE_BYTES=$(wc -c < "$MODELS_DIR/model.onnx")
    if [ "$SIZE_BYTES" -ge 100000000 ]; then
        echo "Existing model.onnx is larger than 100MB, replacing with MiniLM..."
        rm -f "$MODELS_DIR/model.onnx"
    fi
fi

if [ ! -f "$MODELS_DIR/model.onnx" ]; then
    echo "Downloading model.onnx (~90MB)..."
    curl -L -o "$MODELS_DIR/model.onnx" "$BASE_URL/onnx/model.onnx"
    echo "  -> $MODELS_DIR/model.onnx"
else
    echo "model.onnx already exists, skipping."
fi

echo ""
echo "Done. BERT model files are in: $MODELS_DIR"
