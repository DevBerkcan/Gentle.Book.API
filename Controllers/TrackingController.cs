using GentleBook.Api.Data;
using GentleBook.Api.DTOs;
using Microsoft.AspNetCore.Mvc;
using Skinbloom.Api.Services;

namespace GentleBook.Api.Controllers.Admin;

[ApiController]
[Route("api/admin/tracking")]
public class TrackingController : ControllerBase
{
    private readonly TrackingService _trackingService;

    public TrackingController(TrackingService trackingService)
    {
        _trackingService = trackingService;
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SimplifiedTrackingStatisticsDto>> GetTrackingStatistics(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate)
    {
        var result = await _trackingService.GetTrackingStatisticsAsync(fromDate, toDate);
        return Ok(result);
    }

    /// <summary>
    /// Get revenue statistics for different time periods
    /// </summary>
    [HttpGet("revenue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RevenueStatisticsDto>> GetRevenueStatistics()
    {
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