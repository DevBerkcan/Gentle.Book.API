// Gentle.Book.API.Tests/BrandThemeValidatorTests.cs
// Covers spec section 19 "Tests" → Branding: invalid colors/fonts/template-ids are rejected or
// replaced with safe values, and low-contrast text/background combinations get fixed.
using GentleBook.Api.Services.BrandImport;
using Xunit;

namespace Gentle.Book.API.Tests;

public class BrandThemeValidatorTests
{
    [Theory]
    [InlineData("#FF00AA", "#FF00AA")]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("not-a-color", "#FALLBACK")]
    [InlineData("javascript:alert(1)", "#FALLBACK")]
    [InlineData(null, "#FALLBACK")]
    public void SanitizeHexColor_InvalidValuesFallBackToDefault(string? candidate, string expected)
    {
        var result = BrandThemeValidator.SanitizeHexColor(candidate, "#FALLBACK");

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("classic", "classic")]
    [InlineData("beauty", "beauty")]
    [InlineData("some-invented-template-id", "classic")]
    [InlineData(null, "classic")]
    public void SanitizeTemplateId_UnknownIdsFallBackToClassic(string? candidate, string expected)
    {
        var result = BrandThemeValidator.SanitizeTemplateId(candidate);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Playfair Display", "playfair")]
    [InlineData("playfair", "playfair")]
    [InlineData("Georgia", "playfair")] // unknown serif -> mapped to closest allowed serif
    [InlineData("Times New Roman", "playfair")]
    [InlineData("Helvetica", "inter")] // unknown sans -> mapped to closest allowed sans
    [InlineData(null, "inter")]
    [InlineData("", "inter")]
    public void MapToAllowedKey_NeverReturnsUnavailableFont(string? detected, string expectedKey)
    {
        var result = AllowedBrandFonts.MapToAllowedKey(detected);

        Assert.Equal(expectedKey, result);
        Assert.Contains(result, AllowedBrandFonts.Fonts.Keys);
    }

    [Fact]
    public void Validate_RejectsUnsafeValuesAcrossAllFields()
    {
        var candidate = new ValidatedTheme
        {
            Background = "not-a-color",
            Surface = "javascript:alert(1)",
            Primary = "#111111",
            Secondary = "#eee",
            Accent = "#ff00ff",
            Text = "#111111",
            TextMuted = "#666",
            Border = "invalid",
            HeadingFontKey = "Some Unlicensed Font",
            BodyFontKey = "Comic Sans MS",
            CardRadiusPx = "9999",   // absurd, must be clamped
            ButtonRadiusPx = "-50",  // negative, must be clamped
            ButtonStyle = "triangle", // not an allowed style
            CardStyle = "invisible",  // not an allowed style
            AnimationSpeed = "ludicrous", // not an allowed speed
            TemplateId = "made-up-template",
        };

        var validated = BrandThemeValidator.Validate(candidate);

        Assert.Matches("^#[0-9A-F]{6}$", validated.Background);
        Assert.Matches("^#[0-9A-F]{6}$", validated.Surface);
        Assert.Matches("^#[0-9A-F]{6}$", validated.Border);
        Assert.Contains(validated.HeadingFontKey, AllowedBrandFonts.Fonts.Keys);
        Assert.Contains(validated.BodyFontKey, AllowedBrandFonts.Fonts.Keys);
        Assert.InRange(double.Parse(validated.CardRadiusPx), 0, 32);
        Assert.InRange(double.Parse(validated.ButtonRadiusPx), 0, 999);
        Assert.Contains(validated.ButtonStyle, new[] { "rounded", "pill", "square" });
        Assert.Contains(validated.CardStyle, new[] { "filled", "outlined", "gradient", "ghost" });
        Assert.Contains(validated.AnimationSpeed, new[] { "none", "slow", "normal", "fast" });
        Assert.Equal("classic", validated.TemplateId);
    }

    [Fact]
    public void Validate_FixesLowContrastTextAgainstBackground()
    {
        var candidate = new ValidatedTheme
        {
            Background = "#FFFFFF",
            Text = "#FEFEFE", // near-white text on white background: unreadable
            Primary = "#111111",
            Secondary = "#eeeeee",
            Accent = "#999999",
            Surface = "#FFFFFF",
            TextMuted = "#666666",
            Border = "#E5E5E5",
            TemplateId = "classic",
        };

        var validated = BrandThemeValidator.Validate(candidate);

        Assert.True(BrandThemeValidator.ContrastRatio(validated.Text, validated.Background) >= 3.0);
    }

    [Fact]
    public void ContrastRatio_BlackOnWhite_IsMaximal()
    {
        var ratio = BrandThemeValidator.ContrastRatio("#000000", "#FFFFFF");

        Assert.True(ratio > 20);
    }

    [Fact]
    public void ContrastRatio_SameColor_IsOne()
    {
        var ratio = BrandThemeValidator.ContrastRatio("#ABABAB", "#ABABAB");

        Assert.Equal(1.0, ratio, precision: 5);
    }
}
