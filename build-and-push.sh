#!/bin/bash
set -e

# Load environment variables from .env file if it exists
if [ -f .env ]; then
    echo "Loading configuration from .env file..."
    export $(cat .env | grep -v '^#' | xargs)
fi

# GitHub Container Registry
REGISTRY="${REGISTRY:-ghcr.io}"
if [ -z "$IMAGE_NAME" ]; then
    if git rev-parse --git-dir > /dev/null 2>&1; then
        IMAGE_NAME=$(git config --get remote.origin.url 2>/dev/null | sed 's/.*github.com[:/]\(.*\)\.git/\1/' | tr '[:upper:]' '[:lower:]')
    fi
    IMAGE_NAME="${IMAGE_NAME:-npm-docker-sync}"
fi
IMAGE="${REGISTRY}/${IMAGE_NAME}"

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

echo "Building multi-architecture image: ${IMAGE}:${TAG}"

# Build (and optionally push) multi-arch image
if [ "$PUSH" = "true" ] || [ "$PUSH" = "1" ]; then
    echo "Building and pushing image..."
    docker buildx build --platform linux/amd64,linux/arm64 -t ${IMAGE}:${TAG} --push .
else
    echo "Building image (local only, not pushing)..."
    docker buildx build --platform linux/amd64,linux/arm64 -t ${IMAGE}:${TAG} --load .
fi

echo "Done!"
