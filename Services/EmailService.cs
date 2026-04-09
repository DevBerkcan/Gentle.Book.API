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
            var cancellationUrl = $"{_emailOptions.BaseUrl}/api/bookings/cancel/{cancellationToken}";

            builder.HtmlBody = GetConfirmationEmailHtml(booking, cancellationUrl);
            builder.TextBody = GetConfirmationEmailText(booking, cancellationUrl);

            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort,
                SecureSocketOptions.StartTls);
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
            var cancellationUrl = $"{_emailOptions.BaseUrl}/api/bookings/cancel/{cancellationToken}";

            builder.HtmlBody = GetConfirmationReceiptHtml(booking, customer, service, cancellationUrl);
            builder.TextBody = GetConfirmationReceiptText(booking, customer, service, cancellationUrl);

            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort,
                SecureSocketOptions.StartTls);
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
                SecureSocketOptions.StartTls);
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
            var cancellationUrl = $"{_emailOptions.BaseUrl}/api/bookings/cancel/{cancellationToken}";

            builder.HtmlBody = GetReminderEmailHtml(booking, cancellationUrl);
            builder.TextBody = GetReminderEmailText(booking, cancellationUrl);

            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort,
                SecureSocketOptions.StartTls);
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
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.StartTls);
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
            var frontendBase = _emailOptions.BaseUrl?.Replace("/api", "") ?? "https://gentle-book-ui.vercel.app";
            var loginUrl = $"{frontendBase}/admin/login";
            var profileUrl = $"{frontendBase}/booking/{tenantSlug}";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_emailOptions.SenderName ?? "GentleBook", _emailOptions.SenderEmail));
            message.To.Add(new MailboxAddress(firstName, recipientEmail));
            message.Subject = "Willkommen bei GentleBook – Ihre Zugangsdaten";

            var builder = new BodyBuilder();
            builder.HtmlBody = $"""
                <!DOCTYPE html>
                <html>
                <head><meta charset="utf-8"></head>
                <body style="font-family:Arial,sans-serif;background:#f5edeb;margin:0;padding:20px;">
                  <div style="max-width:560px;margin:0 auto;background:#fff;border-radius:16px;overflow:hidden;box-shadow:0 4px 24px rgba(0,0,0,.08);">
                    <div style="background:linear-gradient(135deg,#E8C7C3,#D8B0AC);padding:32px;text-align:center;">
                      <h1 style="color:#fff;margin:0;font-size:28px;">Willkommen!</h1>
                      <p style="color:rgba(255,255,255,.9);margin:8px 0 0;">Ihr GentleBook-Konto ist bereit</p>
                    </div>
                    <div style="padding:32px;">
                      <p style="color:#1E1E1E;font-size:16px;">Hallo {firstName},</p>
                      <p style="color:#555;">Ihr Buchungssystem wurde erfolgreich eingerichtet. Hier sind Ihre Zugangsdaten:</p>
                      <div style="background:#f5edeb;border-radius:12px;padding:20px;margin:24px 0;">
                        <p style="margin:0 0 8px;color:#8A8A8A;font-size:12px;text-transform:uppercase;letter-spacing:.5px;">Ihre Login-Daten</p>
                        <p style="margin:4px 0;color:#1E1E1E;"><strong>E-Mail:</strong> {recipientEmail}</p>
                        <p style="margin:4px 0;color:#1E1E1E;"><strong>Passwort:</strong> <code style="background:#fff;padding:2px 8px;border-radius:6px;font-size:15px;">{password}</code></p>
                        <p style="margin:4px 0;color:#1E1E1E;"><strong>Ihr Link:</strong> <a href="{profileUrl}" style="color:#E8C7C3;">{profileUrl}</a></p>
                      </div>
                      <div style="text-align:center;margin:24px 0;">
                        <a href="{loginUrl}" style="display:inline-block;background:linear-gradient(135deg,#E8C7C3,#D8B0AC);color:#fff;text-decoration:none;padding:14px 32px;border-radius:12px;font-weight:bold;font-size:16px;">
                          Jetzt einloggen →
                        </a>
                      </div>
                      <p style="color:#8A8A8A;font-size:13px;">Bitte ändern Sie Ihr Passwort nach dem ersten Login. Bei Fragen stehen wir Ihnen jederzeit zur Verfügung.</p>
                    </div>
                    <div style="background:#f5edeb;padding:16px;text-align:center;">
                      <p style="color:#8A8A8A;font-size:12px;margin:0;">Powered by GentleBook · <a href="{profileUrl}" style="color:#E8C7C3;">Ihr Profil ansehen</a></p>
                    </div>
                  </div>
                </body>
                </html>
                """;

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailOptions.SmtpServer, _emailOptions.SmtpPort, SecureSocketOptions.StartTls);
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
