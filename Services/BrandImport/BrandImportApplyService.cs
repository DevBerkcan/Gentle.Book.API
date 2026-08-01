// Services/BrandImport/BrandImportApplyService.cs
// Applies a confirmed theme proposal onto the tenant's real, live design (spec section 10
// "Theme-Draft statt Direktveröffentlichung"). Only ever runs after the two-step admin
// confirmation (select proposal + fields -> confirm "Entwurf anwenden" dialog) that the
// controller enforces. Existing Services/Employees/Bookings/Links are never touched except for
// adding new social links the admin explicitly selected; nothing is deleted or overwritten
// beyond the specific fields the admin chose.
using System.Text.Json;
using System.Text.Json.Nodes;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services.BrandImport;

public sealed record ApplyBrandProposalOptions(
    bool ApplyLogo,
    bool ApplyColors,
    bool ApplyTypography,
    bool ApplyDescription,
    bool ApplySocialLinks,
    Guid? SelectedLogoAssetId);

public sealed record ApplyBrandProposalResult(bool Success, string? ErrorCode, string? ErrorMessageSafe);

public interface IBrandImportApplyService
{
    Task<ApplyBrandProposalResult> ApplyAsync(
        Guid tenantId,
        Guid resultId,
        Guid proposalId,
        ApplyBrandProposalOptions options,
        CancellationToken cancellationToken);
}

