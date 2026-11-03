using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace FBMA.SalesManagement;

public sealed class ReceiptPdfService(IOptions<OrganizationOptions> organizationOptions)
{
    private readonly OrganizationOptions _organization = organizationOptions.Value;

    public byte[] Create(Sale sale)
    {
        var content = new StringBuilder();
        content.AppendLine("1 g 0 0 595 842 re f 0 g");
        void Text(double x, double y, double size, string value, bool bold = false)
        {
            content.AppendLine($"BT /{(bold ? "F2" : "F1")} {size.ToString("0.##", CultureInfo.InvariantCulture)} Tf {x.ToString("0.##", CultureInfo.InvariantCulture)} {y.ToString("0.##", CultureInfo.InvariantCulture)} Td ({Escape(value)}) Tj ET");
        }
        void Line(double x1, double y1, double x2, double y2)
        {
            content.AppendLine($"{x1.ToString(CultureInfo.InvariantCulture)} {y1.ToString(CultureInfo.InvariantCulture)} m {x2.ToString(CultureInfo.InvariantCulture)} {y2.ToString(CultureInfo.InvariantCulture)} l S");
        }

        var logo = LoadLogo();
        if (logo is not null)
            content.AppendLine("q 250 0 0 116 48 710 cm /Im1 Do Q");
        else
            Text(48, 790, 16, _organization.Name, true);
        Text(48, 688, 13, "INVOICE", true);
        Text(390, 790, 10, $"Invoice No: {sale.InvoiceNumber}", true);
        Text(390, 774, 9, $"Order ID: {sale.OrderId}");
        Text(390, 758, 9, $"Date: {sale.PaymentDate.LocalDateTime:dd MMM yyyy, h:mm tt}");
        Line(48, 674, 547, 674);

        Text(48, 650, 10, "CUSTOMER", true);
        Text(48, 632, 10, sale.CustomerName);
        Text(48, 616, 9, sale.CustomerEmail);
        if (!string.IsNullOrWhiteSpace(sale.CustomerPhone)) Text(48, 600, 9, sale.CustomerPhone!);
        Text(330, 650, 10, "PAYMENT", true);
        Text(330, 632, 9, "Status: Paid");
        Text(330, 616, 9, $"Method: {sale.PaymentMethod}");
        if (!string.IsNullOrWhiteSpace(sale.PaymentReference)) Text(330, 600, 9, $"Reference: {sale.PaymentReference}");

        content.AppendLine("0.96 g 48 534 499 36 re f 0 g");
        Text(56, 548, 9, "Description", true);
        Text(350, 548, 9, "Qty", true);
        Text(400, 548, 9, "Unit price", true);
        Text(490, 548, 9, "Amount", true);
        Text(56, 512, 9, Limit(sale.ItemDescription, 54));
        Text(355, 512, 9, sale.Quantity.ToString(CultureInfo.InvariantCulture));
        Text(400, 512, 9, Money(sale.UnitPrice));
        Text(490, 512, 9, Money(sale.Subtotal));
        Line(48, 492, 547, 492);

        Text(390, 464, 9, "Item total");
        Text(490, 464, 9, Money(sale.Subtotal));
        Text(390, 442, 9, $"VAT ({_organization.VatPercent:0.##}%)");
        Text(490, 442, 9, Money(sale.VatAmount));
        Line(390, 428, 547, 428);
        Text(390, 406, 11, "Total", true);
        Text(480, 406, 11, $"{Money(sale.Total)} {sale.Currency}", true);

        if (sale.BookingDate is not null)
            Text(48, 452, 9, $"Booking date: {sale.BookingDate.Value.LocalDateTime:dd MMM yyyy, h:mm tt}");
        Text(48, 342, 10, "Thanks and come again!", true);
        Text(48, 322, 8, "This invoice was generated electronically.");

        return BuildPdf([content.ToString()], logo);
    }

    public byte[] CreateSalesReport(IReadOnlyList<Sale> sales, DateOnly? from = null, DateOnly? to = null)
    {
        const int rowsPerPage = 28;
        var pages = new List<string>();
        var pageCount = Math.Max(1, (int)Math.Ceiling(sales.Count / (double)rowsPerPage));

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var content = new StringBuilder();
            content.AppendLine("1 g 0 0 595 842 re f 0 g");
            void Text(double x, double y, double size, string value, bool bold = false) =>
                content.AppendLine($"BT /{(bold ? "F2" : "F1")} {size.ToString("0.##", CultureInfo.InvariantCulture)} Tf {x.ToString("0.##", CultureInfo.InvariantCulture)} {y.ToString("0.##", CultureInfo.InvariantCulture)} Td ({Escape(value)}) Tj ET");
            void Line(double x1, double y1, double x2, double y2) =>
                content.AppendLine($"{x1.ToString(CultureInfo.InvariantCulture)} {y1.ToString(CultureInfo.InvariantCulture)} m {x2.ToString(CultureInfo.InvariantCulture)} {y2.ToString(CultureInfo.InvariantCulture)} l S");

            Text(42, 798, 16, _organization.Name, true);
            Text(42, 776, 12, "SALES REPORT", true);
            Text(430, 798, 8, $"Generated: {DateTime.Now:dd MMM yyyy}");
            Text(480, 776, 8, $"Page {pageIndex + 1} of {pageCount}");
            var range = from is null && to is null
                ? "Period: All time"
                : $"Period: {from?.ToString("dd MMM yyyy") ?? "Beginning"} to {to?.ToString("dd MMM yyyy") ?? "Today"}";
            Text(42, 758, 8, range);
            Line(42, 748, 553, 748);
            Text(42, 730, 8, "Invoice", true);
            Text(92, 730, 8, "Order", true);
            Text(188, 730, 8, "Customer", true);
            Text(340, 730, 8, "Paid", true);
            Text(415, 730, 8, "Total", true);
            Text(482, 730, 8, "Email", true);
            Line(42, 721, 553, 721);

            var pageSales = sales.Skip(pageIndex * rowsPerPage).Take(rowsPerPage).ToArray();
            var y = 702d;
            foreach (var sale in pageSales)
            {
                Text(42, y, 7.5, Limit(sale.InvoiceNumber, 8));
                Text(92, y, 7.5, Limit(sale.OrderId, 17));
                Text(188, y, 7.5, Limit(sale.CustomerName, 27));
                Text(340, y, 7.5, sale.PaymentDate.LocalDateTime.ToString("dd MMM yyyy"));
                Text(415, y, 7.5, $"{Money(sale.Total)} {sale.Currency}");
                Text(482, y, 7.5, Limit(sale.EmailStatus, 12));
                y -= 22;
                Line(42, y + 8, 553, y + 8);
            }

            if (sales.Count == 0) Text(42, 692, 9, "No sales recorded.");
            if (pageIndex == pageCount - 1)
            {
                var total = sales.Sum(sale => sale.Total);
                Text(390, 70, 10, "Total collected", true);
                Text(480, 70, 10, $"{Money(total)} {_organization.Currency}", true);
            }
            Text(42, 42, 7, "Confidential - FBMA internal use only");
            pages.Add(content.ToString());
        }

        return BuildPdf(pages, null);
    }

    private byte[]? LoadLogo()
    {
        if (string.IsNullOrWhiteSpace(_organization.LogoPath)) return null;
        var path = Path.IsPathRooted(_organization.LogoPath)
            ? _organization.LogoPath
            : Path.Combine(AppContext.BaseDirectory, _organization.LogoPath);
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), _organization.LogoPath);
        }
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static byte[] BuildPdf(IReadOnlyList<string> pageContents, byte[]? logo)
    {
        static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);
        var pageCount = pageContents.Count;
        var firstPageObject = 3;
        var firstContentObject = firstPageObject + pageCount;
        var regularFontObject = firstContentObject + pageCount;
        var boldFontObject = regularFontObject + 1;
        var imageObjectNumber = boldFontObject + 1;
        var kids = string.Join(" ", Enumerable.Range(firstPageObject, pageCount).Select(number => $"{number} 0 R"));
        var objects = new List<byte[]>
        {
            Bytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Bytes($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>")
        };
        var xObject = logo is null ? "" : $" /XObject << /Im1 {imageObjectNumber} 0 R >>";
        for (var index = 0; index < pageCount; index++)
            objects.Add(Bytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 {regularFontObject} 0 R /F2 {boldFontObject} 0 R >>{xObject} >> /Contents {firstContentObject + index} 0 R >>"));
        foreach (var pageContent in pageContents)
            objects.Add(Bytes($"<< /Length {Encoding.ASCII.GetByteCount(pageContent)} >>\nstream\n{pageContent}endstream"));
        objects.Add(Bytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        objects.Add(Bytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>"));
        if (logo is not null)
        {
            using var imageObject = new MemoryStream();
            imageObject.Write(Bytes($"<< /Type /XObject /Subtype /Image /Width 1215 /Height 566 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {logo.Length} >>\nstream\n"));
            imageObject.Write(logo);
            imageObject.Write(Bytes("\nendstream"));
            objects.Add(imageObject.ToArray());
        }

        using var stream = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            Write($"{i + 1} 0 obj\n");
            stream.Write(objects[i]);
            Write("\nendobj\n");
        }
        var xref = stream.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return stream.ToArray();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", " ").Replace("\n", " ");
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..(length - 3)] + "...";
    private static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
}
