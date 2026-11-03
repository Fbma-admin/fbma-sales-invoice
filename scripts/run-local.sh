#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"

if [[ ! -f .local-credentials ]]; then
  echo "Local setup is incomplete. Run ./scripts/setup-local.sh first." >&2
  exit 1
fi

if ! docker compose ps --status running sql | grep -q sql; then
  docker compose up -d sql
fi

export ASPNETCORE_ENVIRONMENT=Development
exec dotnet run --no-launch-profile --urls http://127.0.0.1:5080
