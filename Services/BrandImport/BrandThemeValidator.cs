// Services/BrandImport/BrandThemeValidator.cs
// Server-side validation/sanitization of any AI- or heuristic-generated theme proposal (spec
// section 4 "Die KI-Ausgabe muss serverseitig validiert werden" + section 8 contrast rules).
// Nothing coming out of DeterministicBrandAnalyzer (or any future real AI provider) is trusted:
// every color, font, template id and layout token is re-validated/clamped/replaced here before
// it is persisted or shown to the admin as a proposal.
using System.Text.RegularExpressions;
using GentleBook.Api.Configuration;

namespace GentleBook.Api.Services.BrandImport;

/// <summary>Fonts actually available/rendered by the booking-hub templates today (components/admin/links/types.ts FontFamily).</summary>
public static class AllowedBrandFonts
{
    // key -> (display name, isSerif) — isSerif drives the "map unknown font to closest allowed alternative" heuristic.
    public static readonly IReadOnlyDictionary<string, (string DisplayName, bool IsSerif)> Fonts =
        new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["inter"] = ("Inter", false),
            ["playfair"] = ("Playfair Display", true),
            ["montserrat"] = ("Montserrat", false),
            ["dm-serif"] = ("DM Serif Display", true),
            ["josefin"] = ("Josefin Sans", false),
        };

    public const string DefaultSansKey = "inter";
    public const string DefaultSerifKey = "playfair";

    private static readonly HashSet<string> SerifHints = new(StringComparer.OrdinalIgnoreCase)
        { "serif", "playfair", "georgia", "times", "garamond", "didot", "merriweather", "lora", "dm serif", "cormorant" };

    /// <summary>Maps an arbitrary detected font name to the closest allowed GentleBook font key. Never returns an unavailable font (spec section 8C).</summary>
    public static string MapToAllowedKey(string? detectedFontName)
    {
        if (string.IsNullOrWhiteSpace(detectedFontName)) return DefaultSansKey;

        if (Fonts.ContainsKey(detectedFontName)) return detectedFontName.ToLowerInvariant();

        var normalized = detectedFontName.Trim();
        foreach (var (key, (displayName, _)) in Fonts)
        {
            if (normalized.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        var looksSerif = SerifHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase));
        return looksSerif ? DefaultSerifKey : DefaultSansKey;
    }
}

public sealed class ValidatedTheme
{
    public string Background { get; set; } = "#FAFAFA";
    public string Surface { get; set; } = "#FFFFFF";
    public string Primary { get; set; } = "#111111";
    public string PrimaryForeground { get; set; } = "#FFFFFF";
    public string Secondary { get; set; } = "#F0F0F0";
    public string Accent { get; set; } = "#999999";
    public string Text { get; set; } = "#111111";
    public string TextMuted { get; set; } = "#666666";
    public string Border { get; set; } = "#E5E5E5";
    public string HeadingFontKey { get; set; } = AllowedBrandFonts.DefaultSansKey;
    public string BodyFontKey { get; set; } = AllowedBrandFonts.DefaultSansKey;
    public string CardRadiusPx { get; set; } = "16";
    public string ButtonRadiusPx { get; set; } = "12";
    public string ButtonStyle { get; set; } = "rounded"; // rounded | pill | square
    public string CardStyle { get; set; } = "filled"; // filled | outlined | gradient | ghost
    public string AnimationSpeed { get; set; } = "normal"; // none | slow | normal | fast
    public string TemplateId { get; set; } = "classic";
}

public static class BrandThemeValidator
{
    private static readonly Regex HexRegex = new(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedButtonStyles = new(StringComparer.OrdinalIgnoreCase) { "rounded", "pill", "square" };
    private static readonly HashSet<string> AllowedCardStyles = new(StringComparer.OrdinalIgnoreCase) { "filled", "outlined", "gradient", "ghost" };
    private static readonly HashSet<string> AllowedAnimationSpeeds = new(StringComparer.OrdinalIgnoreCase) { "none", "slow", "normal", "fast" };

    public static string SanitizeHexColor(string? candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return fallback;
        var trimmed = candidate.Trim();
        return HexRegex.IsMatch(trimmed) ? NormalizeHex(trimmed) : fallback;
    }

    private static string NormalizeHex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        return "#" + h.ToUpperInvariant();
    }

