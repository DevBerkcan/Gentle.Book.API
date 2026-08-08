// Data/Entities/TenantSettings.cs
namespace GentleBook.Api.Data.Entities;

public class TenantSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    // ── Branding ──────────────────────────────────────────────
    public string CompanyName { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    public string? LogoUrl { get; set; }
    public string PrimaryColor { get; set; } = "#000000";
    public string SecondaryColor { get; set; } = "#ffffff";
    public string AccentColor { get; set; } = "#cccccc";

    // ── Contact Info ──────────────────────────────────────────
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? Address { get; set; }

    // ── Billing Address (structured, for Mollie + CRM invoicing) ──
    public string? LegalCompanyName { get; set; }
    public string? BillingStreet { get; set; }
    public string? BillingZipCode { get; set; }
    public string? BillingCity { get; set; }
    public string? BillingCountry { get; set; } // ISO 3166-1 alpha-2, e.g. "DE"
    public string? VatId { get; set; }

    public bool HasCompleteBillingProfile =>
        !string.IsNullOrWhiteSpace(LegalCompanyName) &&
        !string.IsNullOrWhiteSpace(BillingStreet) &&
        !string.IsNullOrWhiteSpace(BillingZipCode) &&
        !string.IsNullOrWhiteSpace(BillingCity) &&
        !string.IsNullOrWhiteSpace(BillingCountry);

    // ── Booking Configuration ─────────────────────────────────
    public int BookingIntervalMinutes { get; set; } = 30;
    public int MaxAdvanceBookingDays { get; set; } = 60;
    public string TimeZone { get; set; } = "Europe/Berlin";
    public string DefaultCurrency { get; set; } = "EUR";

    // ── Custom Texts ──────────────────────────────────────────
    public string? WelcomeMessage { get; set; }
    public string? CancellationPolicy { get; set; }

    // ── Cancellation Policy ───────────────────────────────────
    public int CancellationHoursNotice { get; set; } = 0;    // 0 = immer kostenlos stornierbar
    public decimal CancellationFeePercent { get; set; } = 0m; // % des Servicepreises als Stornogebühr

    // ── Linktree Customization ────────────────────────────────
    public string LinktreeStyle { get; set; } = "gradient";
    public string? LinktreeConfig { get; set; } // JSON blob for fine-grained design

    // ── AI Brand Import (website → branding draft) ────────────
    // The admin must explicitly confirm they're entitled to use this website's content/branding
    // before any analysis runs (see spec section 15 "Datenschutz und Rechte"). Stored here rather
    // than a separate TENANT_WEBSITE_SETTINGS table since Website already lives on this entity.
    public bool WebsiteConsentConfirmed { get; set; }
    public DateTime? WebsiteConsentConfirmedAt { get; set; }
    public Guid? WebsiteConsentConfirmedBy { get; set; }
    public DateTime? LastBrandAnalysisOn { get; set; }
    public string? LastBrandAnalysisStatus { get; set; } // mirrors BrandImportJobStatus as string

    // ── Treuepunkte (Agency) ───────────────────────────────────
    public int LoyaltyPointsPerBooking { get; set; } = 0; // 0 = Feature deaktiviert

    // ── Stammkunden-Belohnung (Agency) ──────────────────────────
    public int LoyaltyRewardEveryNVisits { get; set; } = 0; // 0 = deaktiviert
    public string LoyaltyRewardType { get; set; } = "MonetaryValue"; // "MonetaryValue" | "PercentageDiscount" | "SessionPackage"
    public decimal? LoyaltyRewardValue { get; set; }

    // ── Team-Reports (Agency) ──────────────────────────────────
    public string DigestFrequency { get; set; } = "None"; // "None" | "Daily" | "Weekly"

    // ── Eigene Domain (Agency) ─────────────────────────────────
    // No automatic Vercel provisioning yet (see plan) — SuperAdmin flips CustomDomainStatus to
    // "Verified" by hand once the domain has been added in the Vercel dashboard.
    public string? CustomDomain { get; set; }
    public string CustomDomainStatus { get; set; } = "None"; // "None" | "PendingVerification" | "Verified" | "Failed"
    public DateTime? CustomDomainRequestedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
