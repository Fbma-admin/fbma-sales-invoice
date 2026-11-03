using System.Net;
using System.Globalization;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using FBMA.SalesManagement;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<OrganizationOptions>()
    .Bind(builder.Configuration.GetSection("Organization"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
var securityOptions = builder.Services.AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection("Security"));
if (builder.Environment.IsProduction())
    securityOptions.ValidateDataAnnotations().ValidateOnStart();
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddSingleton<SalesDatabase>();
builder.Services.AddSingleton<ReceiptPdfService>();
builder.Services.AddSingleton<ReceiptEmailService>();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = builder.Environment.IsProduction()
            ? "__Host-FBMA.Sales.Auth"
            : "FBMA.Sales.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.LoginPath = "/login";
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options => options.AddPolicy("login", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        })));

var app = builder.Build();
await app.Services.GetRequiredService<SalesDatabase>().InitializeAsync();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'";
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery, IOptions<OrganizationOptions> organization) =>
{
    if (context.User.Identity?.IsAuthenticated == true) return Results.Redirect("/");
    var token = antiforgery.GetAndStoreTokens(context).RequestToken ?? "";
    return Results.Content(LoginPage(organization.Value.Name, token), "text/html");
});

app.MapPost("/login", async (HttpContext context, IAntiforgery antiforgery,
    IOptions<SecurityOptions> security, IOptions<OrganizationOptions> organization) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest("The sign-in form expired. Reload the page and try again.");
    }

    var form = await context.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    if (!SecureEquals(username, security.Value.AdminUsername) ||
        !SecureEquals(password, security.Value.AdminPassword))
    {
        var token = antiforgery.GetAndStoreTokens(context).RequestToken ?? "";
        return Results.Content(LoginPage(organization.Value.Name, token, "Invalid username or password."),
            "text/html", statusCode: StatusCodes.Status401Unauthorized);
    }

    var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, security.Value.AdminUsername), new Claim(ClaimTypes.Role, "Administrator")],
        CookieAuthenticationDefaults.AuthenticationScheme));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
        new AuthenticationProperties { IsPersistent = false });
    return Results.Redirect("/");
}).RequireRateLimiting("login");

app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).RequireAuthorization();

