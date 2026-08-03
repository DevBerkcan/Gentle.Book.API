// Data/Entities/Subscription.cs
namespace GentleBook.Api.Data.Entities;

public class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public SubscriptionPlan Plan { get; set; } = SubscriptionPlan.Trial;
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trial;
    public SubscriptionInterval Interval { get; set; } = SubscriptionInterval.Monthly;

    // ── Trial ─────────────────────────────────────────────────
    public DateTime TrialStartedAt { get; set; } = DateTime.UtcNow;
    public DateTime TrialEndsAt { get; set; } = DateTime.UtcNow.AddDays(14);
    public Guid? TrialActivatedByUserId { get; set; }
    public DateTime? AccessRestrictedAt { get; set; }

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

    // ── Self-service cancellation (tenant clicked "cancel"; access continues to period end) ──
    public DateTime? CancelRequestedAt { get; set; }
    public string? CancelReason { get; set; }

    // ── Dunning (failed recurring SEPA collection) ────────────
    public DateTime? PastDueSince { get; set; }
    public int FailedPaymentCount { get; set; }
    public string? LastFailedMolliePaymentId { get; set; }
    public bool DunningWarningEmailSent { get; set; }

    public DateTime? CancelledAt { get; set; }
    public DateTime? RetentionEndsAt { get; set; }
    public bool RetentionWarningEmailSent { get; set; }
    public DateTime? OperationalDataDeletedAt { get; set; }
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

public enum SubscriptionInterval
{
    Monthly,
    Yearly
}

public enum SubscriptionStatus
{
    PendingAcceptance,
    PendingActivation,
    Trial,
    Active,
    PastDue,
    Cancelled,
    Expired
}
