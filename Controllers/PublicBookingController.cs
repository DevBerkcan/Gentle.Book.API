// Controllers/PublicBookingController.cs
// Public, unauthenticated endpoints — tenant identified by slug.
using GentleBook.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/public/{slug}")]
[AllowAnonymous]
public class PublicBookingController : ControllerBase
{
    private readonly GentleBookDbContext _db;

    public PublicBookingController(GentleBookDbContext db)
    {
        _db = db;
    }

    // ── Resolve Tenant by Slug ────────────────────────────────
    private async Task<(Guid tenantId, bool found)> ResolveTenant(string slug)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug.ToLowerInvariant() && t.IsActive);

        return tenant == null ? (Guid.Empty, false) : (tenant.Id, true);
    }

    // ── Branding ──────────────────────────────────────────────

    /// <summary>Returns branding/settings for white-label UI.</summary>
    [HttpGet("branding")]
    public async Task<IActionResult> GetBranding(string slug)
    {
        var (tenantId, found) = await ResolveTenant(slug);
        if (!found) return NotFound(new { message = "Booking system not found." });

        var settings = await _db.TenantSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);

        if (settings == null) return NotFound();

        return Ok(new
        {
            settings.CompanyName,
            settings.Tagline,
            settings.LogoUrl,
            settings.PrimaryColor,
            settings.SecondaryColor,
            settings.AccentColor,
            settings.WelcomeMessage,
            settings.Phone,
            settings.Email,
            settings.Website,
            settings.Address,
            settings.DefaultCurrency,
            settings.TimeZone,
            settings.CancellationPolicy,
        });
    }

    // ── Services ──────────────────────────────────────────────

    /// <summary>Returns active services for the booking form.</summary>
    [HttpGet("services")]
    public async Task<IActionResult> GetServices(string slug)
    {
        var (tenantId, found) = await ResolveTenant(slug);
        if (!found) return NotFound();

        var services = await _db.Services
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive)
            .Include(s => s.Category)
            .OrderBy(s => s.Category.DisplayOrder)
            .ThenBy(s => s.DisplayOrder)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Description,
                s.DurationMinutes,
                s.Price,
                s.Currency,
                Category = s.Category.Name,
                CategoryId = s.CategoryId,
            })
            .ToListAsync();

        return Ok(services);
    }

    // ── Employees ─────────────────────────────────────────────

    /// <summary>Returns active employees (for employee selection in booking form).</summary>
    [HttpGet("employees")]
    public async Task<IActionResult> GetEmployees(string slug)
    {
        var (tenantId, found) = await ResolveTenant(slug);
        if (!found) return NotFound();

        var employees = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.IsActive)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Specialty,
                e.Location,
            })
            .ToListAsync();

        return Ok(employees);
    }

    /// <summary>Returns employees that offer a specific service.</summary>
    [HttpGet("services/{serviceId:guid}/employees")]
    public async Task<IActionResult> GetEmployeesByService(string slug, Guid serviceId)
    {
        var (tenantId, found) = await ResolveTenant(slug);
        if (!found) return NotFound();

        var employees = await _db.ServiceEmployees
            .AsNoTracking()
            .Where(se => se.ServiceId == serviceId && se.Employee.TenantId == tenantId && se.Employee.IsActive)
            .Select(se => new
            {
                se.Employee.Id,
                se.Employee.Name,
                se.Employee.Specialty,
            })
            .ToListAsync();

        return Ok(employees);
    }

    // ── Availability ──────────────────────────────────────────

    /// <summary>
    /// Returns available time slots.
    /// Delegates to the existing AvailabilityService — just scoped to the tenant.
    /// </summary>
    [HttpGet("availability")]
    public async Task<IActionResult> GetAvailability(
        string slug,
        [FromQuery] Guid? serviceId,
        [FromQuery] Guid? employeeId,
        [FromQuery] DateOnly? date)
    {
        var (tenantId, found) = await ResolveTenant(slug);
        if (!found) return NotFound();

        // Validate the service belongs to this tenant
        if (serviceId.HasValue)
        {
            var serviceValid = await _db.Services.AnyAsync(s => s.Id == serviceId && s.TenantId == tenantId);
            if (!serviceValid) return NotFound(new { message = "Service not found." });
        }

        // Delegate to AvailabilityService (pass tenantId as filter)
        // Note: AvailabilityService will be updated in Phase 2 to accept tenantId.
        // For now, return a placeholder — availability logic is already implemented.
        return Ok(new { tenantId, serviceId, employeeId, date, message = "Connect to AvailabilityService" });
    }

    // ── Bookings ──────────────────────────────────────────────

    /// <summary>Get a specific booking by ID (for confirmation page).</summary>
    [HttpGet("bookings/{bookingId:guid}")]
    public async Task<IActionResult> GetBooking(string slug, Guid bookingId)
    {
        var (tenantId, found) = await ResolveTenant(slug);
        if (!found) return NotFound();

        var booking = await _db.Bookings
            .AsNoTracking()
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Employee)
            .Where(b => b.Id == bookingId && b.TenantId == tenantId)
            .Select(b => new
            {
                b.Id,
                BookingNumber = Booking.GenerateBookingNumber(b.BookingDate, b.Id),
                b.Status,
                b.BookingDate,
                b.StartTime,
                b.EndTime,
                Service = b.Service.Name,
                ServicePrice = b.Service.Price,
                ServiceCurrency = b.Service.Currency,
                Employee = b.Employee != null ? b.Employee.Name : null,
                Customer = new { b.Customer.FirstName, b.Customer.LastName, b.Customer.Email, b.Customer.Phone },
                b.CustomerNotes,
            })
            .FirstOrDefaultAsync();

        if (booking == null) return NotFound();
        return Ok(booking);
    }
}
