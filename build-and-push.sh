#!/bin/bash
set -e

# Load environment variables from .env file if it exists
if [ -f .env ]; then
    echo "Loading configuration from .env file..."
    export $(cat .env | grep -v '^#' | xargs)
fi

# Primary registry (GitHub Container Registry)
PRIMARY_REGISTRY="${PRIMARY_REGISTRY:-ghcr.io}"
if [ -z "$PRIMARY_IMAGE_NAME" ]; then
    if git rev-parse --git-dir > /dev/null 2>&1; then
        PRIMARY_IMAGE_NAME=$(git config --get remote.origin.url 2>/dev/null | sed 's/.*github.com[:/]\(.*\)\.git/\1/' | tr '[:upper:]' '[:lower:]')
    fi
    PRIMARY_IMAGE_NAME="${PRIMARY_IMAGE_NAME:-npm-docker-sync}"
fi
PRIMARY_IMAGE="${PRIMARY_REGISTRY}/${PRIMARY_IMAGE_NAME}"

# Tag (default to git branch or 'latest')
if [ -z "$TAG" ]; then
    if git rev-parse --git-dir > /dev/null 2>&1; then
        TAG=$(git branch --show-current 2>/dev/null)
    fi
    TAG="${TAG:-latest}"
fi

# Ensure buildx builder exists for multi-platform builds
if ! docker buildx inspect multiarch-builder > /dev/null 2>&1; then
    echo "Creating buildx builder for multi-platform builds..."
    docker buildx create --name multiarch-builder --use --bootstrap
else
    echo "Using existing buildx builder..."
    docker buildx use multiarch-builder
fi

echo "Building multi-architecture image..."
echo "Primary image: ${PRIMARY_IMAGE}:${TAG}"

# Build image tags - start with secondary registry if configured (for local testing)
IMAGE_TAGS=""

# Add secondary registry if configured
if [ -n "$SECONDARY_REGISTRY" ] && [ -n "$SECONDARY_IMAGE_NAME" ]; then
    SECONDARY_IMAGE="${SECONDARY_REGISTRY}/${SECONDARY_IMAGE_NAME}"
    echo "Secondary image: ${SECONDARY_IMAGE}:${TAG}"
    IMAGE_TAGS="-t ${SECONDARY_IMAGE}:${TAG}"

    # Login to secondary registry if credentials provided
    if [ -n "$SECONDARY_REGISTRY_USERNAME" ] && [ -n "$SECONDARY_REGISTRY_PASSWORD" ]; then
        echo "Logging in to ${SECONDARY_REGISTRY}..."
        echo "$SECONDARY_REGISTRY_PASSWORD" | docker login "$SECONDARY_REGISTRY" -u "$SECONDARY_REGISTRY_USERNAME" --password-stdin
    fi
fi

# Add primary registry only if explicitly enabled
if [ "$PUSH_PRIMARY" = "true" ] || [ "$PUSH_PRIMARY" = "1" ]; then
    echo "Primary image: ${PRIMARY_IMAGE}:${TAG}"
    IMAGE_TAGS="${IMAGE_TAGS} -t ${PRIMARY_IMAGE}:${TAG}"
fi

# Build (and optionally push) multi-arch image
if [ "$PUSH" = "true" ] || [ "$PUSH" = "1" ]; then
    echo "Building and pushing images..."
    docker buildx build --platform linux/amd64,linux/arm64 ${IMAGE_TAGS} --push .
else
    echo "Building images (local only, not pushing)..."
    docker buildx build --platform linux/amd64,linux/arm64 ${IMAGE_TAGS} --load .
fi

echo "Done!"
