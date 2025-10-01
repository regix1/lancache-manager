#!/bin/bash
# Docker multi-arch build script for LancacheManager
# Builds for linux/amd64 and linux/arm64

set -e

VERSION=${1:-"latest"}
REGISTRY=${2:-"ghcr.io/regix1/lancache-manager"}

echo "🐳 Building multi-arch Docker image for LancacheManager"
echo "Version: $VERSION"
echo "Registry: $REGISTRY"

# Ensure buildx is available
if ! docker buildx version &> /dev/null; then
    echo "❌ Docker buildx is not available. Please install it."
    exit 1
fi

# Create buildx builder if it doesn't exist
if ! docker buildx inspect lancache-builder &> /dev/null; then
    echo "Creating buildx builder..."
    docker buildx create --name lancache-builder --use
else
    docker buildx use lancache-builder
fi

# Build and push multi-arch image
echo "Building for platforms: linux/amd64, linux/arm64"
docker buildx build \
    --platform linux/amd64,linux/arm64 \
    --build-arg VERSION=$VERSION \
    --tag $REGISTRY:$VERSION \
    --tag $REGISTRY:latest \
    --push \
    --file Dockerfile \
    .

echo "✅ Multi-arch build complete!"
echo "Images pushed:"
echo "  - $REGISTRY:$VERSION"
echo "  - $REGISTRY:latest"
