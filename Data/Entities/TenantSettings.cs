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

    // ── Legal (per-studio, see LEGAL_AUDIT_2026-09-06.md BLOCKER "Studiospezifischer
    // Datenschutzlink/-text fehlt") ────────────────────────────
    // The studio (data controller for its own customers' booking data) gets its own privacy
    // policy / imprint link shown on its booking page, instead of only GentleBook's own generic
    // ones. HTTPS-only is enforced at write time (TenantController.UpdateSettings) — never
    // trust a stored scheme, a javascript:/data: URL here would render as a clickable link on
    // every public booking page.
    public string? PrivacyUrl { get; set; }
    public string? ImprintUrl { get; set; }

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

    // Agency-exclusive chat booking assistant widget, shown bottom-right on the public booking
    // page alongside (never instead of) the click-through wizard. A dedicated column rather than
    // folded into LinktreeConfig's JSON blob — that blob is fine-grained visual preferences
    // (colors, layout), this is a real feature on/off switch, same idiom as
    // TenantIndustrySettings.IsFinderEnabled for the rule-based Finder.
    public bool IsChatbotEnabled { get; set; } = false;

    // Admin-tunable cost/abuse control: max free-text messages (i.e. real LLM calls, see
    // PublicAiChatbotController) a single conversation may send before the widget declines
    // further AI turns and points the customer to the studio directly. Counted per AiConversation,
    // not per customer/IP — there's no customer identity on this anonymous endpoint.
    public int ChatbotMaxMessagesPerConversation { get; set; } = 20;

    // Second, independent cap: without this, a visitor could trivially reset the per-conversation
    // counter above by just reloading the widget (a new browser-side conversationId starts at 0
    // again) and burn through the tenant's whole monthly AI budget alone. Enforced per IP address
    // (hashed, see AiConversation.VisitorHash) across a rolling UTC calendar day — there's no
    // login/session token on this anonymous endpoint, so IP+day is the closest available proxy for
    // "one visitor," same idiom already used for the platform-wide finder-limit rate limiter.
    public int ChatbotMaxMessagesPerVisitorPerDay { get; set; } = 30;

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
