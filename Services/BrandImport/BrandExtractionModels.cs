// Services/BrandImport/BrandExtractionModels.cs
// Plain data-transfer models for the deterministic extraction pipeline (spec section 7/8).
// None of these carry raw HTML/CSS/JS — only already-parsed, primitive values — so anything
// downstream (AI analyzer, validators, persistence) never has to deal with unsanitized markup.
namespace GentleBook.Api.Services.BrandImport;

public sealed class ExtractedLogoCandidate
{
    public string SourceUrl { get; set; } = string.Empty;
    public string DiscoveryHint { get; set; } = string.Empty;
    public int? Width { get; set; }
    public int? Height { get; set; }
}

public sealed class ExtractedContent
{
    public string? Description { get; set; }
    public string? Heading { get; set; }
    public string? CallToAction { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? OpeningHours { get; set; }
    public List<string> SocialLinks { get; set; } = new();
    public List<DetectedServiceDto> Services { get; set; } = new();
}

/// <summary>
/// One bookable service/treatment as read off a tenant's existing website by
/// <see cref="IServiceListExtractor"/>. Price/duration are null whenever the source text didn't
/// state them — callers must not invent values, only apply sane fallbacks at import time.
/// </summary>
public sealed record DetectedServiceDto(string Name, decimal? PriceAmount, string? Currency, int? DurationMinutes);

public sealed class ExtractedComponentStyle
{
    public string? BorderRadius { get; set; }
    public bool HasShadow { get; set; }
    public string? ButtonShape { get; set; } // rounded | pill | square
    public string? Density { get; set; } // tight | normal | airy
    public bool IsDarkTheme { get; set; }
}

public sealed class BrandExtractionResult
{
    public string? WebsiteTitle { get; set; }
    public string? ThemeColorMeta { get; set; }
    public List<string> DetectedColors { get; set; } = new(); // hex, priority order (most-frequent/most-significant first)
    public List<string> DetectedFonts { get; set; } = new();
    public List<ExtractedLogoCandidate> LogoCandidates { get; set; } = new();
    public string? FaviconUrl { get; set; }
    public ExtractedContent Content { get; set; } = new();
    public ExtractedComponentStyle ComponentStyle { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
