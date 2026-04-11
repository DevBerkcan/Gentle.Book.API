using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
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
    private readonly GentleBookDbContext _context;
    private readonly ILogger<EmailService> _logger;

    public EmailService(
        IOptions<EmailOptions> emailOptions,
        GentleBookDbContext context,
        ILogger<EmailService> logger)
    {
        _emailOptions = emailOptions.Value;
        _context = context;
        _logger = logger;
    }

    public async Task SendBookingConfirmationAsync(Guid bookingId)
    {
        var booking = await _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null)
        {
            throw new ArgumentException("Booking not found");
        }

        var emailLog = new EmailLog
        {
            BookingId = bookingId,
            EmailType = EmailType.Confirmation,
            RecipientEmail = booking.Customer.Email,
            Subject = $"Ihre Buchungsbestätigung - GentleBook",
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(booking.Customer.FullName, booking.Customer.Email));
            message.Subject = emailLog.Subject;

            var builder = new BodyBuilder();
            var cancellationToken = GenerateCancellationToken(bookingId);
            var frontendBase = string.IsNullOrEmpty(_emailOptions.FrontendUrl) ? _emailOptions.BaseUrl : _emailOptions.FrontendUrl;
            var cancellationUrl = $"{frontendBase}/booking/cancel/{cancellationToken}";

            builder.HtmlBody = GetConfirmationEmailHtml(booking, cancellationUrl);
            builder.TextBody = GetConfirmationEmailText(booking, cancellationUrl);

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

            await SendInternalNotificationAsync(
                $"Neue Buchung: {booking.Customer.FullName} – {booking.Service.Name} am {booking.BookingDate:dd.MM.yyyy}",
                GetInternalBookingNotificationHtml(booking, booking.Customer, booking.Service),
                GetInternalBookingNotificationText(booking, booking.Customer, booking.Service)
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
            BookingId = booking.Id,
            EmailType = EmailType.Confirmation,
            RecipientEmail = customer.Email,
            Subject = $"Buchungsbestätigung: {service.Name} am {booking.BookingDate:dd.MM.yyyy}",
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(customer.FullName, customer.Email));
            message.Subject = emailLog.Subject;

            var builder = new BodyBuilder();
            var cancellationToken = GenerateCancellationToken(booking.Id);
            var frontendBase2 = string.IsNullOrEmpty(_emailOptions.FrontendUrl) ? _emailOptions.BaseUrl : _emailOptions.FrontendUrl;
            var cancellationUrl = $"{frontendBase2}/booking/cancel/{cancellationToken}";

            builder.HtmlBody = GetConfirmationReceiptHtml(booking, customer, service, cancellationUrl);
            builder.TextBody = GetConfirmationReceiptText(booking, customer, service, cancellationUrl);

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

            await SendInternalNotificationAsync(
                $"Buchung bestätigt: {customer.FullName} – {service.Name} am {booking.BookingDate:dd.MM.yyyy}",
                GetInternalBookingNotificationHtml(booking, customer, service),
                GetInternalBookingNotificationText(booking, customer, service)
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
            BookingId = booking.Id,
            EmailType = EmailType.Cancellation,
            RecipientEmail = customer.Email,
            Subject = $"Ihre Stornierung - GentleBook",
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(customer.FullName, customer.Email));
            message.Subject = emailLog.Subject;

            var builder = new BodyBuilder();

            builder.HtmlBody = GetCancellationEmailHtml(booking, customer, service);
            builder.TextBody = GetCancellationEmailText(booking, customer, service);

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

            await SendInternalNotificationAsync(
                $"Stornierung: {customer.FullName} – {service.Name} am {booking.BookingDate:dd.MM.yyyy}",
                GetInternalCancellationNotificationHtml(booking, customer, service),
                GetInternalCancellationNotificationText(booking, customer, service)
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
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null || booking.Status != BookingStatus.Confirmed)
            return;

        var emailLog = new EmailLog
        {
            BookingId = bookingId,
            EmailType = EmailType.Reminder,
            RecipientEmail = booking.Customer.Email,
            Subject = $"Erinnerung: Termin am {booking.BookingDate:dd.MM.yyyy}",
            Status = EmailStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook", _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(booking.Customer.FullName, booking.Customer.Email));
            message.Subject = emailLog.Subject;

            var builder = new BodyBuilder();
            var cancellationToken = GenerateCancellationToken(bookingId);
            var frontendBase3 = string.IsNullOrEmpty(_emailOptions.FrontendUrl) ? _emailOptions.BaseUrl : _emailOptions.FrontendUrl;
            var cancellationUrl = $"{frontendBase3}/booking/cancel/{cancellationToken}";

            builder.HtmlBody = GetReminderEmailHtml(booking, cancellationUrl);
            builder.TextBody = GetReminderEmailText(booking, cancellationUrl);

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

    #region Internal Notifications

    private async Task SendInternalNotificationAsync(string subject, string htmlBody, string textBody)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GentleBook Buchungssystem", _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress("GentleBook", _emailOptions.SenderEmail));
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

    private string GetInternalBookingNotificationHtml(Booking booking, Customer customer, Service service)
    {
        return $@"<!DOCTYPE html>
<html lang='de'>
<head>
    <meta charset='UTF-8'>
    <title>Neue Buchung</title>
</head>
<body style='font-family: Arial, sans-serif; background-color: #f5f5f5; padding: 20px; margin: 0;'>
    <div style='max-width: 600px; margin: 0 auto; background-color: #ffffff; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 12px rgba(0,0,0,0.1);'>
        <div style='background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); padding: 30px; text-align: center; border-bottom: 3px solid #C09995;'>
            <p style='color: #C09995; font-size: 32px; margin: 0 0 10px 0;'>✧</p>
            <h1 style='color: #ffffff; font-size: 20px; margin: 0 0 6px 0;'>Neue Buchung eingegangen</h1>
            <p style='color: #C09995; font-size: 13px; margin: 0; letter-spacing: 1px; text-transform: uppercase;'>GentleBook Buchungssystem</p>
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
                    <td style='padding: 12px 15px; color: #C09995; border: 1px solid #e2e8f0; font-size: 16px; font-weight: 700;'>{service.Price:0.00} CHF</td>
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
        <div style='background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 100%); padding: 20px; text-align: center; border-top: 3px solid #C09995;'>
            <p style='color: #C09995; font-size: 18px; margin: 0 0 8px 0;'>✧</p>
            <p style='color: #ffffff; font-size: 13px; font-weight: 700; margin: 0 0 4px 0;'>GentleBook</p>
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

    private string GetInternalCancellationNotificationHtml(Booking booking, Customer customer, Service service)
    {
        return $@"<!DOCTYPE html>
<html lang='de'>
<head>
    <meta charset='UTF-8'>
    <title>Stornierung</title>
</head>
<body style='font-family: Arial, sans-serif; background-color: #f5f5f5; padding: 20px; margin: 0;'>
    <div style='max-width: 600px; margin: 0 auto; background-color: #ffffff; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 12px rgba(0,0,0,0.1);'>
        <div style='background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); padding: 30px; text-align: center; border-bottom: 3px solid #C09995;'>
            <p style='color: #C09995; font-size: 32px; margin: 0 0 10px 0;'>✧</p>
            <h1 style='color: #ffffff; font-size: 20px; margin: 0 0 6px 0;'>Buchung storniert</h1>
            <p style='color: #C09995; font-size: 13px; margin: 0; letter-spacing: 1px; text-transform: uppercase;'>GentleBook Buchungssystem</p>
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
        <div style='background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 100%); padding: 20px; text-align: center; border-top: 3px solid #C09995;'>
            <p style='color: #C09995; font-size: 18px; margin: 0 0 8px 0;'>✧</p>
            <p style='color: #ffffff; font-size: 13px; font-weight: 700; margin: 0 0 4px 0;'>GentleBook</p>
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

    #endregion

    #region Email Templates

    private string GetBaseEmailTemplate(string title, string content)
    {
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
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
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
            --accent-light: #f8f0ef;
            --accent-primary: #C09995;
            --accent-dark: #A87B77;
            --button-gradient-start: #C09995;
            --button-gradient-end: #A87B77;
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
                --accent-primary: #C09995;
                --accent-dark: #A87B77;
                --button-gradient-start: #C09995;
                --button-gradient-end: #A87B77;
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
            background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%);
            padding: 40px 30px;
            text-align: center;
            border-bottom: 3px solid #C09995;
        }}
        
        .header-logo {{
            color: #C09995;
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
            color: #C09995;
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
            color: #C09995 !important;
            border: 2px solid #C09995;
            box-shadow: none;
        }}
        
        .footer {{
            background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 100%);
            padding: 30px;
            text-align: center;
            font-size: 14px;
            border-top: 3px solid #C09995;
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
            color: #C09995;
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
        <div style='background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); padding: 40px 30px; text-align: center; border-bottom: 3px solid #C09995;'>
            <div style='color: #C09995; font-size: 44px; font-weight: 300; margin-bottom: 16px; line-height: 1;'>✧</div>
            <p style='color: #C09995; font-size: 26px; font-weight: 600; margin: 0 0 8px 0; letter-spacing: 0.5px; font-family: Arial, sans-serif;'>GentleBook</p>
            <p style='color: #C09995; font-size: 14px; margin: 0; letter-spacing: 1px; text-transform: uppercase; opacity: 0.9;'>Ihre Premium Beauty-Experience</p>
        </div>
        
        <div class='content'>
            {content}
        </div>
        
        <div class='footer'>
            <div style='color: #C09995; font-size: 22px; margin-bottom: 12px;'>✧</div>
            <div class='footer-brand'>GentleBook</div>
            <div class='footer-address'>
                Elisabethenstrasse 41<br>
                4051 Basel, Schweiz
            </div>
            <div class='footer-contact'>
                Tel: +41 61 123 45 67<br>
                info@gentlebook.app
            </div>
            <div class='footer-links'>
                <a href='{_emailOptions.BaseUrl}/datenschutz'>Datenschutz</a>
                <span class='footer-divider'>|</span>
                <a href='{_emailOptions.BaseUrl}/impressum'>Impressum</a>
                <span class='footer-divider'>|</span>
                <a href='{_emailOptions.BaseUrl}/agb'>AGB</a>
            </div>
            <div class='footer-copy'>
                © {DateTime.UtcNow.Year} GentleBook. Alle Rechte vorbehalten.
            </div>
        </div>
    </div>
</body>
</html>";
    }

    private string GetConfirmationEmailHtml(Booking booking, string cancellationUrl)
    {
        var content = $@"
            <div class='greeting'>
                Hallo {booking.Customer.FirstName},
            </div>
            
            <p style='color: var(--text-secondary); margin-bottom: 30px;'>
                Vielen Dank für Ihre Buchung bei GentleBook. Ihr Termin wurde erfolgreich bestätigt.
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
                    <span class='detail-value'><span class='price'>{booking.Service.Price:0.00} CHF</span></span>
                </div>
            </div>
            
            <div class='cancel-section'>
                <div class='cancel-title'>Termin stornieren?</div>
                <div class='cancel-text'>
                    Falls Sie Ihren Termin nicht wahrnehmen können, stornieren Sie diesen bitte rechtzeitig.
                </div>
                <a href='{cancellationUrl}' style='display: inline-block; background: linear-gradient(135deg, #C09995 0%, #A87B77 100%); color: #000000; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; box-shadow: 0 4px 12px rgba(0,2,3,0.15);'>Termin stornieren
                </a>
                <p style='color: var(--text-secondary); font-size: 12px; margin-top: 15px;'>
                    Die Stornierung ist bis 24 Stunden vor dem Termin kostenlos möglich.
                </p>
            </div>";

        return GetBaseEmailTemplate("Ihre Buchungsbestätigung", content);
    }

    private string GetConfirmationReceiptHtml(Booking booking, Customer customer, Service service, string cancellationUrl)
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
                    <span class='detail-value'><span class='price'>{service.Price:0.00} CHF</span></span>
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
                    strokecolor='#C09995' strokeweight='2pt' filled='f'>
                    <w:anchorlock/>
                    <center style='color:#C09995;font-family:Arial,sans-serif;font-size:16px;font-weight:bold;'>Termin stornieren</center>
                </v:roundrect>
                <![endif]--><!--[if !mso]><!-->
                <a href='{cancellationUrl}' style='display:inline-block;background:transparent;border:2px solid #C09995;border-radius:40px;color:#000000;font-family:Arial,sans-serif;font-size:16px;font-weight:bold;padding:12px 30px;text-decoration:none;'>Termin stornieren</a>
                <!--<![endif]-->
            </div>";

        return GetBaseEmailTemplate("Buchung bestätigt", content);
    }

    private string GetCancellationEmailHtml(Booking booking, Customer customer, Service service)
    {
        var content = $@"
            <div class='greeting'>
                Hallo {customer.FirstName},
            </div>
            
            <p style='color: var(--text-secondary); margin-bottom: 30px;'>
                Ihre Buchung bei GentleBook wurde erfolgreich storniert.
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
                <a href='https://gentlebook.runasp.net' style='display: inline-block; background: linear-gradient(135deg, #3c3d3c, #A87B77 100%); color: #000000; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; box-shadow: 0 4px 12px rgba(0,0,0,0.15);'>Neuen Termin buchen
                </a>
            </div>";

        return GetBaseEmailTemplate("Termin storniert", content);
    }

    private string GetReminderEmailHtml(Booking booking, string cancellationUrl)
    {
        var content = $@"
            <div class='greeting'>
                Hallo {booking.Customer.FirstName},
            </div>
            
            <p style='color: var(--text-secondary); margin-bottom: 30px;'>
                dies ist eine freundliche Erinnerung an Ihren morgigen Termin bei GentleBook.
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
                    strokecolor='#C09995' strokeweight='2pt' filled='f'>
                    <w:anchorlock/>
                    <center style='color:#C09995;font-family:Arial,sans-serif;font-size:16px;font-weight:bold;'>Termin stornieren</center>
                </v:roundrect>
                <![endif]--><!--[if !mso]><!-->
                <a href='{cancellationUrl}' style='display:inline-block;background:transparent;border:2px solid #3c3d3c;border-radius:40px;color:#000000;font-family:Arial,sans-serif;font-size:16px;font-weight:bold;padding:12px 30px;text-decoration:none;'>Termin stornieren</a>
                <!--<![endif]-->
            </div>";

        return GetBaseEmailTemplate("Terminerinnerung", content);
    }

    #endregion

    #region Plain Text Versions

    private string GetConfirmationEmailText(Booking booking, string cancellationUrl)
    {
        return $@"
GENTLEBOOK - IHRE BUCHUNGSBESTÄTIGUNG

------------------------------------------------
Hallo {booking.Customer.FirstName},

vielen Dank für Ihre Buchung bei GentleBook. Ihr Termin wurde erfolgreich bestätigt.

BUCHUNGSDETAILS:
------------------------------------------------
Service: {booking.Service.Name}
Datum: {booking.BookingDate:dd.MM.yyyy}
Uhrzeit: {booking.StartTime:HH:mm} - {booking.EndTime:HH:mm} Uhr
Dauer: {booking.Service.DurationMinutes} Minuten
Preis: {booking.Service.Price:0.00} CHF
Status: Bestätigt


TERMIN STORNIEREN:
------------------------------------------------
Falls Sie Ihren Termin nicht wahrnehmen können:
{cancellationUrl}

KONTAKT:
------------------------------------------------
GentleBook
Elisabethenstrasse 41
4051 Basel, Schweiz

Tel: +41 61 123 45 67
info@gentlebook.app
gentlebook.app

------------------------------------------------
© {DateTime.UtcNow.Year} GentleBook. Alle Rechte vorbehalten.";
    }

    private string GetConfirmationReceiptText(Booking booking, Customer customer, Service service, string cancellationUrl)
    {
        return $@"
GENTLEBOOK - BUCHUNG BESTÄTIGT

------------------------------------------------
Hallo {customer.FirstName},

Ihre Buchung wurde erfolgreich bestätigt.

BUCHUNGSDETAILS:
------------------------------------------------
Service: {service.Name}
Datum: {booking.BookingDate:dd.MM.yyyy}
Uhrzeit: {booking.StartTime:HH:mm} - {booking.EndTime:HH:mm} Uhr
Dauer: {service.DurationMinutes} Minuten
Preis: {service.Price:0.00} CHF
Status: Bestätigt


KONTAKT:
------------------------------------------------
GentleBook
Tel: +41 61 123 45 67
info@gentlebook.app";
    }

    private string GetCancellationEmailText(Booking booking, Customer customer, Service service)
    {
        return $@"
GENTLEBOOK - STORNIERUNGSBESTÄTIGUNG

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

NEUEN TERMIN BUCHEN:
------------------------------------------------
https://gentlebook.runasp.net

KONTAKT:
------------------------------------------------
GentleBook
Tel: +41 61 123 45 67
info@gentlebook.app";
    }

    private string GetReminderEmailText(Booking booking, string cancellationUrl)
    {
        return $@"
GENTLEBOOK - TERMINERINNERUNG

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

KONTAKT:
------------------------------------------------
GentleBook
Tel: +41 61 123 45 67";
    }

    #endregion

    private string GenerateCancellationToken(Guid bookingId)
    {
        return Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{bookingId}:{DateTime.UtcNow.Ticks}:cancel")
        ).Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    public (Guid bookingId, string action) DecodeToken(string token)
    {
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
    /// Sends a welcome / onboarding email to a newly created TenantAdmin with their login credentials.
    /// </summary>
    public async Task SendWelcomeEmailAsync(string recipientEmail, string firstName, string tenantSlug, string password)
    {
        try
        {
            var frontendBase = string.IsNullOrEmpty(_emailOptions.FrontendUrl)
                ? _emailOptions.BaseUrl?.Replace("/api", "") ?? "https://gentle-book-ui.vercel.app"
                : _emailOptions.FrontendUrl;
            var loginUrl    = $"{frontendBase}/admin/login";
            var profileUrl  = $"{frontendBase}/booking/{tenantSlug}";
            var settingsUrl = $"{frontendBase}/admin/settings";
            var linksUrl    = $"{frontendBase}/admin/links";

            var message = new MimeMessage();
            // Always send from noreply@gentlegroup.de
            message.From.Add(new MailboxAddress("GentleGroup", "noreply@gentlegroup.de"));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = $"Willkommen bei GentleBook, {firstName}! 🎉 Ihre Zugangsdaten";

            var builder = new BodyBuilder();

            builder.HtmlBody = $"""
                <!DOCTYPE html>
                <html lang="de">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width,initial-scale=1">
                  <title>Willkommen bei GentleBook</title>
                </head>
                <body style="margin:0;padding:0;background:#F5EDEB;font-family:'Helvetica Neue',Arial,sans-serif;">
                  <table width="100%" cellpadding="0" cellspacing="0" style="background:#F5EDEB;padding:32px 16px;">
                    <tr><td align="center">
                      <table width="100%" style="max-width:580px;" cellpadding="0" cellspacing="0">

                        <!-- HEADER -->
                        <tr>
                          <td style="background:linear-gradient(135deg,#E8C7C3 0%,#C9A8A4 100%);border-radius:20px 20px 0 0;padding:40px 32px 36px;text-align:center;">
                            <div style="display:inline-block;width:64px;height:64px;background:rgba(255,255,255,0.25);border-radius:50%;line-height:64px;font-size:30px;margin-bottom:16px;">🎉</div>
                            <h1 style="margin:0;color:#fff;font-size:28px;font-weight:700;letter-spacing:-0.5px;">Herzlich willkommen!</h1>
                            <p style="margin:10px 0 0;color:rgba(255,255,255,0.88);font-size:15px;">Ihr GentleBook-Buchungssystem ist einsatzbereit</p>
                          </td>
                        </tr>

                        <!-- GREETING -->
                        <tr>
                          <td style="background:#ffffff;padding:32px 32px 24px;">
                            <p style="margin:0 0 12px;color:#1E1E1E;font-size:16px;">Hallo <strong>{firstName}</strong>,</p>
                            <p style="margin:0;color:#555;font-size:15px;line-height:1.6;">
                              wir freuen uns sehr, Sie als neuen Kunden bei GentleBook begrüßen zu dürfen!
                              Ihr persönliches Online-Buchungssystem wurde erfolgreich für Sie eingerichtet und ist ab sofort aktiv.
                            </p>
                          </td>
                        </tr>

                        <!-- LOGIN CREDENTIALS -->
                        <tr>
                          <td style="background:#ffffff;padding:0 32px 28px;">
                            <div style="background:#F5EDEB;border-radius:14px;padding:22px 24px;border-left:4px solid #E8C7C3;">
                              <p style="margin:0 0 14px;color:#8A8A8A;font-size:11px;text-transform:uppercase;letter-spacing:1px;font-weight:600;">🔐 Ihre Zugangsdaten</p>
                              <table cellpadding="0" cellspacing="0" width="100%">
                                <tr>
                                  <td style="padding:5px 0;color:#8A8A8A;font-size:14px;width:90px;">E-Mail</td>
                                  <td style="padding:5px 0;color:#1E1E1E;font-size:14px;font-weight:600;">{recipientEmail}</td>
                                </tr>
                                <tr>
                                  <td style="padding:5px 0;color:#8A8A8A;font-size:14px;">Passwort</td>
                                  <td style="padding:5px 0;">
                                    <code style="background:#fff;color:#D8706A;padding:4px 12px;border-radius:8px;font-size:15px;font-family:monospace;border:1px solid #f0e0de;letter-spacing:1px;">{password}</code>
                                  </td>
                                </tr>
                                <tr>
                                  <td style="padding:5px 0;color:#8A8A8A;font-size:14px;">Ihr Profil</td>
                                  <td style="padding:5px 0;">
                                    <a href="{profileUrl}" style="color:#E8C7C3;font-size:14px;text-decoration:none;font-weight:500;">{profileUrl}</a>
                                  </td>
                                </tr>
                              </table>
                            </div>
                          </td>
                        </tr>

                        <!-- CTA BUTTON -->
                        <tr>
                          <td style="background:#ffffff;padding:0 32px 32px;text-align:center;">
                            <a href="{loginUrl}"
                               style="display:inline-block;background:linear-gradient(135deg,#E8C7C3,#C9A8A4);color:#fff;text-decoration:none;padding:16px 40px;border-radius:14px;font-weight:700;font-size:16px;letter-spacing:0.3px;box-shadow:0 4px 16px rgba(232,199,195,0.45);">
                              Jetzt einloggen &rarr;
                            </a>
                            <p style="margin:12px 0 0;color:#AAAAAA;font-size:12px;">
                              Bitte ändern Sie Ihr Passwort nach dem ersten Login.
                            </p>
                          </td>
                        </tr>

                        <!-- DIVIDER -->
                        <tr>
                          <td style="background:#ffffff;padding:0 32px;">
                            <div style="border-top:1px solid #F0E8E7;"></div>
                          </td>
                        </tr>

                        <!-- NEXT STEPS -->
                        <tr>
                          <td style="background:#ffffff;padding:28px 32px;">
                            <p style="margin:0 0 18px;color:#1E1E1E;font-size:15px;font-weight:700;">✅ Ihre nächsten Schritte</p>
                            <table cellpadding="0" cellspacing="0" width="100%">

                              <tr>
                                <td valign="top" style="width:36px;padding:0 0 16px;">
                                  <div style="width:28px;height:28px;background:#F5EDEB;border-radius:50%;text-align:center;line-height:28px;font-size:13px;font-weight:700;color:#D8B0AC;">1</div>
                                </td>
                                <td style="padding:0 0 16px 8px;">
                                  <p style="margin:0 0 2px;color:#1E1E1E;font-size:14px;font-weight:600;">Einloggen &amp; Passwort ändern</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.5;">
                                    Melden Sie sich unter <a href="{loginUrl}" style="color:#E8C7C3;text-decoration:none;">{loginUrl}</a> an und ändern Sie sofort Ihr Passwort unter Einstellungen.
                                  </p>
                                </td>
                              </tr>

                              <tr>
                                <td valign="top" style="width:36px;padding:0 0 16px;">
                                  <div style="width:28px;height:28px;background:#F5EDEB;border-radius:50%;text-align:center;line-height:28px;font-size:13px;font-weight:700;color:#D8B0AC;">2</div>
                                </td>
                                <td style="padding:0 0 16px 8px;">
                                  <p style="margin:0 0 2px;color:#1E1E1E;font-size:14px;font-weight:600;">Profil &amp; Branding einrichten</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.5;">
                                    Laden Sie Ihr Logo hoch, wählen Sie Ihre Branchenfarbe und passen Sie Ihren Profiltext unter
                                    <a href="{settingsUrl}" style="color:#E8C7C3;text-decoration:none;">Einstellungen</a> an.
                                  </p>
                                </td>
                              </tr>

                              <tr>
                                <td valign="top" style="width:36px;padding:0 0 16px;">
                                  <div style="width:28px;height:28px;background:#F5EDEB;border-radius:50%;text-align:center;line-height:28px;font-size:13px;font-weight:700;color:#D8B0AC;">3</div>
                                </td>
                                <td style="padding:0 0 16px 8px;">
                                  <p style="margin:0 0 2px;color:#1E1E1E;font-size:14px;font-weight:600;">Links &amp; Design gestalten</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.5;">
                                    Fügen Sie unter <a href="{linksUrl}" style="color:#E8C7C3;text-decoration:none;">Meine Links</a> Instagram, WhatsApp oder andere Links hinzu
                                    und wählen Sie eine Branchenvorlage für Ihr Design.
                                  </p>
                                </td>
                              </tr>

                              <tr>
                                <td valign="top" style="width:36px;padding:0 0 16px;">
                                  <div style="width:28px;height:28px;background:#F5EDEB;border-radius:50%;text-align:center;line-height:28px;font-size:13px;font-weight:700;color:#D8B0AC;">4</div>
                                </td>
                                <td style="padding:0 0 16px 8px;">
                                  <p style="margin:0 0 2px;color:#1E1E1E;font-size:14px;font-weight:600;">Buchungslink teilen</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.5;">
                                    Teilen Sie Ihren persönlichen Link <a href="{profileUrl}" style="color:#E8C7C3;text-decoration:none;">{profileUrl}</a>
                                    mit Ihren Kunden — per WhatsApp, Instagram Bio oder QR-Code (im Admin unter „Meine Links" → „QR-Code").
                                  </p>
                                </td>
                              </tr>

                              <tr>
                                <td valign="top" style="width:36px;">
                                  <div style="width:28px;height:28px;background:#F5EDEB;border-radius:50%;text-align:center;line-height:28px;font-size:13px;font-weight:700;color:#D8B0AC;">5</div>
                                </td>
                                <td style="padding:0 0 0 8px;">
                                  <p style="margin:0 0 2px;color:#1E1E1E;font-size:14px;font-weight:600;">Leistungen &amp; Mitarbeiter anlegen</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.5;">
                                    Legen Sie Ihre Dienstleistungen (Preise, Dauer) und Mitarbeiter im Admin-Bereich an,
                                    damit Kunden direkt online buchen können.
                                  </p>
                                </td>
                              </tr>

                            </table>
                          </td>
                        </tr>

                        <!-- DIVIDER -->
                        <tr>
                          <td style="background:#ffffff;padding:0 32px;">
                            <div style="border-top:1px solid #F0E8E7;"></div>
                          </td>
                        </tr>

                        <!-- SUPPORT -->
                        <tr>
                          <td style="background:#ffffff;padding:24px 32px 32px;">
                            <table cellpadding="0" cellspacing="0" width="100%" style="background:#F5EDEB;border-radius:12px;padding:20px 24px;">
                              <tr>
                                <td>
                                  <p style="margin:0 0 6px;color:#1E1E1E;font-size:14px;font-weight:700;">💬 Fragen? Wir sind für Sie da!</p>
                                  <p style="margin:0;color:#888;font-size:13px;line-height:1.6;">
                                    Bei Fragen oder Problemen stehen wir Ihnen jederzeit zur Verfügung.<br>
                                    Schreiben Sie uns einfach an:
                                    <a href="mailto:support@gentlegroup.de" style="color:#E8C7C3;font-weight:600;text-decoration:none;">support@gentlegroup.de</a>
                                  </p>
                                </td>
                              </tr>
                            </table>
                          </td>
                        </tr>

                        <!-- FOOTER -->
                        <tr>
                          <td style="background:#EEE4E2;border-radius:0 0 20px 20px;padding:20px 32px;text-align:center;">
                            <p style="margin:0 0 4px;color:#AAA;font-size:12px;">
                              Diese E-Mail wurde automatisch von GentleBook versandt.
                            </p>
                            <p style="margin:0;color:#AAA;font-size:12px;">
                              &copy; {DateTime.UtcNow.Year} GentleGroup &middot;
                              <a href="mailto:support@gentlegroup.de" style="color:#C9A8A4;text-decoration:none;">support@gentlegroup.de</a>
                              &middot; <a href="{profileUrl}" style="color:#C9A8A4;text-decoration:none;">Ihr Profil</a>
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

                Ihr Buchungssystem ist einsatzbereit. Hier sind Ihre Zugangsdaten:

                E-Mail:   {recipientEmail}
                Passwort: {password}
                Profil:   {profileUrl}

                LOGIN: {loginUrl}

                IHRE NÄCHSTEN SCHRITTE:

                1. Einloggen & Passwort ändern
                   Melden Sie sich an und ändern Sie sofort Ihr Passwort unter Einstellungen.

                2. Profil & Branding einrichten
                   Laden Sie Ihr Logo hoch und wählen Sie Ihre Branchenfarbe.
                   -> {settingsUrl}

                3. Links & Design gestalten
                   Fügen Sie Instagram, WhatsApp oder andere Links hinzu und wählen Sie eine Branchenvorlage.
                   -> {linksUrl}

                4. Buchungslink teilen
                   Teilen Sie {profileUrl} mit Ihren Kunden per WhatsApp, Instagram Bio oder QR-Code.

                5. Leistungen & Mitarbeiter anlegen
                   Legen Sie Ihre Dienstleistungen (Preise, Dauer) und Mitarbeiter an.

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

            _logger.LogInformation("Welcome email sent to {Email}", recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send welcome email to {Email}", recipientEmail);
            // Don't throw — user was already created, email failure is non-critical
        }
    }
}
