#!/usr/bin/env bash
# install.sh - Install VibeRails (vb) on Linux/macOS
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.sh | bash
#   wget -qO-  https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.sh | bash

set -euo pipefail

GITHUB_REPO="robstokes857/vibe-rails"
INSTALL_DIR="$HOME/.vibe_rails"

install_bertv2_assets() {
    local root_dir="$1"
    local bundled_dir="$root_dir/Models/BertV2"
    local bundled_model_archive="$bundled_dir/model.onnx.gz"
    local bundled_vocab="$bundled_dir/vocab.txt"

    local runtime_dir="$root_dir/models/bertv2"
    local runtime_model="$runtime_dir/model.onnx"
    local runtime_vocab="$runtime_dir/vocab.txt"

    if [ -f "$runtime_model" ] && [ -f "$runtime_vocab" ]; then
        echo -e "${GREEN}BertV2 model assets already installed, skipping.${NC}"
        return
    fi

    if [ ! -f "$bundled_model_archive" ]; then
        echo -e "${RED}Error: Bundled BertV2 model archive not found at $bundled_model_archive. The release package is incomplete.${NC}" >&2
        exit 1
    fi
    if [ ! -f "$bundled_vocab" ]; then
        echo -e "${RED}Error: Bundled BertV2 vocab not found at $bundled_vocab. The release package is incomplete.${NC}" >&2
        exit 1
    fi

    mkdir -p "$runtime_dir"

    if [ ! -f "$runtime_vocab" ]; then
        echo -e "${CYAN}Installing BertV2 vocab...${NC}"
        cp "$bundled_vocab" "$runtime_vocab"
    fi

    if [ ! -f "$runtime_model" ]; then
        require_cmd gzip
        echo -e "${CYAN}Extracting BertV2 model...${NC}"
        gzip -dc "$bundled_model_archive" > "$runtime_model"
    fi

    echo -e "${GREEN}BertV2 model assets installed to $runtime_dir${NC}"
}

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
MAGENTA='\033[0;35m'
NC='\033[0m' # No Color

echo -e "${MAGENTA}"
cat << 'EOF'

  ╦  ╦╦╔╗ ╔═╗  ╦═╗╔═╗╦╦  ╔═╗  ╦╔╗╔╔═╗╔╦╗╔═╗╦  ╦  ╔═╗╦═╗
  ╚╗╔╝║╠╩╗║╣   ╠╦╝╠═╣║║  ╚═╗  ║║║║╚═╗ ║ ╠═╣║  ║  ║╣ ╠╦╝
   ╚╝ ╩╚═╝╚═╝  ╩╚═╩ ╩╩╩═╝╚═╝  ╩╝╚╝╚═╝ ╩ ╩ ╩╩═╝╩═╝╚═╝╩╚═

EOF
echo -e "${NC}"

require_cmd() {
    local cmd="$1"
    if ! command -v "$cmd" &> /dev/null; then
        echo -e "${RED}Error: Required command '$cmd' not found.${NC}"
        exit 1
    fi
}

download_to_file() {
    local url="$1"
    local output="$2"

    if command -v curl &> /dev/null; then
        curl -fsSL "$url" -o "$output"
        return
    fi

    if command -v wget &> /dev/null; then
        wget -q -O "$output" "$url"
        return
    fi

    echo -e "${RED}Error: Neither 'curl' nor 'wget' is installed.${NC}"
    exit 1
}

fetch_text() {
    local url="$1"

    if command -v curl &> /dev/null; then
        curl -fsSL -H "User-Agent: VibeRails-Installer" "$url"
        return
    fi

    if command -v wget &> /dev/null; then
        wget -qO- --header="User-Agent: VibeRails-Installer" "$url"
        return
    fi

    echo -e "${RED}Error: Neither 'curl' nor 'wget' is installed.${NC}"
    exit 1
}

sha256_file() {
    local file="$1"

    if command -v sha256sum &> /dev/null; then
        sha256sum "$file" | awk '{print tolower($1)}'
        return
    fi

    if command -v shasum &> /dev/null; then
        shasum -a 256 "$file" | awk '{print tolower($1)}'
        return
    fi

    echo -e "${RED}Error: Neither 'sha256sum' nor 'shasum' is installed.${NC}"
    exit 1
}

