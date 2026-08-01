// Services/BrandImport/WebsiteBrandAnalysisService.cs
// Orchestrates the full pipeline described in spec section 5:
//   URL validieren → DNS/Ziel prüfen → HTML sicher abrufen → relevante Ressourcen erkennen →
//   CSS analysieren → Branding-Merkmale extrahieren → Inhalte strukturieren → AI-Analyse →
//   Theme-Vorschläge validieren → Entwurf speichern.
// Split into CreateJobAsync (fast, called from the HTTP request) and ProcessJobAsync (slow,
// called from a Hangfire background job) so website analysis never blocks the admin's request.
using System.Text.Json;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services.BrandImport;

public interface IWebsiteBrandAnalysisService
{
    /// <summary>
    /// Validates the URL and creates a Queued job. Returns null (with an out error) if the tenant
    /// already has an in-flight analysis for the same URL, or if the URL fails the SSRF/basic checks.
    /// </summary>
    Task<(Guid? JobId, string? ErrorCode, string? ErrorMessageSafe)> CreateJobAsync(
        Guid tenantId, Uri websiteUrl, Guid createdBy, CancellationToken cancellationToken);

    /// <summary>Runs the full analysis pipeline for an existing Queued job. Safe to invoke from a background worker.</summary>
    Task ProcessJobAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed class WebsiteBrandAnalysisService : IWebsiteBrandAnalysisService
{
    private const long MaxHtmlBytes = 3 * 1024 * 1024; // 3 MB
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(12);
    private const int MaxRedirects = 5;

    private readonly GentleBookDbContext _db;
    private readonly IUrlSecurityValidator _urlValidator;
    private readonly ISafeWebsiteFetcher _fetcher;
    private readonly IHtmlBrandExtractor _extractor;
    private readonly IBrandAiAnalyzer _analyzer;
    private readonly ILogger<WebsiteBrandAnalysisService> _logger;

    public WebsiteBrandAnalysisService(
        GentleBookDbContext db,
        IUrlSecurityValidator urlValidator,
        ISafeWebsiteFetcher fetcher,
        IHtmlBrandExtractor extractor,
        IBrandAiAnalyzer analyzer,
        ILogger<WebsiteBrandAnalysisService> logger)
    {
        _db = db;
        _urlValidator = urlValidator;
        _fetcher = fetcher;
        _extractor = extractor;
        _analyzer = analyzer;
        _logger = logger;
    }

    public async Task<(Guid? JobId, string? ErrorCode, string? ErrorMessageSafe)> CreateJobAsync(
        Guid tenantId, Uri websiteUrl, Guid createdBy, CancellationToken cancellationToken)
    {
        var validation = await _urlValidator.ValidateAsync(websiteUrl, cancellationToken);
        if (!validation.IsAllowed)
            return (null, validation.ErrorCode, validation.ErrorMessageSafe);

        var normalizedUrl = websiteUrl.ToString();
        var hasActiveJob = await _db.BrandImportJobs
            .Where(j => j.TenantId == tenantId && j.SourceUrl == normalizedUrl)
            .Where(j => j.Status == BrandImportJobStatus.Queued
                || j.Status == BrandImportJobStatus.Fetching
                || j.Status == BrandImportJobStatus.Extracting
                || j.Status == BrandImportJobStatus.Analyzing)
            .AnyAsync(cancellationToken);

        if (hasActiveJob)
            return (null, "analysis_already_running", "Für diese Website läuft bereits eine Analyse.");

        var job = new BrandImportJob
        {
            TenantId = tenantId,
            SourceUrl = normalizedUrl,
            Status = BrandImportJobStatus.Queued,
            CreatedBy = createdBy,
        };
        _db.BrandImportJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);

        return (job.Id, null, null);
    }

