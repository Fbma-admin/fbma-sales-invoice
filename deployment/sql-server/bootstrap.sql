:setvar DatabaseName "FbmaSales"
:setvar AppLogin "fbma_sales_app"
-- AppPassword must be supplied securely with sqlcmd -v AppPassword="...".

IF DB_ID(N'$(DatabaseName)') IS NULL
BEGIN
    EXEC(N'CREATE DATABASE [' + '$(DatabaseName)' + N']');
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(AppLogin)')
BEGIN
    CREATE LOGIN [$(AppLogin)] WITH PASSWORD = N'$(AppPassword)', CHECK_POLICY = ON;
END;
GO

USE [$(DatabaseName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(AppLogin)')
BEGIN
    CREATE USER [$(AppLogin)] FOR LOGIN [$(AppLogin)];
END;
GO

ALTER ROLE db_datareader ADD MEMBER [$(AppLogin)];
ALTER ROLE db_datawriter ADD MEMBER [$(AppLogin)];
ALTER ROLE db_ddladmin ADD MEMBER [$(AppLogin)];
GO

-- The application creates dbo.Sales, dbo.Costs, and dbo.NumberSequences on its
-- first successful startup. Afterward, db_ddladmin can be removed if schema
-- changes will be applied separately by an administrator:
-- ALTER ROLE db_ddladmin DROP MEMBER [$(AppLogin)];
