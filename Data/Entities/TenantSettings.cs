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

    // ── Booking Configuration ─────────────────────────────────
    public int BookingIntervalMinutes { get; set; } = 30;
    public int MaxAdvanceBookingDays { get; set; } = 60;
    public string TimeZone { get; set; } = "Europe/Berlin";
    public string DefaultCurrency { get; set; } = "EUR";

    // ── Custom Texts ──────────────────────────────────────────
    public string? WelcomeMessage { get; set; }
    public string? CancellationPolicy { get; set; }

    // ── Linktree Customization ────────────────────────────────
    public string LinktreeStyle { get; set; } = "gradient";
    public string? LinktreeConfig { get; set; } // JSON blob for fine-grained design

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
