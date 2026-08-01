// Services/BrandImport/BrandAiAnalyzer.cs
// AI-classification step (spec section 9). Only structured, already-extracted data is ever
// passed in — never raw HTML/CSS/JS — and the output schema is fixed, so nothing here can
// smuggle arbitrary markup or scripts back out. Since Gentle.Book.API only has a
// NullAiProviderAdapter today (see Services/AI/AiProviderAdapter.cs), DeterministicBrandAnalyzer
// is the always-available, no-AI fallback described in spec sections 9/18 ("Fallback ohne KI").
// A future real provider would implement IBrandAiAnalyzer and still pipe its output through
// BrandThemeValidator exactly like this one does — validation never trusts the source.
using GentleBook.Api.Configuration;

namespace GentleBook.Api.Services.BrandImport;

public sealed class BrandAnalysisProposal
{
    public string ProposalKey { get; set; } = string.Empty; // "original" | "modernized" | "premium"
    public string Name { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public ValidatedTheme Theme { get; set; } = new();
}

public sealed class BrandAnalysisOutput
{
    public string BrandStyle { get; set; } = "modern-minimal";
    public double Confidence { get; set; } = 0.5;
    public List<string> RecommendedTemplateIds { get; set; } = new();
    public List<BrandAnalysisProposal> Proposals { get; set; } = new();
    public string DetectedTone { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
    public bool UsedAiFallback { get; set; }
}

public interface IBrandAiAnalyzer
{
    Task<BrandAnalysisOutput> AnalyzeAsync(BrandExtractionResult extraction, CancellationToken cancellationToken);
}

public sealed class DeterministicBrandAnalyzer : IBrandAiAnalyzer
{
    private readonly ILogger<DeterministicBrandAnalyzer> _logger;

    public DeterministicBrandAnalyzer(ILogger<DeterministicBrandAnalyzer> logger)
    {
        _logger = logger;
    }

    public Task<BrandAnalysisOutput> AnalyzeAsync(BrandExtractionResult extraction, CancellationToken cancellationToken)
    {
        var warnings = new List<string>(extraction.Warnings);

        var primaryCandidate = extraction.DetectedColors.FirstOrDefault() ?? extraction.ThemeColorMeta;
        var secondaryCandidate = extraction.DetectedColors.Skip(1).FirstOrDefault();
        var accentCandidate = extraction.DetectedColors.Skip(2).FirstOrDefault();

        var headingFontRaw = extraction.DetectedFonts.FirstOrDefault();
        var bodyFontRaw = extraction.DetectedFonts.Skip(1).FirstOrDefault() ?? headingFontRaw;
        var isSerifBrand = !string.IsNullOrEmpty(headingFontRaw) && AllowedBrandFonts.MapToAllowedKey(headingFontRaw) is "playfair" or "dm-serif";

        var brandStyle = ClassifyStyle(primaryCandidate, isSerifBrand, extraction.ComponentStyle);
        var recommendedTemplateId = RecommendTemplateId(brandStyle, extraction.ComponentStyle);

        var baseTheme = new ValidatedTheme
        {
            Primary = BrandThemeValidator.SanitizeHexColor(primaryCandidate, "#111111"),
            Secondary = BrandThemeValidator.SanitizeHexColor(secondaryCandidate, "#F0EDE7"),
            Accent = BrandThemeValidator.SanitizeHexColor(accentCandidate ?? secondaryCandidate, "#999999"),
            Background = "#FAFAFA",
            Surface = "#FFFFFF",
            Text = "#1A1A1A",
            TextMuted = "#6B6B6B",
            Border = "#E5E5E5",
            HeadingFontKey = AllowedBrandFonts.MapToAllowedKey(headingFontRaw),
            BodyFontKey = AllowedBrandFonts.MapToAllowedKey(bodyFontRaw),
            CardRadiusPx = extraction.ComponentStyle.BorderRadius?.TrimEnd('p', 'x') ?? "16",
            ButtonRadiusPx = extraction.ComponentStyle.ButtonShape == "pill" ? "999" : extraction.ComponentStyle.ButtonShape == "square" ? "4" : "12",
            ButtonStyle = extraction.ComponentStyle.ButtonShape ?? "rounded",
            CardStyle = extraction.ComponentStyle.HasShadow ? "filled" : "outlined",
            AnimationSpeed = "normal",
            TemplateId = recommendedTemplateId,
        };

        var proposals = new List<BrandAnalysisProposal>
        {
            BuildOriginalProposal(baseTheme, recommendedTemplateId),
            BuildModernizedProposal(baseTheme, recommendedTemplateId),
            BuildPremiumProposal(baseTheme, recommendedTemplateId, brandStyle),
        };

        var output = new BrandAnalysisOutput
        {
            BrandStyle = brandStyle,
            Confidence = extraction.DetectedColors.Count > 0 && extraction.DetectedFonts.Count > 0 ? 0.75 : 0.4,
            RecommendedTemplateIds = proposals.Select(p => p.TemplateId).Distinct().ToList(),
            Proposals = proposals,
            DetectedTone = DescribeTone(brandStyle),
            Warnings = warnings,
            UsedAiFallback = true,
        };

        _logger.LogInformation("Brand analysis completed deterministically (no external AI provider configured). Style={BrandStyle}", brandStyle);
        return Task.FromResult(output);
    }

