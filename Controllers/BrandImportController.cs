// Controllers/BrandImportController.cs
// "AI Brand Import" admin endpoints. TenantAdmin only. Every endpoint resolves the tenant from
// the authenticated request (ITenantContext / JWT claims) — the tenant is never taken from the
// request body — and every mutating action writes an audit log entry (spec section 12).
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using GentleBook.Api.Services.BrandImport;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/admin/brand-import")]
[Authorize]
[EnableRateLimiting("brand-import-limit")]
public class BrandImportController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IWebsiteBrandAnalysisService _analysisService;
    private readonly IBrandImportApplyService _applyService;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly AuditService _audit;
    private readonly ILogger<BrandImportController> _logger;

    public BrandImportController(
        GentleBookDbContext db,
        ITenantContext tenantContext,
        IWebsiteBrandAnalysisService analysisService,
        IBrandImportApplyService applyService,
        IBackgroundJobClient backgroundJobClient,
        AuditService audit,
        ILogger<BrandImportController> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _analysisService = analysisService;
        _applyService = applyService;
        _backgroundJobClient = backgroundJobClient;
        _audit = audit;
        _logger = logger;
    }

    private IActionResult? RequireTenantAdmin(out Guid tenantId)
    {
        tenantId = Guid.Empty;
        var role = JwtService.GetRole(User);
        if (role != "TenantAdmin") return Forbid();
        if (!_tenantContext.TenantId.HasValue) return Unauthorized(new { message = "Kein Tenant im Token" });
        tenantId = _tenantContext.TenantId.Value;
        return null;
    }

    private async Task<SubscriptionPlan> GetTenantPlanAsync(Guid tenantId, CancellationToken ct) =>
        await _db.Subscriptions.Where(s => s.TenantId == tenantId).Select(s => s.Plan).FirstOrDefaultAsync(ct);

    // ── Settings ─────────────────────────────────────────────────────────────
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var check = RequireTenantAdmin(out var tenantId);
        if (check != null) return check;

        var settings = await _db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (settings == null) return Ok(new { data = (object?)null });

        return Ok(new
        {
            data = new
            {
                websiteUrl = settings.Website,
                isConsentConfirmed = settings.WebsiteConsentConfirmed,
                consentConfirmedAt = settings.WebsiteConsentConfirmedAt,
                lastAnalysisOn = settings.LastBrandAnalysisOn,
                lastAnalysisStatus = settings.LastBrandAnalysisStatus,
            }
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateBrandImportSettingsRequest request, CancellationToken ct)
    {
        var check = RequireTenantAdmin(out var tenantId);
        if (check != null) return check;

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (settings == null) return NotFound(new { message = "Keine Einstellungen vorhanden." });

        if (request.WebsiteUrl != null)
        {
            var trimmed = request.WebsiteUrl.Trim();
            if (trimmed.Length > 0 && !Uri.TryCreate(trimmed, UriKind.Absolute, out _))
                return BadRequest(new { message = "Die Website-Adresse ist ungültig." });
            settings.Website = trimmed.Length == 0 ? null : trimmed;
        }

        if (request.ConsentConfirmed == true && !settings.WebsiteConsentConfirmed)
        {
            settings.WebsiteConsentConfirmed = true;
            settings.WebsiteConsentConfirmedAt = DateTime.UtcNow;
            settings.WebsiteConsentConfirmedBy = JwtService.GetUserId(User);
        }
        else if (request.ConsentConfirmed == false)
        {
            settings.WebsiteConsentConfirmed = false;
            settings.WebsiteConsentConfirmedAt = null;
            settings.WebsiteConsentConfirmedBy = null;
        }

        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("brand_import.settings_updated", "TenantSettings", settings.Id.ToString(), tenantId: tenantId);

        return Ok(new { message = "Gespeichert." });
    }

    [HttpDelete("settings/connection")]
    public async Task<IActionResult> RemoveConnection(CancellationToken ct)
    {
        var check = RequireTenantAdmin(out var tenantId);
        if (check != null) return check;

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (settings == null) return NotFound();

        settings.Website = null;
        settings.WebsiteConsentConfirmed = false;
        settings.WebsiteConsentConfirmedAt = null;
        settings.WebsiteConsentConfirmedBy = null;
        settings.LastBrandAnalysisOn = null;
        settings.LastBrandAnalysisStatus = null;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("brand_import.connection_removed", "TenantSettings", settings.Id.ToString(), tenantId: tenantId);

        return Ok(new { message = "Verbindung entfernt." });
    }

    // ── Analysis ─────────────────────────────────────────────────────────────
    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze([FromBody] StartBrandAnalysisRequest request, CancellationToken ct)
    {
        var check = RequireTenantAdmin(out var tenantId);
        if (check != null) return check;

        var plan = await GetTenantPlanAsync(tenantId, ct);
        var requiredPlan = BrandImportPlanGate.ValidateAnalysisForPlan(plan);
        if (requiredPlan != null)
            return StatusCode(402, new
            {
                message = $"Die automatische Branding-Analyse ist ab dem Tarif \"{requiredPlan}\" verfügbar.",
                feature = BrandImportPlanGate.FeatureAiBrandAnalysis,
                upgrade = true,
                requiredPlan
            });

        if (!request.ConfirmConsent)
            return BadRequest(new { message = "Bitte bestätige, dass du berechtigt bist, diese Website zu verwenden." });

        if (string.IsNullOrWhiteSpace(request.WebsiteUrl) || !Uri.TryCreate(request.WebsiteUrl.Trim(), UriKind.Absolute, out var uri))
            return BadRequest(new { message = "Bitte gib eine gültige Website-Adresse ein." });

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (settings != null)
        {
            settings.Website = uri.ToString();
            settings.WebsiteConsentConfirmed = true;
            settings.WebsiteConsentConfirmedAt = DateTime.UtcNow;
            settings.WebsiteConsentConfirmedBy = JwtService.GetUserId(User);
            settings.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        var actorId = JwtService.GetUserId(User) ?? Guid.Empty;
        var (jobId, errorCode, errorMessage) = await _analysisService.CreateJobAsync(tenantId, uri, actorId, ct);
        if (jobId == null)
            return BadRequest(new { message = errorMessage ?? "Die Analyse konnte nicht gestartet werden.", errorCode });

        _backgroundJobClient.Enqueue<IWebsiteBrandAnalysisService>(s => s.ProcessJobAsync(jobId.Value, CancellationToken.None));
        await _audit.LogAsync("brand_import.analysis_started", "BrandImportJob", jobId.Value.ToString(), tenantId: tenantId);

        return Ok(new { jobId });
    }

    [HttpPost("results/{id:guid}/reanalyze")]
    public async Task<IActionResult> Reanalyze(Guid id, CancellationToken ct)
    {
        var check = RequireTenantAdmin(out var tenantId);
        if (check != null) return check;

        var plan = await GetTenantPlanAsync(tenantId, ct);
        var requiredPlan = BrandImportPlanGate.ValidateReanalysisForPlan(plan);
        if (requiredPlan != null)
            return StatusCode(402, new
            {
                message = $"Die erneute Analyse ist ab dem Tarif \"{requiredPlan}\" verfügbar.",
                feature = BrandImportPlanGate.FeatureBrandImportReanalysis,
                upgrade = true,
                requiredPlan
            });

        var previousResult = await _db.BrandImportResults.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (previousResult == null) return NotFound();

        var previousJob = await _db.BrandImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == previousResult.JobId && j.TenantId == tenantId, ct);
        if (previousJob == null || !Uri.TryCreate(previousJob.SourceUrl, UriKind.Absolute, out var uri))
            return BadRequest(new { message = "Die ursprüngliche Website-Adresse konnte nicht ermittelt werden." });

        var actorId = JwtService.GetUserId(User) ?? Guid.Empty;
        var (jobId, errorCode, errorMessage) = await _analysisService.CreateJobAsync(tenantId, uri, actorId, ct);
        if (jobId == null)
            return BadRequest(new { message = errorMessage ?? "Die Analyse konnte nicht gestartet werden.", errorCode });

        _backgroundJobClient.Enqueue<IWebsiteBrandAnalysisService>(s => s.ProcessJobAsync(jobId.Value, CancellationToken.None));
        await _audit.LogAsync("brand_import.reanalysis_started", "BrandImportJob", jobId.Value.ToString(), tenantId: tenantId);

        return Ok(new { jobId });
    }

    [HttpGet("jobs/{id:guid}")]
    public async Task<IActionResult> GetJob(Guid id, CancellationToken ct)
    {
        var check = RequireTenantAdmin(out var tenantId);
        if (check != null) return check;

        var job = await _db.BrandImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.TenantId == tenantId, ct);
        if (job == null) return NotFound();

        return Ok(new
        {
            data = new
            {
                job.Id,
                job.Status,
                job.ErrorCode,
                job.ErrorMessageSafe,
                job.StartedOn,
                job.CompletedOn,
                job.ResultId,
            }
        });
    }

    // ── Results ──────────────────────────────────────────────────────────────
    [HttpGet("results/{id:guid}")]
    public async Task<IActionResult> GetResult(Guid id, CancellationToken ct)
    {
        var check = RequireTenantAdmin(out var tenantId);
        if (check != null) return check;

        var result = await _db.BrandImportResults.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (result == null) return NotFound();

        var proposalEntities = await _db.BrandThemeProposals.AsNoTracking()
            .Where(p => p.ImportResultId == id && p.TenantId == tenantId)
            .ToListAsync(ct);

        var proposals = proposalEntities.Select(p => new
        {
            p.Id,
            p.ProposalKey,
            p.Name,
            p.Reason,
            p.TemplateId,
            theme = System.Text.Json.JsonSerializer.Deserialize<object>(p.ThemeJson),
            content = System.Text.Json.JsonSerializer.Deserialize<object>(p.ContentJson),
            p.IsSelected,
            p.IsApplied,
        });

        var assets = await _db.BrandAssetCandidates.AsNoTracking()
            .Where(a => a.ImportResultId == id && a.TenantId == tenantId)
            .Select(a => new
            {
                a.Id,
                a.AssetType,
                a.SourceUrl,
                a.DiscoveryHint,
                a.Width,
                a.Height,
                a.IsSelected,
            })
            .ToListAsync(ct);

        return Ok(new
        {
            data = new
            {
                result.Id,
                result.JobId,
                result.WebsiteTitle,
                result.BrandStyle,
                result.Confidence,
                detectedData = System.Text.Json.JsonSerializer.Deserialize<object>(result.DetectedDataJson),
                proposals,
                assets,
                result.CreatedOn,
                result.AppliedOn,
            }
        });
    }

    [HttpDelete("results/{id:guid}")]
    public async Task<IActionResult> DiscardResult(Guid id, CancellationToken ct)
    {
        var check = RequireTenantAdmin(out var tenantId);
        if (check != null) return check;

        var result = await _db.BrandImportResults.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (result == null) return NotFound();

        var proposals = await _db.BrandThemeProposals.Where(p => p.ImportResultId == id && p.TenantId == tenantId).ToListAsync(ct);
        var assets = await _db.BrandAssetCandidates.Where(a => a.ImportResultId == id && a.TenantId == tenantId).ToListAsync(ct);
        var job = await _db.BrandImportJobs.FirstOrDefaultAsync(j => j.Id == result.JobId && j.TenantId == tenantId, ct);

        _db.BrandThemeProposals.RemoveRange(proposals);
        _db.BrandAssetCandidates.RemoveRange(assets);
        _db.BrandImportResults.Remove(result);
        if (job != null) job.Status = BrandImportJobStatus.Archived;

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("brand_import.result_discarded", "BrandImportResult", id.ToString(), tenantId: tenantId);

        return NoContent();
    }

    [HttpPost("results/{id:guid}/apply")]
    public async Task<IActionResult> ApplyResult(Guid id, [FromBody] ApplyBrandProposalRequest request, CancellationToken ct)
    {
        var check = RequireTenantAdmin(out var tenantId);
        if (check != null) return check;

        var options = new ApplyBrandProposalOptions(
            request.ApplyLogo,
            request.ApplyColors,
            request.ApplyTypography,
            request.ApplyDescription,
            request.ApplySocialLinks,
            request.SelectedLogoAssetId,
            request.ApplyServices,
            request.SelectedServiceNames);

        var applyResult = await _applyService.ApplyAsync(tenantId, id, request.ProposalId, options, ct);
        if (!applyResult.Success)
            return BadRequest(new { message = applyResult.ErrorMessageSafe, errorCode = applyResult.ErrorCode });

        await _audit.LogAsync(
            "brand_import.proposal_applied",
            "BrandThemeProposal",
            request.ProposalId.ToString(),
            details: $"resultId={id}",
            tenantId: tenantId);

        return Ok(new
        {
            message = "Branding-Entwurf angewendet.",
            importedServicesCount = applyResult.ImportedServicesCount,
            skippedServicesCount = applyResult.SkippedServicesCount,
        });
    }

    [HttpPost("assets/{id:guid}/select")]
    public async Task<IActionResult> SelectAsset(Guid id, [FromBody] bool isSelected, CancellationToken ct)
    {
        var check = RequireTenantAdmin(out var tenantId);
        if (check != null) return check;

        var asset = await _db.BrandAssetCandidates.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        if (asset == null) return NotFound();

        asset.IsSelected = isSelected;
        await _db.SaveChangesAsync(ct);
        return Ok(new { asset.Id, asset.IsSelected });
    }
}
