// Services/BookingCompletionService.cs
// Automatically marks Confirmed bookings as Completed once their end time (plus a grace period)
// has passed — closing a gap where BookingStatus.Completed previously only happened via manual
// staff action (AdminService.UpdateBookingStatusAsync). Agency-exclusive: this is the trigger
// point both the review-request flow (ReviewRequestService) and the loyalty program
// (LoyaltyService) hang off, and both are Agency features — non-Agency tenants keep today's
// fully-manual behavior unchanged. Mirrors NoShowService.cs's per-tenant-timezone loop.
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class BookingCompletionService
{
    private readonly GentleBookDbContext _context;
    private readonly LoyaltyService _loyaltyService;
    private readonly ILogger<BookingCompletionService> _logger;

    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(1);

    public BookingCompletionService(GentleBookDbContext context, LoyaltyService loyaltyService, ILogger<BookingCompletionService> logger)
    {
        _context = context;
        _loyaltyService = loyaltyService;
        _logger = logger;
    }

    public async Task AutoCompleteBookingsAsync()
    {
        var now = DateTime.UtcNow;

        var agencyTenantIds = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.Plan == SubscriptionPlan.Agency)
            .Select(s => s.TenantId)
            .ToListAsync();
        if (agencyTenantIds.Count == 0) return;

        var tenants = await _context.TenantSettings.IgnoreQueryFilters()
            .Where(t => agencyTenantIds.Contains(t.TenantId))
            .Select(t => new { t.TenantId, t.TimeZone })
            .ToListAsync();

        var completed = new List<Booking>();

        foreach (var tenant in tenants)
        {
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone ?? "Europe/Berlin"); }
            catch { tz = TimeZoneInfo.Utc; }

            var localCutoff = TimeZoneInfo.ConvertTimeFromUtc(now - GracePeriod, tz);
            var cutoffDate = DateOnly.FromDateTime(localCutoff);
            var cutoffTime = TimeOnly.FromDateTime(localCutoff);

            var tenantOverdue = await _context.Bookings.IgnoreQueryFilters()
                .Where(b =>
                    b.TenantId == tenant.TenantId &&
                    b.Status == BookingStatus.Confirmed &&
                    (b.BookingDate < cutoffDate ||
                     (b.BookingDate == cutoffDate && b.EndTime <= cutoffTime)))
                .ToListAsync();

            completed.AddRange(tenantOverdue);
        }

        if (completed.Count == 0) return;

        foreach (var booking in completed)
        {
            booking.Status = BookingStatus.Completed;
            booking.UpdatedAt = now;
        }
        await _context.SaveChangesAsync();

        foreach (var booking in completed)
            await _loyaltyService.AwardPointsForBookingAsync(booking.Id);

        _logger.LogInformation("Auto-complete: {Count} booking(s) marked Completed across {TenantCount} Agency tenant(s).", completed.Count, tenants.Count);
    }
}
