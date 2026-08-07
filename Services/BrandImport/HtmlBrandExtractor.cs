// Services/BrandImport/HtmlBrandExtractor.cs
// Deterministic, non-AI extraction step (spec section 7 "Fetch-Strategie" MVP + section 8
// "Branding-Extraktion"). Uses HtmlAgilityPack purely as a DOM parser — it never executes
// scripts or CSS, it only walks the already-fetched markup. Same-domain stylesheets are fetched
// through the same SSRF-safe fetcher used for the HTML itself, with their own size/count caps.
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace GentleBook.Api.Services.BrandImport;

public interface IHtmlBrandExtractor
{
    Task<BrandExtractionResult> ExtractAsync(string html, Uri pageUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort guess at a linked "pricing/services" page (matched by anchor text/href against
    /// a small keyword list) — real price lists frequently live on a subpage rather than the
    /// homepage. Returns null when no plausible candidate is found; never throws.
    /// </summary>
    string? FindPricingPageUrl(string html, Uri pageUrl);

    /// <summary>Strips script/style/nav/footer noise and returns the page's visible text, for feeding to the service-list LLM extractor.</summary>
    string ExtractVisibleText(string html);
}

public sealed class HtmlBrandExtractor : IHtmlBrandExtractor
{
    private const int MaxStylesheets = 5;
    private const long MaxStylesheetBytes = 300 * 1024;
    private const int MaxImagesConsidered = 40;

    private static readonly Regex HexColorRegex = new(@"#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})\b", RegexOptions.Compiled);
    private static readonly Regex CssCustomPropRegex = new(@"--([a-zA-Z0-9-]+)\s*:\s*([^;]+);", RegexOptions.Compiled);
    private static readonly Regex FontFamilyRegex = new(@"font-family\s*:\s*([^;}""']+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BorderRadiusRegex = new(@"border-radius\s*:\s*([0-9.]+)(px|rem|em|%)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BoxShadowRegex = new(@"box-shadow\s*:\s*[^;]+;", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly (string Domain, string Label)[] SocialDomains =
    {
        ("instagram.com", "Instagram"), ("facebook.com", "Facebook"), ("tiktok.com", "TikTok"),
        ("youtube.com", "YouTube"), ("wa.me", "WhatsApp"), ("linkedin.com", "LinkedIn"),
        ("twitter.com", "Twitter"), ("x.com", "X"), ("pinterest.com", "Pinterest"),
    };

    private readonly ISafeWebsiteFetcher _fetcher;
    private readonly ILogger<HtmlBrandExtractor> _logger;

    public HtmlBrandExtractor(ISafeWebsiteFetcher fetcher, ILogger<HtmlBrandExtractor> logger)
    {
        _fetcher = fetcher;
        _logger = logger;
    }

    public async Task<BrandExtractionResult> ExtractAsync(string html, Uri pageUrl, CancellationToken cancellationToken)
    {
        var result = new BrandExtractionResult();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var root = doc.DocumentNode;

        result.WebsiteTitle = Clean(root.SelectSingleNode("//title")?.InnerText);

        // ── Meta tags & OpenGraph ───────────────────────────────────────────
        var metaTags = root.SelectNodes("//meta") ?? new HtmlNodeCollection(null);
        string? ogDescription = null, ogTitle = null, ogImage = null;
        foreach (var meta in metaTags)
        {
            var name = meta.GetAttributeValue("name", "").ToLowerInvariant();
            var property = meta.GetAttributeValue("property", "").ToLowerInvariant();
            var content = meta.GetAttributeValue("content", "");
            if (string.IsNullOrWhiteSpace(content)) continue;

            if (name == "theme-color") result.ThemeColorMeta = content.Trim();
            if (name == "description") result.Content.Description = Clean(content);
            if (property == "og:description") ogDescription = Clean(content);
            if (property == "og:title") ogTitle = Clean(content);
            if (property == "og:image") ogImage = ResolveUrl(pageUrl, content);
        }

        result.Content.Description ??= ogDescription;
        result.Content.Heading = ogTitle ?? Clean(root.SelectSingleNode("//h1")?.InnerText);

        // ── Favicon ──────────────────────────────────────────────────────────
        var faviconNode = root.SelectSingleNode("//link[translate(@rel,'ICON','icon')='icon' or @rel='shortcut icon']");
        if (faviconNode != null)
            result.FaviconUrl = ResolveUrl(pageUrl, faviconNode.GetAttributeValue("href", ""));

        var appleTouchIcon = root.SelectSingleNode("//link[@rel='apple-touch-icon']");
        if (appleTouchIcon != null)
        {
            result.LogoCandidates.Add(new ExtractedLogoCandidate
            {
                SourceUrl = ResolveUrl(pageUrl, appleTouchIcon.GetAttributeValue("href", "")),
                DiscoveryHint = "link[rel=apple-touch-icon]",
            });
        }

        if (!string.IsNullOrEmpty(ogImage))
        {
            result.LogoCandidates.Add(new ExtractedLogoCandidate { SourceUrl = ogImage, DiscoveryHint = "og:image" });
        }

        // ── Logo candidates from <img> ──────────────────────────────────────
        var images = (root.SelectNodes("//img") ?? new HtmlNodeCollection(null)).Take(MaxImagesConsidered);
        foreach (var img in images)
        {
            var cls = img.GetAttributeValue("class", "");
            var id = img.GetAttributeValue("id", "");
            var alt = img.GetAttributeValue("alt", "");
            var src = img.GetAttributeValue("src", "");
            if (string.IsNullOrWhiteSpace(src)) continue;

            var looksLikeLogo = ContainsLogoHint(cls) || ContainsLogoHint(id) || ContainsLogoHint(alt) || ContainsLogoHint(src);
            if (!looksLikeLogo) continue;

            result.LogoCandidates.Add(new ExtractedLogoCandidate
            {
                SourceUrl = ResolveUrl(pageUrl, src),
                DiscoveryHint = "img[logo-hint]",
                Width = TryParseInt(img.GetAttributeValue("width", null)),
                Height = TryParseInt(img.GetAttributeValue("height", null)),
            });
        }

        // ── JSON-LD structured data ──────────────────────────────────────────
        ExtractJsonLd(root, pageUrl, result);

        // ── Contact info heuristics (mailto:/tel: links) ────────────────────
        var anchors = root.SelectNodes("//a[@href]") ?? new HtmlNodeCollection(null);
        foreach (var a in anchors)
        {
            var href = a.GetAttributeValue("href", "");
            if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && result.Content.Email == null)
                result.Content.Email = href[7..].Split('?')[0].Trim();
            else if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) && result.Content.Phone == null)
                result.Content.Phone = href[4..].Trim();
            else
            {
                foreach (var (domain, label) in SocialDomains)
                {
                    if (href.Contains(domain, StringComparison.OrdinalIgnoreCase) &&
                        !result.Content.SocialLinks.Any(l => l.Contains(domain, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Content.SocialLinks.Add(href);
                        _ = label; // label reserved for future UI grouping
                    }
                }
            }
        }

        // ── Call-to-action heuristic: first prominent button/link text ─────
        var ctaNode = root.SelectSingleNode("//a[contains(translate(@class,'BTN','btn'),'btn') or contains(translate(@class,'BUTTON','button'),'button')]")
            ?? root.SelectSingleNode("//button");
        result.Content.CallToAction = Clean(ctaNode?.InnerText);

        // ── Inline <style> + same-domain stylesheets ────────────────────────
        var cssBuilder = new System.Text.StringBuilder();
        foreach (var styleNode in root.SelectNodes("//style") ?? new HtmlNodeCollection(null))
            cssBuilder.AppendLine(styleNode.InnerText);

        var stylesheetLinks = (root.SelectNodes("//link[translate(@rel,'STYLESHEET','stylesheet')='stylesheet']") ?? new HtmlNodeCollection(null))
            .Select(n => n.GetAttributeValue("href", ""))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => ResolveUrl(pageUrl, h))
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var parsed) && string.Equals(parsed.Host, pageUrl.Host, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .Take(MaxStylesheets)
            .ToList();

        foreach (var sheetUrl in stylesheetLinks)
        {
            if (!Uri.TryCreate(sheetUrl, UriKind.Absolute, out var sheetUri)) continue;
            try
            {
                var fetch = await _fetcher.FetchAsync(
                    sheetUri,
                    new[] { "text/css" },
                    MaxStylesheetBytes,
                    maxRedirects: 2,
                    timeout: TimeSpan.FromSeconds(8),
                    cancellationToken);

                if (fetch.Success && fetch.Body != null)
                    cssBuilder.AppendLine(fetch.Body);
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Stylesheet fetch skipped: {ErrorType}", ex.GetType().Name);
            }
        }

        AnalyzeCss(cssBuilder.ToString(), result);

        if (result.DetectedColors.Count == 0)
            result.Warnings.Add("no_colors_detected");
        if (result.LogoCandidates.Count == 0 && string.IsNullOrEmpty(result.FaviconUrl))
            result.Warnings.Add("no_logo_detected");
        if (result.DetectedFonts.Count == 0)
            result.Warnings.Add("no_fonts_detected");

        return result;
    }

    private static readonly string[] PricingPageKeywords =
    {
        "preise", "preisliste", "leistungen", "behandlungen", "service", "services", "pricing", "treatments",
    };

    public string? FindPricingPageUrl(string html, Uri pageUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var candidates = (doc.DocumentNode.SelectNodes("//a[@href]") ?? new HtmlNodeCollection(null))
            .Select(a => (Href: a.GetAttributeValue("href", ""), Text: Clean(a.InnerText) ?? ""))
            .Where(a => !string.IsNullOrWhiteSpace(a.Href) && !a.Href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && !a.Href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase));

        foreach (var keyword in PricingPageKeywords)
        {
            var match = candidates.FirstOrDefault(a =>
                a.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                a.Href.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            if (match.Href == null) continue;

            var resolved = ResolveUrl(pageUrl, match.Href);
            if (Uri.TryCreate(resolved, UriKind.Absolute, out var resolvedUri) &&
                string.Equals(resolvedUri.Host, pageUrl.Host, StringComparison.OrdinalIgnoreCase) &&
                (resolvedUri.Scheme == Uri.UriSchemeHttp || resolvedUri.Scheme == Uri.UriSchemeHttps))
                return resolvedUri.ToString();
        }

        return null;
    }

    public string ExtractVisibleText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var noise in doc.DocumentNode.SelectNodes("//script|//style|//noscript") ?? new HtmlNodeCollection(null))
            noise.Remove();

        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        var text = System.Net.WebUtility.HtmlDecode(body.InnerText);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static void ExtractJsonLd(HtmlNode root, Uri pageUrl, BrandExtractionResult result)
    {
        var scripts = root.SelectNodes("//script[@type='application/ld+json']");
        if (scripts == null) return;

        foreach (var script in scripts)
        {
            string json = script.InnerText;
            if (string.IsNullOrWhiteSpace(json)) continue;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                ExtractFromJsonLdElement(doc.RootElement, pageUrl, result);
            }
            catch
            {
                // Malformed JSON-LD is common on real-world sites — ignore rather than fail the whole analysis.
            }
        }
    }

