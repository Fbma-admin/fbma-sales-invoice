using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace FBMA.SalesManagement;

public sealed class ReceiptEmailService(
    IOptions<SmtpOptions> smtpOptions,
    IOptions<OrganizationOptions> organizationOptions,
    ILogger<ReceiptEmailService> logger)
{
    private readonly SmtpOptions _smtp = smtpOptions.Value;
    private readonly OrganizationOptions _organization = organizationOptions.Value;

    public async Task<bool> SendAsync(Sale sale, byte[] pdf)
    {
        if (string.IsNullOrWhiteSpace(_smtp.Host) || string.IsNullOrWhiteSpace(_smtp.SenderEmail))
        {
            logger.LogWarning("Receipt {InvoiceNumber} was saved but email is not configured.", sale.InvoiceNumber);
            return false;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_smtp.SenderEmail),
                Subject = $"Invoice {sale.InvoiceNumber} - {_organization.Name}",
                Body = $"Hello {WebUtility.HtmlEncode(sale.CustomerName)},<br><br>Your payment was successful. Your invoice is attached.<br><br>Order ID: <strong>{WebUtility.HtmlEncode(sale.OrderId)}</strong><br>Total paid: <strong>{sale.Total:N2} {WebUtility.HtmlEncode(sale.Currency)}</strong><br><br>Thank you.",
                IsBodyHtml = true
            };
            message.To.Add(sale.CustomerEmail);
            foreach (var internalEmail in _organization.InternalEmails
                         .Where(address => !string.IsNullOrWhiteSpace(address))
                         .Select(address => address.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(internalEmail, sale.CustomerEmail, StringComparison.OrdinalIgnoreCase))
                    message.CC.Add(internalEmail);
            }

            using var attachmentStream = new MemoryStream(pdf);
            message.Attachments.Add(new Attachment(attachmentStream, $"invoice-{sale.InvoiceNumber}.pdf", "application/pdf"));
            using var client = new SmtpClient(_smtp.Host, _smtp.Port)
            {
                EnableSsl = _smtp.EnableSsl,
                Credentials = string.IsNullOrWhiteSpace(_smtp.Username)
                    ? CredentialCache.DefaultNetworkCredentials
                    : new NetworkCredential(_smtp.Username, _smtp.Password)
            };
            await client.SendMailAsync(message);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not email receipt {InvoiceNumber}.", sale.InvoiceNumber);
            return false;
        }
    }
}