    public async Task ProcessJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _db.BrandImportJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job == null) return;
        if (job.Status is BrandImportJobStatus.Applied or BrandImportJobStatus.Archived) return;

        job.StartedOn = DateTime.UtcNow;
        job.Status = BrandImportJobStatus.Fetching;
        await _db.SaveChangesAsync(cancellationToken);

        if (!Uri.TryCreate(job.SourceUrl, UriKind.Absolute, out var uri))
        {
            await FailAsync(job, "invalid_url", "Die hinterlegte Website-Adresse ist ungültig.", cancellationToken);
            return;
        }

        var fetch = await _fetcher.FetchAsync(uri, new[] { "text/html", "application/xhtml+xml" }, MaxHtmlBytes, MaxRedirects, FetchTimeout, cancellationToken);
        if (!fetch.Success || fetch.Body == null || fetch.FinalUri == null)
        {
            await FailAsync(job, fetch.ErrorCode ?? "fetch_failed", fetch.ErrorMessageSafe ?? "Die Website konnte nicht analysiert werden.", cancellationToken);
            return;
        }

        job.Status = BrandImportJobStatus.Extracting;
        await _db.SaveChangesAsync(cancellationToken);

        BrandExtractionResult extraction;
        try
        {
            extraction = await _extractor.ExtractAsync(fetch.Body, fetch.FinalUri, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Brand extraction failed for job {JobId}", job.Id);
            await FailAsync(job, "extraction_failed", "Die Inhalte der Website konnten nicht ausgewertet werden.", cancellationToken);
            return;
        }

        job.Status = BrandImportJobStatus.Analyzing;
        await _db.SaveChangesAsync(cancellationToken);

        BrandAnalysisOutput analysis;
        try
        {
            analysis = await _analyzer.AnalyzeAsync(extraction, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Brand analysis failed for job {JobId}", job.Id);
            await FailAsync(job, "analysis_failed", "Die Branding-Analyse konnte nicht abgeschlossen werden.", cancellationToken);
            return;
        }

        if (analysis.Proposals.Count == 0)
        {
            await FailAsync(job, "no_proposals", "Es konnten keine Designvorschläge erstellt werden.", cancellationToken);
            return;
        }

        var detectedDataJson = JsonSerializer.Serialize(new
        {
            colors = extraction.DetectedColors,
            themeColorMeta = extraction.ThemeColorMeta,
            fonts = extraction.DetectedFonts,
            componentStyle = extraction.ComponentStyle,
            content = extraction.Content,
            faviconUrl = extraction.FaviconUrl,
            warnings = analysis.Warnings,
        });

        var result = new BrandImportResult
        {
            TenantId = job.TenantId,
            JobId = job.Id,
            WebsiteTitle = extraction.WebsiteTitle,
            BrandStyle = analysis.BrandStyle,
            DetectedDataJson = detectedDataJson,
            Confidence = analysis.Confidence,
        };
        _db.BrandImportResults.Add(result);

        foreach (var proposal in analysis.Proposals)
        {
            _db.BrandThemeProposals.Add(new BrandThemeProposal
            {
                TenantId = job.TenantId,
                ImportResultId = result.Id,
                ProposalKey = proposal.ProposalKey,
                Name = proposal.Name,
                Reason = proposal.Reason,
                TemplateId = proposal.TemplateId,
                ThemeJson = JsonSerializer.Serialize(proposal.Theme),
                ContentJson = JsonSerializer.Serialize(extraction.Content),
            });
        }

        if (!string.IsNullOrEmpty(extraction.FaviconUrl))
        {
            _db.BrandAssetCandidates.Add(new BrandAssetCandidate
            {
                TenantId = job.TenantId,
                ImportResultId = result.Id,
                AssetType = BrandAssetType.Favicon,
                SourceUrl = extraction.FaviconUrl,
                DiscoveryHint = "link[rel=icon]",
            });
        }

        foreach (var logo in extraction.LogoCandidates.Take(8))
        {
            _db.BrandAssetCandidates.Add(new BrandAssetCandidate
            {
                TenantId = job.TenantId,
                ImportResultId = result.Id,
                AssetType = BrandAssetType.Logo,
                SourceUrl = logo.SourceUrl,
                DiscoveryHint = logo.DiscoveryHint,
                Width = logo.Width,
                Height = logo.Height,
            });
        }

        job.Status = BrandImportJobStatus.Ready;
        job.CompletedOn = DateTime.UtcNow;
        job.ResultId = result.Id;
        await _db.SaveChangesAsync(cancellationToken);

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == job.TenantId, cancellationToken);
        if (settings != null)
        {
            settings.LastBrandAnalysisOn = DateTime.UtcNow;
            settings.LastBrandAnalysisStatus = nameof(BrandImportJobStatus.Ready);
            settings.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task FailAsync(BrandImportJob job, string errorCode, string safeMessage, CancellationToken cancellationToken)
    {
        job.Status = BrandImportJobStatus.Failed;
        job.ErrorCode = errorCode;
        job.ErrorMessageSafe = safeMessage;
        job.CompletedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == job.TenantId, cancellationToken);
        if (settings != null)
        {
            settings.LastBrandAnalysisOn = DateTime.UtcNow;
            settings.LastBrandAnalysisStatus = nameof(BrandImportJobStatus.Failed);
            settings.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
