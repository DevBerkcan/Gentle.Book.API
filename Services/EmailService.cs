using System.Reflection;
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GentleBook.Api.Services;

public class EmailService
{
    private readonly EmailOptions _emailOptions;
    private readonly InvoiceEmailOptions _invoiceEmailOptions;
    private readonly GentleBookDbContext _context;
    private readonly ILogger<EmailService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;

    public string FrontendUrl => string.IsNullOrEmpty(_emailOptions.FrontendUrl)
        ? _emailOptions.BaseUrl
        : _emailOptions.FrontendUrl;

    // The real GentleBook mark (white variant, for dark email headers/footers), embedded as a
    // resource — not a loose file — so it can never be missed on deploy, then inlined as a
    // base64 data URI. This template already relies on CSS custom properties and gradients that
    // classic desktop Outlook can't render anyway, so a data URI (which that same client also
    // doesn't support) costs nothing extra there while working in every modern client (Gmail,
    // Apple Mail, Outlook.com, mobile) without depending on an externally hosted image staying up.
    private static string? _logoBase64;
    private static string LogoBase64
    {
        get
        {
            if (_logoBase64 != null) return _logoBase64;
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("GentleBook.Api.Assets.gentlebook-mark-white.png");
            using var ms = new MemoryStream();
            stream?.CopyTo(ms);
            return _logoBase64 = Convert.ToBase64String(ms.ToArray());
        }
    }

    public EmailService(
        IOptions<EmailOptions> emailOptions,
        IOptions<InvoiceEmailOptions> invoiceEmailOptions,
        GentleBookDbContext context,
        ILogger<EmailService> logger,
        IServiceScopeFactory scopeFactory,
        IConfiguration config)
    {
        _emailOptions = emailOptions.Value;
        _invoiceEmailOptions = invoiceEmailOptions.Value;
        _context = context;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _config = config;
    }