app.MapGet("/health", async (SalesDatabase database, CancellationToken cancellationToken) =>
    await database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "healthy", database = "connected" })
        : Results.Json(new { status = "unhealthy", database = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapPost("/api/sales", async (HttpContext context, SaleRequest request, SalesDatabase database,
    ReceiptPdfService pdfService, ReceiptEmailService emailService, IOptions<SecurityOptions> security,
    IOptions<OrganizationOptions> organization) =>
{
    if (!SecureEquals(context.Request.Headers["X-API-Key"], security.Value.ApiKey)) return Results.Unauthorized();
    var validation = Validate(request);
    if (validation is not null) return validation;
    var (sale, created) = await database.CreateSaleAsync(request, organization.Value.Currency, organization.Value.VatPercent);
    if (created || !string.Equals(sale.EmailStatus, "Sent", StringComparison.OrdinalIgnoreCase))
    {
        var emailed = await emailService.SendAsync(sale, pdfService.Create(sale));
        await database.SetEmailStatusAsync(sale.Id, emailed ? "Sent" : "Pending");
        sale = sale with { EmailStatus = emailed ? "Sent" : "Pending" };
    }
    return Results.Ok(new { created, sale.Id, sale.InvoiceNumber, sale.OrderId, sale.Total, sale.Currency, sale.EmailStatus });
});

app.MapGet("/api/sales/{id:long}/receipt", async (HttpContext context, long id, SalesDatabase database,
    ReceiptPdfService pdfService, IOptions<SecurityOptions> security) =>
{
    if (!IsAuthorized(context, security.Value)) return Results.Unauthorized();
    var sale = await database.GetSaleAsync(id);
    return sale is null ? Results.NotFound() : Results.File(pdfService.Create(sale), "application/pdf", $"invoice-{sale.InvoiceNumber}.pdf");
});

app.MapGet("/sales/export.pdf", async (string? from, string? to, string? period,
    SalesDatabase database, ReceiptPdfService pdfService) =>
{
    var filter = ResolveDateFilter(from, to, period);
    var sales = await database.GetSalesAsync(1000, filter.From, filter.To);
    return Results.File(pdfService.CreateSalesReport(sales, filter.From, filter.To), "application/pdf",
        $"fbma-sales-{DateTime.UtcNow:yyyy-MM-dd}.pdf");
}).RequireAuthorization();

app.MapPost("/api/costs", async (HttpContext context, CostRequest request, SalesDatabase database,
    IOptions<SecurityOptions> security) =>
{
    if (!IsAuthorized(context, security.Value)) return Results.Unauthorized();
    var validation = Validate(request);
    if (validation is not null) return validation;
    return Results.Ok(await database.CreateCostAsync(request));
});

app.MapGet("/", async (HttpContext context, string? from, string? to, string? period,
    SalesDatabase database, IAntiforgery antiforgery,
    IOptions<OrganizationOptions> organization) =>
{
    context.Response.Headers.CacheControl = "no-store";

    var filter = ResolveDateFilter(from, to, period);
    var sales = await database.GetSalesAsync(1000, filter.From, filter.To);
    var costs = await database.GetCostsAsync(1000, filter.From, filter.To);
    var revenue = sales.Sum(s => s.Total);
    var expense = costs.Sum(c => c.Amount);
    var rows = string.Join("", sales.Select(s => $"<tr><td>{H(s.InvoiceNumber)}</td><td>{H(s.OrderId)}</td><td>{H(s.CustomerName)}</td><td>{H(s.ItemDescription)}</td><td>{s.PaymentDate.LocalDateTime:dd MMM yyyy}</td><td class='money'>{s.Total:N2} {H(s.Currency)}</td><td><span class='status'>{H(s.EmailStatus)}</span></td><td><a href='/api/sales/{s.Id}/receipt'>PDF</a></td></tr>"));
    var costRows = string.Join("", costs.Select(c => $"<tr><td>{c.CostDate:dd MMM yyyy}</td><td>{H(c.Category)}</td><td>{H(c.Description)}</td><td class='money'>{c.Amount:N2}</td><td>{H(c.Notes)}</td></tr>"));
    var csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken ?? "";
    return Results.Content(Dashboard(organization.Value.Name, context.User.Identity?.Name ?? "Administrator",
        csrfToken, filter.From, filter.To, filter.Period, revenue, expense, sales.Count, rows, costRows), "text/html");
}).RequireAuthorization();

app.Run();

static bool IsAuthorized(HttpContext context, SecurityOptions security)
{
    if (context.User.Identity?.IsAuthenticated == true) return true;
    if (SecureEquals(context.Request.Headers["X-API-Key"], security.ApiKey)) return true;
    return false;
}

static bool SecureEquals(string? supplied, string expected)
{
    if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(expected)) return false;
    var left = Encoding.UTF8.GetBytes(supplied);
    var right = Encoding.UTF8.GetBytes(expected);
    return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}

static string H(string? value) => WebUtility.HtmlEncode(value ?? "");

static IResult? Validate<T>(T request)
{
    var results = new List<ValidationResult>();
    Validator.TryValidateObject(request!, new ValidationContext(request!), results, true);
    if (request is SaleRequest sale && sale.PaymentDate == default)
        results.Add(new ValidationResult("Payment date is required.", [nameof(SaleRequest.PaymentDate)]));
    if (request is CostRequest cost && cost.CostDate == default)
        results.Add(new ValidationResult("Cost date is required.", [nameof(CostRequest.CostDate)]));
    if (results.Count == 0) return null;
    var errors = results
        .SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty)
            .Select(member => (member, message: result.ErrorMessage ?? "Invalid value.")))
        .GroupBy(item => item.member)
        .ToDictionary(group => group.Key, group => group.Select(item => item.message).Distinct().ToArray());
    return Results.ValidationProblem(errors);
}

