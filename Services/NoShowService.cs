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

        // Computed PER TENANT — a single global timezone (the previous "first TenantSettings
        // row found" approach) would mark bookings as NoShow too early/late for every tenant
        // except whichever one happened to be first in the table, once tenants span timezones.
        var tenants = await _context.TenantSettings
            .IgnoreQueryFilters()
            .Select(t => new { t.TenantId, t.TimeZone })
            .ToListAsync();

        var overdue = new List<Booking>();

        foreach (var tenant in tenants)
        {
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone ?? "Europe/Berlin"); }
            catch { tz = TimeZoneInfo.Utc; }

            var localCutoff = TimeZoneInfo.ConvertTimeFromUtc(now - GracePeriod, tz);
            var cutoffDate = DateOnly.FromDateTime(localCutoff);
            var cutoffTime = TimeOnly.FromDateTime(localCutoff);

            // Only bookings still Confirmed (never checked in / completed / cancelled) whose
            // end time plus the grace period is in the past. Pending bookings are intentionally
            // left untouched — they were never confirmed in the first place.
            var tenantOverdue = await _context.Bookings
                .IgnoreQueryFilters()
                .Where(b =>
                    b.TenantId == tenant.TenantId &&
                    b.Status == BookingStatus.Confirmed &&
                    (b.BookingDate < cutoffDate ||
                     (b.BookingDate == cutoffDate && b.EndTime <= cutoffTime)))
                .ToListAsync();

            overdue.AddRange(tenantOverdue);
        }

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

        _logger.LogInformation("No-show check: {Count} booking(s) marked as NoShow across {TenantCount} tenant(s).", overdue.Count, tenants.Count);
    }
}