    public async Task SendBookingConfirmationAsync(Guid bookingId)
    {
        var booking = await _context.Bookings
            .IgnoreQueryFilters()
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Tenant).ThenInclude(t => t.Settings)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null)
        {
            throw new ArgumentException("Booking not found");
        }

        var tenantName1    = booking.Tenant?.Settings?.CompanyName ?? booking.Tenant?.Name ?? "Buchungssystem";
        var tenantLogo1    = GetAbsoluteLogoUrl(booking.Tenant?.Settings?.LogoUrl);
        var primaryColor1  = booking.Tenant?.Settings?.PrimaryColor ?? "#6355E4";
        var currency1      = booking.Tenant?.Settings?.DefaultCurrency ?? "EUR";

        var emailLog = new EmailLog
        {
            TenantId = booking.TenantId,
            BookingId = bookingId,
            EmailType = EmailType.Confirmation,
            RecipientEmail = booking.Customer.Email!,
            Subject = $"Ihre Buchungsbestätigung – {tenantName1}",
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(tenantName1, _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(booking.Customer.FullName, booking.Customer.Email!));
            message.Subject = emailLog.Subject;

            var builder = new BodyBuilder();
            var cancellationToken = GenerateCancellationToken(bookingId);
            var frontendBase = string.IsNullOrEmpty(_emailOptions.FrontendUrl) ? _emailOptions.BaseUrl : _emailOptions.FrontendUrl;
            var cancellationUrl = $"{frontendBase}/booking/cancel/{cancellationToken}";

            builder.HtmlBody = GetConfirmationEmailHtml(booking, cancellationUrl, tenantName1, tenantLogo1, currency1, primaryColor1);
            builder.TextBody = GetConfirmationEmailText(booking, cancellationUrl, tenantName1, currency1);

            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort,
                SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            emailLog.Status = EmailStatus.Sent;
            emailLog.SentAt = DateTime.UtcNow;
            booking.ConfirmationSentAt = DateTime.UtcNow;

            _logger.LogInformation("Confirmation email sent to {Email}", booking.Customer.Email);

            var (adminEmail1, adminName1) = await GetTenantAdminEmailAsync(booking.TenantId);
            await SendInternalNotificationAsync(
                $"Neue Buchung: {booking.Customer.FullName} – {booking.Service.Name} am {booking.BookingDate:dd.MM.yyyy}",
                GetInternalBookingNotificationHtml(booking, booking.Customer, booking.Service, tenantName1, tenantLogo1, primaryColor1),
                GetInternalBookingNotificationText(booking, booking.Customer, booking.Service),
                adminEmail1, adminName1
            );
        }
        catch (Exception ex)
        {
            emailLog.Status = EmailStatus.Failed;
            emailLog.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to send confirmation email to {Email}",
                booking.Customer.Email);
        }

        _context.EmailLogs.Add(emailLog);
        await _context.SaveChangesAsync();
    }

    public async Task SendConfirmationReceiptAsync(Booking booking, Customer customer, Service service)
    {
        var emailLog = new EmailLog
        {
            TenantId = booking.TenantId,
            BookingId = booking.Id,
            EmailType = EmailType.Confirmation,
            RecipientEmail = customer.Email!,
            Subject = $"Buchungsbestätigung: {service.Name} am {booking.BookingDate:dd.MM.yyyy}",
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var message = new MimeMessage();
            var tenantSettings2  = await _context.TenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == booking.TenantId);
            var tenantName2      = tenantSettings2?.CompanyName ?? "Buchungssystem";
            var tenantLogo2      = GetAbsoluteLogoUrl(tenantSettings2?.LogoUrl);
            var primaryColor2    = tenantSettings2?.PrimaryColor ?? "#6355E4";
            var currency2        = tenantSettings2?.DefaultCurrency ?? "EUR";

            message.From.Add(new MailboxAddress(tenantName2, _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(customer.FullName, customer.Email!));
            message.Subject = emailLog.Subject;

            var builder = new BodyBuilder();
            var cancellationToken = GenerateCancellationToken(booking.Id);
            var frontendBase2 = string.IsNullOrEmpty(_emailOptions.FrontendUrl) ? _emailOptions.BaseUrl : _emailOptions.FrontendUrl;
            var cancellationUrl = $"{frontendBase2}/booking/cancel/{cancellationToken}";

            builder.HtmlBody = GetConfirmationReceiptHtml(booking, customer, service, cancellationUrl, tenantName2, tenantLogo2, currency2, primaryColor2);
            builder.TextBody = GetConfirmationReceiptText(booking, customer, service, cancellationUrl, tenantName2, currency2);

            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort,
                SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            emailLog.Status = EmailStatus.Sent;
            emailLog.SentAt = DateTime.UtcNow;

            _logger.LogInformation("Confirmation receipt sent to {Email}", customer.Email);

            var (adminEmail2, adminName2) = await GetTenantAdminEmailAsync(booking.TenantId);
            await SendInternalNotificationAsync(
                $"Buchung bestätigt: {customer.FullName} – {service.Name} am {booking.BookingDate:dd.MM.yyyy}",
                GetInternalBookingNotificationHtml(booking, customer, service, tenantName2, tenantLogo2, primaryColor2),
                GetInternalBookingNotificationText(booking, customer, service),
                adminEmail2, adminName2
            );
        }
        catch (Exception ex)
        {
            emailLog.Status = EmailStatus.Failed;
            emailLog.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to send confirmation receipt to {Email}",
                customer.Email);
        }

        _context.EmailLogs.Add(emailLog);
        await _context.SaveChangesAsync();
    }

    public async Task SendCancellationConfirmationAsync(Booking booking, Customer customer, Service service)
    {
        var emailLog = new EmailLog
        {
            TenantId = booking.TenantId,
            BookingId = booking.Id,
            EmailType = EmailType.Cancellation,
            RecipientEmail = customer.Email!,
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var message = new MimeMessage();

            var tenantSettings3  = await _context.TenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == booking.TenantId);
            var tenantName3      = tenantSettings3?.CompanyName ?? "Buchungssystem";
            var tenantLogo3      = GetAbsoluteLogoUrl(tenantSettings3?.LogoUrl);
            var primaryColor3    = tenantSettings3?.PrimaryColor ?? "#6355E4";

            emailLog.Subject = $"Ihre Stornierung – {tenantName3}";

            message.From.Add(new MailboxAddress(tenantName3, _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(customer.FullName, customer.Email!));
            message.Subject = emailLog.Subject;

            var builder = new BodyBuilder();

            builder.HtmlBody = GetCancellationEmailHtml(booking, customer, service, tenantName3, tenantLogo3, primaryColor3);
            builder.TextBody = GetCancellationEmailText(booking, customer, service, tenantName3);

            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort,
                SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            emailLog.Status = EmailStatus.Sent;
            emailLog.SentAt = DateTime.UtcNow;

            _logger.LogInformation("Cancellation confirmation sent to {Email}", customer.Email);

            var (adminEmail3, adminName3) = await GetTenantAdminEmailAsync(booking.TenantId);
            await SendInternalNotificationAsync(
                $"Stornierung: {customer.FullName} – {service.Name} am {booking.BookingDate:dd.MM.yyyy}",
                GetInternalCancellationNotificationHtml(booking, customer, service, tenantName3, tenantLogo3, primaryColor3),
                GetInternalCancellationNotificationText(booking, customer, service),
                adminEmail3, adminName3
            );
        }
        catch (Exception ex)
        {
            emailLog.Status = EmailStatus.Failed;
            emailLog.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to send cancellation confirmation to {Email}",
                customer.Email);
        }

        _context.EmailLogs.Add(emailLog);
        await _context.SaveChangesAsync();
    }

    public async Task SendBookingReminderAsync(Guid bookingId)
    {
        var booking = await _context.Bookings
            .IgnoreQueryFilters()
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Tenant).ThenInclude(t => t.Settings)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null || booking.Status != BookingStatus.Confirmed)
            return;

        var emailLog = new EmailLog
        {
            TenantId = booking.TenantId,
            BookingId = bookingId,
            EmailType = EmailType.Reminder,
            RecipientEmail = booking.Customer.Email!,
            Subject = $"Erinnerung: Termin am {booking.BookingDate:dd.MM.yyyy}",
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var message = new MimeMessage();

            var tenantName4   = booking.Tenant?.Settings?.CompanyName ?? booking.Tenant?.Name ?? "Buchungssystem";
            var tenantLogo4   = GetAbsoluteLogoUrl(booking.Tenant?.Settings?.LogoUrl);
            var primaryColor4 = booking.Tenant?.Settings?.PrimaryColor ?? "#6355E4";

            message.From.Add(new MailboxAddress(tenantName4, _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(booking.Customer.FullName, booking.Customer.Email!));
            message.Subject = emailLog.Subject;

            var builder = new BodyBuilder();
            var cancellationToken = GenerateCancellationToken(bookingId);
            var frontendBase3 = string.IsNullOrEmpty(_emailOptions.FrontendUrl) ? _emailOptions.BaseUrl : _emailOptions.FrontendUrl;
            var cancellationUrl = $"{frontendBase3}/booking/cancel/{cancellationToken}";

            builder.HtmlBody = GetReminderEmailHtml(booking, cancellationUrl, tenantName4, tenantLogo4, primaryColor4);
            builder.TextBody = GetReminderEmailText(booking, cancellationUrl, tenantName4);

            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort,
                SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            emailLog.Status = EmailStatus.Sent;
            emailLog.SentAt = DateTime.UtcNow;
            booking.ReminderSentAt = DateTime.UtcNow;

            _logger.LogInformation("Reminder email sent to {Email}", booking.Customer.Email);
        }
        catch (Exception ex)
        {
            emailLog.Status = EmailStatus.Failed;
            emailLog.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to send reminder email to {Email}",
                booking.Customer.Email);
        }

        _context.EmailLogs.Add(emailLog);
        await _context.SaveChangesAsync();
    }

    /// <summary>Same HMAC-signed token mechanism as the cancel-booking link, just a different action tag.</summary>
    public string GenerateReviewToken(Guid bookingId) => GenerateActionToken(bookingId, "review");

    /// <summary>Same HMAC-signed token mechanism as the cancel-booking link, just a different action tag.</summary>
    public string GenerateIntakeFormToken(Guid bookingId) => GenerateActionToken(bookingId, "intake");

    /// <summary>Sent by ReviewRequestService once a booking auto-completes (Agency only). Never re-sent for the same booking (Booking.ReviewRequestSentAt guards that at the caller).</summary>
    public async Task<bool> SendReviewRequestEmailAsync(Booking booking, Customer customer, Service service, string tenantName, string? tenantLogoUrl, string primaryColor)
    {
        if (string.IsNullOrWhiteSpace(customer.Email)) return false;

        var emailLog = new EmailLog
        {
            TenantId = booking.TenantId,
            BookingId = booking.Id,
            EmailType = EmailType.ReviewRequest,
            RecipientEmail = customer.Email,
            Subject = $"Wie war Ihr Termin bei {tenantName}?",
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            var reviewUrl = $"{FrontendUrl.TrimEnd('/')}/review/{GenerateReviewToken(booking.Id)}";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(tenantName, _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(customer.FullName, customer.Email));
            message.Subject = emailLog.Subject;

            var content = $@"
                <div class='greeting'>Hallo {customer.FirstName},</div>
                <p style='color: var(--text-secondary); margin-bottom: 24px;'>
                    wir hoffen, Ihr Termin „{service.Name}“ bei {tenantName} hat Ihnen gefallen. Wir würden uns sehr
                    über eine kurze Bewertung freuen — das dauert nur eine Minute.
                </p>
                <div class='cancel-section'>
                    <a href='{reviewUrl}' style='display: inline-block; background: linear-gradient(135deg, {primaryColor} 0%, {DarkenHex(primaryColor)} 100%); color: #ffffff; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px;'>
                        Jetzt bewerten
                    </a>
                </div>";

            var builder = new BodyBuilder
            {
                HtmlBody = GetBaseEmailTemplate("Wie war Ihr Termin?", content, tenantName, tenantLogoUrl, primaryColor),
                TextBody = $"Hallo {customer.FirstName},\n\nwir würden uns über eine kurze Bewertung Ihres Termins „{service.Name}“ bei {tenantName} freuen:\n{reviewUrl}",
            };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            emailLog.Status = EmailStatus.Sent;
            emailLog.SentAt = DateTime.UtcNow;
            _logger.LogInformation("Review request email sent to {Email}", customer.Email);
        }
        catch (Exception ex)
        {
            emailLog.Status = EmailStatus.Failed;
            emailLog.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to send review request email to {Email}", customer.Email);
            _context.EmailLogs.Add(emailLog);
            await _context.SaveChangesAsync();
            return false;
        }

        _context.EmailLogs.Add(emailLog);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>Daily/weekly team-report digest (Agency). Sent by AdminDigestService, one row per Agency tenant whose TenantSettings.DigestFrequency matches the run.</summary>
    public async Task<bool> SendAdminDigestEmailAsync(Guid tenantId, string recipientEmail, string tenantName, string? tenantLogoUrl, string primaryColor, string frequencyLabel, DashboardStatisticsDto stats)
    {
        if (string.IsNullOrWhiteSpace(recipientEmail)) return false;

        var emailLog = new EmailLog
        {
            TenantId = tenantId,
            EmailType = EmailType.AdminDigest,
            RecipientEmail = recipientEmail,
            Subject = $"Dein {frequencyLabel}-Report — {tenantName}",
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(tenantName, _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(tenantName, recipientEmail));
            message.Subject = emailLog.Subject;

            var revenueRows = string.Join("", stats.RevenueThisMonthByCurrency.Select(kv =>
                $"<tr><td style='padding: 6px 0; color: var(--text-secondary);'>Umsatz {kv.Key} (Monat)</td><td style='padding: 6px 0; text-align: right; font-weight: 600;'>{kv.Value:N2} {kv.Key}</td></tr>"));

            var topServiceRows = string.Join("", stats.PopularServices.Take(3).Select(s =>
                $"<tr><td style='padding: 6px 0; color: var(--text-secondary);'>{s.ServiceName}</td><td style='padding: 6px 0; text-align: right; font-weight: 600;'>{s.BookingCount} Buchungen</td></tr>"));

            var content = $@"
                <div class='greeting'>Hallo,</div>
                <p style='color: var(--text-secondary); margin-bottom: 24px;'>
                    hier ist dein {frequencyLabel}-Report für {tenantName}.
                </p>
                <table style='width: 100%; border-collapse: collapse; margin-bottom: 20px;'>
                    <tr><td style='padding: 6px 0; color: var(--text-secondary);'>Buchungen diesen Monat</td><td style='padding: 6px 0; text-align: right; font-weight: 600;'>{stats.TotalBookingsThisMonth}</td></tr>
                    <tr><td style='padding: 6px 0; color: var(--text-secondary);'>Neue Kund:innen diesen Monat</td><td style='padding: 6px 0; text-align: right; font-weight: 600;'>{stats.NewCustomersThisMonth}</td></tr>
                    {revenueRows}
                </table>
                {(topServiceRows.Length > 0 ? $@"
                <p style='color: var(--text-secondary); font-weight: 600; margin-bottom: 8px;'>Top-Services</p>
                <table style='width: 100%; border-collapse: collapse; margin-bottom: 20px;'>{topServiceRows}</table>" : "")}
                <div class='cancel-section'>
                    <a href='{FrontendUrl.TrimEnd('/')}/admin/dashboard' style='display: inline-block; background: linear-gradient(135deg, {primaryColor} 0%, {DarkenHex(primaryColor)} 100%); color: #ffffff; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px;'>
                        Zum Dashboard
                    </a>
                </div>";

            var builder = new BodyBuilder
            {
                HtmlBody = GetBaseEmailTemplate($"Dein {frequencyLabel}-Report", content, tenantName, tenantLogoUrl, primaryColor),
                TextBody = $"Dein {frequencyLabel}-Report für {tenantName}:\n\nBuchungen diesen Monat: {stats.TotalBookingsThisMonth}\nNeue Kund:innen diesen Monat: {stats.NewCustomersThisMonth}\n\nDashboard: {FrontendUrl.TrimEnd('/')}/admin/dashboard",
            };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            emailLog.Status = EmailStatus.Sent;
            emailLog.SentAt = DateTime.UtcNow;
            _logger.LogInformation("Admin digest email sent to {Email}", recipientEmail);
        }
        catch (Exception ex)
        {
            emailLog.Status = EmailStatus.Failed;
            emailLog.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to send admin digest email to {Email}", recipientEmail);
            _context.EmailLogs.Add(emailLog);
            await _context.SaveChangesAsync();
            return false;
        }

        _context.EmailLogs.Add(emailLog);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>Sent when a voucher/session-package is issued to a customer (Agency). Code + remaining balance, no payment link — the tenant collects payment itself.</summary>
    public async Task<bool> SendVoucherIssuedEmailAsync(Guid tenantId, string recipientEmail, string customerName, string code, VoucherType type, decimal? remainingAmount, int? remainingSessions, string tenantName, string? tenantLogoUrl, string primaryColor)
    {
        if (string.IsNullOrWhiteSpace(recipientEmail)) return false;

        var emailLog = new EmailLog
        {
            TenantId = tenantId,
            EmailType = EmailType.VoucherIssued,
            RecipientEmail = recipientEmail,
            Subject = $"Dein Gutschein bei {tenantName}",
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        var valueLabel = type == VoucherType.MonetaryValue
            ? $"{remainingAmount:N2} Guthaben"
            : $"{remainingSessions} Sitzung(en)";

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(tenantName, _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(customerName, recipientEmail));
            message.Subject = emailLog.Subject;

            var content = $@"
                <div class='greeting'>Hallo {customerName},</div>
                <p style='color: var(--text-secondary); margin-bottom: 24px;'>
                    du hast einen Gutschein bei {tenantName} erhalten.
                </p>
                <table style='width: 100%; border-collapse: collapse; margin-bottom: 20px;'>
                    <tr><td style='padding: 6px 0; color: var(--text-secondary);'>Code</td><td style='padding: 6px 0; text-align: right; font-weight: 700; letter-spacing: 1px;'>{code}</td></tr>
                    <tr><td style='padding: 6px 0; color: var(--text-secondary);'>Guthaben</td><td style='padding: 6px 0; text-align: right; font-weight: 600;'>{valueLabel}</td></tr>
                </table>
                <p style='color: var(--text-secondary); font-size: 14px;'>
                    Nenne diesen Code einfach bei deiner nächsten Buchung.
                </p>";

            var builder = new BodyBuilder
            {
                HtmlBody = GetBaseEmailTemplate("Dein Gutschein", content, tenantName, tenantLogoUrl, primaryColor),
                TextBody = $"Hallo {customerName},\n\ndu hast einen Gutschein bei {tenantName} erhalten.\n\nCode: {code}\nGuthaben: {valueLabel}\n\nNenne diesen Code einfach bei deiner nächsten Buchung.",
            };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            emailLog.Status = EmailStatus.Sent;
            emailLog.SentAt = DateTime.UtcNow;
            _logger.LogInformation("Voucher issued email sent to {Email}", recipientEmail);
        }
        catch (Exception ex)
        {
            emailLog.Status = EmailStatus.Failed;
            emailLog.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to send voucher issued email to {Email}", recipientEmail);
            _context.EmailLogs.Add(emailLog);
            await _context.SaveChangesAsync();
            return false;
        }

        _context.EmailLogs.Add(emailLog);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task SendWelcomeEmailAsync(Customer customer, Guid tenantId)
    {
        var tenantSettings = await _context.TenantSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);

        var tenantName    = tenantSettings?.CompanyName ?? "Buchungssystem";
        var tenantLogo    = GetAbsoluteLogoUrl(tenantSettings?.LogoUrl);
        var primaryColorW = tenantSettings?.PrimaryColor ?? "#6355E4";
        var frontendBase  = string.IsNullOrEmpty(_emailOptions.FrontendUrl) ? _emailOptions.BaseUrl : _emailOptions.FrontendUrl;
        var verifyUrl = $"{frontendBase}/verify-email?token={customer.EmailVerificationToken}";

        var subject = $"Willkommen bei {tenantName} – Bitte bestätigen Sie Ihre E-Mail";

        var emailLog = new EmailLog
        {
            TenantId = tenantId,
            EmailType = EmailType.Welcome,
            RecipientEmail = customer.Email!,
            Subject = subject,
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(tenantName, _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(customer.FullName, customer.Email!));
            message.Subject = subject;

            var content = $@"
                <div class='greeting'>Hallo {customer.FirstName},</div>
                <p style='color: var(--text-secondary); margin-bottom: 24px;'>
                    Sie wurden als Kunde bei <strong>{tenantName}</strong> registriert.
                    Bitte bestätigen Sie Ihre E-Mail-Adresse, damit wir Ihnen Terminbestätigungen und Erinnerungen zusenden dürfen.
                </p>
                <div class='cancel-section'>
                    <div class='cancel-title'>E-Mail-Adresse bestätigen</div>
                    <div class='cancel-text'>
                        Klicken Sie auf den Button, um Ihre E-Mail-Adresse zu bestätigen und Ihre Einwilligung zur Datenspeicherung zu erteilen.
                    </div>
                    <a href='{verifyUrl}' style='display: inline-block; background: linear-gradient(135deg, {primaryColorW} 0%, {DarkenHex(primaryColorW)} 100%); color: #ffffff; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; box-shadow: 0 4px 12px rgba(0,0,0,0.15);'>
                        E-Mail bestätigen
                    </a>
                    <p style='color: var(--text-secondary); font-size: 13px; margin-top: 16px;'>
                        Dieser Link ist 7 Tage gültig.
                    </p>
                </div>
                <div class='info-box' style='margin-top: 24px;'>
                    <h3>Datenschutzhinweis</h3>
                    <p style='font-size: 14px; margin-top: 8px;'>
                        Ihre Daten werden ausschließlich für die Terminverwaltung bei {tenantName} verwendet.
                        Wenn Sie keine Registrierung beantragt haben, können Sie diese E-Mail ignorieren – es entstehen keine Konsequenzen.
                    </p>
                </div>";

            var builder = new BodyBuilder
            {
                HtmlBody = GetBaseEmailTemplate(subject, content, tenantName, tenantLogo, primaryColorW),
                TextBody = $@"{tenantName.ToUpperInvariant()} – WILLKOMMEN

Hallo {customer.FirstName},

Sie wurden als Kunde bei {tenantName} registriert.
Bitte bestätigen Sie Ihre E-Mail-Adresse unter folgendem Link:

{verifyUrl}

Dieser Link ist 7 Tage gültig.

Wenn Sie keine Registrierung beantragt haben, können Sie diese E-Mail ignorieren.

---
Ihre Daten werden ausschließlich für die Terminverwaltung verwendet."
            };

            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            emailLog.Status = EmailStatus.Sent;
            emailLog.SentAt = DateTime.UtcNow;
            _logger.LogInformation("Welcome email sent to {Email}", customer.Email);
        }
        catch (Exception ex)
        {
            emailLog.Status = EmailStatus.Failed;
            emailLog.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to send welcome email to {Email}", customer.Email);
        }

        _context.EmailLogs.Add(emailLog);
        await _context.SaveChangesAsync();
    }

    /// <summary>Notifies a waitlist customer that a slot has opened up.</summary>
    public async Task<bool> SendWaitlistNotificationAsync(
        string toEmail, string toFirstName, string toLastName,
        string serviceName, DateOnly date, TimeOnly startTime, TimeOnly endTime,
        string tenantSlug, string tenantName, string? tenantLogoUrl,
        string primaryColor, string reservationToken,
        Guid serviceId, Guid? employeeId)
    {
        try
        {
            var frontendBase = string.IsNullOrEmpty(_emailOptions.FrontendUrl)
                ? _emailOptions.BaseUrl
                : _emailOptions.FrontendUrl;
            var bookingUrl = $"{frontendBase}/booking/{tenantSlug}/book" +
                $"?waitlistToken={Uri.EscapeDataString(reservationToken)}" +
                $"&serviceId={serviceId}&date={date:yyyy-MM-dd}" +
                $"&time={startTime:HH\\:mm}" +
                (employeeId.HasValue ? $"&employeeId={employeeId.Value}" : "");

            var content = $@"
                <div class='greeting'>Hallo {toFirstName},</div>
                <p style='color: var(--text-secondary); margin-bottom: 24px;'>
                    gute Neuigkeit! Ein Termin bei <strong>{tenantName}</strong> ist wieder frei geworden.
                </p>
                <div class='booking-card'>
                    <div class='booking-title'>Freier Termin</div>
                    <div class='detail-row'>
                        <span class='detail-label'>Service</span>
                        <span class='detail-value'>{serviceName}</span>
                    </div>
                    <div class='detail-row'>
                        <span class='detail-label'>Datum</span>
                        <span class='detail-value'>{date:dd.MM.yyyy}</span>
                    </div>
                    <div class='detail-row'>
                        <span class='detail-label'>Uhrzeit</span>
                        <span class='detail-value'>{startTime:HH\\:mm} – {endTime:HH\\:mm}</span>
                    </div>
                </div>
                <div class='cancel-section'>
                    <div class='cancel-title'>Jetzt buchen!</div>
                    <div class='cancel-text'>Dieser Termin ist ab Versand dieser E-Mail 15 Minuten exklusiv für Sie reserviert.</div>
                    <a href='{bookingUrl}' style='display: inline-block; background: linear-gradient(135deg, {primaryColor} 0%, {DarkenHex(primaryColor)} 100%); color: #ffffff; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; box-shadow: 0 4px 12px rgba(0,0,0,0.15);'>
                        Termin jetzt buchen
                    </a>
                </div>";

            var html = GetBaseEmailTemplate("Termin verfügbar!", content, tenantName, tenantLogoUrl, primaryColor);

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(tenantName, _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress($"{toFirstName} {toLastName}", toEmail));
            message.Subject = $"Termin verfügbar – {tenantName}";

            var builder = new BodyBuilder { HtmlBody = html };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, MailKit.Security.SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Waitlist notification sent to {Email}", toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send waitlist notification to {Email}", toEmail);
            return false;
        }
    }

    /// <summary>Emails a generated subscription invoice PDF to the tenant's billing contact.</summary>
    public async Task<bool> SendSubscriptionInvoiceAsync(Invoice invoice, TenantSettings settings)
    {
        var toEmail = invoice.RecipientEmail ?? settings.Email;
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogWarning("No recipient email for invoice {InvoiceNumber}, cannot send.", invoice.InvoiceNumber);
            return false;
        }

        try
        {
            var subscriptionUrl = $"{FrontendUrl.TrimEnd('/')}/admin/subscription";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(
                string.IsNullOrEmpty(_invoiceEmailOptions.SenderName) ? "GentleBook" : _invoiceEmailOptions.SenderName,
                string.IsNullOrEmpty(_invoiceEmailOptions.SenderEmail) ? _emailOptions.SenderEmail : _invoiceEmailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(invoice.RecipientName, toEmail));
            message.Subject = $"Deine GentleBook-Rechnung {invoice.InvoiceNumber}";

            var content = $@"
                <div class='greeting'>Hallo,</div>
                <p style='color: var(--text-secondary); margin-bottom: 24px;'>
                    anbei deine Rechnung für den GentleBook {invoice.PlanName}-Plan, Zeitraum {invoice.PeriodStart:dd.MM.yyyy} – {invoice.PeriodEnd:dd.MM.yyyy}.
                </p>
                <div class='booking-card'>
                    <div class='detail-row'><span class='detail-label'>Rechnungsnr.</span><span class='detail-value'>{invoice.InvoiceNumber}</span></div>
                    <div class='detail-row'><span class='detail-label'>Betrag</span><span class='detail-value'>{invoice.Amount:0.00} {invoice.Currency}</span></div>
                    <div class='detail-row'><span class='detail-label'>Bezahlt am</span><span class='detail-value'>{invoice.IssueDate:dd.MM.yyyy}</span></div>
                </div>
                <p style='color: var(--text-secondary); font-size: 13px; margin-top: 16px;'>Die Rechnung findest du im Anhang dieser E-Mail als PDF.</p>
                <div class='cancel-section'>
                    <a href='{subscriptionUrl}' style='display: inline-block; background: linear-gradient(135deg, #6355E4 0%, {DarkenHex("#6355E4")} 100%); color: #ffffff; text-decoration: none; padding: 12px 28px; border-radius: 40px; font-weight: 600; font-size: 14px;'>
                        Zu deinem Abonnement →
                    </a>
                </div>";

            var builder = new BodyBuilder
            {
                HtmlBody = GetBaseEmailTemplate($"Rechnung {invoice.InvoiceNumber}", content, "GentleBook", null, "#6355E4"),
                TextBody = $@"GENTLEBOOK RECHNUNG

Rechnungsnr.: {invoice.InvoiceNumber}
Betrag: {invoice.Amount:0.00} {invoice.Currency}
Zeitraum: {invoice.PeriodStart:dd.MM.yyyy} – {invoice.PeriodEnd:dd.MM.yyyy}

Die Rechnung ist als PDF angehängt.

Dein Abonnement verwalten: {subscriptionUrl}"
            };
            builder.Attachments.Add($"Rechnung-{invoice.InvoiceNumber}.pdf", invoice.PdfContent, ContentType.Parse("application/pdf"));

            message.Body = builder.ToMessageBody();

            var smtpServer = string.IsNullOrEmpty(_invoiceEmailOptions.SmtpServer) ? _emailOptions.SmtpServer : _invoiceEmailOptions.SmtpServer;
            var smtpPort = _invoiceEmailOptions.SmtpPort != 0 ? _invoiceEmailOptions.SmtpPort : _emailOptions.SmtpPort;
            var smtpUser = string.IsNullOrEmpty(_invoiceEmailOptions.SmtpUsername) ? _emailOptions.SmtpUsername : _invoiceEmailOptions.SmtpUsername;
            var smtpPass = string.IsNullOrEmpty(_invoiceEmailOptions.SmtpPassword) ? _emailOptions.SmtpPassword : _invoiceEmailOptions.SmtpPassword;

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(smtpServer, smtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(smtpUser, smtpPass);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Invoice {InvoiceNumber} emailed to {Email}", invoice.InvoiceNumber, toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to email invoice {InvoiceNumber}", invoice.InvoiceNumber);
            return false;
        }
    }

    #region Internal Notifications

    private async Task<(string email, string name)> GetTenantAdminEmailAsync(Guid tenantId)
    {
        // 1. Try active TenantAdmin PlatformUser
        var adminUser = await _context.PlatformUsers
            .Where(u => u.TenantId == tenantId && u.Role == PlatformRole.TenantAdmin && u.IsActive)
            .Select(u => new { u.Email, u.FirstName, u.LastName })
            .FirstOrDefaultAsync();

        if (adminUser != null)
            return (adminUser.Email, $"{adminUser.FirstName} {adminUser.LastName}".Trim());

        // 2. Fallback: TenantSettings contact email
        var settings = await _context.TenantSettings
            .Where(s => s.TenantId == tenantId)
            .Select(s => new { s.Email, s.CompanyName })
            .FirstOrDefaultAsync();

        if (!string.IsNullOrEmpty(settings?.Email))
            return (settings.Email, settings.CompanyName ?? "Admin");

        // 3. Last resort: platform sender
        return (_emailOptions.SenderEmail, "GentleBook");
    }

    private async Task SendInternalNotificationAsync(string subject, string htmlBody, string textBody,
        string toEmail, string toName)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook Buchungssystem", _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(toName, toEmail));
            message.Subject = $"[Buchungssystem] {subject}";

            var builder = new BodyBuilder
            {
                HtmlBody = htmlBody,
                TextBody = textBody
            };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Internal notification sent: {Subject}", subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send internal notification: {Subject}", subject);
        }
    }

    private string GetInternalBookingNotificationHtml(Booking booking, Customer customer, Service service, string tenantName = "GentleBook", string? tenantLogoUrl = null, string primaryColor = "#6355E4")
    {
        return $@"<!DOCTYPE html>
<html lang='de'>
<head>
    <meta charset='UTF-8'>
    <title>Neue Buchung</title>
</head>
<body style='font-family: 'Plus Jakarta Sans', Arial, sans-serif; background-color: #f5f5f5; padding: 20px; margin: 0;'>
    <div style='max-width: 600px; margin: 0 auto; background-color: #ffffff; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 12px rgba(0,0,0,0.1);'>
        <div style='background: linear-gradient(135deg, #14162B 0%, #201F47 50%, #14162B 100%); padding: 30px; text-align: center; border-bottom: 3px solid {primaryColor};'>
            {(tenantLogoUrl != null ? $"<img src='{tenantLogoUrl}' alt='{tenantName}' style='max-height:50px;max-width:160px;object-fit:contain;margin-bottom:10px;'/><br/>" : $"<p style='color: {primaryColor}; font-size: 32px; margin: 0 0 10px 0;'>✧</p>")}
            <h1 style='color: #ffffff; font-size: 20px; margin: 0 0 6px 0;'>Neue Buchung eingegangen</h1>
            <p style='color: {primaryColor}; font-size: 13px; margin: 0; letter-spacing: 1px; text-transform: uppercase;'>{tenantName}</p>
        </div>
        <div style='padding: 30px;'>
            <table style='width: 100%; border-collapse: collapse;'>
                <tr style='background-color: #f8f9fa;'>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; width: 140px; border: 1px solid #e2e8f0; font-size: 14px;'>Kunde</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px; font-weight: 600;'>{customer.FullName}</td>
                </tr>
                <tr>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>E-Mail</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px;'>{customer.Email ?? "–"}</td>
                </tr>
                <tr style='background-color: #f8f9fa;'>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Telefon</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px;'>{customer.Phone ?? "–"}</td>
                </tr>
                <tr>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Service</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px; font-weight: 600;'>{service.Name}</td>
                </tr>
                <tr style='background-color: #f8f9fa;'>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Datum</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px; font-weight: 600;'>{booking.BookingDate:dd.MM.yyyy}</td>
                </tr>
                <tr>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Uhrzeit</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px; font-weight: 600;'>{booking.StartTime:HH:mm} – {booking.EndTime:HH:mm} Uhr</td>
                </tr>
                <tr style='background-color: #f8f9fa;'>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Dauer</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px;'>{service.DurationMinutes} Minuten</td>
                </tr>
                <tr>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Preis</td>
                    <td style='padding: 12px 15px; color: {primaryColor}; border: 1px solid #e2e8f0; font-size: 16px; font-weight: 700;'>{service.Price:0.00} CHF</td>
                </tr>
                <tr style='background-color: #f8f9fa;'>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Status</td>
                    <td style='padding: 12px 15px; border: 1px solid #e2e8f0;'><span style='background-color: #d4edda; color: #155724; padding: 4px 12px; border-radius: 20px; font-size: 13px; font-weight: 600;'>Bestätigt</span></td>
                </tr>
                {(!string.IsNullOrEmpty(booking.CustomerNotes) ? $@"
                <tr>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Notizen</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px;'>{booking.CustomerNotes}</td>
                </tr>" : "")}
            </table>
            <p style='color: #64748b; font-size: 12px; margin-top: 20px; text-align: center;'>
                Diese Nachricht wurde automatisch vom GentleBook Buchungssystem generiert.
            </p>
        </div>
        <div style='background: linear-gradient(135deg, #14162B 0%, #201F47 100%); padding: 20px; text-align: center; border-top: 3px solid {primaryColor};'>
            <p style='color: {primaryColor}; font-size: 18px; margin: 0 0 8px 0;'>✧</p>
            <p style='color: #ffffff; font-size: 13px; font-weight: 700; margin: 0 0 4px 0;'>{tenantName}</p>
            <p style='color: #a3a3a3; font-size: 12px; margin: 0;'>GentleBook – Online Buchungssystem</p>
        </div>
    </div>
</body>
</html>";
    }

    private string GetInternalBookingNotificationText(Booking booking, Customer customer, Service service)
    {
        return $@"
GENTLEBOOK BUCHUNGSSYSTEM – NEUE BUCHUNG
------------------------------------------------
Kunde:    {customer.FullName}
E-Mail:   {customer.Email ?? "–"}
Telefon:  {customer.Phone ?? "–"}

Service:  {service.Name}
Datum:    {booking.BookingDate:dd.MM.yyyy}
Uhrzeit:  {booking.StartTime:HH:mm} – {booking.EndTime:HH:mm} Uhr
Dauer:    {service.DurationMinutes} Minuten
Preis:    {service.Price:0.00} CHF
Status:   Bestätigt
{(!string.IsNullOrEmpty(booking.CustomerNotes) ? $"\nNotizen:  {booking.CustomerNotes}" : "")}
------------------------------------------------
Diese Nachricht wurde automatisch generiert.";
    }

    private string GetInternalCancellationNotificationHtml(Booking booking, Customer customer, Service service, string tenantName = "GentleBook", string? tenantLogoUrl = null, string primaryColor = "#6355E4")
    {
        return $@"<!DOCTYPE html>
<html lang='de'>
<head>
    <meta charset='UTF-8'>
    <title>Stornierung</title>
</head>
<body style='font-family: 'Plus Jakarta Sans', Arial, sans-serif; background-color: #f5f5f5; padding: 20px; margin: 0;'>
    <div style='max-width: 600px; margin: 0 auto; background-color: #ffffff; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 12px rgba(0,0,0,0.1);'>
        <div style='background: linear-gradient(135deg, #14162B 0%, #201F47 50%, #14162B 100%); padding: 30px; text-align: center; border-bottom: 3px solid {primaryColor};'>
            {(tenantLogoUrl != null ? $"<img src='{tenantLogoUrl}' alt='{tenantName}' style='max-height:50px;max-width:160px;object-fit:contain;margin-bottom:10px;'/><br/>" : $"<p style='color: {primaryColor}; font-size: 32px; margin: 0 0 10px 0;'>✧</p>")}
            <h1 style='color: #ffffff; font-size: 20px; margin: 0 0 6px 0;'>Buchung storniert</h1>
            <p style='color: {primaryColor}; font-size: 13px; margin: 0; letter-spacing: 1px; text-transform: uppercase;'>{tenantName}</p>
        </div>
        <div style='padding: 30px;'>
            <table style='width: 100%; border-collapse: collapse;'>
                <tr style='background-color: #f8f9fa;'>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; width: 140px; border: 1px solid #e2e8f0; font-size: 14px;'>Kunde</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px; font-weight: 600;'>{customer.FullName}</td>
                </tr>
                <tr>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>E-Mail</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px;'>{customer.Email ?? "–"}</td>
                </tr>
                <tr style='background-color: #f8f9fa;'>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Telefon</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px;'>{customer.Phone ?? "–"}</td>
                </tr>
                <tr>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Service</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px; font-weight: 600;'>{service.Name}</td>
                </tr>
                <tr style='background-color: #f8f9fa;'>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Datum</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px; font-weight: 600;'>{booking.BookingDate:dd.MM.yyyy}</td>
                </tr>
                <tr>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Uhrzeit</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px;'>{booking.StartTime:HH:mm} Uhr</td>
                </tr>
                <tr style='background-color: #f8f9fa;'>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Storniert am</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px;'>{DateTime.UtcNow:dd.MM.yyyy HH:mm} Uhr</td>
                </tr>
                <tr>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Status</td>
                    <td style='padding: 12px 15px; border: 1px solid #e2e8f0;'><span style='background-color: #fff3cd; color: #856404; padding: 4px 12px; border-radius: 20px; font-size: 13px; font-weight: 600;'>Storniert</span></td>
                </tr>
                {(!string.IsNullOrEmpty(booking.CancellationReason) ? $@"
                <tr style='background-color: #f8f9fa;'>
                    <td style='padding: 12px 15px; font-weight: bold; color: #64748b; border: 1px solid #e2e8f0; font-size: 14px;'>Grund</td>
                    <td style='padding: 12px 15px; color: #1e293b; border: 1px solid #e2e8f0; font-size: 14px;'>{booking.CancellationReason}</td>
                </tr>" : "")}
            </table>
            <p style='color: #64748b; font-size: 12px; margin-top: 20px; text-align: center;'>
                Diese Nachricht wurde automatisch vom GentleBook Buchungssystem generiert.
            </p>
        </div>
        <div style='background: linear-gradient(135deg, #14162B 0%, #201F47 100%); padding: 20px; text-align: center; border-top: 3px solid {primaryColor};'>
            <p style='color: {primaryColor}; font-size: 18px; margin: 0 0 8px 0;'>✧</p>
            <p style='color: #ffffff; font-size: 13px; font-weight: 700; margin: 0 0 4px 0;'>{tenantName}</p>
            <p style='color: #a3a3a3; font-size: 12px; margin: 0;'>GentleBook – Online Buchungssystem</p>
        </div>
    </div>
</body>
</html>";
    }

    private string GetInternalCancellationNotificationText(Booking booking, Customer customer, Service service)
    {
        return $@"
GENTLEBOOK BUCHUNGSSYSTEM – STORNIERUNG
------------------------------------------------
Kunde:        {customer.FullName}
E-Mail:       {customer.Email ?? "–"}
Telefon:      {customer.Phone ?? "–"}

Service:      {service.Name}
Datum:        {booking.BookingDate:dd.MM.yyyy}
Uhrzeit:      {booking.StartTime:HH:mm} Uhr
Storniert am: {DateTime.UtcNow:dd.MM.yyyy HH:mm} Uhr
Status:       Storniert
{(!string.IsNullOrEmpty(booking.CancellationReason) ? $"\nGrund:        {booking.CancellationReason}" : "")}
------------------------------------------------
Diese Nachricht wurde automatisch generiert.";
    }

    /// <summary>
    /// Sends a support / contact message from a TenantAdmin to support@gentlegroup.de.
    /// Always sends from noreply@gentlegroup.de with Reply-To set to the tenant's email.
    /// </summary>
    public async Task SendSupportMessageAsync(
        string tenantSlug,
        string companyName,
        string senderEmail,
        string senderName,
        string subject,
        string messageBody)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook Support", _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress("GentleBook Support", "support@gentlegroup.de"));
            message.ReplyTo.Add(new MailboxAddress(senderName, senderEmail));
            message.Subject = $"[Support] {companyName} ({tenantSlug}): {subject}";

            // GentleBook brand teal (#17A398) darkened via the shared helper — keeps every
            // platform-owned email on the same palette instead of ad-hoc gradient stops.
            var brandTealDark = DarkenHex("#17A398");

            var html = $@"<!DOCTYPE html>
<html lang='de'>
<head><meta charset='UTF-8'></head>
<body style='font-family:Inter,Arial,sans-serif;background:#f4f4f5;padding:40px 20px;margin:0'>
<div style='max-width:600px;margin:0 auto'>
  <!-- Header -->
  <div style='background:linear-gradient(135deg,#17A398,{brandTealDark});border-radius:16px 16px 0 0;padding:28px 32px;'>
    <h1 style='color:#fff;margin:0;font-size:20px;font-weight:700'>📩 Support-Anfrage</h1>
    <p style='color:rgba(255,255,255,0.75);margin:6px 0 0;font-size:13px'>Eingegangen über GentleBook Dashboard</p>
  </div>
  <!-- Body -->
  <div style='background:#ffffff;border-radius:0 0 16px 16px;padding:32px;border:1px solid #e5e7eb;border-top:none'>
    <!-- Firma Info -->
    <table style='width:100%;border-collapse:collapse;margin-bottom:24px;background:#f8fafc;border-radius:10px;overflow:hidden'>
      <tr style='border-bottom:1px solid #e5e7eb'>
        <td style='padding:12px 16px;font-size:12px;color:#6b7280;font-weight:600;width:140px'>Firma</td>
        <td style='padding:12px 16px;font-size:14px;color:#111827;font-weight:700'>{companyName}</td>
      </tr>
      <tr style='border-bottom:1px solid #e5e7eb'>
        <td style='padding:12px 16px;font-size:12px;color:#6b7280;font-weight:600'>Tenant-ID</td>
        <td style='padding:12px 16px;font-size:14px;color:#17A398;font-family:monospace'>{tenantSlug}</td>
      </tr>
      <tr style='border-bottom:1px solid #e5e7eb'>
        <td style='padding:12px 16px;font-size:12px;color:#6b7280;font-weight:600'>Absender</td>
        <td style='padding:12px 16px;font-size:14px;color:#111827'>{senderName}</td>
      </tr>
      <tr>
        <td style='padding:12px 16px;font-size:12px;color:#6b7280;font-weight:600'>E-Mail</td>
        <td style='padding:12px 16px;font-size:14px;'><a href='mailto:{senderEmail}' style='color:#17A398'>{senderEmail}</a></td>
      </tr>
    </table>
    <!-- Betreff -->
    <p style='font-size:11px;color:#9ca3af;text-transform:uppercase;letter-spacing:1px;margin:0 0 6px'>Betreff</p>
    <p style='font-size:16px;font-weight:700;color:#111827;margin:0 0 20px'>{subject}</p>
    <!-- Nachricht -->
    <p style='font-size:11px;color:#9ca3af;text-transform:uppercase;letter-spacing:1px;margin:0 0 8px'>Nachricht</p>
    <div style='background:#f8fafc;border-left:4px solid #17A398;border-radius:0 8px 8px 0;padding:16px 20px;font-size:14px;color:#374151;line-height:1.7;white-space:pre-wrap'>{messageBody}</div>
    <!-- CTA -->
    <div style='margin-top:28px;padding-top:20px;border-top:1px solid #e5e7eb;text-align:center'>
      <a href='mailto:{senderEmail}' style='background:linear-gradient(135deg,#17A398,{brandTealDark});color:#fff;text-decoration:none;padding:12px 28px;border-radius:10px;font-weight:600;font-size:14px;display:inline-block'>
        Direkt antworten →
      </a>
    </div>
    <p style='text-align:center;font-size:11px;color:#9ca3af;margin-top:20px'>
      Eingegangen am {DateTime.Now:dd.MM.yyyy} um {DateTime.Now:HH:mm} Uhr
    </p>
  </div>
</div>
</body>
</html>";

            var text = $@"Support-Anfrage via GentleBook
==============================
Firma:     {companyName}
Tenant-ID: {tenantSlug}
Absender:  {senderName}
E-Mail:    {senderEmail}

Betreff: {subject}

Nachricht:
{messageBody}

---
Zum Antworten: {senderEmail}
Eingegangen: {DateTime.Now:dd.MM.yyyy HH:mm}";

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Support message sent from {TenantSlug} ({SenderEmail})", tenantSlug, senderEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send support message from {TenantSlug}", tenantSlug);
            throw;
        }
    }

    #endregion

    #region Helpers

    public string? GetAbsoluteLogoUrl(string? logoUrl)
    {
        if (logoUrl == null) return null;
        if (logoUrl.StartsWith("http")) return logoUrl;
        var origin = new Uri(_emailOptions.BaseUrl).GetLeftPart(UriPartial.Authority);
        return $"{origin}{logoUrl}";
    }

    private static string DarkenHex(string hex, double factor = 0.15)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
            int r = Math.Max(0, (int)(Convert.ToInt32(hex.Substring(0, 2), 16) * (1 - factor)));
            int g = Math.Max(0, (int)(Convert.ToInt32(hex.Substring(2, 2), 16) * (1 - factor)));
            int b = Math.Max(0, (int)(Convert.ToInt32(hex.Substring(4, 2), 16) * (1 - factor)));
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        catch { return hex.StartsWith('#') ? hex : $"#{hex}"; }
    }

    #endregion

    #region Email Templates

    private string GetBaseEmailTemplate(string title, string content, string tenantName = "GentleBook", string? tenantLogoUrl = null, string primaryColor = "#6355E4")
    {
        var darkenedColor = DarkenHex(primaryColor);
        var isGentleBookOwnEmail = string.Equals(tenantName, "GentleBook", StringComparison.OrdinalIgnoreCase);
        var headerContent = tenantLogoUrl != null
            ? $@"<img src='{tenantLogoUrl}' alt='{tenantName}' style='max-height:60px; max-width:200px; object-fit:contain; margin-bottom:12px;' /><br/><p style='color: {primaryColor}; font-size: 14px; margin: 0; letter-spacing: 1px; text-transform: uppercase; opacity: 0.9;'>{tenantName}</p>"
            : isGentleBookOwnEmail
                ? $@"<img src='data:image/png;base64,{LogoBase64}' alt='gentlebook' width='40' height='40' style='display:block; margin: 0 auto 12px;' />
            <p style='color: #ffffff; font-size: 24px; font-weight: 700; margin: 0 0 8px 0; letter-spacing: -0.3px; font-family: ''Plus Jakarta Sans'',Arial,sans-serif;'>gentlebook</p>
            <p style='color: {primaryColor}; font-size: 14px; margin: 0; letter-spacing: 1px; text-transform: uppercase; opacity: 0.9;'>Online Buchungssystem</p>"
                : $@"<div style='color: {primaryColor}; font-size: 44px; font-weight: 300; margin-bottom: 16px; line-height: 1;'>✧</div>
            <p style='color: {primaryColor}; font-size: 26px; font-weight: 600; margin: 0 0 8px 0; letter-spacing: 0.5px; font-family: Arial, sans-serif;'>{tenantName}</p>
            <p style='color: {primaryColor}; font-size: 14px; margin: 0; letter-spacing: 1px; text-transform: uppercase; opacity: 0.9;'>Online Buchungssystem</p>";
        return $@"<!DOCTYPE html>
<html lang='de'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <meta name='color-scheme' content='light dark'>
    <meta name='supported-color-schemes' content='light dark'>
    <title>{title}</title>
    <style>
        * {{
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }}
        
        body {{
            font-family: 'Plus Jakarta Sans', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            line-height: 1.6;
            margin: 0;
            padding: 20px;
            background-color: #f5f5f5;
        }}
        
        :root {{
            color-scheme: light dark;
            --bg-primary: #ffffff;
            --bg-secondary: #f8f9fa;
            --text-primary: #1e293b;
            --text-secondary: #64748b;
            --border-color: #e2e8f0;
            --accent-light: #f8f8ff;
            --accent-primary: {primaryColor};
            --accent-dark: {darkenedColor};
            --button-gradient-start: {primaryColor};
            --button-gradient-end: {darkenedColor};
            --success-bg: #d4edda;
            --success-text: #155724;
            --success-border: #c3e6cb;
            --warning-bg: #fff3cd;
            --warning-text: #856404;
            --warning-border: #ffeeba;
            --info-bg: #eff6ff;
            --info-text: #1e40af;
            --info-border: #3b82f6;
        }}

        @media (prefers-color-scheme: dark) {{
            :root {{
                --bg-primary: #1a1a1a;
                --bg-secondary: #2d2d2d;
                --text-primary: #e5e5e5;
                --text-secondary: #a3a3a3;
                --border-color: #404040;
                --accent-light: #2d2d2d;
                --accent-primary: {primaryColor};
                --accent-dark: {darkenedColor};
                --button-gradient-start: {primaryColor};
                --button-gradient-end: {darkenedColor};
                --success-bg: #1e3a2a;
                --success-text: #a3e9a3;
                --success-border: #2d5a2d;
                --warning-bg: #3a3a1e;
                --warning-text: #ffd700;
                --warning-border: #5a5a2d;
                --info-bg: #1e3a4a;
                --info-text: #93c5fd;
                --info-border: #60a5fa;
            }}
        }}
        
        .container {{
            max-width: 600px;
            margin: 0 auto;
            background-color: var(--bg-primary);
            border-radius: 24px;
            overflow: hidden;
            box-shadow: 0 20px 40px rgba(0,0,0,0.1);
        }}
        
        .header {{
            background: linear-gradient(135deg, #14162B 0%, #201F47 50%, #14162B 100%);
            padding: 40px 30px;
            text-align: center;
            border-bottom: 3px solid {primaryColor};
        }}

        .header-logo {{
            color: {primaryColor};
            font-size: 44px;
            font-weight: 300;
            margin-bottom: 16px;
            display: block;
            line-height: 1;
        }}

        .header h1 {{
            color: #ffffff;
            font-size: 26px;
            font-weight: 600;
            margin: 0 0 8px 0;
            letter-spacing: 0.5px;
        }}

        .header p {{
            color: {primaryColor};
            font-size: 14px;
            margin: 0;
            letter-spacing: 1px;
            text-transform: uppercase;
            opacity: 0.9;
        }}
        
        .content {{
            padding: 40px 30px;
            background-color: var(--bg-primary);
            color: var(--text-primary);
        }}
        
        .greeting {{
            font-size: 18px;
            font-weight: 600;
            color: var(--text-primary);
            margin-bottom: 20px;
        }}
        
        .booking-card {{
            background-color: var(--bg-secondary);
            border-radius: 16px;
            padding: 30px;
            margin: 30px 0;
            border: 1px solid var(--border-color);
        }}
        
        .booking-title {{
            font-size: 18px;
            font-weight: 700;
            color: var(--text-primary);
            margin-bottom: 20px;
            padding-bottom: 15px;
            border-bottom: 2px solid var(--accent-primary);
        }}
        
        .detail-row {{
            display: flex;
            padding: 12px 0;
            border-bottom: 1px solid var(--border-color);
        }}
        
        .detail-row:last-child {{
            border-bottom: none;
        }}
        
        .detail-label {{
            width: 120px;
            color: var(--text-secondary);
            font-weight: 500;
        }}
        
        .detail-value {{
            flex: 1;
            color: var(--text-primary);
            font-weight: 600;
        }}
        
        .price {{
            color: var(--text-primary);
            font-size: 20px;
            font-weight: 700;
        }}
        
        .status-badge {{
            display: inline-block;
            padding: 6px 16px;
            border-radius: 40px;
            font-size: 14px;
            font-weight: 600;
            letter-spacing: 0.5px;
        }}
        
        .status-badge.confirmed {{
            background-color: var(--success-bg);
            color: var(--success-text);
            border: 1px solid var(--success-border);
        }}
        
        .status-badge.cancelled {{
            background-color: var(--warning-bg);
            color: var(--warning-text);
            border: 1px solid var(--warning-border);
        }}
        
        .info-box {{
            background-color: var(--info-bg);
            border-left: 4px solid var(--info-border);
            padding: 20px;
            margin: 20px 0;
            border-radius: 4px;
            color: var(--text-primary);
        }}
        
        .info-box h3 {{
            color: var(--info-text);
            margin-bottom: 10px;
            font-size: 16px;
        }}
        
        .info-box ul {{
            margin-left: 20px;
            color: var(--text-primary);
        }}
        
        .info-box li {{
            margin-bottom: 8px;
        }}
        
        .cancel-section {{
            text-align: center;
            margin: 40px 0 20px;
            padding: 30px;
            background-color: var(--bg-secondary);
            border-radius: 16px;
            border: 1px solid var(--border-color);
        }}
        
        .cancel-title {{
            font-size: 18px;
            font-weight: 700;
            color: var(--text-primary);
            margin-bottom: 10px;
        }}
        
        .cancel-text {{
            color: var(--text-secondary);
            margin-bottom: 25px;
            font-size: 15px;
        }}
        
        .button {{
            display: inline-block;
            background: linear-gradient(135deg, var(--button-gradient-start) 0%, var(--button-gradient-end) 100%);
            color: #ffffff !important;
            text-decoration: none;
            padding: 14px 32px;
            border-radius: 40px;
            font-weight: 600;
            font-size: 16px;
            box-shadow: 0 4px 12px rgba(0,0,0,0.15);
            border: none;
            cursor: pointer;
        }}
        
        .button-outline {{
            background: transparent;
            color: {primaryColor} !important;
            border: 2px solid {primaryColor};
            box-shadow: none;
        }}

        .footer {{
            background: linear-gradient(135deg, #14162B 0%, #201F47 100%);
            padding: 30px;
            text-align: center;
            font-size: 14px;
            border-top: 3px solid {primaryColor};
        }}
        
        .footer-brand {{
            font-weight: 700;
            color: #ffffff;
            font-size: 15px;
            margin-bottom: 8px;
        }}
        
        .footer-address {{
            color: #a3a3a3;
            font-style: normal;
            line-height: 1.7;
            font-size: 13px;
        }}
        
        .footer-contact {{
            color: #a3a3a3;
            margin-top: 12px;
            font-size: 13px;
        }}
        
        .footer-links {{
            margin-top: 20px;
        }}
        
        .footer-links a {{
            color: {primaryColor};
            text-decoration: none;
            margin: 0 10px;
            font-size: 12px;
        }}
        
        .footer-links a:hover {{
            text-decoration: underline;
        }}
        
        .footer-divider {{
            color: #404040;
            margin: 0 5px;
        }}
        
        .footer-copy {{
            margin-top: 16px;
            font-size: 11px;
            color: #555555;
        }}
        
        @media only screen and (max-width: 600px) {{
            .container {{
                margin: 10px;
                width: auto;
            }}
            .content {{
                padding: 30px 20px;
            }}
            .detail-row {{
                flex-direction: column;
            }}
            .detail-label {{
                width: 100%;
                margin-bottom: 5px;
            }}
            .booking-card {{
                padding: 20px;
            }}
        }}
    </style>
</head>
<body>
    <div class='container'>
        <div style='background: linear-gradient(135deg, #14162B 0%, #201F47 50%, #14162B 100%); padding: 40px 30px; text-align: center;'>
            {headerContent}
        </div>
        <div style='height:3px; background: linear-gradient(90deg, {primaryColor} 0%, #17A398 100%);'></div>

        <div class='content'>
            {content}
        </div>

        <div class='footer'>
            {(isGentleBookOwnEmail
                ? $"<img src='data:image/png;base64,{LogoBase64}' alt='gentlebook' width='24' height='24' style='display:block; margin: 0 auto 10px;' />"
                : $"<div style='color: {primaryColor}; font-size: 22px; margin-bottom: 12px;'>✧</div>")}
            <div class='footer-brand'>{(isGentleBookOwnEmail ? "gentlebook" : tenantName)}</div>
            <div class='footer-links'>
                <a href='{FrontendUrl}/datenschutz'>Datenschutz</a>
                <span class='footer-divider'>|</span>
                <a href='{FrontendUrl}/impressum'>Impressum</a>
            </div>
            <div class='footer-copy'>
                © {DateTime.UtcNow.Year} {tenantName}. Buchungssystem powered by GentleBook.
            </div>
        </div>
    </div>
</body>
</html>";
    }

    private string GetConfirmationEmailHtml(Booking booking, string cancellationUrl, string tenantName = "GentleBook", string? tenantLogoUrl = null, string currency = "EUR", string primaryColor = "#6355E4")
    {
        var content = $@"
            <div class='greeting'>
                Hallo {booking.Customer.FirstName},
            </div>

            <p style='color: var(--text-secondary); margin-bottom: 30px;'>
                Vielen Dank für Ihre Buchung bei {tenantName}. Ihr Termin wurde erfolgreich bestätigt.
            </p>
            
            <div class='booking-card'>
                <div class='booking-title'>
                    Buchungsdetails
                    <span style='float: right;'><span class='status-badge confirmed'>Bestätigt</span></span>
                </div>
                
                <div class='detail-row'>
                    <span class='detail-label'>Service</span>
                    <span class='detail-value'>{booking.Service.Name}</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Datum</span>
                    <span class='detail-value'>{booking.BookingDate:dd.MM.yyyy}</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Uhrzeit</span>
                    <span class='detail-value'>{booking.StartTime:HH:mm} - {booking.EndTime:HH:mm} Uhr</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Dauer</span>
                    <span class='detail-value'>{booking.Service.DurationMinutes} Minuten</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Preis</span>
                    <span class='detail-value'><span class='price'>{booking.Service.Price:0.00} {currency}</span></span>
                </div>
            </div>
            
            <div class='cancel-section'>
                <div class='cancel-title'>Termin stornieren?</div>
                <div class='cancel-text'>
                    Falls Sie Ihren Termin nicht wahrnehmen können, stornieren Sie diesen bitte rechtzeitig.
                </div>
                <a href='{cancellationUrl}' style='display: inline-block; background: linear-gradient(135deg, {primaryColor} 0%, {DarkenHex(primaryColor)} 100%); color: #ffffff; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; box-shadow: 0 4px 12px rgba(0,0,0,0.15);'>Termin stornieren
                </a>
                <p style='color: var(--text-secondary); font-size: 12px; margin-top: 15px;'>
                    Die Stornierung ist bis 24 Stunden vor dem Termin kostenlos möglich.
                </p>
            </div>";

        return GetBaseEmailTemplate("Ihre Buchungsbestätigung", content, tenantName, tenantLogoUrl, primaryColor);
    }

    private string GetConfirmationReceiptHtml(Booking booking, Customer customer, Service service, string cancellationUrl, string tenantName = "GentleBook", string? tenantLogoUrl = null, string currency = "EUR", string primaryColor = "#6355E4")
    {
        var content = $@"
            <div class='greeting'>
                Hallo {customer.FirstName},
            </div>
            
            <p style='color: var(--text-secondary); margin-bottom: 30px;'>
                Ihre Buchung wurde erfolgreich bestätigt. Hier sind Ihre Buchungsdetails:
            </p>
            
            <div class='booking-card'>
                <div class='booking-title'>
                    Buchungsdetails
                    <span style='float: right;'><span class='status-badge confirmed'>Bestätigt</span></span>
                </div>
                
                <div class='detail-row'>
                    <span class='detail-label'>Service</span>
                    <span class='detail-value'>{service.Name}</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Datum</span>
                    <span class='detail-value'>{booking.BookingDate:dd.MM.yyyy}</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Uhrzeit</span>
                    <span class='detail-value'>{booking.StartTime:HH:mm} - {booking.EndTime:HH:mm} Uhr</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Dauer</span>
                    <span class='detail-value'>{service.DurationMinutes} Minuten</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Preis</span>
                    <span class='detail-value'><span class='price'>{service.Price:0.00} {currency}</span></span>
                </div>
            </div>
            
            <div class='cancel-section'>
                <div class='cancel-title'>Termin verwalten</div>
                <div class='cancel-text'>
                    Sie können Ihren Termin hier stornieren, falls nötig.
                </div>
                <!--[if mso]>
                <v:roundrect xmlns:v='urn:schemas-microsoft-com:vml' xmlns:w='urn:schemas-microsoft-com:office:word'
                    href='{cancellationUrl}'
                    style='height:48px;v-text-anchor:middle;width:220px;' arcsize='50%'
                    strokecolor='{primaryColor}' strokeweight='2pt' filled='f'>
                    <w:anchorlock/>
                    <center style='color:{primaryColor};font-family:Arial,sans-serif;font-size:16px;font-weight:bold;'>Termin stornieren</center>
                </v:roundrect>
                <![endif]--><!--[if !mso]><!-->
                <a href='{cancellationUrl}' style='display:inline-block;background:transparent;border:2px solid {primaryColor};border-radius:40px;color:{primaryColor};font-family:Arial,sans-serif;font-size:16px;font-weight:bold;padding:12px 30px;text-decoration:none;'>Termin stornieren</a>
                <!--<![endif]-->
            </div>";

        return GetBaseEmailTemplate("Buchung bestätigt", content, tenantName, tenantLogoUrl, primaryColor);
    }

    private string GetCancellationEmailHtml(Booking booking, Customer customer, Service service, string tenantName = "GentleBook", string? tenantLogoUrl = null, string primaryColor = "#6355E4")
    {
        var content = $@"
            <div class='greeting'>
                Hallo {customer.FirstName},
            </div>
            
            <p style='color: var(--text-secondary); margin-bottom: 30px;'>
                Ihre Buchung bei {tenantName} wurde erfolgreich storniert.
            </p>
            
            <div class='booking-card'>
                <div class='booking-title'>
                    Stornierte Buchung
                    <span style='float: right;'><span class='status-badge cancelled'>Storniert</span></span>
                </div>
                
                <div class='detail-row'>
                    <span class='detail-label'>Service</span>
                    <span class='detail-value'>{service.Name}</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Datum</span>
                    <span class='detail-value'>{booking.BookingDate:dd.MM.yyyy}</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Uhrzeit</span>
                    <span class='detail-value'>{booking.StartTime:HH:mm} Uhr</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Storniert am</span>
                    <span class='detail-value'>{DateTime.UtcNow:dd.MM.yyyy HH:mm} Uhr</span>
                </div>
                {(!string.IsNullOrEmpty(booking.CancellationReason) ? $@"
                <div class='detail-row'>
                    <span class='detail-label'>Grund</span>
                    <span class='detail-value'>{booking.CancellationReason}</span>
                </div>" : "")}
            </div>
            
            <div class='cancel-section'>
                <div class='cancel-title'>Neuen Termin buchen?</div>
                <div class='cancel-text'>
                    Wir freuen uns, Sie bald wieder bei uns begrüßen zu dürfen.
                </div>
                <a href='{FrontendUrl}' style='display: inline-block; background: linear-gradient(135deg, {primaryColor} 0%, {DarkenHex(primaryColor)} 100%); color: #ffffff; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; box-shadow: 0 4px 12px rgba(0,0,0,0.15);'>Neuen Termin buchen
                </a>
            </div>";

        return GetBaseEmailTemplate("Termin storniert", content, tenantName, tenantLogoUrl, primaryColor);
    }

    private string GetReminderEmailHtml(Booking booking, string cancellationUrl, string tenantName = "GentleBook", string? tenantLogoUrl = null, string primaryColor = "#6355E4")
    {
        var content = $@"
            <div class='greeting'>
                Hallo {booking.Customer.FirstName},
            </div>
            
            <p style='color: var(--text-secondary); margin-bottom: 30px;'>
                dies ist eine freundliche Erinnerung an Ihren morgigen Termin bei {tenantName}.
            </p>
            
            <div class='booking-card'>
                <div class='booking-title'>
                    Termindetails
                    <span style='float: right;'><span class='status-badge confirmed'>Bestätigt</span></span>
                </div>
                
                <div class='detail-row'>
                    <span class='detail-label'>Service</span>
                    <span class='detail-value'>{booking.Service.Name}</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Datum</span>
                    <span class='detail-value'>{booking.BookingDate:dd.MM.yyyy}</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Uhrzeit</span>
                    <span class='detail-value'>{booking.StartTime:HH:mm} Uhr</span>
                </div>
                <div class='detail-row'>
                    <span class='detail-label'>Dauer</span>
                    <span class='detail-value'>{booking.Service.DurationMinutes} Minuten</span>
                </div>
            </div>
            
            <div class='info-box'>
                <h3>Bitte beachten Sie:</h3>
                <ul>
                    <li>Bitte kommen Sie 5 Minuten vor Ihrem Termin</li>
                    <li>Bei Verspätung kann es zu Verkürzungen der Behandlungszeit kommen</li>
                </ul>
            </div>
            
            <div class='cancel-section'>
                <div class='cancel-title'>Termin absagen?</div>
                <div class='cancel-text'>
                    Falls Sie den Termin nicht wahrnehmen können, stornieren Sie bitte rechtzeitig.
                </div>
                <!--[if mso]>
                <v:roundrect xmlns:v='urn:schemas-microsoft-com:vml' xmlns:w='urn:schemas-microsoft-com:office:word'
                    href='{cancellationUrl}'
                    style='height:48px;v-text-anchor:middle;width:220px;' arcsize='50%'
                    strokecolor='{primaryColor}' strokeweight='2pt' filled='f'>
                    <w:anchorlock/>
                    <center style='color:{primaryColor};font-family:Arial,sans-serif;font-size:16px;font-weight:bold;'>Termin stornieren</center>
                </v:roundrect>
                <![endif]--><!--[if !mso]><!-->
                <a href='{cancellationUrl}' style='display:inline-block;background:transparent;border:2px solid {primaryColor};border-radius:40px;color:{primaryColor};font-family:Arial,sans-serif;font-size:16px;font-weight:bold;padding:12px 30px;text-decoration:none;'>Termin stornieren</a>
                <!--<![endif]-->
            </div>";

        return GetBaseEmailTemplate("Terminerinnerung", content, tenantName, tenantLogoUrl, primaryColor);
    }

    #endregion

    #region Plain Text Versions

    private string GetConfirmationEmailText(Booking booking, string cancellationUrl, string tenantName = "Buchungssystem", string currency = "EUR")
    {
        return $@"
{tenantName.ToUpperInvariant()} - IHRE BUCHUNGSBESTÄTIGUNG

------------------------------------------------
Hallo {booking.Customer.FirstName},

vielen Dank für Ihre Buchung bei {tenantName}. Ihr Termin wurde erfolgreich bestätigt.

BUCHUNGSDETAILS:
------------------------------------------------
Service: {booking.Service.Name}
Datum: {booking.BookingDate:dd.MM.yyyy}
Uhrzeit: {booking.StartTime:HH:mm} - {booking.EndTime:HH:mm} Uhr
Dauer: {booking.Service.DurationMinutes} Minuten
Preis: {booking.Service.Price:0.00} {currency}
Status: Bestätigt


TERMIN STORNIEREN:
------------------------------------------------
Falls Sie Ihren Termin nicht wahrnehmen können:
{cancellationUrl}

------------------------------------------------
© {DateTime.UtcNow.Year} {tenantName}. Alle Rechte vorbehalten.";
    }

    private string GetConfirmationReceiptText(Booking booking, Customer customer, Service service, string cancellationUrl, string tenantName = "Buchungssystem", string currency = "EUR")
    {
        return $@"
{tenantName.ToUpperInvariant()} - BUCHUNG BESTÄTIGT

------------------------------------------------
Hallo {customer.FirstName},

Ihre Buchung wurde erfolgreich bestätigt.

BUCHUNGSDETAILS:
------------------------------------------------
Service: {service.Name}
Datum: {booking.BookingDate:dd.MM.yyyy}
Uhrzeit: {booking.StartTime:HH:mm} - {booking.EndTime:HH:mm} Uhr
Dauer: {service.DurationMinutes} Minuten
Preis: {service.Price:0.00} {currency}
Status: Bestätigt

------------------------------------------------
© {DateTime.UtcNow.Year} {tenantName}. Alle Rechte vorbehalten.";
    }

    private string GetCancellationEmailText(Booking booking, Customer customer, Service service, string tenantName = "Buchungssystem")
    {
        return $@"
{tenantName.ToUpperInvariant()} - STORNIERUNGSBESTÄTIGUNG

------------------------------------------------
Hallo {customer.FirstName},

Ihre Buchung wurde erfolgreich storniert.

STORNIERTE BUCHUNG:
------------------------------------------------
Service: {service.Name}
Datum: {booking.BookingDate:dd.MM.yyyy}
Uhrzeit: {booking.StartTime:HH:mm} Uhr
Storniert am: {DateTime.UtcNow:dd.MM.yyyy HH:mm} Uhr
{(booking.CancellationReason != null ? $"Grund: {booking.CancellationReason}" : "")}

------------------------------------------------
© {DateTime.UtcNow.Year} {tenantName}. Alle Rechte vorbehalten.";
    }

    private string GetReminderEmailText(Booking booking, string cancellationUrl, string tenantName = "Buchungssystem")
    {
        return $@"
{tenantName.ToUpperInvariant()} - TERMINERINNERUNG

------------------------------------------------
Hallo {booking.Customer.FirstName},

dies ist eine freundliche Erinnerung an Ihren morgigen Termin.

TERMINDETAILS:
------------------------------------------------
Service: {booking.Service.Name}
Datum: {booking.BookingDate:dd.MM.yyyy}
Uhrzeit: {booking.StartTime:HH:mm} Uhr
Dauer: {booking.Service.DurationMinutes} Minuten

WICHTIG:
------------------------------------------------
• Bitte kommen Sie 5 Minuten vor Ihrem Termin
• Bei Verspätung kann es zu Verkürzungen kommen

STORNIERUNG:
------------------------------------------------
{cancellationUrl}

------------------------------------------------
© {DateTime.UtcNow.Year} {tenantName}. Alle Rechte vorbehalten.";
    }

    #endregion

    // New tokens are HMAC-signed ("{payloadBase64Url}.{signatureBase64Url}", payload =
    // "{bookingId}:{action}"). DecodeToken keeps accepting the old, unsigned Base64
    // triplet format indefinitely so links already sent by e-mail never break.
    private byte[] TokenSigningKey =>
        System.Text.Encoding.UTF8.GetBytes(_config["Jwt:Secret"]
            ?? throw new InvalidOperationException("Jwt:Secret is not configured"));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static byte[] FromBase64Url(string input)
    {
        var s = input.Replace("-", "+").Replace("_", "/");
        while (s.Length % 4 != 0) s += "=";
        return Convert.FromBase64String(s);
    }

    private string GenerateCancellationToken(Guid bookingId) => GenerateActionToken(bookingId, "cancel");

    private string GenerateActionToken(Guid bookingId, string action)
    {
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes($"{bookingId}:{action}");
        using var hmac = new System.Security.Cryptography.HMACSHA256(TokenSigningKey);
        var signature = hmac.ComputeHash(payloadBytes);
        return $"{Base64Url(payloadBytes)}.{Base64Url(signature)}";
    }

    public (Guid bookingId, string action) DecodeToken(string token)
    {
        // New signed format: "payload.signature"
        if (token.Contains('.'))
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length == 2)
                {
                    var payloadBytes = FromBase64Url(parts[0]);
                    var providedSig = FromBase64Url(parts[1]);
                    using var hmac = new System.Security.Cryptography.HMACSHA256(TokenSigningKey);
                    var expectedSig = hmac.ComputeHash(payloadBytes);
                    if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedSig, providedSig))
                    {
                        var payload = System.Text.Encoding.UTF8.GetString(payloadBytes);
                        var segments = payload.Split(':');
                        if (segments.Length == 2 && Guid.TryParse(segments[0], out var signedBookingId))
                            return (signedBookingId, segments[1]);
                    }
                }
            }
            catch
            {
            }
            return (Guid.Empty, string.Empty);
        }

        // Legacy unsigned format — kept so previously sent e-mail links stay valid.
        try
        {
            var base64 = token.Replace("-", "+").Replace("_", "/");
            while (base64.Length % 4 != 0)
                base64 += "=";

            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var parts = decoded.Split(':');

            if (parts.Length == 3 && Guid.TryParse(parts[0], out var bookingId))
            {
                return (bookingId, parts[2]);
            }
        }
        catch
        {
        }

        return (Guid.Empty, string.Empty);
    }

    /// <summary>
    /// Sends a password reset link to a TenantAdmin.
    /// </summary>
    public async Task SendPasswordResetEmailAsync(string recipientEmail, string firstName, string resetUrl)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = "Passwort zurücksetzen – GentleBook";

            var brandTealDark = DarkenHex("#17A398");

            var html = $@"<!DOCTYPE html>