    public static string SanitizeTemplateId(string? candidate, string fallback = "classic") =>
        !string.IsNullOrWhiteSpace(candidate) && LinkPageTemplates.AllTemplateIds.Contains(candidate)
            ? candidate
            : fallback;

    public static string SanitizeButtonStyle(string? candidate, string fallback = "rounded") =>
        !string.IsNullOrWhiteSpace(candidate) && AllowedButtonStyles.Contains(candidate) ? candidate.ToLowerInvariant() : fallback;

    public static string SanitizeCardStyle(string? candidate, string fallback = "filled") =>
        !string.IsNullOrWhiteSpace(candidate) && AllowedCardStyles.Contains(candidate) ? candidate.ToLowerInvariant() : fallback;

    public static string SanitizeAnimationSpeed(string? candidate, string fallback = "normal") =>
        !string.IsNullOrWhiteSpace(candidate) && AllowedAnimationSpeeds.Contains(candidate) ? candidate.ToLowerInvariant() : fallback;

    public static string ClampPixelValue(string? candidate, double min, double max, double fallback)
    {
        var digits = candidate?.Where(c => char.IsDigit(c) || c == '.').ToArray();
        var numeric = digits != null && digits.Length > 0 && double.TryParse(new string(digits), out var v) ? v : fallback;
        return Math.Clamp(numeric, min, max).ToString("0");
    }

    /// <summary>
    /// WCAG-style relative-luminance contrast ratio. Values below ~3.0 are hard to read for UI
    /// text on colored surfaces — spec section 8A "Erzeuge keine unlesbaren Kombinationen."
    /// </summary>
    public static double ContrastRatio(string hexA, string hexB)
    {
        var lumA = RelativeLuminance(hexA);
        var lumB = RelativeLuminance(hexB);
        var lighter = Math.Max(lumA, lumB);
        var darker = Math.Min(lumA, lumB);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Returns "#111111" or "#FFFFFF", whichever contrasts better against <paramref name="backgroundHex"/>.</summary>
    public static string ReadableForegroundFor(string backgroundHex) =>
        RelativeLuminance(backgroundHex) > 0.5 ? "#111111" : "#FFFFFF";

    private static double RelativeLuminance(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        if (h.Length != 6) return 0.5;

        double Channel(int offset)
        {
            var value = Convert.ToInt32(h.Substring(offset, 2), 16) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    /// <summary>Validates every field of a proposed theme, replacing anything unsafe/unknown with a safe default, and fixing low-contrast text.</summary>
    public static ValidatedTheme Validate(ValidatedTheme candidate)
    {
        var validated = new ValidatedTheme
        {
            Background = SanitizeHexColor(candidate.Background, "#FAFAFA"),
            Surface = SanitizeHexColor(candidate.Surface, "#FFFFFF"),
            Primary = SanitizeHexColor(candidate.Primary, "#111111"),
            Secondary = SanitizeHexColor(candidate.Secondary, "#F0F0F0"),
            Accent = SanitizeHexColor(candidate.Accent, "#999999"),
            Text = SanitizeHexColor(candidate.Text, "#111111"),
            TextMuted = SanitizeHexColor(candidate.TextMuted, "#666666"),
            Border = SanitizeHexColor(candidate.Border, "#E5E5E5"),
            HeadingFontKey = AllowedBrandFonts.MapToAllowedKey(candidate.HeadingFontKey),
            BodyFontKey = AllowedBrandFonts.MapToAllowedKey(candidate.BodyFontKey),
            CardRadiusPx = ClampPixelValue(candidate.CardRadiusPx, 0, 32, 16),
            ButtonRadiusPx = ClampPixelValue(candidate.ButtonRadiusPx, 0, 999, 12),
            ButtonStyle = SanitizeButtonStyle(candidate.ButtonStyle),
            CardStyle = SanitizeCardStyle(candidate.CardStyle),
            AnimationSpeed = SanitizeAnimationSpeed(candidate.AnimationSpeed),
            TemplateId = SanitizeTemplateId(candidate.TemplateId),
        };

        validated.PrimaryForeground = ContrastRatio(validated.Primary, "#FFFFFF") >= 3.0
            ? "#FFFFFF"
            : ReadableForegroundFor(validated.Primary);

        if (ContrastRatio(validated.Text, validated.Background) < 3.0)
            validated.Text = ReadableForegroundFor(validated.Background);

        return validated;
    }
}
