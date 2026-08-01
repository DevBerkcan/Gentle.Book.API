// Data/Entities/BrandImportEntities.cs
// "AI Brand Import" feature — an admin enters their existing company website URL and
// GentleBook derives a branding draft (colors, fonts, logo candidates, template suggestion)
// for the public booking-hub page. The external site is only ever a data/inspiration source:
// nothing here is applied to a tenant's live design until the admin explicitly confirms it
// (see BrandThemeProposal.IsApplied / BrandImportResult.AppliedAt).
namespace GentleBook.Api.Data.Entities;

public enum BrandImportJobStatus
{
    Queued,
    Fetching,
    Extracting,
    Analyzing,
    Ready,
    Applied,
    Failed,
    Archived
}

/// <summary>
/// One analysis run of a tenant's external website. Short-lived; the durable output lives in
/// <see cref="BrandImportResult"/>. Kept separate from the result so progress/status can be
/// polled cheaply and so retries/re-analysis don't mutate a previously-shown result.
/// </summary>
public class BrandImportJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string SourceUrl { get; set; } = string.Empty;

    public BrandImportJobStatus Status { get; set; } = BrandImportJobStatus.Queued;

    /// <summary>Machine-readable error code, e.g. "ssrf_blocked", "timeout", "too_large". Never contains raw exception details.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>User-safe error message (German), never includes stack traces, internal hostnames or IPs.</summary>
    public string? ErrorMessageSafe { get; set; }

    public int RetryCount { get; set; } = 0;

    public DateTime? StartedOn { get; set; }
    public DateTime? CompletedOn { get; set; }

    public Guid CreatedBy { get; set; }
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    public Guid? ResultId { get; set; }
}

/// <summary>
/// Deterministic-extraction + AI-classification output for one completed job. Immutable once
/// created (a re-analysis creates a new Job + Result rather than overwriting this one).
/// </summary>
public class BrandImportResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid JobId { get; set; }

    public string? WebsiteTitle { get; set; }
    public string? BrandStyle { get; set; }

    /// <summary>
    /// JSON blob: { colors, fonts, componentStyle, content: { description, heading, cta, phone,
    /// email, address, openingHours, socialLinks[], services[] }, logoCandidates[], warnings[] }
    /// Flexible by design (mirrors the spec's DETECTED_DATA_JSON) — validated fields (colors,
    /// fonts, template ids) are re-validated server-side again at apply-time regardless of what
    /// is stored here.
    /// </summary>
    public string DetectedDataJson { get; set; } = "{}";

    public double Confidence { get; set; }

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedOn { get; set; }
}

/// <summary>One of (at least) three theme/template proposals generated for a BrandImportResult.</summary>
public class BrandThemeProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ImportResultId { get; set; }

    /// <summary>Stable key used by the frontend, e.g. "original" | "modernized" | "premium".</summary>
    public string ProposalKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Reason { get; set; }

    /// <summary>Must be one of the existing template ids in Configuration/LinkPageTemplates — re-validated server-side before apply.</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>JSON: { background, surface, primary, primaryForeground, secondary, accent, text, textMuted, border, headingFont, bodyFont, cardRadius, buttonRadius, buttonStyle, cardStyle, animationPreset }</summary>
    public string ThemeJson { get; set; } = "{}";

    /// <summary>JSON: { description, heading, cta, socialLinks[], openingHours, address, phone, email, services[] } — content suggestions only, never auto-applied.</summary>
    public string ContentJson { get; set; } = "{}";

    public bool IsSelected { get; set; }
    public bool IsApplied { get; set; }

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedOn { get; set; }
}

public enum BrandAssetType
{
    Logo,
    Favicon,
    OgImage,
    HeaderImage
}

/// <summary>
/// A candidate image discovered on the external site (logo, favicon, ...). Only the SourceUrl
/// is stored (never re-hosted/mirrored automatically) — see spec section 15: images must not be
/// copied without explicit admin selection + confirmation.
/// </summary>
public class BrandAssetCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ImportResultId { get; set; }

    public BrandAssetType AssetType { get; set; }
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>Where the candidate was found, e.g. "link[rel=icon]", "og:image", "json-ld:logo" — shown to admin for transparency, never user input.</summary>
    public string? DiscoveryHint { get; set; }

    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? ContentType { get; set; }

    public bool IsSelected { get; set; }

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}
