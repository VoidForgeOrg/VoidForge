#!/bin/bash
set -euo pipefail

CONTAINER_NAME="voidforge-postgres"
PASSWORD="${POSTGRES_PASSWORD:-voidforge_dev}"

# Detect if running inside a Docker container
IN_CONTAINER=false
if docker inspect "$(hostname)" &>/dev/null; then
  IN_CONTAINER=true
fi

# Build docker run flags based on environment
if $IN_CONTAINER; then
  NETWORK_ARG="--network container:$(hostname)"
  echo "Detected devcontainer — sharing network stack" >&2
else
  NETWORK_ARG="-p 5432:5432"
  echo "Detected host environment — using port publishing" >&2
fi

# Start postgres idempotently
if docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"; then
  FULL_ID="$(docker inspect "$(hostname)" --format '{{.Id}}' 2>/dev/null || echo "")"
  EXISTING_NET="$(docker inspect "$CONTAINER_NAME" --format '{{.HostConfig.NetworkMode}}' 2>/dev/null || true)"
  EXPECTED_NET="container:$FULL_ID"

  if $IN_CONTAINER && [ "$EXISTING_NET" != "$EXPECTED_NET" ]; then
    echo "Container exists with wrong network mode, recreating..." >&2
    docker rm -f "$CONTAINER_NAME" >/dev/null
  elif docker ps --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"; then
    echo "Postgres is already running." >&2
    docker exec "$CONTAINER_NAME" pg_isready -U postgres -d voidforge &>/dev/null && exit 0
  else
    echo "Starting existing postgres container..." >&2
    docker start "$CONTAINER_NAME" >/dev/null
  fi
fi

# Create container if it doesn't exist
if ! docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"; then
  echo "Creating postgres container..." >&2
  docker run -d --name "$CONTAINER_NAME" \
    $NETWORK_ARG \
    -e POSTGRES_DB=voidforge \
    -e POSTGRES_USER=postgres \
    -e "POSTGRES_PASSWORD=$PASSWORD" \
    postgres:16 >/dev/null
fi

# Wait for healthy
echo "Waiting for postgres to be ready..." >&2
until docker exec "$CONTAINER_NAME" pg_isready -U postgres -d voidforge &>/dev/null; do
  sleep 1
done

# Ensure test database exists
docker exec "$CONTAINER_NAME" psql -U postgres -d voidforge -tc \
  "SELECT 1 FROM pg_database WHERE datname = 'voidforge_test'" | grep -q 1 || \
  docker exec "$CONTAINER_NAME" psql -U postgres -d voidforge -c "CREATE DATABASE voidforge_test;" >/dev/null

echo "Postgres is ready on localhost:5432" >&2
