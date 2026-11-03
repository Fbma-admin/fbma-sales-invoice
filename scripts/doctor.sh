#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"

failed=0
check_command() {
  if command -v "$1" >/dev/null 2>&1; then
    echo "OK   $1"
  else
    echo "FAIL $1 is not installed"
    failed=1
  fi
}

check_command dotnet
check_command docker
check_command openssl

if [[ -f .local-credentials && -f .env ]]; then
  echo "OK   local credential files"
else
  echo "FAIL local setup files are missing; run ./scripts/setup-local.sh"
  failed=1
fi

if docker info >/dev/null 2>&1; then
  echo "OK   Docker engine"
  if [[ -n "$(docker compose ps -q sql 2>/dev/null)" ]]; then
    sql_health="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$(docker compose ps -q sql)")"
    echo "INFO SQL Server: $sql_health"
  else
    echo "INFO SQL Server: not created"
  fi
else
  echo "FAIL Docker engine is not running"
  failed=1
fi

if dotnet build --nologo >/dev/null; then
  echo "OK   .NET restore and build"
else
  echo "FAIL .NET build"
  failed=1
fi

exit "$failed"
