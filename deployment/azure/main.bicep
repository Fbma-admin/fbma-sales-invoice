@description('Globally unique Azure App Service name.')
param appName string

@description('Azure region for the App Service resources.')
param location string = resourceGroup().location

@description('Complete encrypted SQL Server connection string. Use the exact on-premises DNS host configured in Hybrid Connections.')
@secure()
param sqlConnectionString string

@description('DNS hostname of the on-premises SQL Server. It must resolve from the Hybrid Connection Manager host. Do not use an IP address or localhost.')
param onPremSqlHost string

@description('TCP port used by the on-premises SQL Server.')
param onPremSqlPort int = 1433

@description('Globally unique Azure Relay namespace name.')
param relayNamespaceName string = '${appName}-relay'

param hybridConnectionName string = 'onprem-sql'

@secure()
param apiKey string

@secure()
param adminPassword string

@secure()
param smtpPassword string

param adminUsername string = 'admin'

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${appName}-plan'
  location: location
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  properties: {
    reserved: true
  }
}

resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      healthCheckPath: '/health'
      appSettings: [
        { name: 'ConnectionStrings__SalesDatabase', value: sqlConnectionString }
        { name: 'Security__ApiKey', value: apiKey }
        { name: 'Security__AdminUsername', value: adminUsername }
        { name: 'Security__AdminPassword', value: adminPassword }
        { name: 'Smtp__Password', value: smtpPassword }
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
      ]
    }
  }
}

resource relayNamespace 'Microsoft.Relay/namespaces@2024-01-01' = {
  name: relayNamespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
}

resource hybridConnection 'Microsoft.Relay/namespaces/hybridConnections@2024-01-01' = {
  parent: relayNamespace
  name: hybridConnectionName
  properties: {
    requiresClientAuthorization: true
    userMetadata: '[{"key":"endpoint","value":"${onPremSqlHost}:${onPremSqlPort}"}]'
  }
}

resource hybridConnectionRule 'Microsoft.Relay/namespaces/hybridConnections/authorizationRules@2024-01-01' = {
  parent: hybridConnection
  name: 'appservice'
  properties: {
    rights: [
      'Listen'
      'Send'
    ]
  }
}

resource appHybridConnection 'Microsoft.Web/sites/hybridConnectionNamespaces/relays@2025-03-01' = {
  name: '${app.name}/${relayNamespace.name}/${hybridConnection.name}'
  properties: {
    hostname: onPremSqlHost
    port: onPremSqlPort
    relayArmUri: hybridConnection.id
    relayName: hybridConnection.name
    sendKeyName: hybridConnectionRule.name
    sendKeyValue: hybridConnectionRule.listKeys().primaryKey
    serviceBusNamespace: relayNamespace.name
    serviceBusSuffix: '.servicebus.windows.net'
  }
}

output appUrl string = 'https://${app.properties.defaultHostName}'
output relayNamespace string = relayNamespace.name
output hybridConnection string = hybridConnection.name