validate_release_payload() {
    local root_dir="$1"
    local binary_name="$2"
    local model_archive_name="$3"
    local relative_path
    local entry
    local entry_name
    local entry_name_lower

    local required_files=(
        "$binary_name"
        "appsettings.json"
        "wwwroot/index.html"
        "Models/BertV2/$model_archive_name"
        "Models/BertV2/vocab.txt"
        "scripts/pre-commit-hook.sh"
        "scripts/commit-msg-hook.sh"
    )

    case "$OS_TYPE" in
        linux)
            required_files+=("libonnxruntime.so" "libe_sqlite3.so" "vec0.so")
            ;;
        macos)
            required_files+=("libonnxruntime.dylib" "libe_sqlite3.dylib" "vec0.dylib")
            ;;
        *)
            echo -e "${RED}Error: Cannot validate native libraries for unsupported platform '$OS_TYPE'.${NC}" >&2
            return 1
            ;;
    esac

    for relative_path in "${required_files[@]}"; do
        if [ ! -f "$root_dir/$relative_path" ]; then
            echo -e "${RED}Error: Release package is incomplete: required file '$relative_path' is missing. The existing installation was not changed.${NC}" >&2
            return 1
        fi
    done

    # The payload is overlaid into ~/.vibe_rails. Refuse an archive that
    # accidentally contains known user-owned roots before it can overwrite them.
    # Compare the archive's actual entry spelling so bundled `Models/` remains
    # distinct from runtime `models/`, even on case-insensitive macOS volumes.
    for entry in "$root_dir"/*; do
        if [ ! -e "$entry" ] && [ ! -L "$entry" ]; then
            continue
        fi
        entry_name=${entry##*/}
        entry_name_lower=$(printf '%s' "$entry_name" | tr '[:upper:]' '[:lower:]')
        if [ "$entry_name" = "Models" ]; then
            continue
        fi
        case "$entry_name_lower" in
            config.json|envs|history|logs|models|sandboxes|state.db|state.db-*)
                echo -e "${RED}Error: Release package contains protected user-data path '$entry_name'. The existing installation was not changed.${NC}" >&2
                return 1
                ;;
        esac
    done
}

validate_install_target() {
    local owner_uid
    local current_uid

    if [ -L "$INSTALL_DIR" ]; then
        echo -e "${RED}Error: Installation target must not be a symlink: $INSTALL_DIR${NC}" >&2
        return 1
    fi
    if [ -e "$INSTALL_DIR" ] && [ ! -d "$INSTALL_DIR" ]; then
        echo -e "${RED}Error: Installation target exists but is not a directory: $INSTALL_DIR${NC}" >&2
        return 1
    fi
    if [ ! -d "$INSTALL_DIR" ]; then
        return 0
    fi

    current_uid=$(id -u)
    case "$OS_TYPE" in
        linux) owner_uid=$(stat -c '%u' "$INSTALL_DIR") ;;
        macos) owner_uid=$(stat -f '%u' "$INSTALL_DIR") ;;
        *)
            echo -e "${RED}Error: Cannot validate installation ownership for '$OS_TYPE'.${NC}" >&2
            return 1
            ;;
    esac
    if [ "$owner_uid" != "$current_uid" ]; then
        echo -e "${RED}Error: Installation target must be owned by the current user: $INSTALL_DIR${NC}" >&2
        return 1
    fi
}

get_vbd_status() {
    local executable="$1"
    local json
    local compact_json

    if ! json=$("$executable" --job-daemon-service status --json 2>/dev/null); then
        echo "VBD status command failed." >&2
        return 1
    fi

    compact_json=$(printf '%s' "$json" | tr -d '\r\n')
    if ! grep -Eq '"isInstalled"[[:space:]]*:[[:space:]]*(true|false)' <<<"$compact_json"; then
        echo "VBD status JSON did not contain isInstalled." >&2
        return 1
    fi
    if ! grep -Eq '"isRunning"[[:space:]]*:[[:space:]]*(true|false)' <<<"$compact_json"; then
        echo "VBD status JSON did not contain isRunning." >&2
        return 1
    fi

    printf '%s' "$compact_json"
}

