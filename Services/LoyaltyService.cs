// Services/LoyaltyService.cs
// Treuepunkte-/Bonusprogramm (Agency). Points are earned automatically once a booking reaches
// BookingStatus.Completed — called from both the automatic completion job
// (BookingCompletionService) and the manual staff transition (AdminService.UpdateBookingStatusAsync)
// so a customer earns points regardless of which path completed the booking. See
// Data/Entities/LoyaltyPointsTransaction.cs for the ledger-not-just-a-counter rationale.
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class LoyaltyService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<LoyaltyService> _logger;

    public LoyaltyService(GentleBookDbContext context, ILogger<LoyaltyService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Idempotent: safe to call more than once for the same booking (e.g. if both the automatic
    /// job and a manual status change somehow race) — a second call is a silent no-op.
    /// </summary>
    public async Task AwardPointsForBookingAsync(Guid bookingId)
    {
        var booking = await _context.Bookings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking == null) return;

        var alreadyAwarded = await _context.LoyaltyPointsTransactions.IgnoreQueryFilters()
            .AnyAsync(l => l.BookingId == bookingId && l.Reason == "booking_completed");
        if (alreadyAwarded) return;

        var plan = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.TenantId == booking.TenantId)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync() ?? SubscriptionPlan.Trial;
        if (AgencyFeatureGate.ValidateForPlan(plan) != null) return; // not Agency — feature not available

        var pointsPerBooking = await _context.TenantSettings.IgnoreQueryFilters()
            .Where(s => s.TenantId == booking.TenantId)
            .Select(s => s.LoyaltyPointsPerBooking)
            .FirstOrDefaultAsync();
        if (pointsPerBooking <= 0) return; // feature disabled for this tenant

        var customer = await _context.Customers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == booking.CustomerId);
        if (customer == null) return;

        customer.LoyaltyPoints += pointsPerBooking;
        _context.LoyaltyPointsTransactions.Add(new LoyaltyPointsTransaction
        {
            TenantId = booking.TenantId,
            CustomerId = customer.Id,
            BookingId = booking.Id,
            Points = pointsPerBooking,
            Reason = "booking_completed",
        });

        await _context.SaveChangesAsync();
        _logger.LogInformation("Awarded {Points} loyalty points to customer {CustomerId} for booking {BookingId}.",
            pointsPerBooking, customer.Id, booking.Id);
    }

    /// <summary>Manual staff adjustment (e.g. redemption in person). Positive = credit, negative = debit.</summary>
    public async Task<int> AdjustPointsAsync(Guid tenantId, Guid customerId, int points, string reason)
    {
        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Id == customerId && c.TenantId == tenantId)
            ?? throw new ArgumentException("Kunde nicht gefunden");

        var newBalance = customer.LoyaltyPoints + points;
        if (newBalance < 0)
            throw new InvalidOperationException("Der Punktestand darf nicht negativ werden.");

        customer.LoyaltyPoints = newBalance;
        _context.LoyaltyPointsTransactions.Add(new LoyaltyPointsTransaction
        {
            TenantId = tenantId,
            CustomerId = customerId,
            Points = points,
            Reason = reason,
        });

        await _context.SaveChangesAsync();
        return customer.LoyaltyPoints;
    }
}
