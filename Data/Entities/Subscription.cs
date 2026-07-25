// Data/Entities/Subscription.cs
namespace GentleBook.Api.Data.Entities;

public class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public SubscriptionPlan Plan { get; set; } = SubscriptionPlan.Trial;
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trial;

    // ── Trial ─────────────────────────────────────────────────
    public DateTime TrialStartedAt { get; set; } = DateTime.UtcNow;
    public DateTime TrialEndsAt { get; set; } = DateTime.UtcNow.AddDays(14);

    // ── Paid Period ───────────────────────────────────────────
    public DateTime? CurrentPeriodStart { get; set; }
    public DateTime? CurrentPeriodEnd { get; set; }

    // ── Mollie (SEPA Direct Debit via Mollie Subscriptions API) ───
    public string? MollieCustomerId { get; set; }
    public string? MollieMandateId { get; set; }
    public string? MollieSubscriptionId { get; set; }
    public string? LastMolliePaymentId { get; set; }
    public DateTime? MollieMandateSignedAt { get; set; }

    // ── CRM (Gentle.Suite invoicing) ──────────────────────────
    public string? CrmCustomerId { get; set; }

    public DateTime? CancelledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Computed ──────────────────────────────────────────────
    public bool IsInTrial => Status == SubscriptionStatus.Trial && TrialEndsAt > DateTime.UtcNow;
    public int TrialDaysRemaining => IsInTrial
        ? Math.Max(0, (int)(TrialEndsAt - DateTime.UtcNow).TotalDays)
        : 0;
    public bool IsAccessAllowed =>
        (Status == SubscriptionStatus.Trial && TrialEndsAt > DateTime.UtcNow) ||
        Status == SubscriptionStatus.Active;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}

public enum SubscriptionPlan
{
    Trial,
    Starter,
    Professional,
    Agency
}

public enum SubscriptionStatus
{
    Trial,
    Active,
    PastDue,
    Cancelled,
    Expired
}