json_boolean_is_true() {
    local json="$1"
    local property_name="$2"
    grep -Eq "\"${property_name}\"[[:space:]]*:[[:space:]]*true" <<<"$json"
}

json_string_value() {
    local json="$1"
    local property_name="$2"
    printf '%s' "$json" |
        sed -n "s/.*\"${property_name}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" |
        head -1
}

assert_no_destination_links() {
    local payload_dir="$1"
    local install_dir="$2"
    local entry relative destination

    # cp -R overlays THROUGH an existing symlink at any payload-shadowed path (e.g. a planted
    # 'wwwroot' link would redirect application files into another directory). Refuse to copy
    # while any such link exists.
    while IFS= read -r -d '' entry; do
        relative="${entry#"$payload_dir"/}"
        destination="$install_dir/$relative"
        if [ -L "$destination" ]; then
            echo -e "${RED}Error: Refusing to overlay through a symlink at '$destination'. Remove the link and retry; no application files were replaced.${NC}" >&2
            return 1
        fi
    done < <(find "$payload_dir" -mindepth 1 -print0)
}

wait_for_vbd_running_state() {
    local executable="$1"
    local expected_running="$2"
    local deadline=$((SECONDS + 10))
    local status_json
    local is_running

    while (( SECONDS < deadline )); do
        if status_json=$(get_vbd_status "$executable" 2>/dev/null); then
            is_running=false
            if json_boolean_is_true "$status_json" "isRunning"; then
                is_running=true
            fi
            if [ "$is_running" = "$expected_running" ]; then
                return 0
            fi
        fi
        sleep 0.25
    done

    return 1
}

wait_for_process_exit() {
    local target_pid="$1"
    local deadline=$((SECONDS + 10))

    while kill -0 "$target_pid" 2>/dev/null; do
        if (( SECONDS >= deadline )); then
            return 1
        fi
        sleep 0.25
    done

    return 0
}

print_vbd_recovery_commands() {
    local installed_executable="$INSTALL_DIR/vb"
    local quoted_executable
    printf -v quoted_executable '%q' "$installed_executable"

    echo "" >&2
    echo -e "${RED}VBD could not be restored automatically.${NC}" >&2
    echo -e "${YELLOW}After resolving the installation error, run these current-user commands:${NC}" >&2
    echo "  $quoted_executable --job-daemon-service repair" >&2
    if [ "$DAEMON_WAS_RUNNING" = true ]; then
        echo "  $quoted_executable --job-daemon-service start" >&2
    fi
    echo "" >&2
}

# Detect OS
OS="$(uname -s)"
ARCH="$(uname -m)"
case "$OS" in
    Linux*)  OS_TYPE="linux" ;;
    Darwin*) OS_TYPE="macos" ;;
    *)       echo -e "${RED}Error: Unsupported operating system: $OS${NC}"; exit 1 ;;
esac

# Resolve release asset name by OS + arch
case "$OS_TYPE:$ARCH" in
    linux:x86_64|linux:amd64)
        ASSET_NAME="vb-linux-x64.tar.gz"
        ;;
    macos:x86_64|macos:amd64)
        echo -e "${RED}Error: Intel Macs are no longer supported.${NC}"
        echo -e "${YELLOW}VibeRails requires Apple Silicon on macOS. The upstream ONNX Runtime${NC}"
        echo -e "${YELLOW}no longer ships a macOS x86_64 build, so semantic search cannot run.${NC}"
        echo -e "${YELLOW}The last Intel Mac release is v1.10.0.${NC}"
        exit 1
        ;;
    macos:arm64|macos:aarch64)
        ASSET_NAME="vb-osx-arm64.tar.gz"
        ;;
    *)
        echo -e "${RED}Error: Unsupported platform: $OS_TYPE/$ARCH${NC}"
        echo -e "${YELLOW}Supported targets: linux-x64, osx-arm64${NC}"
        exit 1
        ;;
esac

echo -e "${CYAN}Detected platform: $OS_TYPE/$ARCH${NC}"
echo -e "${CYAN}Using asset: $ASSET_NAME${NC}"

