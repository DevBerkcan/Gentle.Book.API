using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services.AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/public/{slug}/finder")]
[AllowAnonymous]
public class PublicAiFinderController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly IServiceFinderEngine _engine;
    private readonly IAiOrchestrator _orchestrator;
    private readonly IBookingDraftService _bookingDraftService;

    public PublicAiFinderController(
        GentleBookDbContext db,
        IServiceFinderEngine engine,
        IAiOrchestrator orchestrator,
        IBookingDraftService bookingDraftService)
    {
        _db = db;
        _engine = engine;
        _orchestrator = orchestrator;
        _bookingDraftService = bookingDraftService;
    }

    // /api/public/* is exempt from TenantMiddleware's JWT-based subscription gate (there's no
    // token to read a tenantId claim from here — the tenant is resolved by slug instead), so
    // this endpoint must run the same "trial expired / subscription inactive" check itself.
    // Mirrors AuthController.Login, the other anonymous, slug-resolved endpoint with this exact
    // check.
    private async Task<(Guid tenantId, IActionResult? error)> ResolveTenantAsync(string slug, CancellationToken cancellationToken)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .Include(t => t.Subscription)
            .FirstOrDefaultAsync(t => t.Slug == slug.ToLowerInvariant() && t.IsActive, cancellationToken);

        if (tenant == null)
            return (Guid.Empty, NotFound(new { message = "Booking system not found." }));

        if (tenant.Subscription == null || !tenant.Subscription.IsAccessAllowed)
            return (Guid.Empty, StatusCode(402, new { message = "Trial expired or subscription inactive." }));

        return (tenant.Id, null);
    }

    private readonly record struct FinderAvailability(bool Enabled, string? RequiredPlanName);

    // Combines the tenant's own IsFinderEnabled toggle with the plan-tier gate. Previously only
    // GetBootstrap checked IsFinderEnabled at all, so disabling the finder (or a tenant plan that
    // never should have had it) didn't actually stop Evaluate/CreateBookingDraft/ConfirmBookingDraft.
    private async Task<FinderAvailability> GetFinderAvailabilityAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var settings = await _db.TenantIndustrySettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (settings == null || !settings.IsFinderEnabled)
            return new FinderAvailability(false, null);

        var tenantPlan = await _db.Subscriptions
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.Plan)
            .FirstOrDefaultAsync(cancellationToken);

        var requiredPlanName = AiFinderPlanGate.ValidateForPlan(tenantPlan);
        return requiredPlanName == null
            ? new FinderAvailability(true, null)
            : new FinderAvailability(false, requiredPlanName);
    }

    private IActionResult FinderUnavailableResult(FinderAvailability availability) =>
        availability.RequiredPlanName != null
            ? StatusCode(402, new
            {
                message = $"Der KI-Finder ist ab dem Tarif \"{availability.RequiredPlanName}\" verfügbar.",
                feature = "aiFinder",
                upgrade = true,
                requiredPlan = availability.RequiredPlanName
            })
            : NotFound(new { message = "Finder is not enabled for this booking page." });

    [HttpGet("bootstrap")]
    public async Task<IActionResult> GetBootstrap(string slug, CancellationToken cancellationToken)
    {
        var (tenantId, error) = await ResolveTenantAsync(slug, cancellationToken);
        if (error != null) return error;

        var availability = await GetFinderAvailabilityAsync(tenantId, cancellationToken);
        if (!availability.Enabled)
            return Ok(new { enabled = false });

        var questions = await _db.ServiceFinderQuestions
            .AsNoTracking()
            .Where(q => q.TenantId == tenantId && q.IsActive)
            .OrderBy(q => q.DisplayOrder)
            .Select(q => new FinderQuestionDto(q.Id, q.QuestionKey, q.QuestionText, q.AnswerType, q.IsRequired, q.DisplayOrder, q.ConfigJson, q.IsActive, q.IndustryProfileId))
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            enabled = true,
            aiDisclosure = "Du kommunizierst mit einem KI-gestutzten Assistenten.",
            medicalDisclaimer = "Der Finder ersetzt keine medizinische oder fachliche Beratung.",
            questions
        });
    }

    [HttpPost("evaluate")]
    [EnableRateLimiting("finder-limit")]
    public async Task<IActionResult> Evaluate(
        string slug,
        [FromBody] EvaluateFinderRequestDto request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = await ResolveTenantAsync(slug, cancellationToken);
        if (error != null) return error;

        var availability = await GetFinderAvailabilityAsync(tenantId, cancellationToken);
        if (!availability.Enabled)
            return FinderUnavailableResult(availability);

        await _bookingDraftService.ExpireOutdatedDraftsAsync(tenantId, cancellationToken);

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

    [HttpPost("booking-drafts")]
    [EnableRateLimiting("finder-limit")]
    public async Task<IActionResult> CreateBookingDraft(
        string slug,
        [FromBody] CreateBookingDraftRequestDto request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = await ResolveTenantAsync(slug, cancellationToken);
        if (error != null) return error;

        var availability = await GetFinderAvailabilityAsync(tenantId, cancellationToken);
        if (!availability.Enabled)
            return FinderUnavailableResult(availability);

        var draft = await _bookingDraftService.CreateDraftAsync(tenantId, request, cancellationToken);

        var serviceName = await _db.Services
            .AsNoTracking()
            .Where(s => s.Id == draft.ServiceId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "Service";

        return Ok(new BookingDraftResponseDto(
            draft.Id,
            draft.ExpiresAt,
            draft.Status.ToString(),
            serviceName,
            draft.BookingDate.ToString("yyyy-MM-dd"),
            draft.StartTime.ToString("HH:mm"),
            draft.EmployeeId));
    }

    [HttpPost("booking-drafts/{draftId:guid}/confirm")]
    [EnableRateLimiting("finder-limit")]
    public async Task<IActionResult> ConfirmBookingDraft(
        string slug,
        Guid draftId,
        [FromBody] ConfirmBookingDraftRequestDto request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = await ResolveTenantAsync(slug, cancellationToken);
        if (error != null) return error;

        var availability = await GetFinderAvailabilityAsync(tenantId, cancellationToken);
        if (!availability.Enabled)
            return FinderUnavailableResult(availability);

        var booking = await _bookingDraftService.ConfirmDraftAsync(tenantId, draftId, request.CustomerConfirmed, cancellationToken);
        return Ok(booking);
    }
}
