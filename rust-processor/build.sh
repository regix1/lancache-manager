#!/bin/bash
set -e

# Rust build script for multi-platform support
# Supports: x86_64-pc-windows-msvc, x86_64-unknown-linux-gnu, aarch64-unknown-linux-gnu

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${GREEN}[BUILD] Building Rust executables for multiple platforms...${NC}"

# Detect current platform
if [[ "$OSTYPE" == "msys" ]] || [[ "$OSTYPE" == "win32" ]]; then
    CURRENT_OS="windows"
elif [[ "$OSTYPE" == "linux-gnu"* ]]; then
    CURRENT_OS="linux"
else
    CURRENT_OS="unknown"
fi

# Default to building for current platform only if no target specified
BUILD_TARGET=${1:-"current"}

build_for_target() {
    local target=$1
    local output_dir=$2

    echo -e "${YELLOW}Building for ${target}...${NC}"

    # Install target if not already installed
    rustup target add $target 2>/dev/null || true

    # Build both binaries
    cargo build --release --target $target

    # Create output directory
    mkdir -p "$output_dir"

    # Copy binaries to output directory
    if [[ $target == *"windows"* ]]; then
        cp "target/${target}/release/lancache_processor.exe" "$output_dir/"
        cp "target/${target}/release/database_reset.exe" "$output_dir/"
        echo -e "${GREEN}[OK] Windows executables copied to $output_dir${NC}"
    else
        cp "target/${target}/release/lancache_processor" "$output_dir/"
        cp "target/${target}/release/database_reset" "$output_dir/"
        chmod +x "$output_dir/lancache_processor"
        chmod +x "$output_dir/database_reset"
        echo -e "${GREEN}[OK] Linux executables copied to $output_dir${NC}"
    fi
}

case $BUILD_TARGET in
    "current")
        echo -e "${YELLOW}Building for current platform only${NC}"
        cargo build --release
        ;;

    "all")
        echo -e "${YELLOW}Building for all supported platforms${NC}"
        build_for_target "x86_64-pc-windows-msvc" "bin/win-x64"
        build_for_target "x86_64-unknown-linux-gnu" "bin/linux-x64"
        build_for_target "aarch64-unknown-linux-gnu" "bin/linux-arm64"
        ;;

    "windows")
        build_for_target "x86_64-pc-windows-msvc" "bin/win-x64"
        ;;

    "linux-x64")
        build_for_target "x86_64-unknown-linux-gnu" "bin/linux-x64"
        ;;

    "linux-arm64")
        build_for_target "aarch64-unknown-linux-gnu" "bin/linux-arm64"
        ;;

    *)
        echo -e "${RED}Unknown target: $BUILD_TARGET${NC}"
        echo "Usage: $0 [current|all|windows|linux-x64|linux-arm64]"
        exit 1
        ;;
esac

echo -e "${GREEN}[OK] Build complete!${NC}"