# Check required tools
require_cmd tar
if ! command -v curl &> /dev/null && ! command -v wget &> /dev/null; then
    echo -e "${RED}Error: Either 'curl' or 'wget' is required.${NC}"
    exit 1
fi

# Get latest release info
echo -e "${CYAN}Fetching latest release...${NC}"
RELEASE_URL="https://api.github.com/repos/$GITHUB_REPO/releases/latest"

RELEASE_JSON=$(fetch_text "$RELEASE_URL") || {
    echo -e "${RED}Error: Could not fetch release info. Check your internet connection.${NC}"
    exit 1
}

VERSION=$(echo "$RELEASE_JSON" | grep -o '"tag_name": *"[^"]*"' | head -1 | cut -d'"' -f4)
echo -e "${GREEN}Latest version: $VERSION${NC}"

# Extract download URLs
TAR_URL=$(echo "$RELEASE_JSON" | grep -o "\"browser_download_url\": *\"[^\"]*$ASSET_NAME\"" | cut -d'"' -f4)
CHECKSUM_URL=$(echo "$RELEASE_JSON" | grep -o "\"browser_download_url\": *\"[^\"]*$ASSET_NAME.sha256\"" | cut -d'"' -f4)

if [ -z "$TAR_URL" ]; then
    echo -e "${RED}Error: Could not find $ASSET_NAME in release assets.${NC}"
    exit 1
fi

# Fail closed: without the published checksum the installer would extract and execute an
# unverified download. A release missing its .sha256 asset is a broken release.
if [ -z "$CHECKSUM_URL" ]; then
    echo -e "${RED}Error: Could not find $ASSET_NAME.sha256 in release assets. Refusing to install an unverified download.${NC}"
    exit 1
fi

# Create a private, random staging directory. The archive is fully extracted and
# validated here before the live installation or VBD process is touched.
TEMP_ROOT="${TMPDIR:-/tmp}"
TEMP_DIR=$(mktemp -d "$TEMP_ROOT/vibe_rails_install.XXXXXXXX")
chmod 700 "$TEMP_DIR"
PAYLOAD_DIR="$TEMP_DIR/payload"
mkdir -m 700 "$PAYLOAD_DIR"

DAEMON_WAS_INSTALLED=false
DAEMON_WAS_RUNNING=false
RECOVERY_NEEDED=false

cleanup() {
    local exit_code="$1"
    if [ "$exit_code" -ne 0 ] && [ "$RECOVERY_NEEDED" = true ]; then
        print_vbd_recovery_commands
    fi
    if [ -n "${TEMP_DIR:-}" ] && [ -d "$TEMP_DIR" ]; then
        rm -rf -- "$TEMP_DIR"
    fi
    trap - EXIT
    exit "$exit_code"
}
trap 'cleanup $?' EXIT

# Download files
TAR_PATH="$TEMP_DIR/$ASSET_NAME"
CHECKSUM_PATH="$TEMP_DIR/$ASSET_NAME.sha256"

echo -e "${CYAN}Downloading $ASSET_NAME...${NC}"
download_to_file "$TAR_URL" "$TAR_PATH"

echo -e "${CYAN}Downloading checksum...${NC}"
download_to_file "$CHECKSUM_URL" "$CHECKSUM_PATH"

# Verify checksum (mandatory: the asset's presence was asserted before downloading)
echo -e "${CYAN}Verifying checksum...${NC}"
EXPECTED_HASH=$(cut -d' ' -f1 "$CHECKSUM_PATH" | tr '[:upper:]' '[:lower:]')
ACTUAL_HASH=$(sha256_file "$TAR_PATH")

if [ "$EXPECTED_HASH" != "$ACTUAL_HASH" ]; then
    echo -e "${RED}Error: Checksum verification failed!${NC}"
    echo -e "${RED}Expected: $EXPECTED_HASH${NC}"
    echo -e "${RED}Actual:   $ACTUAL_HASH${NC}"
    exit 1
fi
echo -e "${GREEN}Checksum verified!${NC}"

echo -e "${CYAN}Extracting release into private staging...${NC}"
tar -xzf "$TAR_PATH" -C "$PAYLOAD_DIR"
validate_release_payload "$PAYLOAD_DIR" "vb" "model.onnx.gz"
validate_install_target
chmod +x "$PAYLOAD_DIR/vb"
echo -e "${GREEN}Release payload validated.${NC}"

