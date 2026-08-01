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

        // "Tomorrow" is computed PER TENANT — a single global timezone would either miss
        // bookings or fire a day early/late for every tenant except whichever one happened to
        // be first in the table, once more than one tenant with a different timezone exists.
        var tenants = await _context.TenantSettings
            .IgnoreQueryFilters()
            .Select(t => new { t.TenantId, t.TimeZone })
            .ToListAsync();

        int successCount = 0;
        int failureCount = 0;
        int skippedCount = 0;
        int totalFound = 0;

        foreach (var tenant in tenants)
        {
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone ?? "Europe/Berlin"); }
            catch { tz = TimeZoneInfo.Utc; }

            var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, tz);
            var tomorrow = DateOnly.FromDateTime(localNow.AddDays(1));

            // Get all confirmed bookings for tomorrow (this tenant's local "tomorrow")
            var bookingsToRemind = await _context.Bookings
                .IgnoreQueryFilters()
                .Include(b => b.Customer)
                .Include(b => b.Service)
                .Where(b =>
                    b.TenantId == tenant.TenantId &&
                    b.BookingDate == tomorrow &&
                    b.Status == BookingStatus.Confirmed && // Only confirmed bookings
                    !b.ReminderSentAt.HasValue             // Not yet reminded
                )
                .ToListAsync();

            totalFound += bookingsToRemind.Count;

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

                    // Mark as sent so we don't try again — if a customer never adds an
                    // email we'd otherwise retry forever with nothing to send to.
                    booking.ReminderSentAt = DateTime.UtcNow;
                    continue;
                }

                try
                {
                    // Send reminder email — SendBookingReminderAsync already writes its own
                    // EmailLog entry (Sent or Failed) and sets ReminderSentAt on the tracked
                    // booking, so neither must be duplicated here.
                    await _emailService.SendBookingReminderAsync(booking.Id);

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
                }
            }
        }

        // Save all changes
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Daily reminder job completed across {TenantCount} tenant(s), {Found} booking(s) found. Success: {Success}, Failed: {Failed}, Skipped (no email): {Skipped}",
            tenants.Count,
            totalFound,
            successCount,
            failureCount,
            skippedCount
        );
    }
}