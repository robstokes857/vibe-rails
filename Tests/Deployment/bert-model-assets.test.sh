#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
# Load only this function, never the network/installation entry point.
source <(sed -n '/^install_model_assets() {/,/^}/p' Scripts/install.sh)
GREEN='' CYAN='' RED='' YELLOW='' NC=''
require_cmd() { command -v "$1" >/dev/null; }
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT

# An older release without the re-ranker installs; a missing required model fails.
install_model_assets "$fixture" Reranker optional
[ ! -d "$fixture/models/reranker" ]
if (install_model_assets "$fixture" BertV2); then exit 1; fi

for name in BertV2 Reranker; do
    lower=$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')
    bundle="$fixture/Models/$name"
    runtime="$fixture/models/$lower"
    mkdir -p "$bundle" "$runtime"
    payload() { printf 'expected %s model' "$name" | gzip > "$bundle/model.onnx.gz"; printf 'expected %s vocab' "$name" > "$bundle/vocab.txt"; }
    assert_assets() {
        [ "$(cat "$runtime/model.onnx")" = "expected $name model" ]
        [ "$(cat "$runtime/vocab.txt")" = "expected $name vocab" ]
        [ ! -f "$bundle/model.onnx.gz" ]
        [ ! -f "$runtime/model.onnx.br" ]
    }
    payload
    install_model_assets "$fixture" "$name" optional
    assert_assets
    printf obsolete > "$runtime/model.onnx"
    printf obsolete > "$runtime/vocab.txt"
    printf obsolete > "$runtime/model.onnx.br"
    payload
    install_model_assets "$fixture" "$name"
    assert_assets
    payload
    touch -t 200101010000 "$runtime/model.onnx"
    before=$(stat -c %Y "$runtime/model.onnx" 2>/dev/null || stat -f %m "$runtime/model.onnx")
    install_model_assets "$fixture" "$name"
    [ "$(stat -c %Y "$runtime/model.onnx" 2>/dev/null || stat -f %m "$runtime/model.onnx")" = "$before" ]
    payload
    printf broken > "$bundle/model.onnx.gz"
    if (set -e; install_model_assets "$fixture" "$name" optional); then exit 1; fi
    [ "$(cat "$runtime/model.onnx")" = "expected $name model" ]
done
# Each model keeps its own runtime folder.
[ "$(cat "$fixture/models/bertv2/model.onnx")" = 'expected BertV2 model' ]
echo 'Passed: BertV2 and Reranker shell install, upgrade, unchanged assets, cleanup, failed extraction and optional skip.'
