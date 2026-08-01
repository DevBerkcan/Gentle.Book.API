using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GentleBook.Api.Services;

namespace GentleBook.Api.Controllers.Admin;

[ApiController]
[Route("api/admin/tracking")]
public class TrackingController : ControllerBase
{
    private readonly TrackingService _trackingService;
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;

    public TrackingController(TrackingService trackingService, GentleBookDbContext db, ITenantContext tenantContext)
    {
        _trackingService = trackingService;
        _db = db;
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Feature gate: detailed analytics require a plan with HasAnalytics — determined from
    /// PlanLimits (the single source of truth for plan names/features), never hardcoded here.
    /// Returns 402 with the tenant's current plan and the actual required plan when blocked.
    /// </summary>
    private async Task<ObjectResult?> RequireAnalyticsPlanAsync()
    {
        var tenantId = _tenantContext.TenantId;
        if (!tenantId.HasValue) return null; // SuperAdmin or misconfigured — don't block here

        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId.Value);
        var currentPlan = sub?.Plan ?? SubscriptionPlan.Trial;
        var limits = PlanLimits.Get(currentPlan);
        if (!limits.HasAnalytics)
        {
            // Cheapest plan tier that actually includes analytics, so this never needs
            // updating by hand if plan names/tiers change.
            var requiredPlan = PlanLimits.All
                .Where(kv => kv.Value.HasAnalytics)
                .OrderBy(kv => kv.Value.MonthlyPrice)
                .Select(kv => kv.Value)
                .FirstOrDefault();

            return StatusCode(402, new
            {
                message = requiredPlan != null
                    ? $"Detaillierte Auswertungen zu Klicks, Seitenaufrufen und Umsatz sind in deinem aktuellen Tarif nicht enthalten. Mit dem Tarif \"{requiredPlan.DisplayName}\" kannst du sehen, wie deine Buchungsseite performt."
                    : "Detaillierte Statistiken sind in deinem aktuellen Tarif nicht enthalten.",
                feature = "analytics",
                upgrade = true,
                currentPlan = limits.DisplayName,
                requiredPlan = requiredPlan?.DisplayName,
            });
        }
        return null;
    }

    /// <summary>
    /// Track a link click
    /// </summary>
    [HttpPost("click")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TrackLinkClick([FromBody] TrackLinkClickDto dto)
    {
        var referrerUrl = Request.Headers.Referer.ToString();
        await _trackingService.TrackLinkClickAsync(dto, referrerUrl);

        return Ok();
    }

    /// <summary>
    /// Get simplified tracking statistics
    /// If no date filters are provided, returns ALL TIME statistics
    /// </summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SimplifiedTrackingStatisticsDto>> GetTrackingStatistics(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate)
    {
        var gate = await RequireAnalyticsPlanAsync();
        if (gate != null) return gate;

        var result = await _trackingService.GetTrackingStatisticsAsync(fromDate, toDate);
        return Ok(result);
    }

    /// <summary>
    /// Get revenue statistics for different time periods
    /// </summary>
    [HttpGet("revenue")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RevenueStatisticsDto>> GetRevenueStatistics()
    {
        var gate = await RequireAnalyticsPlanAsync();
        if (gate != null) return gate;

        var result = await _trackingService.GetRevenueStatisticsAsync();
        return Ok(result);
    }

    [HttpPost("pageview")]
    public async Task<IActionResult> TrackPageView([FromBody] TrackPageViewDto dto)
    {
        var userAgent = Request.Headers.UserAgent.ToString();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        var (success, errorMessage) = await _trackingService.TrackPageViewAsync(dto, userAgent, ipAddress);

        if (!success)
        {
            return Ok(new { success = false, error = errorMessage });
        }

        return Ok(new { success = true });
    }
}