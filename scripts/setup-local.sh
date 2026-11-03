#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"

for command_name in dotnet docker openssl; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Missing required command: $command_name" >&2
    exit 1
  fi
done

if ! docker info >/dev/null 2>&1; then
  echo "Docker is not running. Start Docker Desktop and run this script again." >&2
  exit 1
fi

credentials_file="$project_dir/.local-credentials"
compose_env_file="$project_dir/.env"
if [[ ! -f "$credentials_file" ]]; then
  umask 077
  sql_sa_password="FbmaSa_$(openssl rand -hex 18)"
  sql_app_password="FbmaApp_$(openssl rand -hex 18)"
  api_key="$(openssl rand -hex 32)"
  admin_password="FbmaAdmin_$(openssl rand -hex 18)"
  {
    echo "MSSQL_SA_PASSWORD=$sql_sa_password"
    echo "FBMA_SALES_DB_PASSWORD=$sql_app_password"
    echo "FBMA_API_KEY=$api_key"
    echo "FBMA_ADMIN_USERNAME=admin"
    echo "FBMA_ADMIN_PASSWORD=$admin_password"
  } > "$credentials_file"
  echo "Generated local credentials in .local-credentials (mode 600)."
fi

chmod 600 "$credentials_file"
set -a
source "$credentials_file"
set +a

if [[ ! -f "$compose_env_file" ]]; then
  umask 077
  {
    echo "MSSQL_SA_PASSWORD=$MSSQL_SA_PASSWORD"
    echo "FBMA_SALES_DB_PASSWORD=$FBMA_SALES_DB_PASSWORD"
  } > "$compose_env_file"
fi
chmod 600 "$compose_env_file"

docker compose up -d sql
echo "Waiting for SQL Server to become healthy..."
for attempt in {1..60}; do
  health="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}starting{{end}}' "$(docker compose ps -q sql)" 2>/dev/null || true)"
  if [[ "$health" == "healthy" ]]; then
    break
  fi
  if [[ "$attempt" == "60" ]]; then
    docker compose logs --tail=100 sql
    echo "SQL Server did not become healthy." >&2
    exit 1
  fi
  sleep 2
done

docker compose exec -T sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C \
  -i /bootstrap/bootstrap.sql \
  -v "AppPassword=$FBMA_SALES_DB_PASSWORD"

connection_string="Server=127.0.0.1;Database=FbmaSales;User ID=fbma_sales_app;Password=$FBMA_SALES_DB_PASSWORD;Encrypt=True;TrustServerCertificate=True;Connect Timeout=30"
dotnet user-secrets set "ConnectionStrings:SalesDatabase" "$connection_string" >/dev/null
dotnet user-secrets set "Security:ApiKey" "$FBMA_API_KEY" >/dev/null
dotnet user-secrets set "Security:AdminUsername" "$FBMA_ADMIN_USERNAME" >/dev/null
dotnet user-secrets set "Security:AdminPassword" "$FBMA_ADMIN_PASSWORD" >/dev/null

dotnet restore
dotnet build --no-restore

echo
echo "Local setup is ready."
echo "Run: ./scripts/run-local.sh"
echo "Dashboard: http://127.0.0.1:5080/"
echo "Health: http://127.0.0.1:5080/health"
echo "Local login and API credentials are stored in .local-credentials."