    private static void ExtractFromJsonLdElement(System.Text.Json.JsonElement element, Uri pageUrl, BrandExtractionResult result)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ExtractFromJsonLdElement(item, pageUrl, result);
            return;
        }

        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        if (element.TryGetProperty("logo", out var logoProp))
        {
            var logoUrl = logoProp.ValueKind == System.Text.Json.JsonValueKind.String
                ? logoProp.GetString()
                : logoProp.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (!string.IsNullOrWhiteSpace(logoUrl))
                result.LogoCandidates.Add(new ExtractedLogoCandidate { SourceUrl = ResolveUrl(pageUrl, logoUrl), DiscoveryHint = "json-ld:logo" });
        }

        if (element.TryGetProperty("telephone", out var tel) && tel.ValueKind == System.Text.Json.JsonValueKind.String)
            result.Content.Phone ??= tel.GetString();

        if (element.TryGetProperty("email", out var email) && email.ValueKind == System.Text.Json.JsonValueKind.String)
            result.Content.Email ??= email.GetString();

        if (element.TryGetProperty("address", out var address))
        {
            result.Content.Address ??= FormatJsonLdAddress(address);
        }

        if (element.TryGetProperty("sameAs", out var sameAs) && sameAs.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var link in sameAs.EnumerateArray())
            {
                if (link.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var value = link.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && !result.Content.SocialLinks.Contains(value))
                        result.Content.SocialLinks.Add(value);
                }
            }
        }

        if (element.TryGetProperty("openingHoursSpecification", out _) && result.Content.OpeningHours == null)
            result.Content.OpeningHours = "erkannt"; // structured schedule present — details shown as a suggestion only, never auto-applied verbatim
    }

    private static string? FormatJsonLdAddress(System.Text.Json.JsonElement address)
    {
        if (address.ValueKind == System.Text.Json.JsonValueKind.String) return address.GetString();
        if (address.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        string? Get(string key) => address.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        var parts = new[] { Get("streetAddress"), Get("postalCode"), Get("addressLocality") }.Where(p => !string.IsNullOrWhiteSpace(p));
        var joined = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    private static void AnalyzeCss(string css, BrandExtractionResult result)
    {
        if (string.IsNullOrWhiteSpace(css)) return;

        var colorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in HexColorRegex.Matches(css))
        {
            var hex = NormalizeHex(m.Value);
            if (hex == null) continue;
            colorCounts[hex] = colorCounts.GetValueOrDefault(hex) + 1;
        }

        result.DetectedColors = colorCounts
            .Where(kv => !IsNearGrayscale(kv.Key)) // pure black/white/gray rarely represents "brand color"
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .Take(8)
            .ToList();

        if (result.DetectedColors.Count == 0 && colorCounts.Count > 0)
            result.DetectedColors = colorCounts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(8).ToList();

        foreach (Match m in CssCustomPropRegex.Matches(css))
        {
            var propName = m.Groups[1].Value;
            var value = m.Groups[2].Value.Trim();
            if (propName.Contains("color", StringComparison.OrdinalIgnoreCase) || propName.Contains("brand", StringComparison.OrdinalIgnoreCase))
            {
                var hex = HexColorRegex.Match(value);
                if (hex.Success)
                {
                    var normalized = NormalizeHex(hex.Value);
                    if (normalized != null && !result.DetectedColors.Contains(normalized))
                        result.DetectedColors.Insert(0, normalized);
                }
            }
        }
        result.DetectedColors = result.DetectedColors.Take(8).ToList();

        var fonts = new List<string>();
        foreach (Match m in FontFamilyRegex.Matches(css))
        {
            var raw = m.Groups[1].Value;
            var first = raw.Split(',')[0].Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(first) &&
                !first.Equals("inherit", StringComparison.OrdinalIgnoreCase) &&
                !fonts.Contains(first, StringComparer.OrdinalIgnoreCase))
                fonts.Add(first);
        }
        result.DetectedFonts = fonts.Take(6).ToList();

        var radiusMatches = BorderRadiusRegex.Matches(css);
        if (radiusMatches.Count > 0)
        {
            var avgPx = radiusMatches
                .Select(m => (Value: double.TryParse(m.Groups[1].Value, out var v) ? v : 0, Unit: m.Groups[2].Value))
                .Where(x => x.Unit.Equals("px", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .DefaultIfEmpty(0)
                .Average();

            result.ComponentStyle.BorderRadius = $"{Math.Clamp(avgPx, 0, 32):0}px";
            result.ComponentStyle.ButtonShape = avgPx >= 24 ? "pill" : avgPx <= 4 ? "square" : "rounded";
        }

        result.ComponentStyle.HasShadow = BoxShadowRegex.IsMatch(css);
        result.ComponentStyle.IsDarkTheme = css.Contains("background:#0", StringComparison.OrdinalIgnoreCase) ||
                                             css.Contains("background: #0", StringComparison.OrdinalIgnoreCase) ||
                                             css.Contains("background-color:#0", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNearGrayscale(string hex)
    {
        if (!TryHexToRgb(hex, out var r, out var g, out var b)) return false;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        return (max - min) < 12; // low saturation → likely black/white/gray UI chrome, not a brand color
    }

    private static bool TryHexToRgb(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        if (h.Length != 6) return false;
        return int.TryParse(h[..2], System.Globalization.NumberStyles.HexNumber, null, out r)
            && int.TryParse(h.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
            && int.TryParse(h.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
    }

    private static string? NormalizeHex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        return h.Length == 6 ? "#" + h.ToUpperInvariant() : null;
    }

    private static bool ContainsLogoHint(string value) =>
        value.Contains("logo", StringComparison.OrdinalIgnoreCase);

    private static int? TryParseInt(string? value) => int.TryParse(value, out var i) ? i : null;

    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : System.Net.WebUtility.HtmlDecode(text).Trim();

    private static string ResolveUrl(Uri baseUri, string possiblyRelative)
    {
        if (string.IsNullOrWhiteSpace(possiblyRelative)) return string.Empty;
        return Uri.TryCreate(baseUri, possiblyRelative, out var resolved) ? resolved.ToString() : possiblyRelative;
    }
}