static (DateOnly? From, DateOnly? To, string Period) ResolveDateFilter(string? from, string? to, string? period)
{
    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Dubai").DateTime);
    if (string.Equals(period, "week", StringComparison.OrdinalIgnoreCase))
    {
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return (today.AddDays(-daysSinceMonday), today, "week");
    }
    if (string.Equals(period, "month", StringComparison.OrdinalIgnoreCase))
        return (new DateOnly(today.Year, today.Month, 1), today, "month");

    var hasFrom = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var parsedFrom);
    var hasTo = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var parsedTo);
    DateOnly? filterFrom = hasFrom ? parsedFrom : null;
    DateOnly? filterTo = hasTo ? parsedTo : null;
    if (filterFrom is not null && filterTo is not null && filterFrom > filterTo)
        (filterFrom, filterTo) = (filterTo, filterFrom);
    return (filterFrom, filterTo, filterFrom is not null || filterTo is not null ? "custom" : "all");
}

static string FilterQuery(DateOnly? from, DateOnly? to)
{
    var values = new List<string>();
    if (from is not null) values.Add($"from={from:yyyy-MM-dd}");
    if (to is not null) values.Add($"to={to:yyyy-MM-dd}");
    return values.Count == 0 ? "" : "?" + string.Join("&", values);
}

