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
    /// Feature gate: detailed analytics are part of Professional/Business plans.
    /// Returns 402 with upgrade hint when the current plan has no analytics.
    /// </summary>
    private async Task<ObjectResult?> RequireAnalyticsPlanAsync()
    {
        var tenantId = _tenantContext.TenantId;
        if (!tenantId.HasValue) return null; // SuperAdmin or misconfigured — don't block here

        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId.Value);
        var limits = PlanLimits.Get(sub?.Plan ?? SubscriptionPlan.Trial);
        if (!limits.HasAnalytics)
            return StatusCode(402, new
            {
                message = "Detaillierte Statistiken sind ab dem Professional-Plan verfügbar.",
                feature = "analytics",
                upgrade = true
            });
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