# FBMA Sales Management

.NET 8 Azure App Service application for recording paid FBMA mobile orders, issuing sequential English PDF invoices, emailing the customer and internal recipients, and tracking operating costs in an on-premises Microsoft SQL Server database running on Linux.

## Numbering and tax

- `OrderId` is the immutable reference supplied by FBMA Mobile.
- Invoice numbers are generated independently as `01`, `02`, `03`, etc.
- VAT is calculated at 5%. The document is labelled `INVOICE`, not `TAX INVOICE`, and does not contain a TRN.
- Reposting the same `OrderId` is safe and returns the original record without issuing another invoice.

## Configure

Do not put production secrets in `appsettings.json`. Set them through environment variables or an on-premises secret store:

```text
Security__ApiKey
Security__AdminUsername
Security__AdminPassword
ConnectionStrings__SalesDatabase
Smtp__Host
Smtp__Port
Smtp__Username
Smtp__Password
Smtp__SenderEmail
Organization__Name
Organization__VatPercent
Organization__InternalEmails__0
Organization__InternalEmails__1
```

For the current Microsoft 365 mailbox, the host is `smtp.office365.com`, the port is `587`, TLS is enabled, and both the username and sender are `developer@fbma.org.ae`. Supply only `Smtp__Password` from the protected on-premises server environment. Do not commit it to this project.

The supplied FBMA logo is stored at `assets/fbma-logo.jpg` and embedded into generated invoices. The organization name and both internal recipients are already configured.

## Complete local setup

The repository pins the .NET 8 SDK and includes a local SQL Server container. Keep all passwords outside source control. The automated setup generates strong local-only credentials, starts SQL Server, creates the database and restricted login, configures .NET user-secrets, restores packages, and builds the application:

```bash
./scripts/setup-local.sh
./scripts/run-local.sh
```

Open `http://127.0.0.1:5080/login`. The generated dashboard user ID, password, and API key are stored locally in `.local-credentials`, which is excluded from Git. The dashboard uses a secure sign-in cookie; the API key is reserved for trusted server-to-server calls. Check the complete environment at any time with:

```bash
./scripts/doctor.sh
```

### Manual setup

Use these steps only when you do not want the automated setup:

```bash
cp .env.example .env
# Edit .env and replace both placeholder passwords.
docker compose up -d
```

Wait until `docker compose ps` reports SQL as healthy. Then load the database and restricted application login. Enter the same two passwords stored in your local `.env` when prompted:

```bash
read -s -p "SQL sa password: " MSSQL_SA_PASSWORD; echo
read -s -p "FBMA sales app password: " FBMA_SALES_DB_PASSWORD; echo
docker compose exec -T sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C \
  -i /bootstrap/bootstrap.sql \
  -v AppPassword="$FBMA_SALES_DB_PASSWORD"
unset MSSQL_SA_PASSWORD FBMA_SALES_DB_PASSWORD
```

Configure development secrets using .NET user-secrets:

```bash
dotnet user-secrets set "ConnectionStrings:SalesDatabase" \
  "Server=127.0.0.1;Database=FbmaSales;User ID=fbma_sales_app;Password=YOUR_APP_PASSWORD;Encrypt=True;TrustServerCertificate=True;Connect Timeout=30"
dotnet user-secrets set "Security:ApiKey" "YOUR_RANDOM_API_KEY_AT_LEAST_32_CHARACTERS"
dotnet user-secrets set "Security:AdminUsername" "admin"
dotnet user-secrets set "Security:AdminPassword" "YOUR_RANDOM_ADMIN_PASSWORD"
# Add SMTP settings only when you intentionally want local email delivery.
```

Build and run:

```bash
dotnet restore
dotnet build
dotnet run --urls http://127.0.0.1:5080
```

Verify `http://127.0.0.1:5080/health`, then open `http://127.0.0.1:5080/login` and sign in with the configured admin credentials. Each sales row has an **Invoice PDF** download, and **Export sales PDF** creates a paginated report of up to the latest 1,000 sales.

## Database: SQL Server on Linux

Install SQL Server on the on-premises Linux host, then run `deployment/sql-server/bootstrap.sql` with an administrator account and supply `AppPassword` through `sqlcmd -v`. The application creates its tables on the first successful connection.

The production connection string must be stored in Azure App Service configuration, not in source control:

```text
Server=tcp:fbma-sql-onprem,1433;Database=FbmaSales;User ID=fbma_sales_app;Password=SECRET;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30
```

Use a stable DNS hostname that matches the SQL Server TLS certificate. `TrustServerCertificate=False` must remain enabled for production certificate validation.

