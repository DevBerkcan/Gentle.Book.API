using System.Text;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] 
public class AdminController : ControllerBase
{
    private readonly AdminService _adminService;
    private readonly ManualBookingService _manualBookingService;
    private readonly EmailService _emailService;
    private readonly GentleBookDbContext _db;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        AdminService adminService,
        ILogger<AdminController> logger,
        ManualBookingService manualBookingService,
        EmailService emailService,
        GentleBookDbContext db)
    {
        _adminService = adminService;
        _logger = logger;
        _manualBookingService = manualBookingService;
        _emailService = emailService;
        _db = db;
    }

    // ── Helper to get current employee from JWT ──────────────────
    private Guid? GetCurrentEmployeeId() => JwtService.GetEmployeeId(User);
    private bool HasTenantWideAccess()
        => JwtService.GetRole(User) is "TenantAdmin" or "SuperAdmin";
    private Guid? GetEmployeeScope() => HasTenantWideAccess() ? null : GetCurrentEmployeeId();

    private async Task<bool> CanAccessBookingAsync(Guid bookingId)
    {
        var employeeId = GetEmployeeScope();
        return !employeeId.HasValue || await _db.Bookings
            .AnyAsync(b => b.Id == bookingId && b.EmployeeId == employeeId.Value);
    }

    /// <summary>
    /// Get dashboard overview with today's bookings, next booking, and statistics
    /// </summary>
    [HttpGet("dashboard")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardOverviewDto>> GetDashboard()
    {
        var employeeId = GetEmployeeScope();
        var dashboard = await _adminService.GetDashboardOverviewAsync(employeeId);
        return Ok(dashboard);
    }

    /// <summary>
    /// Returns onboarding checklist status for the current tenant.
    /// Used to show a setup guide for new tenant admins.
    /// </summary>
    [HttpGet("onboarding")]
    public async Task<IActionResult> GetOnboardingStatus()
    {
        var tenantId = JwtService.GetTenantId(User);
        if (tenantId == null) return Unauthorized();

        var settings = await _db.TenantSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId.Value);

        var hasLogo       = !string.IsNullOrEmpty(settings?.LogoUrl);
        var hasCompany    = !string.IsNullOrEmpty(settings?.CompanyName);
        var hasServices   = await _db.Services.AnyAsync();
        var hasEmployees  = await _db.Employees.AnyAsync();
        var hasHours      = await _db.BusinessHours.AnyAsync();
        var hasBooking    = await _db.Bookings.AnyAsync();

        var completed = new[] { hasLogo, hasCompany, hasServices, hasEmployees, hasHours }.Count(x => x);
        var isComplete = completed >= 5;

        return Ok(new {
            HasLogo      = hasLogo,
            HasCompany   = hasCompany,
            HasServices  = hasServices,
            HasEmployees = hasEmployees,
            HasHours     = hasHours,
            HasBooking   = hasBooking,
            IsComplete   = isComplete,
            CompletedSteps = completed,
            TotalSteps   = 5,
        });
    }

    /// <summary>
    /// Get dashboard statistics
    /// </summary>
    [HttpGet("statistics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardStatisticsDto>> GetStatistics()
    {
        var employeeId = GetEmployeeScope();
        var statistics = await _adminService.GetDashboardStatisticsAsync(employeeId);
        return Ok(statistics);
    }

    /// <summary>
    /// Get paginated list of bookings with filters
    /// Pass ?all=true to see all employees' bookings (admin only)
    /// </summary>
    [HttpGet("bookings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponseDto<BookingListItemDto>>> GetBookings(
        [FromQuery] string? status,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] Guid? serviceId,
        [FromQuery] string? searchTerm,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool all = false)
    {
        var currentEmployeeId = GetEmployeeScope();

        // If all=true, show all bookings (no filter)
        // Otherwise show only current employee's bookings
        Guid? employeeId = all && HasTenantWideAccess() ? null : currentEmployeeId;

        var filter = new BookingFilterDto(
            status,
            fromDate,
            toDate,
            serviceId,
            searchTerm,
            page,
            pageSize,
            employeeId // Pass the employee filter
        );

        var result = await _adminService.GetBookingsAsync(filter);
        return Ok(result);
    }

    /// <summary>
    /// Update booking status
    /// </summary>
    [HttpPatch("bookings/{id}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingListItemDto>> UpdateBookingStatus(
        Guid id,
        [FromBody] UpdateBookingStatusDto dto)
    {
        if (!await CanAccessBookingAsync(id))
            return Forbid();

        try
        {
            var booking = await _adminService.UpdateBookingStatusAsync(id, dto);
            return Ok(booking);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid request for booking {BookingId}", id);
            return NotFound(new { message = ex.Message });
        }
    }

    // In AdminController.cs
    [HttpGet("manual/booking/check-email")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<EmailConflictCheckDto>> CheckEmailConflict(
        [FromQuery] string email,
        [FromQuery] string firstName,
        [FromQuery] string lastName)
    {
        try
        {
            var employeeId = GetEmployeeScope();
            var result = await _manualBookingService.CheckEmailConflictAsync(email, firstName, lastName, employeeId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking email conflict");
            return StatusCode(500, new { message = "Fehler beim Prüfen der E-Mail" });
        }
    }

    // Add this DTO
    public class EmailConflictCheckDto
    {
        public bool HasConflict { get; set; }
        public string? ExistingName { get; set; }
        public string? ExistingEmail { get; set; }
    }

    /// <summary>
    /// Create a manual booking for a customer (e.g., phone call)
    /// </summary>
    [HttpPost("manual/booking")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ManualBookingResponseDto>> CreateManualBooking([FromBody] CreateManualBookingDto dto)
    {
        try
        {
            var employeeId = GetEmployeeScope();
            if (employeeId.HasValue)
                dto = dto with { EmployeeId = employeeId.Value };

            var booking = await _manualBookingService.CreateManualBookingAsync(dto);

            return CreatedAtAction(
                nameof(GetManualBooking),
                new { id = booking.Id },
                booking
            );
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid manual booking request");
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Conflict creating manual booking");
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating manual booking");
            return StatusCode(500, new { message = "Ein unerwarteter Fehler ist aufgetreten" });
        }
    }

    /// <summary>
    /// Get a manual booking by ID
    /// </summary>
    [HttpGet("manual/booking/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ManualBookingResponseDto>> GetManualBooking(Guid id)
    {
        if (!await CanAccessBookingAsync(id))
            return Forbid();

        var booking = await _manualBookingService.GetManualBookingByIdAsync(id);

        if (booking == null)
        {
            return NotFound(new { message = "Buchung nicht gefunden" });
        }

        return Ok(booking);
    }

    /// <summary>
    /// Permanently delete a booking (admin only)
    /// </summary>
    [HttpDelete("bookings/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DeleteBookingResponseDto>> DeleteBooking(
        Guid id,
        [FromBody] DeleteBookingDto? dto = null)
    {
        if (!await CanAccessBookingAsync(id))
            return Forbid();

        try
        {
            var result = await _adminService.DeleteBookingAsync(id, dto?.Reason);
            _logger.LogInformation("Booking permanently deleted: {BookingId} by admin", id);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Booking not found for deletion: {BookingId}", id);
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot delete booking: {BookingId}", id);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Export bookings as CSV with optional filters.</summary>
    [HttpGet("bookings/export")]
    public async Task<IActionResult> ExportBookingsCsv(
        [FromQuery] string? status,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate)
    {
        var query = _db.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Employee)
            .AsQueryable();

        var employeeId = GetEmployeeScope();
        if (employeeId.HasValue)
            query = query.Where(b => b.EmployeeId == employeeId.Value);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(b => b.Status.ToString() == status);
        if (fromDate.HasValue)
            query = query.Where(b => b.BookingDate >= DateOnly.FromDateTime(fromDate.Value));
        if (toDate.HasValue)
            query = query.Where(b => b.BookingDate <= DateOnly.FromDateTime(toDate.Value));

        var bookings = await query.OrderBy(b => b.BookingDate).ThenBy(b => b.StartTime).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Buchungsnummer,Datum,Startzeit,Endzeit,Kunde,E-Mail,Telefon,Service,Mitarbeiter,Preis,Währung,Status,Erstellt am");

        foreach (var b in bookings)
        {
            var price = (b.Service?.Price ?? 0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            var bookingNumber = Booking.GenerateBookingNumber(b.BookingDate, b.Id);
            var row = string.Join(",", new[]
            {
                CsvEscape(bookingNumber),
                b.BookingDate.ToString("dd.MM.yyyy"),
                b.StartTime.ToString(@"hh\:mm"),
                b.EndTime.ToString(@"hh\:mm"),
                CsvEscape(b.Customer?.FullName ?? ""),
                CsvEscape(b.Customer?.Email ?? ""),
                CsvEscape(b.Customer?.Phone ?? ""),
                CsvEscape(b.Service?.Name ?? ""),
                CsvEscape(b.Employee?.Name ?? ""),
                price,
                CsvEscape(b.Service?.Currency ?? "EUR"),
                CsvEscape(b.Status.ToString()),
                b.CreatedAt.ToString("dd.MM.yyyy HH:mm"),
            });
            sb.AppendLine(row);
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        var fileName = $"buchungen_{DateTime.Now:yyyyMMdd_HHmm}.csv";
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    private static string CsvEscape(string val)
    {
        if (val.Contains(',') || val.Contains('"') || val.Contains('\n'))
            return $"\"{val.Replace("\"", "\"\"")}\"";
        return val;
    }

    /// <summary>Get recent cancellation notifications for this tenant (last 14 days).</summary>
    [HttpGet("notifications")]
    public async Task<IActionResult> GetNotifications()
    {
        var cutoff = DateTime.UtcNow.AddDays(-14);

        var employeeId = GetEmployeeScope();
        var cancelledQuery = _db.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Employee)
            .Where(b => b.Status == BookingStatus.Cancelled && b.CancelledAt >= cutoff);

        if (employeeId.HasValue)
            cancelledQuery = cancelledQuery.Where(b => b.EmployeeId == employeeId.Value);

        var cancelled = await cancelledQuery
            .OrderByDescending(b => b.CancelledAt)
            .Take(30)
            .Select(b => new
            {
                b.Id,
                Type = "cancellation",
                CustomerName = b.Customer != null ? b.Customer.FullName : "Unbekannt",
                ServiceName  = b.Service != null ? b.Service.Name : "–",
                EmployeeName = b.Employee != null ? b.Employee.Name : (string?)null,
                BookingDate  = b.BookingDate,
                StartTime    = b.StartTime,
                CancelledAt  = b.CancelledAt,
                Reason       = b.CancellationReason,
            })
            .ToListAsync();

        return Ok(new { notifications = cancelled, count = cancelled.Count });
    }

    /// <summary>Resend booking confirmation email.</summary>
    [HttpPost("bookings/{id}/resend-confirmation")]
    public async Task<IActionResult> ResendConfirmation(Guid id)
    {
        if (!await CanAccessBookingAsync(id))
            return Forbid();

        var booking = await _db.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new { message = "Buchung nicht gefunden." });

        if (booking.Status.ToString() == "Cancelled")
            return BadRequest(new { message = "Stornierte Buchungen können nicht erneut bestätigt werden." });

        try
        {
            await _emailService.SendConfirmationReceiptAsync(booking, booking.Customer, booking.Service);
            _logger.LogInformation("Confirmation resent for booking {BookingId}", id);
            return Ok(new { message = $"Bestätigung wurde erneut an {booking.Customer.Email} gesendet." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resend confirmation for booking {BookingId}", id);
            return StatusCode(500, new { message = "E-Mail konnte nicht gesendet werden." });
        }
    }
}