<html lang='de'>
<head><meta charset='UTF-8'></head>
<body style='font-family:Inter,Arial,sans-serif;background:#f4f4f5;padding:40px 20px;margin:0'>
<div style='max-width:520px;margin:0 auto'>
  <div style='background:linear-gradient(135deg,#17A398,{brandTealDark});border-radius:16px 16px 0 0;padding:32px;text-align:center'>
    <div style='width:56px;height:56px;background:rgba(255,255,255,0.2);border-radius:14px;display:inline-flex;align-items:center;justify-content:center;margin-bottom:16px'>
      <span style='font-size:28px'>🔐</span>
    </div>
    <h1 style='color:#fff;margin:0;font-size:22px;font-weight:700'>Passwort zurücksetzen</h1>
    <p style='color:rgba(255,255,255,0.7);margin:8px 0 0;font-size:14px'>GentleBook Buchungssystem</p>
  </div>
  <div style='background:#fff;border-radius:0 0 16px 16px;padding:32px;border:1px solid #e5e7eb;border-top:none'>
    <p style='font-size:15px;color:#374151;margin:0 0 12px'>Hallo {firstName},</p>
    <p style='font-size:14px;color:#6b7280;line-height:1.6;margin:0 0 24px'>
      Du hast eine Anfrage zum Zurücksetzen deines Passworts gestellt. Klicke auf den Button unten, um ein neues Passwort festzulegen.
    </p>
    <div style='text-align:center;margin:28px 0'>
      <a href='{resetUrl}' style='background:linear-gradient(135deg,#17A398,{brandTealDark});color:#fff;text-decoration:none;padding:14px 36px;border-radius:12px;font-weight:700;font-size:15px;display:inline-block;box-shadow:0 4px 14px rgba(23,163,152,0.3)'>
        Passwort zurücksetzen →
      </a>
    </div>
    <div style='background:#fef3c7;border:1px solid #fde68a;border-radius:10px;padding:14px 16px;margin:24px 0'>
      <p style='font-size:13px;color:#92400e;margin:0'>
        ⏱️ <strong>Achtung:</strong> Dieser Link ist nur <strong>1 Stunde</strong> gültig. Danach musst du eine neue Anfrage stellen.
      </p>
    </div>
    <p style='font-size:13px;color:#9ca3af;margin:0 0 4px'>Falls du diese Anfrage nicht gestellt hast, ignoriere diese E-Mail einfach.</p>
    <p style='font-size:11px;color:#d1d5db;margin:20px 0 0;border-top:1px solid #f3f4f6;padding-top:16px;text-align:center'>
      GentleBook · support@gentlegroup.de
    </p>
  </div>
