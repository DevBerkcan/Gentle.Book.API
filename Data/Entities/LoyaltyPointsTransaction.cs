// Data/Entities/LoyaltyPointsTransaction.cs
// Append-only ledger backing Customer.LoyaltyPoints — the running total on Customer is a cache,
// this table is the source of truth/audit trail (same "ledger, not just a counter" convention
// used elsewhere in this codebase, e.g. AuditService). BookingId is set for automatic awards
// (one row per completed booking, enforced by a unique index for idempotency) and null for
// manual staff adjustments/redemptions.
namespace GentleBook.Api.Data.Entities;

public class LoyaltyPointsTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? BookingId { get; set; }

    /// <summary>Positive = earned, negative = redeemed/adjusted down.</summary>
    public int Points { get; set; }

    /// <summary>Machine-readable reason, e.g. "booking_completed", "manual_redemption", "manual_adjustment".</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
    public Booking? Booking { get; set; }
}
