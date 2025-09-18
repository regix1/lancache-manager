#!/bin/bash

# Script to update version across the project

if [ $# -eq 0 ]; then
    echo "Current version: $(cat VERSION)"
    echo "Usage: $0 <new-version>"
    echo "Example: $0 1.2.0"
    exit 1
fi

NEW_VERSION=$1

# Validate version format (basic check for x.y.z)
if ! [[ $NEW_VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Error: Version must be in format x.y.z (e.g., 1.2.0)"
    exit 1
fi

echo "Updating version to $NEW_VERSION..."

# Update VERSION file
echo "$NEW_VERSION" > VERSION

# Update package.json
sed -i "s/\"version\": \".*\"/\"version\": \"$NEW_VERSION\"/" Web/package.json

# Update Dockerfile default ARG
sed -i "s/ARG VERSION=.*/ARG VERSION=$NEW_VERSION/" Dockerfile

echo "✅ Version updated to $NEW_VERSION in:"
echo "   - VERSION"
echo "   - Web/package.json"
echo "   - Dockerfile"
echo ""
echo "Next steps:"
echo "1. Commit these changes: git add . && git commit -m \"Bump version to $NEW_VERSION\""
echo "2. Create tag: git tag v$NEW_VERSION"
echo "3. Push: git push && git push --tags"