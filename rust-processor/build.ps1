# Rust build script for multi-platform support
# Supports: x86_64-pc-windows-msvc, x86_64-unknown-linux-gnu, aarch64-unknown-linux-gnu

param(
    [ValidateSet("current", "all", "windows", "linux-x64", "linux-arm64")]
    [string]$Target = "current"
)

$ErrorActionPreference = "Stop"

Write-Host "[BUILD] Building Rust executables for multiple platforms..." -ForegroundColor Green

function Build-ForTarget {
    param(
        [string]$TargetTriple,
        [string]$OutputDir
    )

    Write-Host "Building for $TargetTriple..." -ForegroundColor Yellow

    # Install target if not already installed
    rustup target add $TargetTriple 2>$null

    # Build both binaries
    cargo build --release --target $TargetTriple

    # Create output directory
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    # Copy binaries to output directory
    if ($TargetTriple -like "*windows*") {
        Copy-Item "target\$TargetTriple\release\lancache_processor.exe" "$OutputDir\" -Force
        Copy-Item "target\$TargetTriple\release\database_reset.exe" "$OutputDir\" -Force
        Write-Host "[OK] Windows executables copied to $OutputDir" -ForegroundColor Green
    } else {
        Copy-Item "target/$TargetTriple/release/lancache_processor" "$OutputDir/" -Force
        Copy-Item "target/$TargetTriple/release/database_reset" "$OutputDir/" -Force
        Write-Host "[OK] Linux executables copied to $OutputDir" -ForegroundColor Green
    }
}

switch ($Target) {
    "current" {
        Write-Host "Building for current platform only" -ForegroundColor Yellow
        cargo build --release
    }

    "all" {
        Write-Host "Building for all supported platforms" -ForegroundColor Yellow
        Build-ForTarget "x86_64-pc-windows-msvc" "bin\win-x64"
        Build-ForTarget "x86_64-unknown-linux-gnu" "bin\linux-x64"
        Build-ForTarget "aarch64-unknown-linux-gnu" "bin\linux-arm64"
    }

    "windows" {
        Build-ForTarget "x86_64-pc-windows-msvc" "bin\win-x64"
    }

    "linux-x64" {
        Build-ForTarget "x86_64-unknown-linux-gnu" "bin\linux-x64"
    }

    "linux-arm64" {
        Build-ForTarget "aarch64-unknown-linux-gnu" "bin\linux-arm64"
    }
}

Write-Host "[OK] Build complete!" -ForegroundColor Green
