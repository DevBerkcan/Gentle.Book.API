using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

// Agency-exclusive public REST API for external integrations (custom websites, Zapier,
// internal tooling). Authenticated via X-Api-Key (see ApiKeyAuthenticationHandler) — the
// handler already resolves the key to a tenantId claim, which flows through the existing
// TenantMiddleware/ITenantContext exactly like a JWT-authenticated request, so every method
// here reuses the same tenant-scoped services the admin UI uses (no separate data-access path).
// Read-only for services/employees; bookings can be listed and created — no PUT/DELETE on
// services/employees via this API in this round (admin UI is still the only way to manage them).
[ApiController]
[Route("api/v1")]
[Authorize(AuthenticationSchemes = "ApiKey")]
[EnableRateLimiting("api-key-limit")]
public class PublicApiV1Controller : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly ServiceService _serviceService;
    private readonly EmployeeService _employeeService;
    private readonly BookingService _bookingService;

    public PublicApiV1Controller(
        GentleBookDbContext db,
        ITenantContext tenantContext,
        ServiceService serviceService,
        EmployeeService employeeService,
        BookingService bookingService)
    {
        _db = db;
        _tenantContext = tenantContext;
        _serviceService = serviceService;
        _employeeService = employeeService;
        _bookingService = bookingService;
    }

    private async Task<IActionResult?> RequireAgencyAsync()
    {
        var tenantId = _tenantContext.TenantId;
        if (!tenantId.HasValue)
            return Unauthorized(new { message = "TenantId fehlt." });

        var plan = await _db.Subscriptions
            .Where(s => s.TenantId == tenantId.Value)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync();

        if (plan == null || AgencyFeatureGate.ValidateForPlan(plan.Value) != null)
            return StatusCode(402, new { message = "Der API-Zugang ist dem Agency-Plan vorbehalten." });

        return null;
    }

    // ── GET /api/v1/services ───────────────────────────────────────
    [HttpGet("services")]
    public async Task<IActionResult> GetServices()
    {
        if (await RequireAgencyAsync() is { } deny) return deny;
        return Ok(await _serviceService.GetServicesAsync());
    }

    // ── GET /api/v1/employees ──────────────────────────────────────
    [HttpGet("employees")]
    public async Task<IActionResult> GetEmployees()
    {
        if (await RequireAgencyAsync() is { } deny) return deny;
        return Ok(await _employeeService.GetAllAsync());
    }

    // ── GET /api/v1/bookings ───────────────────────────────────────
    [HttpGet("bookings")]
    public async Task<IActionResult> GetBookings([FromQuery] DateOnly? fromDate, [FromQuery] DateOnly? toDate)
    {
        if (await RequireAgencyAsync() is { } deny) return deny;
        var bookings = await _bookingService.GetAllBookingsAsync(employeeId: null, fromDate: fromDate, toDate: toDate);
        return Ok(bookings);
    }

    // ── POST /api/v1/bookings ──────────────────────────────────────
    [HttpPost("bookings")]
    public async Task<IActionResult> CreateBooking([FromBody] CreateBookingDto dto)
    {
        if (await RequireAgencyAsync() is { } deny) return deny;

        // Defense in depth: BookingService derives the booking's tenant from the Service
        // entity itself, not from ITenantContext — for this authenticated, key-scoped surface
        // we additionally verify the requested ServiceId actually belongs to the calling
        // tenant, so one Agency customer's key can never create a booking on another tenant's
        // service by supplying a foreign ServiceId.
        var serviceTenantId = await _db.Services
            .Where(s => s.Id == dto.ServiceId)
            .Select(s => (Guid?)s.TenantId)
            .FirstOrDefaultAsync();
        if (serviceTenantId == null || serviceTenantId != _tenantContext.TenantId)
            return NotFound(new { message = "Service nicht gefunden." });

        try
        {
            var booking = await _bookingService.CreateBookingAsync(dto);
            return StatusCode(StatusCodes.Status201Created, booking);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }
}