</div>
</body>
</html>";

            var text = $@"Passwort zurücksetzen – GentleBook
=====================================
Hallo {firstName},

du hast eine Anfrage zum Zurücksetzen deines Passworts gestellt.

Link zum Zurücksetzen:
{resetUrl}

Dieser Link ist 1 Stunde gültig.
Falls du diese Anfrage nicht gestellt hast, ignoriere diese E-Mail.

GentleBook · support@gentlegroup.de";

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Password reset email sent to {Email}", recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send password reset email to {Email}", recipientEmail);
            throw;
        }
    }

    /// <summary>
    /// Notifies the TenantAdmin that their requested plan was activated.
    /// </summary>
    public async Task SendPlanActivatedEmailAsync(string recipientEmail, string firstName, string planName, decimal monthlyPrice)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = $"Ihr {planName}-Plan ist aktiv – GentleBook";

            var html = $@"<!DOCTYPE html>
<html lang='de'>
<head><meta charset='UTF-8'></head>
<body style='font-family:Inter,Arial,sans-serif;background:#f4f4f5;padding:40px 20px;margin:0'>
<div style='max-width:520px;margin:0 auto'>
  <div style='background:linear-gradient(135deg,#14162B,#201F47);border-radius:16px 16px 0 0;padding:32px;text-align:center'>
    <div style='width:56px;height:56px;background:rgba(255,255,255,0.2);border-radius:14px;display:inline-flex;align-items:center;justify-content:center;margin-bottom:16px'>
      <span style='font-size:28px'>🎉</span>
    </div>
    <h1 style='color:#fff;margin:0;font-size:22px;font-weight:700'>Ihr Plan ist aktiv!</h1>
    <p style='color:rgba(255,255,255,0.75);margin:8px 0 0;font-size:14px'>GentleBook Buchungssystem</p>
  </div>
  <div style='background:#fff;border-radius:0 0 16px 16px;padding:32px;border:1px solid #e5e7eb;border-top:none'>
    <p style='font-size:15px;color:#374151;margin:0 0 12px'>Hallo {firstName},</p>
    <p style='font-size:14px;color:#6b7280;line-height:1.6;margin:0 0 20px'>
      Ihr <strong>{planName}</strong>-Plan ({monthlyPrice:0}&nbsp;€/Monat) wurde soeben aktiviert.
      Alle Funktionen Ihres Plans stehen ab sofort zur Verfügung.
    </p>
    <div style='text-align:center;margin:24px 0'>
      <a href='{FrontendUrl}/admin/subscription' style='background:linear-gradient(135deg,#6355E4,#17A398);color:#fff;text-decoration:none;padding:14px 36px;border-radius:12px;font-weight:700;font-size:15px;display:inline-block'>
        Abo-Details ansehen →
      </a>
    </div>
    <p style='font-size:11px;color:#d1d5db;margin:20px 0 0;border-top:1px solid #f3f4f6;padding-top:16px;text-align:center'>
      GentleBook · support@gentlegroup.de
    </p>
  </div>