    private static BrandAnalysisProposal BuildOriginalProposal(ValidatedTheme baseTheme, string templateId) => new()
    {
        ProposalKey = "original",
        Name = "Originalgetreu",
        Reason = "Orientiert sich eng an den erkannten Markenfarben und bleibt innerhalb des GentleBook-Designsystems.",
        TemplateId = templateId,
        Theme = BrandThemeValidator.Validate(new ValidatedTheme
        {
            Background = baseTheme.Background,
            Surface = baseTheme.Surface,
            Primary = baseTheme.Primary,
            Secondary = baseTheme.Secondary,
            Accent = baseTheme.Accent,
            Text = baseTheme.Text,
            TextMuted = baseTheme.TextMuted,
            Border = baseTheme.Border,
            HeadingFontKey = baseTheme.HeadingFontKey,
            BodyFontKey = baseTheme.BodyFontKey,
            CardRadiusPx = baseTheme.CardRadiusPx,
            ButtonRadiusPx = baseTheme.ButtonRadiusPx,
            ButtonStyle = baseTheme.ButtonStyle,
            CardStyle = baseTheme.CardStyle,
            AnimationSpeed = "none",
            TemplateId = templateId,
        }),
    };

    private static BrandAnalysisProposal BuildModernizedProposal(ValidatedTheme baseTheme, string templateId) => new()
    {
        ProposalKey = "modernized",
        Name = "Modernisiert",
        Reason = "Übernimmt Logo und Markenfarben, modernisiert Karten, Abstände und Typografie.",
        TemplateId = templateId,
        Theme = BrandThemeValidator.Validate(new ValidatedTheme
        {
            Background = "#FAF9F7",
            Surface = "#FFFFFF",
            Primary = baseTheme.Primary,
            Secondary = baseTheme.Secondary,
            Accent = baseTheme.Accent,
            Text = "#1A1A1A",
            TextMuted = "#6B6B6B",
            Border = "#EDEAE4",
            HeadingFontKey = baseTheme.HeadingFontKey,
            BodyFontKey = "inter",
            CardRadiusPx = "20",
            ButtonRadiusPx = "999",
            ButtonStyle = "pill",
            CardStyle = "filled",
            AnimationSpeed = "normal",
            TemplateId = templateId,
        }),
    };

    private static BrandAnalysisProposal BuildPremiumProposal(ValidatedTheme baseTheme, string templateId, string brandStyle)
    {
        var premiumTemplateId = brandStyle switch
        {
            "luxury-beauty" => "beauty",
            "bold-vibrant" => "neon",
            "editorial-magazine" => "magazine",
            _ => templateId,
        };

        return new BrandAnalysisProposal
        {
            ProposalKey = "premium",
            Name = "Premium",
            Reason = "Übernimmt zentrale Branding-Merkmale und ergänzt hochwertigere Layouts und Animationen.",
            TemplateId = BrandThemeValidator.SanitizeTemplateId(premiumTemplateId),
            Theme = BrandThemeValidator.Validate(new ValidatedTheme
            {
                Background = "#F7F5F2",
                Surface = "#FFFFFF",
                Primary = baseTheme.Primary,
                Secondary = baseTheme.Secondary,
                Accent = baseTheme.Accent,
                Text = "#201C18",
                TextMuted = "#756C64",
                Border = "#E4D9CC",
                HeadingFontKey = baseTheme.HeadingFontKey == "inter" ? "playfair" : baseTheme.HeadingFontKey,
                BodyFontKey = "inter",
                CardRadiusPx = "24",
                ButtonRadiusPx = "999",
                ButtonStyle = "pill",
                CardStyle = "gradient",
                AnimationSpeed = "fast",
                TemplateId = BrandThemeValidator.SanitizeTemplateId(premiumTemplateId),
            }),
        };
    }

    private static string ClassifyStyle(string? primaryColor, bool isSerifBrand, ExtractedComponentStyle componentStyle)
    {
        if (isSerifBrand && !componentStyle.IsDarkTheme) return "luxury-beauty";
        if (componentStyle.IsDarkTheme) return "bold-vibrant";
        if (componentStyle.ButtonShape == "square") return "editorial-magazine";
        return "modern-minimal";
    }

    private static string RecommendTemplateId(string brandStyle, ExtractedComponentStyle componentStyle) => brandStyle switch
    {
        "luxury-beauty" => "beauty",
        "bold-vibrant" => "neon",
        "editorial-magazine" => "magazine",
        _ => componentStyle.ButtonShape == "pill" ? "soft" : "classic",
    };

    private static string DescribeTone(string brandStyle) => brandStyle switch
    {
        "luxury-beauty" => "elegant, ruhig und hochwertig",
        "bold-vibrant" => "modern, laut und energiegeladen",
        "editorial-magazine" => "redaktionell, strukturiert und selbstbewusst",
        _ => "klar, freundlich und zeitgemäß",
    };
}
