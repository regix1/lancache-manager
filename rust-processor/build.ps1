# Rust build script for multi-platform support
# Supports: x86_64-pc-windows-msvc, x86_64-unknown-linux-gnu, aarch64-unknown-linux-gnu

param(
    [ValidateSet("current", "all", "windows", "linux-x64", "linux-arm64")]
    [string]$Target = "current"
)

$ErrorActionPreference = "Stop"

Write-Host "[BUILD] Building Rust executables for multiple platforms..." -ForegroundColor Green

# All binary names (from Cargo.toml [[bin]] sections)
$Binaries = @(
    "log_processor",           # Primary log processor (was lancache_processor)
    "log_service_manager",     # Service counting/removal from logs (was log_manager)
    "cache_size",              # Calculate cache size and estimate deletion time
    "cache_clear",             # Clear entire cache (was cache_cleaner)
    "cache_corruption",        # Detect/remove corrupted chunks (was corruption_manager)
    "cache_game_detect",       # Detect games in cache (was game_cache_detector)
    "cache_game_remove",       # Remove game from cache (was game_cache_remover)
    "cache_service_remove",    # Remove service from cache (was service_remover)
    "db_reset",                # Reset database (was database_reset)
    "db_migrate"               # Import from DeveLanCacheUI (was data_migrator)
)

function Build-ForTarget {
    param(
        [string]$TargetTriple,
        [string]$OutputDir
    )

    Write-Host "Building for $TargetTriple..." -ForegroundColor Yellow

    # Install target if not already installed
    rustup target add $TargetTriple 2>$null

    # Build all binaries
    cargo build --release --target $TargetTriple

    # Create output directory
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    # Copy all binaries to output directory
    $isWindowsTarget = $TargetTriple -like "*windows*"
    $extension = if ($isWindowsTarget) { ".exe" } else { "" }
    $separator = if ($isWindowsTarget) { "\" } else { "/" }

    foreach ($binary in $Binaries) {
        $sourcePath = "target$separator$TargetTriple${separator}release$separator$binary$extension"
        $destPath = "$OutputDir$separator$binary$extension"
        Copy-Item $sourcePath $destPath -Force
        Write-Host "  Copied $binary$extension" -ForegroundColor Gray
    }

    Write-Host "[OK] $($Binaries.Count) executables copied to $OutputDir" -ForegroundColor Green
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