</div>
</body>
</html>";

            var text = $@"Ihr Plan ist aktiv – GentleBook

Hallo {firstName},

Ihr {planName}-Plan ({monthlyPrice:0} €/Monat) wurde soeben aktiviert.
Alle Funktionen stehen ab sofort zur Verfügung.

{FrontendUrl}/admin/subscription

GentleBook · support@gentlegroup.de";

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Plan-activated email sent to {Email} ({Plan})", recipientEmail, planName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send plan-activated email to {Email}", recipientEmail);
            throw;
        }
    }

    /// <summary>Confirms a tenant-initiated subscription cancellation. periodEnd is null when the
    /// account has already been finalized (final notice) rather than just recorded.</summary>
    public async Task SendSubscriptionCancelledConfirmationAsync(string recipientEmail, string firstName, string planName, DateTime? periodEnd)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = periodEnd != null ? "Ihre Kündigung wurde bestätigt" : "Ihr Zugang wurde beendet";

            var periodText = periodEnd != null
                ? $"Ihr Zugang zu GentleBook ({planName}-Plan) bleibt bis zum <strong>{periodEnd:dd.MM.yyyy}</strong> bestehen. Danach werden keine weiteren Zahlungen eingezogen."
                : $"Ihr Zugang zu GentleBook ({planName}-Plan) wurde soeben beendet. Es werden keine weiteren Zahlungen eingezogen.";

            var html = $@"<!DOCTYPE html>
<html lang='de'>
<head><meta charset='UTF-8'></head>
<body style='font-family:Inter,Arial,sans-serif;background:#f4f4f5;padding:40px 20px;margin:0'>
<div style='max-width:520px;margin:0 auto'>
  <div style='background:linear-gradient(135deg,#14162B,#201F47);border-radius:16px 16px 0 0;padding:32px;text-align:center'>
    <h1 style='color:#fff;margin:0;font-size:22px;font-weight:700'>{(periodEnd != null ? "Kündigung bestätigt" : "Zugang beendet")}</h1>
    <p style='color:rgba(255,255,255,0.75);margin:8px 0 0;font-size:14px'>GentleBook Buchungssystem</p>
  </div>
  <div style='background:#fff;border-radius:0 0 16px 16px;padding:32px;border:1px solid #e5e7eb;border-top:none'>
    <p style='font-size:15px;color:#374151;margin:0 0 12px'>Hallo {firstName},</p>
    <p style='font-size:14px;color:#6b7280;line-height:1.6;margin:0 0 20px'>{periodText}</p>
    <p style='font-size:13px;color:#9ca3af;line-height:1.6;margin:0'>
      Möchten Sie GentleBook doch weiter nutzen? Melden Sie sich einfach bei uns.
    </p>
    <p style='font-size:11px;color:#d1d5db;margin:20px 0 0;border-top:1px solid #f3f4f6;padding-top:16px;text-align:center'>
      GentleBook · support@gentlegroup.de
    </p>
  </div>
</div>
</body>
</html>";

            var text = $@"{(periodEnd != null ? "Kündigung bestätigt" : "Zugang beendet")} – GentleBook

Hallo {firstName},

{periodText.Replace("<strong>", "").Replace("</strong>", "")}

GentleBook · support@gentlegroup.de";

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Subscription cancellation confirmation sent to {Email}", recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send subscription cancellation confirmation to {Email}", recipientEmail);
            throw;
        }
    }

    /// <summary>Sent once, the first day a subscription is PastDue — warns the grace period is running.</summary>
    public async Task SendDunningWarningEmailAsync(string recipientEmail, string firstName, string planName, int gracePeriodDays)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = "Zahlung fehlgeschlagen – bitte prüfen Sie Ihre Zahlungsdaten";

            var html = $@"<!DOCTYPE html>
<html lang='de'>
<head><meta charset='UTF-8'></head>
<body style='font-family:Inter,Arial,sans-serif;background:#f4f4f5;padding:40px 20px;margin:0'>
<div style='max-width:520px;margin:0 auto'>
  <div style='background:linear-gradient(135deg,#d97706,#f59e0b);border-radius:16px 16px 0 0;padding:32px;text-align:center'>
    <h1 style='color:#fff;margin:0;font-size:22px;font-weight:700'>Zahlung fehlgeschlagen</h1>
    <p style='color:rgba(255,255,255,0.75);margin:8px 0 0;font-size:14px'>GentleBook Buchungssystem</p>
  </div>
  <div style='background:#fff;border-radius:0 0 16px 16px;padding:32px;border:1px solid #e5e7eb;border-top:none'>
    <p style='font-size:15px;color:#374151;margin:0 0 12px'>Hallo {firstName},</p>
    <p style='font-size:14px;color:#6b7280;line-height:1.6;margin:0 0 20px'>
      Die letzte SEPA-Abbuchung für Ihren <strong>{planName}</strong>-Plan ist fehlgeschlagen. Ihr Zugang ist vorübergehend eingeschränkt.
      Bitte kontaktieren Sie uns, damit wir das gemeinsam klären können. Sollte die Zahlung nicht innerhalb von
      <strong>{gracePeriodDays} Tagen</strong> erfolgreich sein, wird Ihr Abonnement automatisch gekündigt.
    </p>
    <p style='font-size:13px;color:#9ca3af;line-height:1.6;margin:0'>
      Kontaktieren Sie uns per E-Mail oder WhatsApp, wir helfen gerne weiter.
    </p>
    <p style='font-size:11px;color:#d1d5db;margin:20px 0 0;border-top:1px solid #f3f4f6;padding-top:16px;text-align:center'>
      GentleBook · support@gentlegroup.de
    </p>
  </div>
</div>
</body>
</html>";

            var text = $@"Zahlung fehlgeschlagen – GentleBook

Hallo {firstName},

Die letzte SEPA-Abbuchung für Ihren {planName}-Plan ist fehlgeschlagen. Ihr Zugang ist vorübergehend eingeschränkt.
Sollte die Zahlung nicht innerhalb von {gracePeriodDays} Tagen erfolgreich sein, wird Ihr Abonnement automatisch gekündigt.

Kontaktieren Sie uns per E-Mail oder WhatsApp, wir helfen gerne weiter.

GentleBook · support@gentlegroup.de";

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Dunning warning email sent to {Email}", recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send dunning warning email to {Email}", recipientEmail);
            throw;
        }
    }

    /// <summary>Sent once, when the dunning grace period expires and the subscription is auto-cancelled.</summary>
    public async Task SendDunningFinalCancellationEmailAsync(string recipientEmail, string firstName, string planName)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = "Ihr GentleBook-Abonnement wurde gekündigt";

            var html = $@"<!DOCTYPE html>
<html lang='de'>
<head><meta charset='UTF-8'></head>
<body style='font-family:Inter,Arial,sans-serif;background:#f4f4f5;padding:40px 20px;margin:0'>
<div style='max-width:520px;margin:0 auto'>
  <div style='background:linear-gradient(135deg,#14162B,#201F47);border-radius:16px 16px 0 0;padding:32px;text-align:center'>
    <h1 style='color:#fff;margin:0;font-size:22px;font-weight:700'>Abonnement gekündigt</h1>
    <p style='color:rgba(255,255,255,0.75);margin:8px 0 0;font-size:14px'>GentleBook Buchungssystem</p>
  </div>
  <div style='background:#fff;border-radius:0 0 16px 16px;padding:32px;border:1px solid #e5e7eb;border-top:none'>
    <p style='font-size:15px;color:#374151;margin:0 0 12px'>Hallo {firstName},</p>
    <p style='font-size:14px;color:#6b7280;line-height:1.6;margin:0 0 20px'>
      Da die ausstehende Zahlung für Ihren <strong>{planName}</strong>-Plan innerhalb der Frist nicht beglichen wurde,
      haben wir Ihr Abonnement gekündigt. Es werden keine weiteren Zahlungen eingezogen.
    </p>
    <p style='font-size:13px;color:#9ca3af;line-height:1.6;margin:0'>
      Möchten Sie GentleBook wieder nutzen? Melden Sie sich einfach bei uns – wir richten Ihr Abo gerne neu ein.
    </p>
    <p style='font-size:11px;color:#d1d5db;margin:20px 0 0;border-top:1px solid #f3f4f6;padding-top:16px;text-align:center'>
      GentleBook · support@gentlegroup.de
    </p>
  </div>
</div>
</body>
</html>";

            var text = $@"Abonnement gekündigt – GentleBook

Hallo {firstName},

Da die ausstehende Zahlung für Ihren {planName}-Plan innerhalb der Frist nicht beglichen wurde,
haben wir Ihr Abonnement gekündigt. Es werden keine weiteren Zahlungen eingezogen.

Möchten Sie GentleBook wieder nutzen? Melden Sie sich einfach bei uns.

GentleBook · support@gentlegroup.de";

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Dunning final-cancellation email sent to {Email}", recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send dunning final-cancellation email to {Email}", recipientEmail);
            throw;
        }
    }

    /// <summary>Warns the tenant seven days before operational data is permanently removed.</summary>
    public async Task SendRetentionDeletionWarningAsync(string recipientEmail, string firstName, DateTime deletionDate)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
        message.To.Add(new MailboxAddress(firstName, recipientEmail));
        message.Subject = "Ihre GentleBook-Daten werden in Kürze gelöscht";

        var formattedDate = deletionDate.ToString("dd.MM.yyyy");
        var text = $@"Hallo {firstName},

die 30-tägige Aufbewahrungsfrist nach Ende Ihrer GentleBook-Testphase oder Ihres Abonnements endet am {formattedDate}.
Danach werden die operativen Account-, Kunden-, Mitarbeiter-, Buchungs- und Konfigurationsdaten dauerhaft gelöscht oder anonymisiert.

Bitte wenden Sie sich vor diesem Datum an support@gentlegroup.de, wenn Sie einen Datenexport benötigen.
Rechnungen und gesetzlich aufzubewahrende Zahlungsnachweise bleiben getrennt erhalten.

GentleBook · support@gentlegroup.de";

        var html = $@"<!DOCTYPE html><html lang='de'><head><meta charset='UTF-8'></head>
<body style='font-family:Inter,Arial,sans-serif;background:#f4f4f5;padding:40px 20px;margin:0'>
<div style='max-width:520px;margin:0 auto;background:#fff;border-radius:16px;padding:32px;border:1px solid #e5e7eb'>
<h1 style='color:#14162B;font-size:22px;margin:0 0 20px'>Bevorstehende Datenlöschung</h1>
<p style='color:#374151'>Hallo {firstName},</p>
<p style='color:#6b7280;line-height:1.6'>Die 30-tägige Aufbewahrungsfrist endet am <strong>{formattedDate}</strong>. Danach werden Ihre operativen GentleBook-Daten dauerhaft gelöscht oder anonymisiert.</p>
<p style='color:#6b7280;line-height:1.6'>Benötigen Sie einen Export, schreiben Sie bitte vor diesem Datum an <a href='mailto:support@gentlegroup.de'>support@gentlegroup.de</a>. Rechnungen und gesetzlich aufzubewahrende Zahlungsnachweise bleiben getrennt erhalten.</p>
</div></body></html>";

        message.Body = new BodyBuilder { HtmlBody = html, TextBody = text }.ToMessageBody();
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
        await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
        _logger.LogInformation("Retention deletion warning sent to {Email}", recipientEmail);
    }

    /// <summary>Sends the mandatory legal-confirmation step before a trial can begin.</summary>
    public async Task SendTrialActivationInvitationAsync(
        string recipientEmail,
        string firstName,
        string tenantName,
        string tenantSlug,
        string activationUrl,
        DateTime invitationExpiresAt)
    {
        var safeFirstName = System.Net.WebUtility.HtmlEncode(firstName);
        var safeTenantName = System.Net.WebUtility.HtmlEncode(tenantName);
        var safeActivationUrl = System.Net.WebUtility.HtmlEncode(activationUrl);
        var bookingUrl = $"{FrontendUrl}/booking/{tenantSlug}";
        var expires = invitationExpiresAt.ToString("dd.MM.yyyy");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
        message.To.Add(new MailboxAddress(firstName, recipientEmail));
        message.Subject = "Bitte bestätigen: Ihr GentleBook-Testzugang";
        message.Body = new BodyBuilder
        {
            TextBody = $@"Hallo {firstName},

wie besprochen haben wir GentleBook für {tenantName} vorbereitet.

Buchungsseite: {bookingUrl}
Testdauer nach Freischaltung: 14 Tage
Zahlungsmethode: nicht erforderlich
Automatische kostenpflichtige Verlängerung: nein

Bitte bestätigen Sie vor Beginn des Tests Ihre Unternehmereigenschaft, die AGB, die Datenschutzerklärung und den Vertrag zur Auftragsverarbeitung:
{activationUrl}

Der Bestätigungslink ist bis {expires} gültig. Ihre Bestätigung startet die Testphase noch nicht. GentleBook prüft anschließend die vorbereitete Einrichtung und gibt den Zugang gesondert frei. Beginn, Ende und Zugangsdaten erhalten Sie mit der Freischaltungsbestätigung.

GentleBook · support@gentlegroup.de",
            HtmlBody = $@"<!DOCTYPE html><html lang='de'><head><meta charset='UTF-8'></head>
<body style='font-family:Inter,Arial,sans-serif;background:#f4f4f5;padding:40px 20px;margin:0'>
<div style='max-width:560px;margin:0 auto;background:#fff;border-radius:16px;padding:32px;border:1px solid #e5e7eb'>
<h1 style='color:#14162B;font-size:22px;margin:0 0 20px'>Ihr GentleBook-Testzugang ist vorbereitet</h1>
<p style='color:#374151'>Hallo {safeFirstName},</p>
<p style='color:#6b7280;line-height:1.6'>Wie besprochen haben wir GentleBook für <strong>{safeTenantName}</strong> vorbereitet. Ihre Bestätigung startet die Testphase noch nicht. GentleBook prüft anschließend die Einrichtung und gibt den Zugang gesondert frei. Eine Zahlungsmethode ist nicht erforderlich und es erfolgt keine automatische kostenpflichtige Verlängerung.</p>
<p style='margin:28px 0'><a href='{safeActivationUrl}' style='display:inline-block;background:#6355E4;color:#fff;text-decoration:none;padding:13px 20px;border-radius:10px;font-weight:700'>Testbedingungen und AVV bestätigen</a></p>
<p style='color:#6b7280;font-size:13px;line-height:1.6'>Der Link ist bis {expires} gültig. Beginn und Ende der 14-tägigen Testphase sowie Ihre Zugangsdaten werden erst mit der separaten Freischaltungsbestätigung mitgeteilt.</p>
</div></body></html>"
        }.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
        await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
        _logger.LogInformation("Trial activation invitation sent to {Email}", recipientEmail);
    }

    /// <summary>
    /// Notifies the TenantAdmin that their plan request was declined.
    /// </summary>
    public async Task SendPlanDeclinedEmailAsync(string recipientEmail, string firstName, string planName)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = "Ihre Plan-Anfrage – GentleBook";

            var html = $@"<!DOCTYPE html>
<html lang='de'>
<head><meta charset='UTF-8'></head>
<body style='font-family:Inter,Arial,sans-serif;background:#f4f4f5;padding:40px 20px;margin:0'>
<div style='max-width:520px;margin:0 auto'>
  <div style='background:linear-gradient(135deg,#14162B,#201F47);border-radius:16px 16px 0 0;padding:32px;text-align:center'>
    <h1 style='color:#fff;margin:0;font-size:22px;font-weight:700'>Zu Ihrer Plan-Anfrage</h1>
    <p style='color:rgba(255,255,255,0.75);margin:8px 0 0;font-size:14px'>GentleBook Buchungssystem</p>
  </div>
  <div style='background:#fff;border-radius:0 0 16px 16px;padding:32px;border:1px solid #e5e7eb;border-top:none'>
    <p style='font-size:15px;color:#374151;margin:0 0 12px'>Hallo {firstName},</p>
    <p style='font-size:14px;color:#6b7280;line-height:1.6;margin:0 0 20px'>
      Ihre Anfrage für den <strong>{planName}</strong>-Plan konnte leider nicht aktiviert werden.
      Bitte melden Sie sich bei unserem Support, damit wir gemeinsam eine Lösung finden.
    </p>
    <div style='text-align:center;margin:24px 0'>
      <a href='mailto:support@gentlegroup.de' style='background:#14162B;color:#fff;text-decoration:none;padding:14px 36px;border-radius:12px;font-weight:700;font-size:15px;display:inline-block'>
        Support kontaktieren
      </a>
    </div>
    <p style='font-size:11px;color:#d1d5db;margin:20px 0 0;border-top:1px solid #f3f4f6;padding-top:16px;text-align:center'>
      GentleBook · support@gentlegroup.de
    </p>
  </div>
