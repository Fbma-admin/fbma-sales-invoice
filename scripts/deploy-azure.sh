#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"

if ! az account show >/dev/null 2>&1; then
  echo "Azure CLI is not signed in. Run 'az login' and select the intended subscription first." >&2
  exit 1
fi

if [[ ! -f .deployment.env ]]; then
  echo "Missing .deployment.env. Copy .deployment.env.example, fill it with production values, and protect it with chmod 600." >&2
  exit 1
fi

chmod 600 .deployment.env
set -a
source .deployment.env
set +a

required=(AZURE_RESOURCE_GROUP AZURE_LOCATION AZURE_APP_NAME AZURE_RELAY_NAMESPACE ONPREM_SQL_HOST ONPREM_SQL_PORT ONPREM_SQL_CONNECTION_STRING APP_API_KEY APP_ADMIN_USERNAME APP_ADMIN_PASSWORD SMTP_PASSWORD)
for variable_name in "${required[@]}"; do
  if [[ -z "${!variable_name:-}" || "${!variable_name}" == *replace-with* || "${!variable_name}" == *REPLACE* ]]; then
    echo "Set a production value for $variable_name in .deployment.env." >&2
    exit 1
  fi
done

az group create --name "$AZURE_RESOURCE_GROUP" --location "$AZURE_LOCATION" --output none
deployment_output="$(az deployment group create \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --template-file deployment/azure/main.bicep \
  --parameters \
    appName="$AZURE_APP_NAME" \
    location="$AZURE_LOCATION" \
    relayNamespaceName="$AZURE_RELAY_NAMESPACE" \
    onPremSqlHost="$ONPREM_SQL_HOST" \
    onPremSqlPort="$ONPREM_SQL_PORT" \
    sqlConnectionString="$ONPREM_SQL_CONNECTION_STRING" \
    apiKey="$APP_API_KEY" \
    adminUsername="$APP_ADMIN_USERNAME" \
    adminPassword="$APP_ADMIN_PASSWORD" \
    smtpPassword="$SMTP_PASSWORD" \
  --query properties.outputs -o json)"

publish_dir="$(mktemp -d /tmp/fbma-sales-publish.XXXXXX)"
archive_dir="$(mktemp -d /tmp/fbma-sales-archive.XXXXXX)"
archive_path="$archive_dir/fbma-sales-management.zip"
dotnet publish -c Release -o "$publish_dir" --nologo
(cd "$publish_dir" && zip -qr "$archive_path" .)
az webapp deploy \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --name "$AZURE_APP_NAME" \
  --src-path "$archive_path" \
  --type zip \
  --restart true \
  --output none

app_url="$(printf '%s' "$deployment_output" | python3 -c 'import json,sys; print(json.load(sys.stdin)["appUrl"]["value"])')"
echo "Deployment completed: $app_url"
echo "Next: install Hybrid Connection Manager on an on-premises host and add '$AZURE_RELAY_NAMESPACE/onprem-sql'."
