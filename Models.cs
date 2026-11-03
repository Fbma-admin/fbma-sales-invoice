using System.ComponentModel.DataAnnotations;

namespace FBMA.SalesManagement;

public sealed record SaleRequest(
    [Required, StringLength(80)] string OrderId,
    [Required, StringLength(200)] string CustomerName,
    [Required, EmailAddress, StringLength(254)] string CustomerEmail,
    [StringLength(40)] string? CustomerPhone,
    [Required, StringLength(240)] string ItemDescription,
    [Range(1, 10000)] int Quantity,
    [Range(typeof(decimal), "0.01", "999999999")] decimal UnitPrice,
    DateTimeOffset PaymentDate,
    [Required, StringLength(40)] string PaymentMethod,
    [StringLength(120)] string? PaymentReference,
    DateTimeOffset? BookingDate);

public sealed record CostRequest(
    [Required, StringLength(200)] string Description,
    [Required, StringLength(100)] string Category,
    [Range(typeof(decimal), "0.01", "999999999")] decimal Amount,
    DateOnly CostDate,
    [StringLength(500)] string? Notes);

public sealed record Sale(
    long Id,
    string InvoiceNumber,
    string OrderId,
    string CustomerName,
    string CustomerEmail,
    string? CustomerPhone,
    string ItemDescription,
    int Quantity,
    decimal UnitPrice,
    decimal Subtotal,
    decimal VatAmount,
    decimal Total,
    string Currency,
    DateTimeOffset PaymentDate,
    string PaymentMethod,
    string? PaymentReference,
    DateTimeOffset? BookingDate,
    string EmailStatus,
    DateTimeOffset CreatedAt);

public sealed record Cost(
    long Id,
    string Description,
    string Category,
    decimal Amount,
    DateOnly CostDate,
    string? Notes,
    DateTimeOffset CreatedAt);

public sealed class OrganizationOptions
{
    public string Name { get; set; } = "Fatima Bint Mubarak Ladies Sports Academy";
    public string LogoPath { get; set; } = "";
    public string Currency { get; set; } = "AED";
    [Range(typeof(decimal), "0", "100")]
    public decimal VatPercent { get; set; } = 5m;
    public string[] InternalEmails { get; set; } = [];
}

public sealed class SecurityOptions
{
    [Required, MinLength(32)]
    public string ApiKey { get; set; } = "";
    [Required]
    public string AdminUsername { get; set; } = "admin";
    [Required, MinLength(12)]
    public string AdminPassword { get; set; } = "";
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
}
