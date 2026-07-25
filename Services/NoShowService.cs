using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

/// <summary>
/// Marks bookings that ended a while ago and were never completed/cancelled as NoShow.
/// Runs on a schedule via Hangfire (see HangfireJobScheduler).
/// </summary>
public class NoShowService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<NoShowService> _logger;

    // Grace period after the booking's end time before it is auto-marked as NoShow.
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(2);

    public NoShowService(GentleBookDbContext context, ILogger<NoShowService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task MarkNoShowsAsync()
    {
        var now = DateTime.UtcNow;

        // Same simplified single-timezone approach as ReminderService.SendDailyRemindersAsync
        // (uses the first TenantSettings row found) — kept consistent with existing behavior.
        var defaultTzId = await _context.TenantSettings
            .IgnoreQueryFilters()
            .Select(t => t.TimeZone)
            .FirstOrDefaultAsync() ?? "Europe/Berlin";

        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(defaultTzId); }
        catch { tz = TimeZoneInfo.Utc; }

        var localCutoff = TimeZoneInfo.ConvertTimeFromUtc(now - GracePeriod, tz);

        // Only bookings still Confirmed (never checked in / completed / cancelled) whose
        // end time plus the grace period is in the past. Pending bookings are intentionally
        // left untouched — they were never confirmed in the first place.
        var overdue = await _context.Bookings
            .IgnoreQueryFilters()
            .Where(b =>
                b.Status == BookingStatus.Confirmed &&
                (b.BookingDate < DateOnly.FromDateTime(localCutoff) ||
                 (b.BookingDate == DateOnly.FromDateTime(localCutoff) && b.EndTime <= TimeOnly.FromDateTime(localCutoff))))
            .ToListAsync();

        if (overdue.Count == 0)
        {
            _logger.LogInformation("No-show check: no overdue confirmed bookings found.");
            return;
        }

        foreach (var booking in overdue)
        {
            booking.Status = BookingStatus.NoShow;
            booking.UpdatedAt = now;
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation("No-show check: {Count} booking(s) marked as NoShow.", overdue.Count);
    }
}