# The staged binary can inspect and control the stable current-user registration even if
# the old installed executable is absent or predates this command. On hosts that refuse to
# execute a fresh download from TMPDIR (noexec mounts, AV), fall back to the installed
# executable; if neither can answer, VBD cannot have been registered by a pre-VBD build,
# so treat it as not installed instead of failing the whole install.
VBD_EXECUTABLE="$PAYLOAD_DIR/vb"
if ! VBD_STATUS_JSON=$(get_vbd_status "$PAYLOAD_DIR/vb"); then
    echo -e "${YELLOW}Staged VBD probe failed (TMPDIR may be mounted noexec).${NC}"
    VBD_STATUS_JSON=""
    if [ -x "$INSTALL_DIR/vb" ]; then
        echo -e "${YELLOW}Falling back to the installed executable for the VBD probe...${NC}"
        if VBD_STATUS_JSON=$(get_vbd_status "$INSTALL_DIR/vb"); then
            VBD_EXECUTABLE="$INSTALL_DIR/vb"
        else
            VBD_STATUS_JSON=""
            echo -e "${YELLOW}WARNING: VBD state could not be determined (the installed executable may predate VBD). Assuming it is not installed.${NC}"
        fi
    fi
fi

VBD_PROCESS_ID=""
if [ -n "$VBD_STATUS_JSON" ]; then
    VBD_STATE=$(json_string_value "$VBD_STATUS_JSON" "state" | tr '[:upper:]' '[:lower:]')
    if json_boolean_is_true "$VBD_STATUS_JSON" "isInstalled"; then
        DAEMON_WAS_INSTALLED=true
    fi
    # isReachable guards against a status whose isRunning was computed while the process
    # was still starting; either signal means a live daemon must stop before file swaps.
    if json_boolean_is_true "$VBD_STATUS_JSON" "isRunning" ||
        json_boolean_is_true "$VBD_STATUS_JSON" "isReachable"; then
        DAEMON_WAS_RUNNING=true
    fi
    VBD_PROCESS_ID=$(printf '%s' "$VBD_STATUS_JSON" |
        sed -n 's/.*"pid"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p')

    # An Error state means VBD's own view of the registration is broken; an Unavailable state
    # alongside an active daemon/registration means lifecycle control (including stop) cannot
    # work. Proceeding would replace files under a running daemon.
    if [ "$VBD_STATE" = "error" ]; then
        echo -e "${RED}Error: VBD reported lifecycle state 'Error'. Resolve it (vb --job-daemon-service status) and retry. The existing installation was not changed.${NC}" >&2
        exit 1
    fi
    if [ "$VBD_STATE" = "unavailable" ] &&
        { [ "$DAEMON_WAS_INSTALLED" = true ] || [ "$DAEMON_WAS_RUNNING" = true ]; }; then
        echo -e "${RED}Error: VBD lifecycle support is unavailable while a VBD process or registration appears active. Resolve it and retry. The existing installation was not changed.${NC}" >&2
        exit 1
    fi
fi

if [ "$DAEMON_WAS_INSTALLED" = true ]; then
    if [ "$DAEMON_WAS_RUNNING" = true ]; then
        echo -e "${CYAN}Detected installed VBD (running).${NC}"
    else
        echo -e "${CYAN}Detected installed VBD (stopped).${NC}"
    fi
else
    echo -e "${CYAN}VBD is not installed for the current user.${NC}"
fi

if [ "$DAEMON_WAS_INSTALLED" = true ] || [ "$DAEMON_WAS_RUNNING" = true ]; then
    echo -e "${CYAN}Ensuring VBD is stopped before replacing files...${NC}"
    RECOVERY_NEEDED=true
    if ! "$VBD_EXECUTABLE" --job-daemon-service stop; then
        echo -e "${RED}Error: Could not stop VBD. The existing installation was not changed.${NC}" >&2
        exit 1
    fi
    if ! wait_for_vbd_running_state "$VBD_EXECUTABLE" false; then
        echo -e "${RED}Error: VBD did not stop within 10 seconds. The existing installation was not changed.${NC}" >&2
        exit 1
    fi
    if [ -n "$VBD_PROCESS_ID" ] && ! wait_for_process_exit "$VBD_PROCESS_ID"; then
        echo -e "${RED}Error: The previous VBD process (PID $VBD_PROCESS_ID) did not exit within 10 seconds. The existing installation was not changed.${NC}" >&2
        exit 1
    fi
    echo -e "${GREEN}VBD stopped.${NC}"