</div>
</body>
</html>";

            var text = $@"Zu Ihrer Plan-Anfrage – GentleBook

Hallo {firstName},

Ihre Anfrage für den {planName}-Plan konnte leider nicht aktiviert werden.
Bitte kontaktieren Sie support@gentlegroup.de für eine Lösung.

GentleBook";

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Plan-declined email sent to {Email} ({Plan})", recipientEmail, planName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send plan-declined email to {Email}", recipientEmail);
            throw;
        }
    }

    /// <summary>
    /// Sends the "Meine Buchungen" magic link to a customer.
    /// </summary>
    public async Task SendMyBookingsLinkAsync(string recipientEmail, string firstName, string portalUrl)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = "Ihre Buchungsübersicht – GentleBook";

            var html = $@"<!DOCTYPE html>
<html lang='de'>
<head><meta charset='UTF-8'></head>
<body style='font-family:Inter,Arial,sans-serif;background:#f4f4f5;padding:40px 20px;margin:0'>
<div style='max-width:520px;margin:0 auto'>
  <div style='background:linear-gradient(135deg,#6355E4,#17A398);border-radius:16px 16px 0 0;padding:32px;text-align:center'>
    <div style='width:56px;height:56px;background:rgba(255,255,255,0.2);border-radius:14px;display:inline-flex;align-items:center;justify-content:center;margin-bottom:16px'>
      <span style='font-size:28px'>📅</span>
    </div>
    <h1 style='color:#fff;margin:0;font-size:22px;font-weight:700'>Ihre Buchungsübersicht</h1>
    <p style='color:rgba(255,255,255,0.7);margin:8px 0 0;font-size:14px'>GentleBook Buchungssystem</p>
  </div>
  <div style='background:#fff;border-radius:0 0 16px 16px;padding:32px;border:1px solid #e5e7eb;border-top:none'>
    <p style='font-size:15px;color:#374151;margin:0 0 12px'>Hallo{(string.IsNullOrWhiteSpace(firstName) ? "" : $" {firstName}")},</p>
    <p style='font-size:14px;color:#6b7280;line-height:1.6;margin:0 0 24px'>
      Sie haben einen Zugriffslink für Ihre Buchungsübersicht angefordert. Klicken Sie auf den Button, um Ihre Termine zu sehen oder zu stornieren.
    </p>
    <div style='text-align:center;margin:28px 0'>
      <a href='{portalUrl}' style='background:linear-gradient(135deg,#6355E4,#17A398);color:#fff;text-decoration:none;padding:14px 36px;border-radius:12px;font-weight:700;font-size:15px;display:inline-block;box-shadow:0 4px 14px rgba(99,85,228,0.3)'>
        Meine Buchungen ansehen →
      </a>
    </div>
    <div style='background:#fef3c7;border:1px solid #fde68a;border-radius:10px;padding:14px 16px;margin:24px 0'>
      <p style='font-size:13px;color:#92400e;margin:0'>
        ⏱️ <strong>Hinweis:</strong> Dieser Link ist nur <strong>1 Stunde</strong> gültig. Danach können Sie jederzeit einen neuen anfordern.
      </p>
    </div>
    <p style='font-size:13px;color:#9ca3af;margin:0 0 4px'>Falls Sie diese Anfrage nicht gestellt haben, ignorieren Sie diese E-Mail einfach.</p>
    <p style='font-size:11px;color:#d1d5db;margin:20px 0 0;border-top:1px solid #f3f4f6;padding-top:16px;text-align:center'>
      GentleBook · support@gentlegroup.de
    </p>
  </div>
</div>
</body>
</html>";

            var text = $@"Ihre Buchungsübersicht – GentleBook
=====================================
Hallo{(string.IsNullOrWhiteSpace(firstName) ? "" : $" {firstName}")},

Sie haben einen Zugriffslink für Ihre Buchungsübersicht angefordert.

Link (1 Stunde gültig):
{portalUrl}

Falls Sie diese Anfrage nicht gestellt haben, ignorieren Sie diese E-Mail.

