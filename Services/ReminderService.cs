using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class ReminderService
{
    private readonly GentleBookDbContext _context;
    private readonly EmailService _emailService;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(
        GentleBookDbContext context,
        EmailService emailService,
        ILogger<ReminderService> logger)
    {
        _context = context;
        _emailService = emailService;
        _logger = logger;
    }

    /// <summary>
    /// Sends reminder emails for bookings that are tomorrow
    /// This method is called daily by Hangfire
    /// </summary>
    public async Task SendDailyRemindersAsync()
    {
        var now = DateTime.UtcNow;
        var tomorrow = DateOnly.FromDateTime(now.AddDays(1));

        _logger.LogInformation("Starting daily reminder job for date: {Date}", tomorrow);

        // Get all confirmed bookings for tomorrow
        var bookingsToRemind = await _context.Bookings
            .IgnoreQueryFilters()
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Where(b =>
                b.BookingDate == tomorrow &&
                b.Status == BookingStatus.Confirmed && // Only confirmed bookings
                !b.ReminderSentAt.HasValue             // Not yet reminded
            )
            .ToListAsync();

        _logger.LogInformation("Found {Count} bookings for tomorrow", bookingsToRemind.Count);

        int successCount = 0;
        int failureCount = 0;
        int skippedCount = 0;

        foreach (var booking in bookingsToRemind)
        {
            // Skip if customer has no email
            if (booking.Customer == null || string.IsNullOrEmpty(booking.Customer.Email))
            {
                skippedCount++;
                _logger.LogInformation(
                    "Skipping reminder for booking {BookingId} - customer has no email",
                    booking.Id
                );

                // Still mark as "sent" to avoid trying again tomorrow?
                // Option 1: Mark as sent so we don't try again
                booking.ReminderSentAt = DateTime.UtcNow;

                // Option 2: Leave as null to try again? 
                // If customer never adds email, we'll try every day forever.
                // Better to mark as sent once we've processed it.
                continue;
            }

            try
            {
                // Send reminder email
                await _emailService.SendBookingReminderAsync(booking.Id);

                // Mark reminder as sent
                booking.ReminderSentAt = DateTime.UtcNow;

                // Log email
                _context.EmailLogs.Add(new EmailLog
                {
                    Id = Guid.NewGuid(),
                    TenantId = booking.TenantId,
                    BookingId = booking.Id,
                    RecipientEmail = booking.Customer.Email,
                    EmailType = EmailType.Reminder,
                    SentAt = DateTime.UtcNow,
                    Status = EmailStatus.Sent,
                    CreatedAt = DateTime.UtcNow
                });

                successCount++;
                _logger.LogInformation(
                    "Reminder sent for booking {BookingId} to {Email}",
                    booking.Id,
                    booking.Customer.Email
                );
            }
            catch (Exception ex)
            {
                failureCount++;
                _logger.LogError(
                    ex,
                    "Failed to send reminder for booking {BookingId} to {Email}",
                    booking.Id,
                    booking.Customer?.Email ?? "unknown"
                );

                // Log email failure
                _context.EmailLogs.Add(new EmailLog
                {
                    Id = Guid.NewGuid(),
                    TenantId = booking.TenantId,
                    BookingId = booking.Id,
                    RecipientEmail = booking.Customer?.Email ?? "unknown",
                    EmailType = EmailType.Reminder,
                    SentAt = DateTime.UtcNow,
                    Status = EmailStatus.Failed,
                    ErrorMessage = ex.Message,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        // Save all changes
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Daily reminder job completed. Success: {Success}, Failed: {Failed}, Skipped (no email): {Skipped}",
            successCount,
            failureCount,
            skippedCount
        );
    }
}