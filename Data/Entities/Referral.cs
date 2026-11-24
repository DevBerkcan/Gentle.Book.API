namespace GentleBook.Api.Data.Entities;

/// <summary>
/// "Freunde machen Freude": one new customer brought in by an existing customer's personal
/// referral link (/booking/{slug}/book?ref={Customer.ReferralCode}).
///
/// Lifecycle: created as Pending on the friend's FIRST booking (only if she is genuinely new to
/// this studio and not the referrer herself); becomes Qualified the first time any of her
/// bookings is Completed (= she actually came — GentleBook doesn't process appointment payments,
/// so "erster wahrgenommener Termin" is the qualifying event). On qualification BOTH sides get
/// the studio's reward (RewardText snapshot); the studio marks each side "eingelöst" separately.
/// </summary>
public class Referral
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReferrerCustomerId { get; set; }
    public Guid ReferredCustomerId { get; set; }

    /// <summary>The friend's first booking (the one made via the referral link).</summary>
    public Guid? FirstBookingId { get; set; }
    /// <summary>The booking whose completion qualified the referral (may differ if the first was cancelled).</summary>
    public Guid? QualifyingBookingId { get; set; }

    public ReferralStatus Status { get; set; } = ReferralStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? QualifiedAt { get; set; }

    /// <summary>Reward as configured by the studio at qualification time — later settings changes don't rewrite earned rewards.</summary>
    public string? RewardText { get; set; }
    public DateTime? ReferrerRewardRedeemedAt { get; set; }
    public DateTime? ReferredRewardRedeemedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public Customer ReferrerCustomer { get; set; } = null!;
    public Customer ReferredCustomer { get; set; } = null!;
}

public enum ReferralStatus
{
    Pending,
    Qualified,
}
