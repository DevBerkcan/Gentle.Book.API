using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using GentleBook.Api.Services.AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/admin/ai-finder")]
[Authorize]
public class AiFinderAdminController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IServiceFinderEngine _engine;
    private readonly IAiOrchestrator _orchestrator;

    public AiFinderAdminController(
        GentleBookDbContext db,
        ITenantContext tenantContext,
        IServiceFinderEngine engine,
        IAiOrchestrator orchestrator)
    {
        _db = db;
        _tenantContext = tenantContext;
        _engine = engine;
        _orchestrator = orchestrator;
    }

    private bool IsAdmin() => JwtService.GetRole(User) is "TenantAdmin" or "SuperAdmin";

    private bool TryGetTenantId(out Guid tenantId)
    {
        tenantId = _tenantContext.TenantId ?? Guid.Empty;
        return _tenantContext.TenantId.HasValue;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(new { message = "Kein Tenant im Token" });

        var setting = await _db.TenantIndustrySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var usage = await _db.AiUsages.AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.CreatedOn >= monthStart)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Requests = g.Count(),
                InputTokens = g.Sum(x => x.InputTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
                Cost = g.Sum(x => x.EstimatedCost)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(new
        {
            finderEnabled = setting?.IsFinderEnabled ?? false,
            primaryIndustryProfileId = setting?.PrimaryIndustryProfileId,
            questionCount = await _db.ServiceFinderQuestions.CountAsync(q => q.TenantId == tenantId && q.IsActive, cancellationToken),
            ruleCount = await _db.ServiceFinderRules.CountAsync(r => r.TenantId == tenantId && r.IsActive, cancellationToken),
            guidanceCount = await _db.ServiceGuidances.CountAsync(g => g.TenantId == tenantId && g.IsActive && g.ApprovalStatus == ApprovalStatus.Approved, cancellationToken),
            usage = usage ?? new { Requests = 0, InputTokens = 0, OutputTokens = 0, Cost = 0m }
        });
    }

    [HttpGet("industry-profiles")]
    public async Task<IActionResult> GetIndustryProfiles(CancellationToken cancellationToken)
    {
        var profiles = await _db.IndustryProfiles.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Key,
                p.Name,
                p.Description,
                capabilities = p.Capabilities
                    .OrderBy(c => c.CapabilityKey)
                    .Select(c => new { c.Id, c.CapabilityKey, c.DefaultEnabled })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return Ok(profiles);
    }

    [HttpGet("industry-settings")]
    public async Task<IActionResult> GetIndustrySettings(CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(new { message = "Kein Tenant im Token" });

        var setting = await _db.TenantIndustrySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        var capabilities = await _db.TenantIndustryCapabilities.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsEnabled)
            .Select(c => c.CapabilityKey)
            .ToListAsync(cancellationToken);

        return Ok(new TenantIndustrySettingsDto(
            setting?.PrimaryIndustryProfileId,
            setting?.IsFinderEnabled ?? false,
            setting?.SettingsJson,
            capabilities));
    }

    [HttpPut("industry-settings")]
    public async Task<IActionResult> UpsertIndustrySettings(
        [FromBody] UpsertTenantIndustrySettingsRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            return Forbid();
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(new { message = "Kein Tenant im Token" });

        var profileExists = await _db.IndustryProfiles
            .AnyAsync(p => p.Id == dto.PrimaryIndustryProfileId && p.IsActive, cancellationToken);
        if (!profileExists)
            return BadRequest(new { message = "Industry profile not found." });

        // Only block *enabling* the finder above the tenant's plan — always allow disabling it,
        // even for a tenant that already downgraded after having it on.
        if (dto.IsFinderEnabled)
        {
            var tenantPlan = await _db.Subscriptions
                .Where(s => s.TenantId == tenantId)
                .Select(s => s.Plan)
                .FirstOrDefaultAsync(cancellationToken);

            var requiredPlanName = AiFinderPlanGate.ValidateForPlan(tenantPlan);
            if (requiredPlanName != null)
                return StatusCode(402, new
                {
                    message = $"Der KI-Finder ist ab dem Tarif \"{requiredPlanName}\" verfügbar.",
                    feature = "aiFinder",
                    upgrade = true,
                    requiredPlan = requiredPlanName
                });
        }

        var setting = await _db.TenantIndustrySettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

        var actorId = JwtService.GetUserId(User);
        if (setting == null)
        {
            setting = new TenantIndustrySetting
            {
                TenantId = tenantId,
                PrimaryIndustryProfileId = dto.PrimaryIndustryProfileId,
                IsFinderEnabled = dto.IsFinderEnabled,
                SettingsJson = dto.SettingsJson,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = actorId,
                UpdatedAt = DateTime.UtcNow,
                UpdatedBy = actorId
            };
            _db.TenantIndustrySettings.Add(setting);
        }
        else
        {
            setting.PrimaryIndustryProfileId = dto.PrimaryIndustryProfileId;
            setting.IsFinderEnabled = dto.IsFinderEnabled;
            setting.SettingsJson = dto.SettingsJson;
            setting.UpdatedAt = DateTime.UtcNow;
            setting.UpdatedBy = actorId;
        }

        // ExecuteDeleteAsync doesn't translate against the EF InMemory provider used by
        // Gentle.Book.API.Tests (combined with the tenant global query filter) — fetch+RemoveRange
        // is the standard EF Core alternative and behaves identically against SQL Server.
        var existingCapabilities = await _db.TenantIndustryCapabilities.Where(c => c.TenantId == tenantId).ToListAsync(cancellationToken);
        _db.TenantIndustryCapabilities.RemoveRange(existingCapabilities);
        foreach (var key in dto.EnabledCapabilities.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _db.TenantIndustryCapabilities.Add(new TenantIndustryCapability
            {
                TenantId = tenantId,
                CapabilityKey = key.Trim(),
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Industry settings saved." });
    }

    [HttpGet("questions")]
    public async Task<IActionResult> GetQuestions(CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(new { message = "Kein Tenant im Token" });

        var questions = await _db.ServiceFinderQuestions.AsNoTracking()
            .Where(q => q.TenantId == tenantId)
            .OrderBy(q => q.DisplayOrder)
            .Select(q => new FinderQuestionDto(
                q.Id,
                q.QuestionKey,
                q.QuestionText,
                q.AnswerType,
                q.IsRequired,
                q.DisplayOrder,
                q.ConfigJson,
                q.IsActive,
                q.IndustryProfileId))
            .ToListAsync(cancellationToken);

        return Ok(questions);
    }

    [HttpPut("questions")]
    public async Task<IActionResult> UpsertQuestions(
        [FromBody] UpsertFinderQuestionsRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            return Forbid();
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(new { message = "Kein Tenant im Token" });

        foreach (var item in dto.Questions)
        {
            var entity = item.Id.HasValue
                ? await _db.ServiceFinderQuestions.FirstOrDefaultAsync(q => q.Id == item.Id.Value && q.TenantId == tenantId, cancellationToken)
                : null;

            if (entity == null)
            {
                entity = new ServiceFinderQuestion
                {
                    TenantId = tenantId,
                    CreatedAt = DateTime.UtcNow
                };
                _db.ServiceFinderQuestions.Add(entity);
            }

            entity.IndustryProfileId = item.IndustryProfileId;
            entity.QuestionKey = item.QuestionKey.Trim();
            entity.QuestionText = item.QuestionText.Trim();
            entity.AnswerType = item.AnswerType;
            entity.IsRequired = item.IsRequired;
            entity.DisplayOrder = item.DisplayOrder;
            entity.ConfigJson = item.ConfigJson;
            entity.IsActive = item.IsActive;
            entity.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Questions saved." });
    }

    [HttpGet("rules")]
    public async Task<IActionResult> GetRules(CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(new { message = "Kein Tenant im Token" });

        var rules = await _db.ServiceFinderRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .Select(r => new FinderRuleDto(
                r.Id,
                r.ServiceId,
                r.RuleType,
                r.ConditionJson,
                r.ResultJson,
                r.Priority,
                r.IsActive,
                r.ApprovalStatus))
            .ToListAsync(cancellationToken);

        return Ok(rules);
    }

    [HttpPut("rules")]
    public async Task<IActionResult> UpsertRules(
        [FromBody] UpsertFinderRulesRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            return Forbid();
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(new { message = "Kein Tenant im Token" });

        var actorId = JwtService.GetUserId(User);
        foreach (var item in dto.Rules)
        {
            var entity = item.Id.HasValue
                ? await _db.ServiceFinderRules.FirstOrDefaultAsync(r => r.Id == item.Id.Value && r.TenantId == tenantId, cancellationToken)
                : null;

            if (entity == null)
            {
                entity = new ServiceFinderRule
                {
                    TenantId = tenantId,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = actorId
                };
                _db.ServiceFinderRules.Add(entity);
            }

            entity.ServiceId = item.ServiceId;
            entity.RuleType = item.RuleType;
            entity.ConditionJson = item.ConditionJson;
            entity.ResultJson = item.ResultJson;
            entity.Priority = item.Priority;
            entity.IsActive = item.IsActive;
            entity.ApprovalStatus = item.ApprovalStatus;
            entity.UpdatedAt = DateTime.UtcNow;
            entity.UpdatedBy = actorId;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Rules saved." });
    }

    [HttpGet("guidance")]
    public async Task<IActionResult> GetGuidance(CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(new { message = "Kein Tenant im Token" });

        var guidance = await _db.ServiceGuidances.AsNoTracking()
            .Where(g => g.TenantId == tenantId)
            .OrderBy(g => g.ServiceId)
            .ThenBy(g => g.GuidanceType)
            .Select(g => new ServiceGuidanceDto(
                g.Id,
                g.ServiceId,
                g.GuidanceType,
                g.Title,
                g.Content,
                g.ApprovalStatus,
                g.IsActive,
                g.ValidFrom,
                g.ValidTo))
            .ToListAsync(cancellationToken);

        return Ok(guidance);
    }

    [HttpPut("guidance")]
    public async Task<IActionResult> UpsertGuidance(
        [FromBody] UpsertServiceGuidanceRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            return Forbid();
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(new { message = "Kein Tenant im Token" });

        var actorId = JwtService.GetUserId(User);
        foreach (var item in dto.Guidance)
        {
            var entity = item.Id.HasValue
                ? await _db.ServiceGuidances.FirstOrDefaultAsync(g => g.Id == item.Id.Value && g.TenantId == tenantId, cancellationToken)
                : null;

            if (entity == null)
            {
                entity = new ServiceGuidance
                {
                    TenantId = tenantId,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = actorId
                };
                _db.ServiceGuidances.Add(entity);
            }

            entity.ServiceId = item.ServiceId;
            entity.GuidanceType = item.GuidanceType;
            entity.Title = item.Title.Trim();
            entity.Content = item.Content.Trim();
            entity.ApprovalStatus = item.ApprovalStatus;
            entity.IsActive = item.IsActive;
            entity.ValidFrom = item.ValidFrom;
            entity.ValidTo = item.ValidTo;
            entity.UpdatedAt = DateTime.UtcNow;
            entity.UpdatedBy = actorId;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Guidance saved." });
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] EvaluateFinderRequestDto request, CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            return Forbid();
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(new { message = "Kein Tenant im Token" });

        var answerMap = request.Answers.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase);
        var result = await _engine.FindServicesAsync(tenantId, new ServiceFinderAnswers(answerMap), cancellationToken);
        var response = await _orchestrator.BuildFinderResponseAsync(tenantId, result, request.FreeText, cancellationToken);

        return Ok(new EvaluateFinderResponseDto(
            response.Message,
            result.Recommendations.Select(r => new ServiceRecommendationDto(r.ServiceId, r.ServiceName, r.Score, r.Price, r.Currency, r.DurationMinutes, r.SuggestedEmployeeIds)).ToList(),
            result.MissingQuestions.Select(q => new RequiredQuestionDto(q.QuestionKey, q.QuestionText, q.AnswerType)).ToList(),
            result.Guidance.Select(g => new CustomerGuidanceItemDto(g.GuidanceId, g.GuidanceType, g.Title, g.Content, g.SourceLabel)).ToList(),
            response.RequiresHumanConsultation,
            response.UsedAiFallback,
            response.Sources,
            response.SuggestedEmployeeIds));
    }
}