public sealed class BrandImportApplyService : IBrandImportApplyService
{
    private static readonly Dictionary<string, string> ContentTypeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
    };
    private const long MaxLogoBytes = 5 * 1024 * 1024; // matches TenantController.UploadLogo's existing 5MB cap

    private readonly GentleBookDbContext _db;
    private readonly ISafeWebsiteFetcher _fetcher;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BrandImportApplyService> _logger;

    public BrandImportApplyService(
        GentleBookDbContext db,
        ISafeWebsiteFetcher fetcher,
        IWebHostEnvironment env,
        ILogger<BrandImportApplyService> logger)
    {
        _db = db;
        _fetcher = fetcher;
        _env = env;
        _logger = logger;
    }

    public async Task<ApplyBrandProposalResult> ApplyAsync(
        Guid tenantId,
        Guid resultId,
        Guid proposalId,
        ApplyBrandProposalOptions options,
        CancellationToken cancellationToken)
    {
        var result = await _db.BrandImportResults.FirstOrDefaultAsync(r => r.Id == resultId && r.TenantId == tenantId, cancellationToken);
        if (result == null)
            return new ApplyBrandProposalResult(false, "result_not_found", "Der Entwurf wurde nicht gefunden.");

        var proposal = await _db.BrandThemeProposals.FirstOrDefaultAsync(
            p => p.Id == proposalId && p.ImportResultId == resultId && p.TenantId == tenantId, cancellationToken);
        if (proposal == null)
            return new ApplyBrandProposalResult(false, "proposal_not_found", "Der ausgewählte Vorschlag wurde nicht gefunden.");

        var theme = JsonSerializer.Deserialize<ValidatedTheme>(proposal.ThemeJson);
        if (theme == null)
            return new ApplyBrandProposalResult(false, "invalid_theme", "Der Theme-Entwurf ist ungültig.");

        // Re-validate again at apply-time — defense in depth in case stored data was ever tampered with.
        theme = BrandThemeValidator.Validate(theme);
        var templateId = BrandThemeValidator.SanitizeTemplateId(proposal.TemplateId);

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (settings == null)
            return new ApplyBrandProposalResult(false, "settings_not_found", "Die Tenant-Einstellungen wurden nicht gefunden.");

        if (options.ApplyColors)
        {
            settings.PrimaryColor = theme.Primary;
            settings.SecondaryColor = theme.Secondary;
            settings.AccentColor = theme.Accent;
        }

        var configNode = string.IsNullOrWhiteSpace(settings.LinktreeConfig)
            ? new JsonObject()
            : (JsonNode.Parse(settings.LinktreeConfig) as JsonObject ?? new JsonObject());

        configNode["pageTemplate"] = templateId;
        configNode["buttonStyle"] = theme.ButtonStyle;
        configNode["cardStyle"] = theme.CardStyle;
        configNode["animationSpeed"] = theme.AnimationSpeed;

        if (options.ApplyTypography)
            configNode["fontFamily"] = theme.HeadingFontKey;

        settings.LinktreeConfig = configNode.ToJsonString();

        if (options.ApplyDescription)
        {
            var content = JsonSerializer.Deserialize<ExtractedContent>(proposal.ContentJson);
            if (content != null)
            {
                if (!string.IsNullOrWhiteSpace(content.Description))
                    settings.WelcomeMessage = Truncate(content.Description, 1000);
                if (!string.IsNullOrWhiteSpace(content.Heading))
                    settings.Tagline = Truncate(content.Heading, 200);
            }
        }

        if (options.ApplyLogo && options.SelectedLogoAssetId.HasValue)
        {
            var asset = await _db.BrandAssetCandidates.FirstOrDefaultAsync(
                a => a.Id == options.SelectedLogoAssetId.Value && a.ImportResultId == resultId && a.TenantId == tenantId,
                cancellationToken);

            if (asset != null && Uri.TryCreate(asset.SourceUrl, UriKind.Absolute, out var logoUri))
            {
                var logoResult = await DownloadAndStoreLogoAsync(tenantId, logoUri, cancellationToken);
                if (logoResult != null)
                    settings.LogoUrl = logoResult;
                else
                    _logger.LogInformation("Brand import logo download skipped/failed for tenant {TenantId}; existing logo left untouched.", tenantId);
            }
        }

        if (options.ApplySocialLinks)
        {
            var content = JsonSerializer.Deserialize<ExtractedContent>(proposal.ContentJson);
            if (content != null)
            {
                var existingUrls = await _db.TenantLinks
                    .Where(l => l.TenantId == tenantId)
                    .Select(l => l.Url)
                    .ToListAsync(cancellationToken);

                var maxOrder = await _db.TenantLinks.Where(l => l.TenantId == tenantId).AnyAsync(cancellationToken)
                    ? await _db.TenantLinks.Where(l => l.TenantId == tenantId).MaxAsync(l => l.DisplayOrder, cancellationToken)
                    : -1;

                foreach (var link in content.SocialLinks)
                {
                    if (existingUrls.Contains(link, StringComparer.OrdinalIgnoreCase)) continue;
                    maxOrder++;
                    _db.TenantLinks.Add(new TenantLink
                    {
                        TenantId = tenantId,
                        Title = GuessSocialLabel(link),
                        Url = link,
                        IconType = GuessSocialIcon(link),
                        DisplayOrder = maxOrder,
                        IsActive = true,
                    });
                }
            }
        }

        settings.UpdatedAt = DateTime.UtcNow;
        proposal.IsSelected = true;
        proposal.IsApplied = true;
        proposal.AppliedOn = DateTime.UtcNow;
        result.AppliedOn = DateTime.UtcNow;

        var job = await _db.BrandImportJobs.FirstOrDefaultAsync(j => j.Id == result.JobId, cancellationToken);
        if (job != null) job.Status = BrandImportJobStatus.Applied;

        await _db.SaveChangesAsync(cancellationToken);
        return new ApplyBrandProposalResult(true, null, null);
    }

    private async Task<string?> DownloadAndStoreLogoAsync(Guid tenantId, Uri logoUri, CancellationToken cancellationToken)
    {
        var fetch = await _fetcher.FetchAsync(
            logoUri,
            new[] { "image/jpeg", "image/png", "image/webp", "image/gif" },
            MaxLogoBytes,
            maxRedirects: 3,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken);

        // FetchAsync reads the body as text (UTF-8), which corrupts binary image data — so for
        // images we only use it to validate reachability/content-type/size, then re-fetch bytes
        // directly via HttpClient once validated (final URI is already SSRF-checked).
        if (!fetch.Success || fetch.FinalUri == null || fetch.ContentType == null) return null;
        if (!ContentTypeExtensions.TryGetValue(fetch.ContentType, out var ext)) return null;

        try
        {
            using var handler = new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false };
            using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var bytes = await client.GetByteArrayAsync(fetch.FinalUri, cancellationToken);
            if (bytes.LongLength > MaxLogoBytes) return null;

            var uploadDir = Path.Combine(_env.WebRootPath, "uploads", "logos");
            Directory.CreateDirectory(uploadDir);
            var fileName = $"{tenantId}{ext}";
            var filePath = Path.Combine(uploadDir, fileName);
            await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);
            return $"/uploads/logos/{fileName}";
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Logo download failed: {ErrorType}", ex.GetType().Name);
            return null;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string GuessSocialLabel(string url)
    {
        if (url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase)) return "Instagram";
        if (url.Contains("facebook.com", StringComparison.OrdinalIgnoreCase)) return "Facebook";
        if (url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase)) return "TikTok";
        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)) return "YouTube";
        if (url.Contains("wa.me", StringComparison.OrdinalIgnoreCase)) return "WhatsApp";
        return "Website";
    }

    private static LinkIconType GuessSocialIcon(string url)
    {
        if (url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase)) return LinkIconType.Instagram;
        if (url.Contains("facebook.com", StringComparison.OrdinalIgnoreCase)) return LinkIconType.Facebook;
        if (url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase)) return LinkIconType.TikTok;
        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)) return LinkIconType.YouTube;
        if (url.Contains("wa.me", StringComparison.OrdinalIgnoreCase)) return LinkIconType.WhatsApp;
        return LinkIconType.Custom;
    }
}