static string LoginPage(string organization, string csrfToken, string? error = null) => $$$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Sign in | {{{H(organization)}}}</title><style>
:root{--ink:#24191d;--muted:#796b70;--brand:#6c1830;--accent:#ef4b31;--line:#eadfe2;--bg:#f7f3f1}*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:linear-gradient(135deg,var(--brand),var(--accent));color:var(--ink);font:14px Arial,sans-serif}.login{width:min(430px,92%);background:#fff;border-radius:18px;padding:32px;box-shadow:0 24px 70px #35081666}.login h1{margin:0 0 8px;font-size:24px}.login p{color:var(--muted);margin:0 0 24px}.field{display:grid;gap:7px;margin:16px 0}.field label{font-weight:700}.field input{width:100%;padding:13px;border:1px solid var(--line);border-radius:9px;font:inherit}.field input:focus{outline:2px solid #ef4b3155;border-color:var(--accent)}button{width:100%;margin-top:10px;padding:13px;border:0;border-radius:9px;background:var(--brand);color:#fff;font-weight:700;cursor:pointer}.error{padding:11px;border-radius:8px;background:#fde8e8;color:#9b1c1c;margin-bottom:16px}
</style></head><body><main class="login"><h1>Sales Management</h1><p>{{{H(organization)}}}</p>{{{(error is null ? "" : $"<div class='error'>{H(error)}</div>")}}}<form method="post" action="/login"><input type="hidden" name="__RequestVerificationToken" value="{{{H(csrfToken)}}}"><div class="field"><label for="username">User ID</label><input id="username" name="username" autocomplete="username" required autofocus></div><div class="field"><label for="password">Password</label><input id="password" name="password" type="password" autocomplete="current-password" required></div><button type="submit">Sign in</button></form></main></body></html>
""";

static string Dashboard(string organization, string username, string csrfToken, DateOnly? from, DateOnly? to,
    string period, decimal revenue, decimal costs, int count, string sales, string costRows) => $$$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{{H(organization)}}} Sales Management</title><style>
:root{--ink:#24191d;--muted:#796b70;--brand:#6c1830;--accent:#ef4b31;--line:#eadfe2;--bg:#f7f3f1}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px Arial,sans-serif}.top{padding:24px 5%;color:white;background:linear-gradient(120deg,var(--brand),var(--accent));display:flex;justify-content:space-between;align-items:center;gap:20px}.top h1{margin:0 0 5px;font-size:28px}.account{display:flex;align-items:center;gap:12px}.account button,.button{border:1px solid #ffffff99;border-radius:8px;padding:9px 13px;background:transparent;color:inherit;font:inherit;font-weight:700;cursor:pointer;text-decoration:none}.wrap{width:min(1180px,92%);margin:24px auto}.filters{display:flex;flex-wrap:wrap;align-items:end;gap:12px;margin-bottom:18px;padding:16px;background:#fff;border:1px solid var(--line);border-radius:14px}.filters label{display:grid;gap:6px;color:var(--muted);font-size:12px;font-weight:700}.filters input{padding:9px;border:1px solid var(--line);border-radius:8px;font:inherit}.filters .button{background:#fff;color:var(--brand);border:1px solid var(--brand)}.filters .apply,.filters .active{background:var(--brand);color:#fff}.cards{display:grid;grid-template-columns:repeat(3,1fr);gap:16px}.card,.panel{background:white;border:1px solid var(--line);border-radius:14px;padding:20px}.label{color:var(--muted);text-transform:uppercase;font-size:11px;letter-spacing:1px}.value{font-size:28px;font-weight:700;margin-top:8px}.panel{margin-top:18px;overflow:auto}.panel-head{display:flex;justify-content:space-between;align-items:center;gap:12px;margin-bottom:16px}.panel h2{margin:0}.panel .button{background:var(--brand);color:white;border:0}table{width:100%;border-collapse:collapse;white-space:nowrap}th,td{text-align:left;padding:12px;border-bottom:1px solid var(--line)}th{color:var(--muted);font-size:11px;text-transform:uppercase}.money{font-weight:700}.status{padding:5px 9px;border-radius:20px;background:#e7f6ed;color:#17683b}a{color:var(--brand);font-weight:700}@media(max-width:720px){.cards{grid-template-columns:1fr}.top{align-items:flex-start;flex-direction:column}}
</style></head><body><header class="top"><div><h1>{{{H(organization)}}}</h1><div>Cloud sales and invoice management</div></div><div class="account"><span>{{{H(username)}}}</span><form method="post" action="/logout"><input type="hidden" name="__RequestVerificationToken" value="{{{H(csrfToken)}}}"><button type="submit">Sign out</button></form></div></header><main class="wrap">
<form class="filters" method="get" action="/"><label>From<input type="date" name="from" value="{{{from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""}}}"></label><label>To<input type="date" name="to" value="{{{to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""}}}"></label><button class="button apply" type="submit">Apply dates</button><a class="button {{{(period == "week" ? "active" : "")}}}" href="/?period=week">This week</a><a class="button {{{(period == "month" ? "active" : "")}}}" href="/?period=month">This month</a><a class="button {{{(period == "all" ? "active" : "")}}}" href="/">All time</a></form>
<section class="cards"><div class="card"><div class="label">Total collected</div><div class="value">{{{revenue:N2}}} AED</div></div><div class="card"><div class="label">Recorded costs</div><div class="value">{{{costs:N2}}} AED</div></div><div class="card"><div class="label">Net</div><div class="value">{{{revenue - costs:N2}}} AED</div><div>{{{count}}} sales</div></div></section>
<section class="panel"><div class="panel-head"><h2>Sales</h2><a class="button" href="/sales/export.pdf{{{FilterQuery(from, to)}}}">Export filtered PDF</a></div><table><thead><tr><th>Invoice</th><th>Order ID</th><th>Customer</th><th>Item</th><th>Paid</th><th>Total</th><th>Email</th><th>Invoice PDF</th></tr></thead><tbody>{{{sales}}}</tbody></table></section>
<section class="panel"><h2>Costs</h2><table><thead><tr><th>Date</th><th>Category</th><th>Description</th><th>Amount (AED)</th><th>Notes</th></tr></thead><tbody>{{{costRows}}}</tbody></table></section>
</main></body></html>
""";