GentleBook · support@gentlegroup.de";

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("My-bookings magic link sent to {Email}", recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send my-bookings magic link to {Email}", recipientEmail);
            throw;
        }
    }

    /// <summary>
    /// Sends a premium onboarding email to a newly created TenantAdmin.
    /// </summary>
    public async Task SendWelcomeEmailAsync(
        string recipientEmail,
        string firstName,
        string setupUrl,
        string tenantName,
        string tenantSlug,
        Guid tenantId,
        string industryType,
        string plan,
        string? personalNote = null,
        DateTime? trialStartedAt = null,
        DateTime? trialEndsAt = null)
    {
        var emailLog = new EmailLog
        {
            TenantId = tenantId,
            EmailType = EmailType.Welcome,
            RecipientEmail = recipientEmail,
            Subject = $"Willkommen bei GentleBook — Ihr Buchungssystem ist bereit, {firstName}!",
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            var frontendBase = string.IsNullOrEmpty(_emailOptions.FrontendUrl)
                ? _emailOptions.BaseUrl?.Replace("/api", "") ?? "https://gentle-book-ui.vercel.app"
                : _emailOptions.FrontendUrl;
            var profileUrl  = $"{frontendBase}/booking/{tenantSlug}";
            var settingsUrl = $"{frontendBase}/admin/settings";
            var linksUrl    = $"{frontendBase}/admin/links";
            var trialPeriodBlock = trialStartedAt.HasValue && trialEndsAt.HasValue ? $"""
                        <tr>
                          <td style="background:#ffffff;padding:0 32px 24px;">
                            <div style="background:#ECFDF5;border:1px solid #A7F3D0;border-radius:12px;padding:16px 20px;color:#065F46;font-size:14px;line-height:1.6;">
                              <strong>Ihre 14-t&auml;gige Testphase ist jetzt aktiv.</strong><br>
                              Beginn: {trialStartedAt.Value:dd.MM.yyyy, HH:mm} UTC &middot; Ende: {trialEndsAt.Value:dd.MM.yyyy, HH:mm} UTC<br>
                              Keine Zahlungsmethode, keine automatische kostenpflichtige Verl&auml;ngerung. Ein Monats- oder Jahresabonnement entsteht nur durch Ihre sp&auml;tere ausdr&uuml;ckliche Tarifwahl.
                            </div>
                          </td>
                        </tr>
                """ : "";
            var trialPeriodText = trialStartedAt.HasValue && trialEndsAt.HasValue
                ? $"TESTPHASE: {trialStartedAt.Value:dd.MM.yyyy, HH:mm} UTC bis {trialEndsAt.Value:dd.MM.yyyy, HH:mm} UTC\nKeine Zahlungsmethode und keine automatische kostenpflichtige Verlängerung.\n\n"
                : "";

            // ── Industry-specific content ────────────────────────────────
            var (industryEmoji, industryLabel, step3Label) = industryType.ToLowerInvariant() switch
            {
                "beauty"              => ("💄", "Beauty-Buchungssystem",         "Template &amp; Farben abstimmen — Beauty-Vorlage empfohlen"),
                "barbershop"          => ("💈", "Barbershop-Buchungssystem",      "Template &amp; Barbershop-Stil einrichten"),
                "tattoo"              => ("🪡", "Tattoo-Studio-Buchungssystem",   "Template wählen &amp; Portfolio-Links hinzufügen"),
                "wellness" or "organic" => ("🌿", "Wellness-Buchungssystem",      "Template &amp; Ambiente gestalten — Organic-Vorlage empfohlen"),
                "corporate"           => ("🏢", "Business-Buchungssystem",        "Corporate-Template einrichten &amp; Branding anpassen"),
                _                     => ("📅", "Buchungssystem",                 "Template &amp; Design personalisieren"),
            };

            // ── Plan data ────────────────────────────────────────────────
            if (!Enum.TryParse<SubscriptionPlan>(plan, ignoreCase: true, out var parsedPlan))
                parsedPlan = SubscriptionPlan.Trial;
            var limits = PlanLimits.Get(parsedPlan);
            var planDisplayName = parsedPlan switch
            {
                SubscriptionPlan.Trial        => "Trial — 14 Tage kostenlos",
                SubscriptionPlan.Starter      => "Starter",
                SubscriptionPlan.Professional => "Professional",
                SubscriptionPlan.Agency       => "Agency",
                _                             => plan,
            };
            var planPrice      = parsedPlan == SubscriptionPlan.Agency ? "Preis auf Anfrage"
                : limits.MonthlyPrice == 0 ? "Kostenlos" : $"&euro;{limits.MonthlyPrice:0}/Monat";
            var empText        = PlanLimits.IsUnlimited(limits.MaxEmployees)         ? "Unbegrenzte Mitarbeiter"     : $"{limits.MaxEmployees} Mitarbeiter";
            var svcText        = PlanLimits.IsUnlimited(limits.MaxServices)          ? "Unbegrenzte Services"        : $"{limits.MaxServices} Services";
            var bkgText        = PlanLimits.IsUnlimited(limits.MaxBookingsPerMonth)  ? "Unbegrenzte Buchungen"       : $"{limits.MaxBookingsPerMonth} Buchungen/Monat";
            var analyticsCheck = limits.HasAnalytics ? "&#10003;" : "&#8722;";
            var apiCheck       = limits.HasApiAccess  ? "&#10003;" : "&#8722;";

            // ── Upgrade section (only if not Agency) ─────────────────────
            var nextPlan = parsedPlan switch
            {
                SubscriptionPlan.Trial        => SubscriptionPlan.Starter,
                SubscriptionPlan.Starter      => SubscriptionPlan.Professional,
                SubscriptionPlan.Professional => SubscriptionPlan.Agency,
                _                             => (SubscriptionPlan?)null,
            };
            var nextLimits      = nextPlan.HasValue ? PlanLimits.Get(nextPlan.Value) : null;
            var nextEmpText     = nextLimits != null ? (PlanLimits.IsUnlimited(nextLimits.MaxEmployees) ? "Unbegrenzt" : nextLimits.MaxEmployees.ToString()) : "";
            var nextSvcText     = nextLimits != null ? (PlanLimits.IsUnlimited(nextLimits.MaxServices) ? "Unbegrenzt" : nextLimits.MaxServices.ToString()) : "";
            var nextBkgText     = nextLimits != null ? (PlanLimits.IsUnlimited(nextLimits.MaxBookingsPerMonth) ? "Unbegrenzt" : nextLimits.MaxBookingsPerMonth.ToString()) : "";
            var nextPlanName    = nextPlan switch { SubscriptionPlan.Starter => "Starter", SubscriptionPlan.Professional => "Professional", SubscriptionPlan.Agency => "Agency", _ => "" };
            var nextPrice       = nextLimits != null ? $"&euro;{nextLimits.MonthlyPrice:0}/Monat" : "";
            var upgradeSubject  = Uri.EscapeDataString($"Upgrade-Anfrage: von {planDisplayName} auf {nextPlanName}");
            var upgradeBody     = Uri.EscapeDataString($"Hallo,\n\nich möchte mein GentleBook-Paket von {planDisplayName} auf {nextPlanName} upgraden.\n\nMein Buchungssystem: {tenantName}\n\nBitte kontaktieren Sie mich für die weiteren Schritte.\n\nVielen Dank!");
            var upgradeHref     = $"mailto:support@gentlegroup.de?subject={upgradeSubject}&body={upgradeBody}";

            // ── Personal note block ──────────────────────────────────────
            var personalNoteBlock = string.IsNullOrWhiteSpace(personalNote) ? "" : $"""
                        <!-- PERSONAL NOTE -->
                        <tr>
                          <td style="background:#ffffff;padding:0 32px 24px;">
                            <table cellpadding="0" cellspacing="0" width="100%" style="background:#FFFBEB;border-radius:12px;border-left:4px solid #F59E0B;padding:18px 22px;">
                              <tr>
                                <td>
                                  <p style="margin:0 0 6px;color:#92400E;font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:0.8px;">&#128276; Persönliche Nachricht von Berkcan</p>
                                  <p style="margin:0;color:#78350F;font-size:14px;line-height:1.6;">{personalNote}</p>
                                </td>
                              </tr>
                            </table>
                          </td>
                        </tr>
                """;

            // ── Upgrade section block ────────────────────────────────────
            var analyticsRow = nextLimits != null && nextLimits.HasAnalytics && !limits.HasAnalytics
                ? "<tr style=\"background:#FAFAFA;\"><td style=\"padding:10px 16px;color:#888;font-size:13px;\">Analysen: &#8722;</td><td style=\"padding:10px 16px;color:#059669;font-size:13px;font-weight:600;\">Analysen: &#10003;</td></tr>"
                : "";
            var apiRow = nextLimits != null && nextLimits.HasApiAccess && !limits.HasApiAccess
                ? "<tr style=\"background:#FAFAFA;\"><td style=\"padding:10px 16px;color:#888;font-size:13px;\">API-Zugang: &#8722;</td><td style=\"padding:10px 16px;color:#059669;font-size:13px;font-weight:600;\">API-Zugang: &#10003;</td></tr>"
                : "";

            var upgradeBlock = nextLimits == null ? "" : $"""
                        <!-- UPGRADE SECTION -->
                        <tr>
                          <td style="background:#F8F8FF;padding:28px 32px;">
                            <p style="margin:0 0 6px;color:#1E1E1E;font-size:15px;font-weight:700;">&#11088; Holen Sie noch mehr heraus</p>
                            <p style="margin:0 0 18px;color:#888;font-size:13px;">Mit dem {nextPlanName}-Paket schalten Sie weitere Funktionen frei:</p>
                            <table cellpadding="0" cellspacing="0" width="100%" style="background:#fff;border-radius:12px;border:1px solid #E5E7EB;overflow:hidden;">
                              <tr style="background:#F3F4F6;">
                                <td style="padding:10px 16px;color:#6B7280;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:0.8px;width:50%;">Ihr Paket</td>
                                <td style="padding:10px 16px;color:#6355E4;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:0.8px;">{nextPlanName} &mdash; {nextPrice}</td>
                              </tr>
                              <tr>
                                <td style="padding:10px 16px;color:#888;font-size:13px;border-top:1px solid #F3F4F6;">{empText}</td>
                                <td style="padding:10px 16px;color:#1E1E1E;font-size:13px;font-weight:600;border-top:1px solid #F3F4F6;">{nextEmpText} Mitarbeiter</td>
                              </tr>
                              <tr style="background:#FAFAFA;">
                                <td style="padding:10px 16px;color:#888;font-size:13px;">{svcText}</td>
                                <td style="padding:10px 16px;color:#1E1E1E;font-size:13px;font-weight:600;">{nextSvcText} Services</td>
                              </tr>
                              <tr>
                                <td style="padding:10px 16px;color:#888;font-size:13px;">{bkgText}</td>
                                <td style="padding:10px 16px;color:#1E1E1E;font-size:13px;font-weight:600;">{nextBkgText} Buchungen/Monat</td>
                              </tr>
                              {analyticsRow}
                              {apiRow}
                            </table>
                            <div style="margin-top:16px;text-align:center;">
                              <a href="{upgradeHref}" style="display:inline-block;background:linear-gradient(135deg,#6355E4,#5448C1);color:#fff;text-decoration:none;padding:12px 32px;border-radius:10px;font-weight:700;font-size:14px;">
                                Upgrade anfragen &rarr;
                              </a>
                              <p style="margin:8px 0 0;color:#AAAAAA;font-size:11px;">Einfach antworten — wir kümmern uns um den Rest.</p>
                            </div>
                          </td>
                        </tr>
                """;


            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
            message.ReplyTo.Add(new MailboxAddress("GentleBook Support", "support@gentlegroup.de"));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = $"Willkommen bei GentleBook — Ihr Buchungssystem ist bereit, {firstName}!";
            message.Headers.Add("X-Mailer", "GentleBook Mailer");
            message.Headers.Add("List-Unsubscribe", $"<mailto:noreply@gentlegroup.de?subject=unsubscribe>");

            var builder = new BodyBuilder();

            builder.HtmlBody = $"""
                <!DOCTYPE html>
                <html lang="de">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width,initial-scale=1">
                  <title>Willkommen bei GentleBook</title>
                </head>
                <body style="margin:0;padding:0;background:#F0F2F5;font-family:'Helvetica Neue',Arial,sans-serif;">
                  <table width="100%" cellpadding="0" cellspacing="0" style="background:#F0F2F5;padding:32px 16px;">
                    <tr><td align="center">
                      <table width="100%" style="max-width:580px;" cellpadding="0" cellspacing="0">

                        <!-- HEADER -->
                        <tr>
                          <td style="background:linear-gradient(135deg,#14162B 0%,#201F47 60%,#14162B 100%);border-radius:20px 20px 0 0;padding:44px 32px 40px;text-align:center;">
                            <div style="display:inline-block;width:72px;height:72px;background:rgba(255,255,255,0.12);border-radius:50%;line-height:72px;font-size:36px;margin-bottom:18px;">{industryEmoji}</div>
                            <h1 style="margin:0;color:#ffffff;font-size:27px;font-weight:700;letter-spacing:-0.5px;">Herzlich willkommen!</h1>
                            <p style="margin:10px 0 0;color:rgba(255,255,255,0.7);font-size:15px;">Ihr {industryLabel} ist einsatzbereit</p>
                          </td>
                        </tr>

                        <!-- GREETING -->
                        <tr>
                          <td style="background:#ffffff;padding:32px 32px 20px;">
                            <p style="margin:0 0 10px;color:#1E1E1E;font-size:16px;">Hallo <strong>{firstName}</strong>,</p>
                            <p style="margin:0;color:#555;font-size:15px;line-height:1.7;">
                              Ihr persönliches Online-Buchungssystem <strong>{tenantName}</strong> wurde erfolgreich eingerichtet und ist ab sofort aktiv.
                              Kunden können ab jetzt direkt online bei Ihnen buchen &mdash; rund um die Uhr, ohne Telefonat.
                            </p>
                          </td>
                        </tr>

                        {personalNoteBlock}

                        {trialPeriodBlock}

                        <!-- PACKAGE -->
                        <tr>
                          <td style="background:#ffffff;padding:0 32px 28px;">
                            <div style="background:#F8FAFF;border-radius:14px;border:1px solid #E5E7EB;overflow:hidden;">
                              <div style="background:linear-gradient(135deg,#6355E4,#5448C1);padding:14px 20px;">
                                <p style="margin:0;color:#fff;font-size:13px;font-weight:700;text-transform:uppercase;letter-spacing:1px;">&#128230; Ihr Paket</p>
                                <p style="margin:4px 0 0;color:rgba(255,255,255,0.85);font-size:18px;font-weight:700;">{planDisplayName} &mdash; {planPrice}</p>
                              </div>
                              <table cellpadding="0" cellspacing="0" width="100%">
                                <tr>
                                  <td style="padding:12px 20px;color:#374151;font-size:13px;width:50%;border-right:1px solid #E5E7EB;border-bottom:1px solid #E5E7EB;">&#10003;&nbsp; {empText}</td>
                                  <td style="padding:12px 20px;color:#374151;font-size:13px;border-bottom:1px solid #E5E7EB;">&#10003;&nbsp; {svcText}</td>
                                </tr>
                                <tr>
                                  <td style="padding:12px 20px;color:#374151;font-size:13px;border-right:1px solid #E5E7EB;border-bottom:1px solid #E5E7EB;">&#10003;&nbsp; {bkgText}</td>
                                  <td style="padding:12px 20px;color:#374151;font-size:13px;border-bottom:1px solid #E5E7EB;">&#10003;&nbsp; Eigene Buchungsseite</td>
                                </tr>
                                <tr>
                                  <td style="padding:12px 20px;color:#374151;font-size:13px;border-right:1px solid #E5E7EB;">&#10003;&nbsp; E-Mail-Benachrichtigungen</td>
                                  <td style="padding:12px 20px;color:{(limits.HasAnalytics ? "#059669" : "#9CA3AF")};font-size:13px;">{analyticsCheck}&nbsp; Analysen &amp; Statistiken</td>
                                </tr>
                              </table>
                            </div>
                          </td>
                        </tr>

                        <!-- CTA BUTTON -->
                        <tr>
                          <td style="background:#ffffff;padding:0 32px 36px;text-align:center;">
                            <a href="{setupUrl}"
                               style="display:inline-block;background:linear-gradient(135deg,#6355E4,#17A398);color:#fff;text-decoration:none;padding:18px 52px;border-radius:14px;font-weight:700;font-size:17px;letter-spacing:0.3px;box-shadow:0 6px 20px rgba(99,85,228,0.35);">
                              &#128274; Passwort festlegen &rarr;
                            </a>
                            <p style="margin:12px 0 0;color:#AAAAAA;font-size:12px;">
                              Link g&uuml;ltig f&uuml;r 72 Stunden &middot; Nur einmal verwendbar
                            </p>
                          </td>
                        </tr>

                        <!-- DIVIDER -->
                        <tr>
                          <td style="background:#ffffff;padding:0 32px;">
                            <div style="border-top:1px solid #E5E7EB;"></div>
                          </td>
                        </tr>

                        <!-- STEPS -->
                        <tr>
                          <td style="background:#ffffff;padding:28px 32px;">
                            <p style="margin:0 0 20px;color:#1E1E1E;font-size:15px;font-weight:700;">&#128203; Ihre ersten 5 Schritte</p>
                            <table cellpadding="0" cellspacing="0" width="100%">

                              <tr>
                                <td valign="top" style="width:36px;padding:0 0 18px;">
                                  <div style="width:30px;height:30px;background:linear-gradient(135deg,#6355E4,#17A398);border-radius:50%;text-align:center;line-height:30px;font-size:13px;font-weight:700;color:#fff;">1</div>
                                </td>
                                <td style="padding:0 0 18px 10px;">
                                  <p style="margin:0 0 3px;color:#1E1E1E;font-size:14px;font-weight:600;">Passwort festlegen &amp; einloggen</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.5;">
                                    Klicken Sie auf den Button oben, legen Sie Ihr Passwort fest und melden Sie sich an.
                                  </p>
                                </td>
                              </tr>

                              <tr>
                                <td valign="top" style="width:36px;padding:0 0 18px;">
                                  <div style="width:30px;height:30px;background:linear-gradient(135deg,#6355E4,#17A398);border-radius:50%;text-align:center;line-height:30px;font-size:13px;font-weight:700;color:#fff;">2</div>
                                </td>
                                <td style="padding:0 0 18px 10px;">
                                  <p style="margin:0 0 3px;color:#1E1E1E;font-size:14px;font-weight:600;">Profil &amp; Branding einrichten</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.5;">
                                    Laden Sie Ihr Logo hoch, wählen Sie Ihre Farbe und passen Sie Profiltext &amp; Öffnungszeiten an.
                                    <a href="{settingsUrl}" style="color:#6355E4;text-decoration:none;">&rarr; Einstellungen</a>
                                  </p>
                                </td>
                              </tr>

                              <tr>
                                <td valign="top" style="width:36px;padding:0 0 18px;">
                                  <div style="width:30px;height:30px;background:linear-gradient(135deg,#6355E4,#17A398);border-radius:50%;text-align:center;line-height:30px;font-size:13px;font-weight:700;color:#fff;">3</div>
                                </td>
                                <td style="padding:0 0 18px 10px;">
                                  <p style="margin:0 0 3px;color:#1E1E1E;font-size:14px;font-weight:600;">{step3Label}</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.5;">
                                    Unter <a href="{linksUrl}" style="color:#6355E4;text-decoration:none;">Meine Links</a> können Sie Instagram, WhatsApp &amp; Co. hinzufügen
                                    und ein passendes Design-Template wählen.
                                  </p>
                                </td>
                              </tr>

                              <tr>
                                <td valign="top" style="width:36px;padding:0 0 18px;">
                                  <div style="width:30px;height:30px;background:linear-gradient(135deg,#6355E4,#17A398);border-radius:50%;text-align:center;line-height:30px;font-size:13px;font-weight:700;color:#fff;">4</div>
                                </td>
                                <td style="padding:0 0 18px 10px;">
                                  <p style="margin:0 0 3px;color:#1E1E1E;font-size:14px;font-weight:600;">Leistungen &amp; Mitarbeiter anlegen</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.5;">
                                    Erstellen Sie Ihre Services (Name, Preis, Dauer) und tragen Sie Ihre Mitarbeiter mit Verfügbarkeiten ein.
                                  </p>
                                </td>
                              </tr>

                              <tr>
                                <td valign="top" style="width:36px;">
                                  <div style="width:30px;height:30px;background:linear-gradient(135deg,#6355E4,#17A398);border-radius:50%;text-align:center;line-height:30px;font-size:13px;font-weight:700;color:#fff;">5</div>
                                </td>
                                <td style="padding:0 0 0 10px;">
                                  <p style="margin:0 0 3px;color:#1E1E1E;font-size:14px;font-weight:600;">Buchungslink teilen</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.5;">
                                    Teilen Sie <a href="{profileUrl}" style="color:#6355E4;text-decoration:none;">{profileUrl}</a>
                                    per WhatsApp, Instagram Bio oder als QR-Code &mdash; und die ersten Buchungen kommen rein.
                                  </p>
                                </td>
                              </tr>

                            </table>
                          </td>
                        </tr>

                        {upgradeBlock}

                        <!-- SUPPORT -->
                        <tr>
                          <td style="background:#ffffff;padding:24px 32px 32px;">
                            <table cellpadding="0" cellspacing="0" width="100%" style="background:#EEEBFC;border-radius:12px;padding:20px 24px;">
                              <tr>
                                <td>
                                  <p style="margin:0 0 6px;color:#1E1E1E;font-size:14px;font-weight:700;">&#128172; Fragen? Wir sind f&uuml;r Sie da!</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.6;">
                                    Bei Fragen oder W&uuml;nschen schreiben Sie uns einfach &mdash; wir antworten innerhalb eines Werktages.<br>
                                    <a href="mailto:support@gentlegroup.de" style="color:#6355E4;font-weight:600;text-decoration:none;">support@gentlegroup.de</a>
                                  </p>
                                </td>
                              </tr>
                            </table>
                          </td>
                        </tr>

                        <!-- FOOTER -->
                        <tr>
                          <td style="background:#F3F4F6;border-radius:0 0 20px 20px;padding:20px 32px;text-align:center;">
                            <p style="margin:0 0 4px;color:#AAA;font-size:12px;">
                              Diese E-Mail wurde automatisch von GentleBook versandt.
                            </p>
                            <p style="margin:0;color:#AAA;font-size:12px;">
                              &copy; {DateTime.UtcNow.Year} GentleGroup &middot;
                              <a href="mailto:support@gentlegroup.de" style="color:#6355E4;text-decoration:none;">support@gentlegroup.de</a>
                              &middot; <a href="{profileUrl}" style="color:#6355E4;text-decoration:none;">Ihr Profil</a>
                            </p>
                          </td>
                        </tr>

                      </table>
                    </td></tr>
                  </table>
                </body>
                </html>
                """;

            builder.TextBody = $"""
                Willkommen bei GentleBook, {firstName}!

                Ihr {industryLabel} "{tenantName}" ist einsatzbereit.

                PAKET: {planDisplayName} — {planPrice}

                {trialPeriodText}PASSWORT FESTLEGEN (72h gültig):
                {setupUrl}

                IHRE ERSTEN 5 SCHRITTE:
                1. Passwort festlegen — Klicken Sie auf den Link oben.
                2. Profil & Branding einrichten → {settingsUrl}
                3. {step3Label} → {linksUrl}
                4. Leistungen & Mitarbeiter anlegen
                5. Buchungslink teilen: {profileUrl}

                ---
                Fragen? Wir sind für Sie da!
                support@gentlegroup.de

                © {DateTime.UtcNow.Year} GentleGroup
                """;

            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            emailLog.Status = EmailStatus.Sent;
            emailLog.SentAt = DateTime.UtcNow;
            _logger.LogInformation("Welcome email sent to {Email}", recipientEmail);
        }
        catch (Exception ex)
        {
            emailLog.Status = EmailStatus.Failed;
            emailLog.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to send welcome email to {Email}", recipientEmail);
        }

        try
        {
            if (tenantId != default)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
                db.EmailLogs.Add(emailLog);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception dbEx)
        {
            _logger.LogError(dbEx, "Failed to save email log for welcome email to {Email}", recipientEmail);
        }
    }

    /// <summary>
    /// Sends a trial-expiring warning email (7 or 3 days before expiry).
    /// </summary>
    public async Task SendTrialExpiringEmailAsync(string recipientEmail, string firstName, string tenantSlug, int daysLeft)
    {
        try
        {
            var frontendBase = string.IsNullOrEmpty(_emailOptions.FrontendUrl)
                ? _emailOptions.BaseUrl?.Replace("/api", "") ?? "https://gentle-book-ui.vercel.app"
                : _emailOptions.FrontendUrl;
            var subscriptionUrl = $"{frontendBase}/admin/subscription";

            var urgencyColor  = daysLeft <= 3 ? "#ef4444" : "#f59e0b";
            var urgencyBg     = daysLeft <= 3 ? "#fef2f2" : "#fffbeb";
            var urgencyBorder = daysLeft <= 3 ? "#fecaca" : "#fde68a";
            var urgencyText   = daysLeft <= 3 ? "#991b1b" : "#92400e";
            var brandTealDark = DarkenHex("#17A398");

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = $"Noch {daysLeft} Tag{(daysLeft == 1 ? "" : "e")} – Ihr GentleBook Testzeitraum läuft ab";

            var html = $"""
                <!DOCTYPE html>
                <html lang="de">
                <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
                <body style="margin:0;padding:0;background:#f4f4f5;font-family:Inter,Arial,sans-serif">
                  <table width="100%" cellpadding="0" cellspacing="0" style="background:#f4f4f5;padding:40px 20px">
                    <tr><td align="center">
                      <table width="600" cellpadding="0" cellspacing="0" style="max-width:600px;width:100%">

                        <!-- Header -->
                        <tr><td style="background:linear-gradient(135deg,#14162B 0%,#201F47 100%);border-radius:16px 16px 0 0;padding:36px 32px;text-align:center">
                          <div style="width:64px;height:64px;background:rgba(255,255,255,0.1);border-radius:16px;display:inline-flex;align-items:center;justify-content:center;margin-bottom:16px">
                            <span style="font-size:32px">⏰</span>
                          </div>
                          <h1 style="color:#fff;margin:0;font-size:24px;font-weight:700">Ihr Testzeitraum läuft ab</h1>
                          <p style="color:rgba(255,255,255,0.6);margin:8px 0 0;font-size:14px">GentleBook Buchungssystem</p>
                        </td></tr>

                        <!-- Body -->
                        <tr><td style="background:#fff;padding:36px 32px;border:1px solid #e5e7eb;border-top:none">
                          <p style="font-size:16px;color:#1e1e1e;margin:0 0 8px;font-weight:600">Hallo {firstName},</p>
                          <p style="font-size:14px;color:#6b7280;line-height:1.7;margin:0 0 28px">
                            Ihr kostenloser Testzeitraum für GentleBook endet in <strong style="color:{urgencyColor}">{daysLeft} Tag{(daysLeft == 1 ? "" : "en")}</strong>.
                            Damit Ihr Buchungssystem unterbrechungsfrei weiterläuft, upgraden Sie jetzt auf unser Monatspaket.
                          </p>

                          <!-- Countdown badge -->
                          <div style="background:{urgencyBg};border:2px solid {urgencyBorder};border-radius:14px;padding:20px 24px;margin:0 0 28px;text-align:center">
                            <p style="font-size:13px;color:{urgencyText};margin:0 0 6px;font-weight:600;text-transform:uppercase;letter-spacing:0.05em">Testzeitraum endet in</p>
                            <p style="font-size:48px;font-weight:900;color:{urgencyColor};margin:0;line-height:1">{daysLeft}</p>
                            <p style="font-size:16px;color:{urgencyText};margin:4px 0 0;font-weight:600">Tag{(daysLeft == 1 ? "" : "en")}</p>
                          </div>

                          <!-- Pricing plans -->
                          <p style="font-size:13px;font-weight:700;color:#374151;margin:0 0 12px;text-transform:uppercase;letter-spacing:0.05em">Wählen Sie Ihren Plan</p>
                          <table cellpadding="0" cellspacing="0" style="width:100%;border-spacing:0;margin:0 0 28px">
                            <tr>
                              <td style="width:33%;padding:4px">
                                <div style="background:#f9fafb;border:1px solid #e5e7eb;border-radius:12px;padding:16px;text-align:center">
                                  <p style="font-size:11px;font-weight:700;color:#6b7280;margin:0 0 4px;text-transform:uppercase">Starter</p>
                                  <p style="font-size:28px;font-weight:900;color:#1f2937;margin:0;line-height:1">€29</p>
                                  <p style="font-size:11px;color:#9ca3af;margin:2px 0 8px">/Monat</p>
                                  <p style="font-size:11px;color:#6b7280;margin:0">2 Mitarbeiter</p>
                                </div>
                              </td>
                              <td style="width:33%;padding:4px">
                                <div style="background:#17A398;border:1px solid {brandTealDark};border-radius:12px;padding:16px;text-align:center">
                                  <p style="font-size:11px;font-weight:700;color:rgba(255,255,255,0.7);margin:0 0 4px;text-transform:uppercase">Professional ⭐</p>
                                  <p style="font-size:28px;font-weight:900;color:#fff;margin:0;line-height:1">€59</p>
                                  <p style="font-size:11px;color:rgba(255,255,255,0.6);margin:2px 0 8px">/Monat</p>
                                  <p style="font-size:11px;color:rgba(255,255,255,0.8);margin:0">10 Mitarbeiter</p>
                                </div>
                              </td>
                              <td style="width:33%;padding:4px">
                                <div style="background:#f9fafb;border:1px solid #e5e7eb;border-radius:12px;padding:16px;text-align:center">
                                  <p style="font-size:11px;font-weight:700;color:#d97706;margin:0 0 4px;text-transform:uppercase">Agency</p>
                                  <p style="font-size:18px;font-weight:900;color:#1f2937;margin:0;line-height:1.2">Preis auf<br/>Anfrage</p>
                                  <p style="font-size:11px;color:#9ca3af;margin:2px 0 8px">individuell</p>
                                  <p style="font-size:11px;color:#6b7280;margin:0">Unlimited</p>
                                </div>
                              </td>
                            </tr>
                          </table>

                          <!-- CTAs -->
                          <table cellpadding="0" cellspacing="0" style="width:100%;margin:0 0 24px">
                            <tr>
                              <td style="padding-right:8px;width:50%">
                                <a href="https://wa.me/491754701892?text=Hallo%2C%20ich%20m%C3%B6chte%20GentleBook%20upgraden%20(%7B{tenantSlug}%7D)"
                                   style="display:block;background:#25D366;color:#fff;text-decoration:none;padding:14px;border-radius:12px;font-weight:700;font-size:14px;text-align:center">
                                  💬 WhatsApp
                                </a>
                              </td>
                              <td style="padding-left:8px;width:50%">
                                <a href="{subscriptionUrl}"
                                   style="display:block;background:#17A398;color:#fff;text-decoration:none;padding:14px;border-radius:12px;font-weight:700;font-size:14px;text-align:center">
                                  Plan anfragen
                                </a>
                              </td>
                            </tr>
                          </table>

                          <p style="font-size:13px;color:#9ca3af;text-align:center;margin:0">
                            Direkt zur <a href="{subscriptionUrl}" style="color:#17A398;text-decoration:none;font-weight:600">Abonnement-Seite</a>
                          </p>
                        </td></tr>

                        <!-- Footer -->
                        <tr><td style="background:#14162B;border-radius:0 0 16px 16px;padding:20px 32px;text-align:center">
                          <p style="margin:0;color:#666;font-size:12px">
                            &copy; {DateTime.UtcNow.Year} GentleGroup &middot;
                            <a href="mailto:support@gentlegroup.de" style="color:#6355E4;text-decoration:none">support@gentlegroup.de</a>
                          </p>
                        </td></tr>

                      </table>
                    </td></tr>
                  </table>
                </body>
                </html>
                """;

            var text = $"""
                Noch {daysLeft} Tag{(daysLeft == 1 ? "" : "e")} – GentleBook Testzeitraum läuft ab
                ================================================================
                Hallo {firstName},

                Ihr Testzeitraum endet in {daysLeft} Tag{(daysLeft == 1 ? "" : "en")}.

                UNSERE PLÄNE:
                Starter       €29/Monat — 2 Mitarbeiter
                Professional  €59/Monat — 10 Mitarbeiter (empfohlen)
                Agency        Preis auf Anfrage — Unbegrenzte Mitarbeiter

                Was Sie erhalten:
                ✓ Unbegrenzte Buchungen
                ✓ Mehrere Mitarbeiter-Konten
                ✓ Automatische E-Mail-Bestätigungen
                ✓ Professionelle Buchungsseite
                ✓ Priority Support & Wartung
                ✓ Alle zukünftigen Updates

                Jetzt upgraden:
                WhatsApp: https://wa.me/491754701892
                E-Mail:   support@gentlegroup.de

                Abonnement-Status: {subscriptionUrl}

                © {DateTime.UtcNow.Year} GentleGroup
                """;

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Trial expiring email sent to {Email} ({DaysLeft} days left)", recipientEmail, daysLeft);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send trial expiring email to {Email}", recipientEmail);
        }
    }

    /// <summary>
    /// Sends a trial-expired notification email with upgrade offer.
    /// </summary>
    public async Task SendTrialExpiredEmailAsync(string recipientEmail, string firstName, string tenantSlug)
    {
        try
        {
            var frontendBase = string.IsNullOrEmpty(_emailOptions.FrontendUrl)
                ? _emailOptions.BaseUrl?.Replace("/api", "") ?? "https://gentle-book-ui.vercel.app"
                : _emailOptions.FrontendUrl;
            var subscriptionUrl = $"{frontendBase}/admin/subscription";
            var brandTealDark = DarkenHex("#17A398");

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", "noreply@gentlegroup.de"));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = "Ihr GentleBook Testzeitraum ist abgelaufen";

            var html = $"""
                <!DOCTYPE html>
                <html lang="de">
                <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
                <body style="margin:0;padding:0;background:#f4f4f5;font-family:Inter,Arial,sans-serif">
                  <table width="100%" cellpadding="0" cellspacing="0" style="background:#f4f4f5;padding:40px 20px">
                    <tr><td align="center">
                      <table width="600" cellpadding="0" cellspacing="0" style="max-width:600px;width:100%">

                        <!-- Header -->
                        <tr><td style="background:linear-gradient(135deg,#14162B 0%,#201F47 100%);border-radius:16px 16px 0 0;padding:36px 32px;text-align:center">
                          <div style="width:64px;height:64px;background:rgba(239,68,68,0.2);border-radius:16px;display:inline-flex;align-items:center;justify-content:center;margin-bottom:16px">
                            <span style="font-size:32px">🔒</span>
                          </div>
                          <h1 style="color:#fff;margin:0;font-size:24px;font-weight:700">Testzeitraum abgelaufen</h1>
                          <p style="color:rgba(255,255,255,0.6);margin:8px 0 0;font-size:14px">GentleBook Buchungssystem</p>
                        </td></tr>

                        <!-- Body -->
                        <tr><td style="background:#fff;padding:36px 32px;border:1px solid #e5e7eb;border-top:none">
                          <p style="font-size:16px;color:#1e1e1e;margin:0 0 8px;font-weight:600">Hallo {firstName},</p>
                          <p style="font-size:14px;color:#6b7280;line-height:1.7;margin:0 0 12px">
                            vielen Dank, dass Sie GentleBook getestet haben! Ihre 14-t&auml;gige Testphase ist beendet. Es wurde kein kostenpflichtiges Abonnement ausgel&ouml;st und es erfolgt keine Abbuchung.
                          </p>
                          <p style="font-size:14px;color:#6b7280;line-height:1.7;margin:0 0 28px">
                            Administration und &ouml;ffentliche Buchungsseite sind jetzt eingeschr&auml;nkt. Sie k&ouml;nnen innerhalb von 30 Tagen einen Tarif mit monatlicher oder j&auml;hrlicher Abrechnung w&auml;hlen oder einen Datenexport anfragen. Danach werden operative Daten gel&ouml;scht oder anonymisiert.
                          </p>

                          <!-- Pricing plans -->
                          <p style="font-size:13px;font-weight:700;color:#374151;margin:0 0 12px;text-transform:uppercase;letter-spacing:0.05em">Wählen Sie Ihren Plan</p>
                          <table cellpadding="0" cellspacing="0" style="width:100%;border-spacing:0;margin:0 0 28px">
                            <tr>
                              <td style="width:33%;padding:4px">
                                <div style="background:#f9fafb;border:1px solid #e5e7eb;border-radius:12px;padding:16px;text-align:center">
                                  <p style="font-size:11px;font-weight:700;color:#6b7280;margin:0 0 4px;text-transform:uppercase">Starter</p>
                                  <p style="font-size:28px;font-weight:900;color:#1f2937;margin:0;line-height:1">€29</p>
                                  <p style="font-size:11px;color:#9ca3af;margin:2px 0 8px">/Monat</p>
                                  <p style="font-size:11px;color:#6b7280;margin:0">2 Mitarbeiter</p>
                                </div>
                              </td>
                              <td style="width:33%;padding:4px">
                                <div style="background:#17A398;border:1px solid {brandTealDark};border-radius:12px;padding:16px;text-align:center">
                                  <p style="font-size:11px;font-weight:700;color:rgba(255,255,255,0.7);margin:0 0 4px;text-transform:uppercase">Professional ⭐</p>
                                  <p style="font-size:28px;font-weight:900;color:#fff;margin:0;line-height:1">€59</p>
                                  <p style="font-size:11px;color:rgba(255,255,255,0.6);margin:2px 0 8px">/Monat</p>
                                  <p style="font-size:11px;color:rgba(255,255,255,0.8);margin:0">10 Mitarbeiter</p>
                                </div>
                              </td>
                              <td style="width:33%;padding:4px">
                                <div style="background:#f9fafb;border:1px solid #e5e7eb;border-radius:12px;padding:16px;text-align:center">
                                  <p style="font-size:11px;font-weight:700;color:#d97706;margin:0 0 4px;text-transform:uppercase">Agency</p>
                                  <p style="font-size:18px;font-weight:900;color:#1f2937;margin:0;line-height:1.2">Preis auf<br/>Anfrage</p>
                                  <p style="font-size:11px;color:#9ca3af;margin:2px 0 8px">individuell</p>
                                  <p style="font-size:11px;color:#6b7280;margin:0">Unlimited</p>
                                </div>
                              </td>
                            </tr>
                          </table>

                          <!-- CTAs -->
                          <table cellpadding="0" cellspacing="0" style="width:100%;margin:0 0 24px">
                            <tr>
                              <td style="padding-right:8px;width:50%">
                                <a href="https://wa.me/491754701892?text=Hallo%2C%20ich%20m%C3%B6chte%20GentleBook%20upgraden%20({tenantSlug})"
                                   style="display:block;background:#25D366;color:#fff;text-decoration:none;padding:14px;border-radius:12px;font-weight:700;font-size:14px;text-align:center">
                                  💬 WhatsApp
                                </a>
                              </td>
                              <td style="padding-left:8px;width:50%">
                                <a href="{subscriptionUrl}"
                                   style="display:block;background:#17A398;color:#fff;text-decoration:none;padding:14px;border-radius:12px;font-weight:700;font-size:14px;text-align:center">
                                  Tarif w&auml;hlen
                                </a>
                              </td>
                            </tr>
                          </table>

                          <p style="font-size:13px;color:#9ca3af;text-align:center;margin:0">
                            Mehr Infos: <a href="{subscriptionUrl}" style="color:#17A398;text-decoration:none;font-weight:600">Abonnement-Seite</a>
                          </p>
                        </td></tr>

                        <!-- Footer -->
                        <tr><td style="background:#14162B;border-radius:0 0 16px 16px;padding:20px 32px;text-align:center">
                          <p style="margin:0;color:#666;font-size:12px">
                            &copy; {DateTime.UtcNow.Year} GentleGroup &middot;
                            <a href="mailto:support@gentlegroup.de" style="color:#6355E4;text-decoration:none">support@gentlegroup.de</a>
                          </p>
                        </td></tr>

                      </table>
                    </td></tr>
                  </table>
                </body>
                </html>
                """;

            var text = $"""
                Ihr GentleBook Testzeitraum ist abgelaufen
                ==========================================
                Hallo {firstName},

                vielen Dank für Ihren Test! Ihre 14-tägige Testphase ist beendet.
                Es wurde kein kostenpflichtiges Abonnement ausgelöst und es erfolgt keine Abbuchung.

                Administration und öffentliche Buchungsseite sind jetzt eingeschränkt.
                Sie können innerhalb von 30 Tagen einen Monats- oder Jahrestarif wählen oder
                einen Datenexport anfragen. Danach werden operative Daten gelöscht oder anonymisiert.

                UNSERE PLÄNE:
                Starter       €29/Monat — 2 Mitarbeiter
                Professional  €59/Monat — 10 Mitarbeiter (empfohlen)
                Agency        Preis auf Anfrage — Unbegrenzte Mitarbeiter

                Was Sie erhalten:
                ✓ Unbegrenzte Buchungen
                ✓ Mehrere Mitarbeiter-Konten
                ✓ Automatische E-Mail-Bestätigungen
                ✓ Professionelle Buchungsseite
                ✓ Priority Support & Wartung
                ✓ Alle zukünftigen Updates

                Jetzt upgraden:
                WhatsApp: https://wa.me/491754701892
                E-Mail:   support@gentlegroup.de

                Abonnement-Status: {subscriptionUrl}

                © {DateTime.UtcNow.Year} GentleGroup
                """;

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Trial expired email sent to {Email}", recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send trial expired email to {Email}", recipientEmail);
        }
    }

    public async Task SendSubscriptionRequestConfirmationAsync(string toEmail, string firstName, string planName, string tenantName)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", _emailOptions.SenderEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = $"Plan-Anfrage erhalten – {planName}";

            var planPrice = planName switch { "Starter" => "€29", "Professional" => "€59", "Agency" => "Preis auf Anfrage", _ => "" };
            var planPriceSuffix = planName == "Agency" ? "" : "/Monat";
            var brandTealDark = DarkenHex("#17A398");

            var html = $"""
                <!DOCTYPE html>
                <html lang="de">
                <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
                <body style="margin:0;padding:0;background:#f3f4f6;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif">
                  <table cellpadding="0" cellspacing="0" style="width:100%;max-width:560px;margin:32px auto">
                    <tr><td>
                      <table cellpadding="0" cellspacing="0" style="width:100%;border-radius:16px;overflow:hidden;box-shadow:0 4px 24px rgba(0,0,0,0.08)">

                        <!-- Header -->
                        <tr><td style="background:linear-gradient(135deg,#17A398 0%,{brandTealDark} 100%);padding:28px 32px;text-align:center">
                          <p style="font-size:22px;font-weight:800;color:#fff;margin:0;letter-spacing:-0.5px">✨ GentleBook</p>
                          <p style="font-size:12px;color:rgba(255,255,255,0.7);margin:4px 0 0">Buchungssystem</p>
                        </td></tr>

                        <!-- Body -->
                        <tr><td style="background:#fff;padding:36px 32px;border:1px solid #e5e7eb;border-top:none">
                          <p style="font-size:16px;color:#1e1e1e;margin:0 0 8px;font-weight:600">Hallo {firstName},</p>
                          <p style="font-size:14px;color:#6b7280;line-height:1.7;margin:0 0 24px">
                            wir haben Ihre Anfrage für den <strong style="color:#17A398">{planName}-Plan</strong> für <strong>{tenantName}</strong> erhalten.
                            Wir aktivieren Ihren Plan innerhalb von 24 Stunden und benachrichtigen Sie per E-Mail.
                          </p>

                          <!-- Requested plan box -->
                          <div style="background:#f0fdfc;border:1px solid #99f6e4;border-radius:12px;padding:20px 24px;margin:0 0 24px;text-align:center">
                            <p style="font-size:12px;font-weight:700;color:{brandTealDark};margin:0 0 4px;text-transform:uppercase">Angefragter Plan</p>
                            <p style="font-size:32px;font-weight:900;color:#17A398;margin:0;line-height:1">{planName}</p>
                            <p style="font-size:18px;color:{brandTealDark};margin:4px 0 0;font-weight:600">{planPrice}{(planPriceSuffix == "" ? "" : " / Monat")}</p>
                          </div>

                          <p style="font-size:13px;color:#9ca3af;line-height:1.6;margin:0">
                            Bei Fragen schreiben Sie uns auf
                            <a href="https://wa.me/491754701892" style="color:#17A398;font-weight:600;text-decoration:none">WhatsApp</a> oder
                            <a href="mailto:support@gentlegroup.de" style="color:#17A398;font-weight:600;text-decoration:none">support@gentlegroup.de</a>.
                          </p>
                        </td></tr>

                        <!-- Footer -->
                        <tr><td style="background:#f9fafb;border-radius:0 0 16px 16px;padding:16px 32px;text-align:center;border:1px solid #e5e7eb;border-top:none">
                          <p style="margin:0;color:#9ca3af;font-size:12px">
                            &copy; {DateTime.UtcNow.Year} GentleGroup &middot;
                            <a href="mailto:support@gentlegroup.de" style="color:#17A398;text-decoration:none">support@gentlegroup.de</a>
                          </p>
                        </td></tr>

                      </table>
                    </td></tr>
                  </table>
                </body>
                </html>
                """;

            var text = $"""
                Plan-Anfrage erhalten – {planName}
                ===================================
                Hallo {firstName},

                wir haben Ihre Anfrage für den {planName}-Plan ({planPrice}{planPriceSuffix})
                für {tenantName} erhalten.

                Aktivierung innerhalb von 24 Stunden.
                Bei Fragen: support@gentlegroup.de

                © {DateTime.UtcNow.Year} GentleGroup
                """;

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Subscription request confirmation sent to {Email}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send subscription request confirmation to {Email}", toEmail);
        }
    }

    public async Task SendSubscriptionRequestNotificationAsync(string planName, string tenantName, string contactEmail, string tenantSlug)
    {
        try
        {
            var superadminEmail = "berkcan@gentle-webdesign.com";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook System", _emailOptions.SenderEmail));
            message.To.Add(MailboxAddress.Parse(superadminEmail));
            message.Subject = $"🔔 Neue Abo-Anfrage: {tenantName} → {planName}";

            var adminUrl = $"{FrontendUrl}/superadmin/requests";
            var planPrice = planName switch { "Starter" => "€29", "Professional" => "€59", "Agency" => "Preis auf Anfrage", _ => "" };
            var planPriceSuffix = planName == "Agency" ? "" : "/Monat";
            var brandTealDark = DarkenHex("#17A398");

            var html = $"""
                <!DOCTYPE html>
                <html lang="de">
                <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
                <body style="margin:0;padding:0;background:#f3f4f6;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif">
                  <table cellpadding="0" cellspacing="0" style="width:100%;max-width:560px;margin:32px auto">
                    <tr><td>
                      <table cellpadding="0" cellspacing="0" style="width:100%;border-radius:16px;overflow:hidden;box-shadow:0 4px 24px rgba(0,0,0,0.08)">

                        <!-- Header -->
                        <tr><td style="background:linear-gradient(135deg,#17A398 0%,{brandTealDark} 100%);padding:28px 32px;text-align:center">
                          <p style="font-size:22px;font-weight:800;color:#fff;margin:0">🔔 Neue Abo-Anfrage</p>
                          <p style="font-size:12px;color:rgba(255,255,255,0.7);margin:4px 0 0">GentleBook Superadmin</p>
                        </td></tr>

                        <!-- Body -->
                        <tr><td style="background:#fff;padding:36px 32px;border:1px solid #e5e7eb;border-top:none">
                          <p style="font-size:15px;color:#1e1e1e;margin:0 0 20px">
                            <strong>{tenantName}</strong> hat eine Anfrage für den <strong style="color:#17A398">{planName}-Plan</strong> gesendet.
                          </p>

                          <table cellpadding="0" cellspacing="0" style="width:100%;margin:0 0 24px">
                            <tr>
                              <td style="padding:8px 0;font-size:13px;color:#6b7280;width:120px">System:</td>
                              <td style="padding:8px 0;font-size:13px;color:#1e1e1e;font-weight:600">{tenantName} ({tenantSlug})</td>
                            </tr>
                            <tr>
                              <td style="padding:8px 0;font-size:13px;color:#6b7280">Plan:</td>
                              <td style="padding:8px 0;font-size:13px;color:#17A398;font-weight:700">{planName} — {planPrice}{planPriceSuffix}</td>
                            </tr>
                            <tr>
                              <td style="padding:8px 0;font-size:13px;color:#6b7280">Kontakt:</td>
                              <td style="padding:8px 0;font-size:13px;color:#1e1e1e">{contactEmail}</td>
                            </tr>
                          </table>

                          <a href="{adminUrl}"
                             style="display:block;background:#17A398;color:#fff;text-decoration:none;padding:14px;border-radius:12px;font-weight:700;font-size:14px;text-align:center;margin:0 0 16px">
                            Anfrage im Superadmin verwalten →
                          </a>

                          <p style="font-size:12px;color:#9ca3af;text-align:center;margin:0">
                            Direkt zum Dashboard: <a href="{adminUrl}" style="color:#17A398;text-decoration:none">{adminUrl}</a>
                          </p>
                        </td></tr>

                        <!-- Footer -->
                        <tr><td style="background:#f9fafb;border-radius:0 0 16px 16px;padding:16px 32px;text-align:center;border:1px solid #e5e7eb;border-top:none">
                          <p style="margin:0;color:#9ca3af;font-size:12px">GentleBook Automatische Benachrichtigung &middot; {DateTime.UtcNow:dd.MM.yyyy HH:mm} UTC</p>
                        </td></tr>

                      </table>
                    </td></tr>
                  </table>
                </body>
                </html>
                """;

            var text = $"""
                Neue Abo-Anfrage: {tenantName} → {planName}
                ============================================
                System: {tenantName} ({tenantSlug})
                Plan:   {planName} — {planPrice}{planPriceSuffix}
                Kontakt: {contactEmail}

                Verwalten: {adminUrl}

                GentleBook Automatische Benachrichtigung
                """;

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Subscription request notification sent to superadmin for tenant {TenantSlug}", tenantSlug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send subscription request notification for tenant {TenantSlug}", tenantSlug);
        }
    }

    /// <summary>
    /// Alerts the owner when a Hangfire background job exhausts all its retries and lands in
    /// the Failed state — without this, a failing recurring job (dunning, invoice retry,
    /// Mollie reconciliation) only ever logs via ILogger, which nobody actively watches in
    /// production since the Hangfire dashboard itself is dev-only.
    /// </summary>
    public async Task SendJobFailureAlertAsync(string jobName, string exceptionMessage)
    {
        try
        {
            var superadminEmail = "berkcan@gentle-webdesign.com";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook System", _emailOptions.SenderEmail));
            message.To.Add(MailboxAddress.Parse(superadminEmail));
            message.Subject = $"⚠️ Hangfire-Job fehlgeschlagen: {jobName}";

            var html = $"""
                <!DOCTYPE html>
                <html lang="de">
                <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
                <body style="margin:0;padding:0;background:#f3f4f6;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif">
                  <table cellpadding="0" cellspacing="0" style="width:100%;max-width:560px;margin:32px auto">
                    <tr><td>
                      <table cellpadding="0" cellspacing="0" style="width:100%;border-radius:16px;overflow:hidden;box-shadow:0 4px 24px rgba(0,0,0,0.08)">

                        <!-- Header -->
                        <tr><td style="background:linear-gradient(135deg,#14162B 0%,#201F47 100%);padding:28px 32px;text-align:center">
                          <p style="font-size:22px;font-weight:800;color:#fff;margin:0">⚠️ Job fehlgeschlagen</p>
                          <p style="font-size:12px;color:rgba(255,255,255,0.7);margin:4px 0 0">GentleBook Hintergrundprozess</p>
                        </td></tr>

                        <!-- Body -->
                        <tr><td style="background:#fff;padding:36px 32px;border:1px solid #e5e7eb;border-top:none">
                          <p style="font-size:15px;color:#1e1e1e;margin:0 0 20px">
                            Ein Hangfire-Job hat alle konfigurierten Wiederholungsversuche aufgebraucht und ist endgültig fehlgeschlagen.
                          </p>

                          <table cellpadding="0" cellspacing="0" style="width:100%;margin:0 0 24px">
                            <tr>
                              <td style="padding:8px 0;font-size:13px;color:#6b7280;width:100px;vertical-align:top">Job:</td>
                              <td style="padding:8px 0;font-size:13px;color:#1e1e1e;font-weight:600;word-break:break-all">{jobName}</td>
                            </tr>
                            <tr>
                              <td style="padding:8px 0;font-size:13px;color:#6b7280;vertical-align:top">Fehler:</td>
                              <td style="padding:8px 0;font-size:13px;color:#dc2626;font-weight:600;word-break:break-all">{exceptionMessage}</td>
                            </tr>
                            <tr>
                              <td style="padding:8px 0;font-size:13px;color:#6b7280">Zeit:</td>
                              <td style="padding:8px 0;font-size:13px;color:#1e1e1e">{DateTime.UtcNow:dd.MM.yyyy HH:mm} UTC</td>
                            </tr>
                          </table>

                          <p style="font-size:12px;color:#9ca3af;margin:0">
                            Details/erneut ausführen: nur über direkten Serverzugriff, da das Hangfire-Dashboard in Production deaktiviert ist.
                          </p>
                        </td></tr>

                        <!-- Footer -->
                        <tr><td style="background:#f9fafb;border-radius:0 0 16px 16px;padding:16px 32px;text-align:center;border:1px solid #e5e7eb;border-top:none">
                          <p style="margin:0;color:#9ca3af;font-size:12px">GentleBook Automatische Benachrichtigung</p>
                        </td></tr>

                      </table>
                    </td></tr>
                  </table>
                </body>
                </html>
                """;

            var text = $"""
                Hangfire-Job fehlgeschlagen: {jobName}
                =======================================
                Fehler: {exceptionMessage}
                Zeit:   {DateTime.UtcNow:dd.MM.yyyy HH:mm} UTC

                GentleBook Automatische Benachrichtigung
                """;

            var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_emailOptions.SmtpUsername, _emailOptions.SmtpPassword);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("Job failure alert sent for {JobName}", jobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send job failure alert for {JobName}", jobName);
        }
    }
}
