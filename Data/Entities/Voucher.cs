// Data/Entities/Voucher.cs
// Pure balance/credit bookkeeping — no payment processing. A tenant issues a voucher after
// collecting payment themselves (cash/bank transfer/in person); GentleBook only tracks the
// remaining balance and lets it be applied against a manually-created booking. See the
// Agency-Ausbau-Runde-2 plan: online payment acceptance is explicitly out of scope.
namespace GentleBook.Api.Data.Entities;

public enum VoucherType
{
    MonetaryValue,
    SessionPackage,
}

public enum VoucherStatus
{
    Active,
    Redeemed,
    Expired,
    Cancelled,
}

public class Voucher
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Human-readable, unique per tenant, e.g. "GB-4F2A-91C7".</summary>
    public string Code { get; set; } = string.Empty;

    public Guid? CustomerId { get; set; }
    public VoucherType Type { get; set; } = VoucherType.MonetaryValue;

    public decimal? InitialAmount { get; set; }
    public decimal? RemainingAmount { get; set; }
    public int? InitialSessions { get; set; }
    public int? RemainingSessions { get; set; }

    public DateTime? ExpiresAt { get; set; }
    public VoucherStatus Status { get; set; } = VoucherStatus.Active;

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public Guid IssuedByPlatformUserId { get; set; }
    public string? Note { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Customer? Customer { get; set; }
}
