# Build Guide for LancacheManager

This guide explains how to build LancacheManager for different platforms.

## Prerequisites

- **Rust**: 1.75 or later
- **.NET SDK**: 8.0 or later
- **Node.js**: 20 or later (for Web UI)
- **Docker** (optional): For containerized builds

## Platform Support

LancacheManager supports the following platforms:
- **Windows**: x64
- **Linux**: x64 (amd64), ARM64

## Building Rust Executables

### Windows

```powershell
cd rust-processor

# Build for current platform (development)
.\build.ps1 -Target current

# Build for specific platform
.\build.ps1 -Target windows       # Windows x64
.\build.ps1 -Target linux-x64     # Linux x64
.\build.ps1 -Target linux-arm64   # Linux ARM64

# Build for all platforms
.\build.ps1 -Target all
```

### Linux/Mac

```bash
cd rust-processor

# Build for current platform (development)
./build.sh current

# Build for specific platform
./build.sh windows      # Windows x64 (requires mingw-w64)
./build.sh linux-x64    # Linux x64
./build.sh linux-arm64  # Linux ARM64 (requires cross-compilation tools)

# Build for all platforms
./build.sh all
```

### Installing Cross-Compilation Tools (Linux)

For cross-compiling to ARM64 on Linux:

```bash
# Ubuntu/Debian
sudo apt-get install gcc-aarch64-linux-gnu g++-aarch64-linux-gnu

# Fedora/RHEL
sudo dnf install gcc-aarch64-linux-gnu g++-aarch64-linux-gnu
```

## Building .NET Application

The .NET build process automatically builds and includes the Rust executables.

### Development Build

```bash
cd Api/LancacheManager
dotnet build
```

### Production Build

```bash
cd Api/LancacheManager

# Windows x64
dotnet publish -c Release -r win-x64 --self-contained false

# Linux x64
dotnet publish -c Release -r linux-x64 --self-contained false

# Linux ARM64
dotnet publish -c Release -r linux-arm64 --self-contained false
```

## Docker Build

### Single Platform

```bash
# Build for current platform
docker build -t lancache-manager .

# Build for specific platform
docker build --platform linux/amd64 -t lancache-manager:amd64 .
docker build --platform linux/arm64 -t lancache-manager:arm64 .
```

### Multi-Architecture Build

```bash
# Build and push multi-arch image
./docker-build.sh <version> <registry>

# Example:
./docker-build.sh 1.2.0 ghcr.io/regix1/lancache-manager
```

This creates images for both `linux/amd64` and `linux/arm64` platforms.

### Testing Multi-Arch Build Locally

```bash
# Create buildx builder
docker buildx create --name lancache-builder --use

# Build for both platforms (load to local)
docker buildx build \
    --platform linux/amd64,linux/arm64 \
    --tag lancache-manager:test \
    --load \
    .
```

## Project Structure

```
lancache-manager/
├── rust-processor/          # Rust log processor and database reset utilities
│   ├── src/
│   │   ├── main.rs         # Log processor
│   │   └── database_reset.rs
│   ├── bin/                # Platform-specific binaries (after build)
│   │   ├── win-x64/
│   │   ├── linux-x64/
│   │   └── linux-arm64/
│   ├── build.sh            # Linux/Mac build script
│   └── build.ps1           # Windows build script
├── Api/LancacheManager/     # .NET Web API
├── Web/                     # React Web UI
└── Dockerfile               # Multi-stage Docker build
```

## Executable Locations

After building, the executables are located in:

### Development
- **Rust binaries**: `rust-processor/target/release/`
- **.NET output**: `Api/LancacheManager/bin/Debug/net8.0/`

### Production
- **Rust binaries**: `rust-processor/bin/<platform>/`
- **.NET output**: `Api/LancacheManager/bin/Release/net8.0/<rid>/publish/`

The .NET build automatically copies the correct Rust executables to:
`bin/<config>/net8.0/rust-processor/`

## Platform Detection

The application automatically detects the platform and uses the correct executable:

- **Windows**: Uses `.exe` executables
- **Linux**: Uses executables without extension

This is handled by the `IPathResolver` interface:
- `WindowsPathResolver`: Returns paths with `.exe` extension
- `LinuxPathResolver`: Returns paths without extension

## Troubleshooting

### Rust Build Fails

1. Ensure Rust toolchain is up to date: `rustup update`
2. Add the target platform: `rustup target add <target>`
3. For ARM64 cross-compilation, install the necessary toolchains

### Executable Not Found

The executable path is resolved through `IPathResolver.GetRustLogProcessorPath()` and `GetRustDatabaseResetPath()`. Ensure the executables are in the `rust-processor/` directory within the application base directory.

### Docker Build Issues

- Ensure Docker buildx is installed: `docker buildx version`
- Create a new builder if needed: `docker buildx create --use`
- Check platform support: `docker buildx ls`

## CI/CD Integration

For automated builds, see the GitHub Actions workflow in `.github/workflows/`.

Example workflow steps:
1. Build Rust binaries for all platforms
2. Build .NET application with platform-specific Rust binaries
3. Create Docker multi-arch image
4. Push to container registry
