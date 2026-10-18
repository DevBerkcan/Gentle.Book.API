using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using GentleBook.Api.Services.AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

// Agency-exclusive AI chat in the "Meine Links" editor (components/admin/links) that helps a
// tenant admin pick a page template, color palette and CTA text from a free-text description of
// their business instead of browsing the picker grids manually. Authenticated (TenantAdmin),
// unlike PublicAiChatbotController's anonymous customer-facing widget — the per-visitor-per-day
// abuse concern that controller guards against doesn't apply to an admin's own authenticated
// session, so the only cost control needed here is the existing per-tenant AI budget gate in
// AiOrchestrator (same one the booking chatbot and Finder already share).
[ApiController]
[Route("api/tenant/design-assistant")]
[Authorize]
public class TenantDesignAssistantController : ControllerBase
{
    private const int MaxHistoryTurns = 10;
    private const int MaxMessageLength = 800;

    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IAiOrchestrator _orchestrator;

    public TenantDesignAssistantController(GentleBookDbContext db, ITenantContext tenantContext, IAiOrchestrator orchestrator)
    {
        _db = db;
        _tenantContext = tenantContext;
        _orchestrator = orchestrator;
    }

    [HttpPost("messages")]
    [EnableRateLimiting("finder-limit")]
    public async Task<IActionResult> SendMessage([FromBody] DesignAssistantMessageRequestDto request, CancellationToken cancellationToken)
    {
        var role = JwtService.GetRole(User);
        if (role != "TenantAdmin")
            return Forbid();
        if (!_tenantContext.TenantId.HasValue)
            return Unauthorized(new { message = "Kein Tenant im Token" });
        var tenantId = _tenantContext.TenantId.Value;

        var tenantPlan = await _db.Subscriptions
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.Plan)
            .FirstOrDefaultAsync(cancellationToken);
        var requiredPlanName = AgencyFeatureGate.ValidateForPlan(tenantPlan);
        if (requiredPlanName != null)
            return StatusCode(402, new { message = $"Der KI-Design-Assistent ist ab dem Tarif \"{requiredPlanName}\" verfügbar.", feature = "aiDesignAssistant", upgrade = true, requiredPlan = requiredPlanName });

        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "Bitte eine Nachricht angeben." });
        if (request.Message.Length > MaxMessageLength)
            return BadRequest(new { message = $"Deine Nachricht ist zu lang (max. {MaxMessageLength} Zeichen)." });

        var industryType = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.IndustryType.ToString())
            .FirstOrDefaultAsync(cancellationToken) ?? "Other";

        var history = (request.History ?? [])
            .TakeLast(MaxHistoryTurns)
            .Select(h => (Role: h.Role, Content: h.Content))
            .ToList();

        var turn = await _orchestrator.BuildDesignAssistantTurnAsync(tenantId, history, request.Message, industryType, cancellationToken);

        return Ok(new DesignAssistantMessageResponseDto(
            turn.Reply,
            turn.Suggestion is { } s ? new DesignSuggestionDto(s.PageTemplate, s.ColorPaletteKey, s.CtaText, s.CtaBadge) : null,
            turn.UsedAiFallback));
    }
}
