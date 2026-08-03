namespace GentleBook.Api.Data.Entities;

/// <summary>
/// Versioned evidence that a business customer accepted the trial contract and AVV
/// before any customer, employee or booking data could be entered.
/// </summary>
public class TrialAccessInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcceptedAt { get; set; }
    public string? AcceptedByName { get; set; }
    public string? IpAddress { get; set; }
    public string TermsVersion { get; set; } = string.Empty;
    public string PrivacyVersion { get; set; } = string.Empty;
    public string DpaVersion { get; set; } = string.Empty;
    public bool BusinessConfirmed { get; set; }
    public bool TermsAccepted { get; set; }
    public bool PrivacyAcknowledged { get; set; }
    public bool DpaAccepted { get; set; }
    public bool NoAutomaticPaidConversionAcknowledged { get; set; }
    public string? PersonalNote { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public PlatformUser? User { get; set; }
}
