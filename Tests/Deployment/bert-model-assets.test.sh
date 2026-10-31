#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
# Load only this function, never the network/installation entry point.
source <(sed -n '/^install_bertv2_assets() {/,/^}/p' Scripts/install.sh)
GREEN='' CYAN='' RED='' NC=''
require_cmd() { command -v "$1" >/dev/null; }
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
bundle="$fixture/Models/BertV2"
runtime="$fixture/models/bertv2"
mkdir -p "$bundle" "$runtime"
payload() { printf 'expected model' | gzip > "$bundle/model.onnx.gz"; printf 'expected vocab' > "$bundle/vocab.txt"; }
assert_assets() {
    [ "$(cat "$runtime/model.onnx")" = 'expected model' ]
    [ "$(cat "$runtime/vocab.txt")" = 'expected vocab' ]
    [ ! -f "$bundle/model.onnx.gz" ]
    [ ! -f "$runtime/model.onnx.br" ]
}
payload
install_bertv2_assets "$fixture"
assert_assets
printf obsolete > "$runtime/model.onnx"
printf obsolete > "$runtime/vocab.txt"
printf obsolete > "$runtime/model.onnx.br"
payload
install_bertv2_assets "$fixture"
assert_assets
payload
touch -t 200101010000 "$runtime/model.onnx"
before=$(stat -c %Y "$runtime/model.onnx")
install_bertv2_assets "$fixture"
[ "$(stat -c %Y "$runtime/model.onnx")" = "$before" ]
payload
printf broken > "$bundle/model.onnx.gz"
if (set -e; install_bertv2_assets "$fixture"); then exit 1; fi
[ "$(cat "$runtime/model.onnx")" = 'expected model' ]
echo 'Passed: shell model install, upgrade, unchanged assets, cleanup and failed extraction.'
