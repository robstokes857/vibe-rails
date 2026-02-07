#!/usr/bin/env bash
# install.sh - Install VibeRails (vb) on Linux/macOS
# Usage: wget -qO- https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.sh | bash

set -euo pipefail

GITHUB_REPO="robstokes857/vibe-rails"
INSTALL_DIR="$HOME/.vibe_rails"
ASSET_NAME="vb-linux-x64.tar.gz"

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

# Detect OS
OS="$(uname -s)"
case "$OS" in
    Linux*)  OS_TYPE="linux" ;;
    Darwin*) OS_TYPE="macos" ;;
    *)       echo -e "${RED}Error: Unsupported operating system: $OS${NC}"; exit 1 ;;
esac

if [ "$OS_TYPE" = "macos" ]; then
    echo -e "${YELLOW}Warning: macOS binary is not yet available. Only Linux x64 is supported.${NC}"
    echo -e "${YELLOW}You can build from source or run in Docker.${NC}"
    exit 1
fi

# Check for required tools
for cmd in wget tar sha256sum; do
    if ! command -v "$cmd" &> /dev/null; then
        echo -e "${RED}Error: Required command '$cmd' not found.${NC}"
        exit 1
    fi
done

# Get latest release info
echo -e "${CYAN}Fetching latest release...${NC}"
RELEASE_URL="https://api.github.com/repos/$GITHUB_REPO/releases/latest"

RELEASE_JSON=$(wget -qO- --header="User-Agent: VibeRails-Installer" "$RELEASE_URL") || {
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

# Create temp directory
TEMP_DIR=$(mktemp -d)
trap 'rm -rf "$TEMP_DIR"' EXIT

# Download files
TAR_PATH="$TEMP_DIR/$ASSET_NAME"
CHECKSUM_PATH="$TEMP_DIR/$ASSET_NAME.sha256"

echo -e "${CYAN}Downloading $ASSET_NAME...${NC}"
wget -q -O "$TAR_PATH" "$TAR_URL"

if [ -n "$CHECKSUM_URL" ]; then
    echo -e "${CYAN}Downloading checksum...${NC}"
    wget -q -O "$CHECKSUM_PATH" "$CHECKSUM_URL"

    # Verify checksum
    echo -e "${CYAN}Verifying checksum...${NC}"
    EXPECTED_HASH=$(cut -d' ' -f1 "$CHECKSUM_PATH")
    ACTUAL_HASH=$(sha256sum "$TAR_PATH" | cut -d' ' -f1)

    if [ "$EXPECTED_HASH" != "$ACTUAL_HASH" ]; then
        echo -e "${RED}Error: Checksum verification failed!${NC}"
        echo -e "${RED}Expected: $EXPECTED_HASH${NC}"
        echo -e "${RED}Actual:   $ACTUAL_HASH${NC}"
        exit 1
    fi
    echo -e "${GREEN}Checksum verified!${NC}"
fi

# Create install directory
if [ -d "$INSTALL_DIR" ]; then
    echo -e "${YELLOW}Removing existing installation...${NC}"
    rm -rf "$INSTALL_DIR"
fi
mkdir -p "$INSTALL_DIR"

# Extract
echo -e "${CYAN}Extracting to $INSTALL_DIR...${NC}"
tar -xzf "$TAR_PATH" -C "$INSTALL_DIR"

# Make binary executable
chmod +x "$INSTALL_DIR/vb"

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
echo -e "  2. Run: ${NC}source ~/.bashrc${YELLOW} (or ~/.zshrc)${NC}"
echo ""
echo -e "${YELLOW}Then run:${NC}"
echo -e "  vb --help"
echo ""