## Azure-to-on-premises connection

For one SQL endpoint, configure an Azure App Service Hybrid Connection targeting the on-premises SQL DNS hostname and TCP port `1433`. Install Hybrid Connection Manager on a Linux machine that can reach SQL Server. The manager makes an outbound TLS connection to Azure over port `443`; do not expose SQL port `1433` publicly.

The hostname in the application connection string must exactly match the Hybrid Connection endpoint hostname.

## Deploy Azure resources

Deploy this application to a dedicated App Service such as `app-fbma-sales`. Do not deploy it to `app-web-01` (website), `app-web-02` (mobile CMS/API), or `app-03` (bowling API).

The Bicep template in `deployment/azure/main.bicep` creates the Linux .NET 8 App Service, an Azure Relay namespace, the SQL Hybrid Connection, HTTPS enforcement, and health checks. The deployment script publishes the application without committing secrets:

```bash
az login
az account set --subscription YOUR_SUBSCRIPTION_ID
cp .deployment.env.example .deployment.env
chmod 600 .deployment.env
# Fill .deployment.env with the production resource names, on-premises SQL DNS hostname,
# encrypted SQL connection string, and SMTP password.
./scripts/deploy-azure.sh
```

The SQL hostname must be a DNS name that resolves from the on-premises Hybrid Connection Manager host. It must exactly match both `ONPREM_SQL_HOST` and the server name in `ONPREM_SQL_CONNECTION_STRING`. Do not publish SQL port 1433 to the internet.

The equivalent manual deployment is:

```bash
az deployment group create \
  --resource-group YOUR_RESOURCE_GROUP \
  --template-file deployment/azure/main.bicep \
  --parameters appName=YOUR_UNIQUE_APP_NAME \
  --parameters sqlConnectionString='YOUR_CONNECTION_STRING' \
  --parameters apiKey='YOUR_RANDOM_API_KEY' \
  --parameters adminPassword='YOUR_RANDOM_ADMIN_PASSWORD' \
  --parameters smtpPassword='YOUR_SMTP_PASSWORD'

dotnet publish -c Release -o publish
cd publish && zip -r ../fbma-sales-management.zip . && cd ..
az webapp deploy \
  --resource-group YOUR_RESOURCE_GROUP \
  --name YOUR_UNIQUE_APP_NAME \
  --src-path fbma-sales-management.zip \
  --type zip
```

Configure the Hybrid Connection in the Azure portal after creating the app: **App Service > Networking > Hybrid connections**.

## Run locally against SQL Server

```bash
dotnet restore
dotnet run --urls http://0.0.0.0:5080
```

Open `http://server-address:5080/` and sign in with the configured admin credentials.

## GitHub CI/CD

The workflow at `.github/workflows/production.yml` builds pushes and pull requests targeting `main`. A push to `main` deploys the release artifact to the dedicated sales App Service configured by the GitHub repository variable `AZURE_SALES_WEBAPP_NAME`. Configure the following GitHub repository secrets for Azure OIDC:

```text
AZURE_CLIENT_ID
AZURE_TENANT_ID
AZURE_SUBSCRIPTION_ID
```

The workflow refuses to deploy when the target is one of the existing website, CMS, or bowling App Services.

## Record a paid sale

Only the server that has verified the payment should call this endpoint:

```bash
curl -X POST http://server-address:5080/api/sales \
  -H 'Content-Type: application/json' \
  -H 'X-API-Key: your-secret-key' \
  -d '{
    "orderId":"FBMA-ABC1234",
    "customerName":"Test Customer",
    "customerEmail":"customer@example.com",
    "customerPhone":"0500000000",
    "itemDescription":"Bowling - Lane 5",
    "quantity":1,
    "unitPrice":40,
    "paymentDate":"2026-10-19T12:00:00+04:00",
    "paymentMethod":"Credit Card",
    "paymentReference":"MPGS-REFERENCE",
    "bookingDate":"2026-10-20T16:00:00+04:00"
  }'
```

## Record a cost

`POST /api/costs` accepts `description`, `category`, `amount`, `costDate`, and optional `notes`. Use an authenticated dashboard session or the API key.

## Production notes

- Keep the Azure App Service HTTPS-only setting enabled.
- Restrict dashboard access to authorized FBMA users and restrict API access where possible.
- Back up the on-premises SQL Server database daily and test restoration regularly.
- Monitor the Linux Hybrid Connection Manager and SQL Server services.
- Replace all placeholder credentials before first use.
- The existing FBMA backend still needs one authenticated call added after verified payment/booking completion.
