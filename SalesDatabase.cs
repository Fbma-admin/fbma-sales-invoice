using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace FBMA.SalesManagement;

public sealed class SalesDatabase(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("SalesDatabase")
        ?? throw new InvalidOperationException("ConnectionStrings:SalesDatabase is required.");

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.Sales', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.Sales (
                    Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sales PRIMARY KEY,
                    InvoiceNumber NVARCHAR(30) NOT NULL CONSTRAINT UQ_Sales_InvoiceNumber UNIQUE,
                    OrderId NVARCHAR(80) NOT NULL CONSTRAINT UQ_Sales_OrderId UNIQUE,
                    CustomerName NVARCHAR(200) NOT NULL,
                    CustomerEmail NVARCHAR(254) NOT NULL,
                    CustomerPhone NVARCHAR(40) NULL,
                    ItemDescription NVARCHAR(240) NOT NULL,
                    Quantity INT NOT NULL,
                    UnitPrice DECIMAL(19,2) NOT NULL,
                    Subtotal DECIMAL(19,2) NOT NULL,
                    VatAmount DECIMAL(19,2) NOT NULL,
                    Total DECIMAL(19,2) NOT NULL,
                    Currency CHAR(3) NOT NULL,
                    PaymentDate DATETIMEOFFSET NOT NULL,
                    PaymentMethod NVARCHAR(40) NOT NULL,
                    PaymentReference NVARCHAR(120) NULL,
                    BookingDate DATETIMEOFFSET NULL,
                    EmailStatus NVARCHAR(20) NOT NULL CONSTRAINT DF_Sales_EmailStatus DEFAULT N'Pending',
                    CreatedAt DATETIMEOFFSET NOT NULL
                );
                CREATE INDEX IX_Sales_PaymentDate ON dbo.Sales(PaymentDate DESC);
            END;

            IF OBJECT_ID(N'dbo.Costs', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.Costs (
                    Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Costs PRIMARY KEY,
                    Description NVARCHAR(200) NOT NULL,
                    Category NVARCHAR(100) NOT NULL,
                    Amount DECIMAL(19,2) NOT NULL,
                    CostDate DATE NOT NULL,
                    Notes NVARCHAR(500) NULL,
                    CreatedAt DATETIMEOFFSET NOT NULL
                );
                CREATE INDEX IX_Costs_CostDate ON dbo.Costs(CostDate DESC);
            END;

            IF OBJECT_ID(N'dbo.NumberSequences', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.NumberSequences (
                    Name NVARCHAR(50) NOT NULL CONSTRAINT PK_NumberSequences PRIMARY KEY,
                    CurrentValue BIGINT NOT NULL
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.NumberSequences WHERE Name = N'Invoice')
                INSERT INTO dbo.NumberSequences(Name, CurrentValue) VALUES (N'Invoice', 0);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(Sale Sale, bool Created)> CreateSaleAsync(SaleRequest request, string currency, decimal vatPercent)
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var orderId = request.OrderId.Trim();
        var existing = await FindByOrderIdAsync(connection, orderId, transaction);
        if (existing is not null)
        {
            await transaction.CommitAsync();
            return (existing, false);
        }

        await using var sequenceCommand = connection.CreateCommand();
        sequenceCommand.Transaction = transaction;
        sequenceCommand.CommandText = """
            UPDATE dbo.NumberSequences WITH (UPDLOCK, HOLDLOCK)
            SET CurrentValue = CurrentValue + 1
            OUTPUT inserted.CurrentValue
            WHERE Name = N'Invoice';
            """;
        var sequence = Convert.ToInt64(await sequenceCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        var invoiceNumber = sequence.ToString("D2", CultureInfo.InvariantCulture);
        var subtotal = decimal.Round(request.UnitPrice * request.Quantity, 2, MidpointRounding.AwayFromZero);
        var vat = decimal.Round(subtotal * vatPercent / 100m, 2, MidpointRounding.AwayFromZero);
        var now = DateTimeOffset.UtcNow;

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO dbo.Sales(InvoiceNumber, OrderId, CustomerName, CustomerEmail, CustomerPhone,
              ItemDescription, Quantity, UnitPrice, Subtotal, VatAmount, Total, Currency,
              PaymentDate, PaymentMethod, PaymentReference, BookingDate, EmailStatus, CreatedAt)
            OUTPUT inserted.Id
            VALUES (@invoice, @order, @name, @email, @phone, @item, @quantity, @unit, @subtotal,
              @vat, @total, @currency, @paymentDate, @method, @reference, @bookingDate, N'Pending', @created);
            """;
        Add(insert, "@invoice", SqlDbType.NVarChar, invoiceNumber, 30);
        Add(insert, "@order", SqlDbType.NVarChar, orderId, 80);
        Add(insert, "@name", SqlDbType.NVarChar, request.CustomerName.Trim(), 200);
        Add(insert, "@email", SqlDbType.NVarChar, request.CustomerEmail.Trim(), 254);
        AddNullable(insert, "@phone", request.CustomerPhone, 40);
        Add(insert, "@item", SqlDbType.NVarChar, request.ItemDescription.Trim(), 240);
        Add(insert, "@quantity", SqlDbType.Int, request.Quantity);
        AddMoney(insert, "@unit", request.UnitPrice);
        AddMoney(insert, "@subtotal", subtotal);
        AddMoney(insert, "@vat", vat);
        AddMoney(insert, "@total", subtotal + vat);
        Add(insert, "@currency", SqlDbType.Char, currency, 3);
        Add(insert, "@paymentDate", SqlDbType.DateTimeOffset, request.PaymentDate);
        Add(insert, "@method", SqlDbType.NVarChar, request.PaymentMethod.Trim(), 40);
        AddNullable(insert, "@reference", request.PaymentReference, 120);
        Add(insert, "@bookingDate", SqlDbType.DateTimeOffset, request.BookingDate is null ? DBNull.Value : request.BookingDate.Value);
        Add(insert, "@created", SqlDbType.DateTimeOffset, now);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        await transaction.CommitAsync();

        return (new Sale(id, invoiceNumber, orderId, request.CustomerName.Trim(), request.CustomerEmail.Trim(),
            request.CustomerPhone, request.ItemDescription.Trim(), request.Quantity, request.UnitPrice, subtotal, vat,
            subtotal + vat, currency, request.PaymentDate, request.PaymentMethod.Trim(), request.PaymentReference,
            request.BookingDate, "Pending", now), true);
    }

    public async Task<Sale?> GetSaleAsync(long id)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM dbo.Sales WHERE Id = @id";
        Add(command, "@id", SqlDbType.BigInt, id);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSale(reader) : null;
    }

    public async Task<IReadOnlyList<Sale>> GetSalesAsync(int limit = 250, DateOnly? from = null, DateOnly? to = null)
    {
        var result = new List<Sale>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (@limit) * FROM dbo.Sales
            WHERE (@from IS NULL OR CAST(PaymentDate AS date) >= @from)
              AND (@to IS NULL OR CAST(PaymentDate AS date) <= @to)
            ORDER BY Id DESC
            """;
        Add(command, "@limit", SqlDbType.Int, Math.Clamp(limit, 1, 1000));
        Add(command, "@from", SqlDbType.Date, from is null ? DBNull.Value : from.Value.ToDateTime(TimeOnly.MinValue));
        Add(command, "@to", SqlDbType.Date, to is null ? DBNull.Value : to.Value.ToDateTime(TimeOnly.MinValue));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadSale(reader));
        return result;
    }

    public async Task<Cost> CreateCostAsync(CostRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.Costs(Description, Category, Amount, CostDate, Notes, CreatedAt)
            OUTPUT inserted.Id
            VALUES (@description, @category, @amount, @date, @notes, @created);
            """;
        Add(command, "@description", SqlDbType.NVarChar, request.Description.Trim(), 200);
        Add(command, "@category", SqlDbType.NVarChar, request.Category.Trim(), 100);
        AddMoney(command, "@amount", request.Amount);
        Add(command, "@date", SqlDbType.Date, request.CostDate.ToDateTime(TimeOnly.MinValue));
        AddNullable(command, "@notes", request.Notes, 500);
        Add(command, "@created", SqlDbType.DateTimeOffset, now);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        return new Cost(id, request.Description.Trim(), request.Category.Trim(), request.Amount, request.CostDate, request.Notes, now);
    }

    public async Task<IReadOnlyList<Cost>> GetCostsAsync(int limit = 250, DateOnly? from = null, DateOnly? to = null)
    {
        var result = new List<Cost>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (@limit) * FROM dbo.Costs
            WHERE (@from IS NULL OR CostDate >= @from)
              AND (@to IS NULL OR CostDate <= @to)
            ORDER BY CostDate DESC, Id DESC
            """;
        Add(command, "@limit", SqlDbType.Int, Math.Clamp(limit, 1, 1000));
        Add(command, "@from", SqlDbType.Date, from is null ? DBNull.Value : from.Value.ToDateTime(TimeOnly.MinValue));
        Add(command, "@to", SqlDbType.Date, to is null ? DBNull.Value : to.Value.ToDateTime(TimeOnly.MinValue));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new Cost(reader.GetInt64(reader.GetOrdinal("Id")), reader.GetString(reader.GetOrdinal("Description")),
                reader.GetString(reader.GetOrdinal("Category")), reader.GetDecimal(reader.GetOrdinal("Amount")),
                DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("CostDate"))), NullableString(reader, "Notes"),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CreatedAt"))));
        }
        return result;
    }

    public async Task SetEmailStatusAsync(long saleId, string status)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.Sales SET EmailStatus = @status WHERE Id = @id";
        Add(command, "@status", SqlDbType.NVarChar, status, 20);
        Add(command, "@id", SqlDbType.BigInt, saleId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<Sale?> FindByOrderIdAsync(SqlConnection connection, string orderId, SqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM dbo.Sales WITH (UPDLOCK, HOLDLOCK) WHERE OrderId = @order";
        Add(command, "@order", SqlDbType.NVarChar, orderId, 80);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSale(reader) : null;
    }

    private static Sale ReadSale(SqlDataReader reader) => new(
        reader.GetInt64(reader.GetOrdinal("Id")), reader.GetString(reader.GetOrdinal("InvoiceNumber")),
        reader.GetString(reader.GetOrdinal("OrderId")), reader.GetString(reader.GetOrdinal("CustomerName")),
        reader.GetString(reader.GetOrdinal("CustomerEmail")), NullableString(reader, "CustomerPhone"),
        reader.GetString(reader.GetOrdinal("ItemDescription")), reader.GetInt32(reader.GetOrdinal("Quantity")),
        reader.GetDecimal(reader.GetOrdinal("UnitPrice")), reader.GetDecimal(reader.GetOrdinal("Subtotal")),
        reader.GetDecimal(reader.GetOrdinal("VatAmount")), reader.GetDecimal(reader.GetOrdinal("Total")),
        reader.GetString(reader.GetOrdinal("Currency")), reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("PaymentDate")),
        reader.GetString(reader.GetOrdinal("PaymentMethod")), NullableString(reader, "PaymentReference"),
        NullableDate(reader, "BookingDate"), reader.GetString(reader.GetOrdinal("EmailStatus")),
        reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CreatedAt")));

    private static void Add(SqlCommand command, string name, SqlDbType type, object value, int? size = null)
    {
        var parameter = command.Parameters.Add(name, type);
        if (size is not null) parameter.Size = size.Value;
        parameter.Value = value;
    }

    private static void AddNullable(SqlCommand command, string name, string? value, int size) =>
        Add(command, name, SqlDbType.NVarChar, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim(), size);

    private static void AddMoney(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 2;
        parameter.Value = value;
    }

    private static string? NullableString(SqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));

    private static DateTimeOffset? NullableDate(SqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(name));
}