fi

# Overlay release files without deleting ~/.vibe_rails, which also contains
# state.db, environments, logs, models, sandboxes, and user scripts.
if [ ! -d "$INSTALL_DIR" ]; then
    mkdir -m 700 "$INSTALL_DIR"
fi
validate_install_target
if [ "$DAEMON_WAS_INSTALLED" = true ]; then
    RECOVERY_NEEDED=true
fi
assert_no_destination_links "$PAYLOAD_DIR" "$INSTALL_DIR"
echo -e "${CYAN}Installing application files to $INSTALL_DIR...${NC}"
cp -R "$PAYLOAD_DIR"/. "$INSTALL_DIR"/

install_bertv2_assets "$INSTALL_DIR"

# Make binary executable
chmod +x "$INSTALL_DIR/vb"

if [ "$DAEMON_WAS_INSTALLED" = true ]; then
    echo -e "${CYAN}Repairing current-user VBD registration...${NC}"
    if ! "$INSTALL_DIR/vb" --job-daemon-service repair; then
        echo -e "${RED}Error: VBD registration repair failed.${NC}" >&2
        exit 1
    fi
fi

if [ "$DAEMON_WAS_RUNNING" = true ]; then
    echo -e "${CYAN}Restarting VBD because it was running before the update...${NC}"
    if ! "$INSTALL_DIR/vb" --job-daemon-service start; then
        echo -e "${RED}Error: VBD restart failed.${NC}" >&2
        exit 1
    fi
    if ! wait_for_vbd_running_state "$INSTALL_DIR/vb" true; then
        echo -e "${RED}Error: VBD did not report running within 10 seconds after restart.${NC}" >&2
        exit 1
    fi
    echo -e "${GREEN}VBD restarted.${NC}"
elif [ "$DAEMON_WAS_INSTALLED" = true ]; then
    echo -e "${GREEN}VBD registration repaired; it remains stopped.${NC}"
fi

if [ "$DAEMON_WAS_INSTALLED" = true ] || [ "$DAEMON_WAS_RUNNING" = true ]; then
    RECOVERY_NEEDED=false
fi

# Add to PATH in shell rc files
add_to_path() {
    local rc_file="$1"
    local export_line='export PATH="$HOME/.vibe_rails:$PATH"'

    if [ -f "$rc_file" ]; then
        if ! grep -q ".vibe_rails" "$rc_file"; then
            echo "" >> "$rc_file"
            echo "# VibeRails" >> "$rc_file"
            echo "$export_line" >> "$rc_file"
            echo -e "${GREEN}Added to $rc_file${NC}"
        else
            echo -e "${GREEN}$rc_file already configured${NC}"
        fi
    fi
}

echo -e "${CYAN}Configuring PATH...${NC}"

# Add to common shell rc files
add_to_path "$HOME/.bashrc"
add_to_path "$HOME/.zshrc"
add_to_path "$HOME/.zprofile"

# Also try profile files for login shells
if [ -f "$HOME/.profile" ] && ! grep -q ".vibe_rails" "$HOME/.profile" 2>/dev/null; then
    add_to_path "$HOME/.profile"
fi

echo ""
echo -e "${GREEN}Installation complete!${NC}"
echo ""
echo -e "${CYAN}Installed to: $INSTALL_DIR${NC}"
echo ""
echo -e "${YELLOW}To get started, either:${NC}"
echo -e "  1. Open a NEW terminal, or"
echo -e "  2. Run: ${NC}source ~/.bashrc${YELLOW} (or ~/.zshrc / ~/.zprofile)${NC}"
echo ""
echo -e "${YELLOW}Then run:${NC}"
echo -e "  vb --help"
echo ""
