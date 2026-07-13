using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using GentleBook.Api.Validators;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BookingsController : ControllerBase
{
    private readonly BookingService _bookingService;
    private readonly ILogger<BookingsController> _logger;
    private readonly EmailService _emailService;
    private readonly IConfiguration _config;

    public BookingsController(
        BookingService bookingService,
        ILogger<BookingsController> logger,
        EmailService emailService,
        IConfiguration config)
    {
        _bookingService = bookingService;
        _logger = logger;
        _emailService = emailService;
        _config = config;
    }

    // ── Helpers ───────────────────────────────────────────────────

    private Guid? GetCurrentEmployeeId() => JwtService.GetEmployeeId(User);
    private bool IsTenantAdmin() => JwtService.GetRole(User) == "TenantAdmin";
    private Guid? GetEmployeeScope() => IsTenantAdmin() ? null : GetCurrentEmployeeId();

    // ─────────────────────────────────────────────────────────────
    // PUBLIC ENDPOINTS
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Create a new booking — public endpoint used by the booking widget.
    /// If an employee is authenticated via JWT their ID is injected automatically
    /// when the request body does not already specify one.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("booking-limit")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingResponseDto>> CreateBooking([FromBody] CreateBookingDto dto)
    {
        var validator = new CreateBookingValidator();
        var validationResult = await validator.ValidateAsync(dto);

        if (!validationResult.IsValid)
        {
            var errors = validationResult.Errors
                .Select(e => new { field = e.PropertyName, message = e.ErrorMessage });
            return BadRequest(new { errors });
        }

        // Auto-inject employee from JWT when body didn't specify one
        if (dto.EmployeeId == null && User.Identity?.IsAuthenticated == true)
        {
            var empId = GetCurrentEmployeeId();
            if (empId != null)
                dto = dto with { EmployeeId = empId };
        }

        try
        {
            var booking = await _bookingService.CreateBookingAsync(dto);
            _logger.LogInformation("Booking created successfully: {BookingId}", booking.Id);

            return CreatedAtAction(nameof(GetBooking), new { id = booking.Id }, booking);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid booking request");
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Booking conflict or validation error");
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error creating booking");
            return StatusCode(500, new { message = "Buchung konnte nicht erstellt werden. Bitte erneut versuchen." });
        }
    }

    /// <summary>
    /// Get booking by ID — public (used for customer confirmation pages).
    /// Authenticated employees are restricted to their own bookings unless admin.
    /// </summary>
    [HttpGet("{id}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingResponseDto>> GetBooking(Guid id)
    {
        var booking = await _bookingService.GetBookingByIdAsync(id);
        if (booking == null)
            return NotFound(new { message = "Buchung nicht gefunden" });

        // Authenticated employees can only view their own bookings
        if (User.Identity?.IsAuthenticated == true)
        {
            var empId = GetCurrentEmployeeId();
            if (empId != null && booking.Employee?.Id != empId)
                return Forbid();
        }

        return Ok(booking);
    }

    /// <summary>
    /// Get bookings by customer email — public.
    /// </summary>
    [HttpGet("by-email/{email}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<BookingResponseDto>>> GetBookingsByEmail(string email)
    {
        var bookings = await _bookingService.GetBookingsByEmailAsync(email);
        return Ok(bookings);
    }

    // ─────────────────────────────────────────────────────────────
    // AUTHENTICATED ENDPOINTS (require JWT)
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Get all bookings — scoped to the authenticated employee.
    /// TenantAdmin and SuperAdmin can request the full tenant scope.
    /// </summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<BookingResponseDto>>> GetAllBookings(
        [FromQuery] bool all = false)
    {
        var employeeId = all ? null : GetEmployeeScope();
        var bookings = await _bookingService.GetAllBookingsAsync(employeeId);
        return Ok(bookings);
    }

    /// <summary>Export all bookings as CSV file.</summary>
    [HttpGet("export/csv")]
    [Authorize]
    public async Task<IActionResult> ExportCsv()
    {
        var bookings = await _bookingService.GetAllBookingsAsync(null);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Buchungsnummer;Status;Datum;Uhrzeit;Ende;Service;Preis;Währung;Vorname;Nachname;E-Mail;Mitarbeiter");

        foreach (var b in bookings)
        {
            var employee = b.Employee?.Name ?? "";
            var line = string.Join(";", new[]
            {
                Escape(b.BookingNumber),
                Escape(b.Status),
                Escape(b.Booking.BookingDate),
                Escape(b.Booking.StartTime),
                Escape(b.Booking.EndTime),
                Escape(b.Booking.ServiceName),
                b.Booking.Price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Escape(b.Booking.Currency),
                Escape(b.Customer.FirstName),
                Escape(b.Customer.LastName),
                Escape(b.Customer.Email ?? ""),
                Escape(employee),
            });
            sb.AppendLine(line);
        }

        var bytes = System.Text.Encoding.UTF8.GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))
            .ToArray();

        var fileName = $"buchungen_{DateTime.UtcNow:yyyyMMdd}.csv";
        return File(bytes, "text/csv; charset=utf-8", fileName);

        static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }
    }

    /// <summary>Resend booking confirmation email to the customer.</summary>
    [HttpPost("{id}/resend-confirmation")]
    [Authorize]
    public async Task<IActionResult> ResendConfirmation(Guid id)
    {
        try
        {
            await _emailService.SendBookingConfirmationAsync(id);
            return Ok(new { message = "Bestätigung wurde erneut gesendet." });
        }
        catch (ArgumentException)
        {
            return NotFound(new { message = "Buchung nicht gefunden." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resend confirmation for booking {BookingId}", id);
            return StatusCode(500, new { message = "E-Mail konnte nicht gesendet werden." });
        }
    }

    /// <summary>
    /// Cancel a booking.
    /// Employees can only cancel their own bookings unless admin.
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CancelBookingResponseDto>> CancelBooking(
        Guid id,
        [FromBody] CancelBookingDto? dto = null)
    {
        dto ??= new CancelBookingDto(null, true);


        var booking = await _bookingService.GetBookingByIdAsync(id);
        if (booking == null)
                return NotFound(new { message = "Buchung nicht gefunden" });

        var empId = GetEmployeeScope();
        if (empId != null && booking.Employee?.Id != empId)
                return Forbid(); 

        try
        {
            var result = await _bookingService.CancelBookingAsync(id, dto);
            _logger.LogInformation("Booking cancelled: {BookingId}", id);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Booking not found: {BookingId}", id);
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot cancel booking: {BookingId}", id);
            return BadRequest(new { message = ex.Message });
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TOKEN-BASED ENDPOINTS (customer email links — anonymous)
    // ─────────────────────────────────────────────────────────────

    [HttpGet("confirm/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> ConfirmBooking(string token)
    {
        try
        {
            var (bookingId, action) = _emailService.DecodeToken(token);

            if (bookingId == Guid.Empty || action != "confirm")
            {
                return Content(@"
                <!DOCTYPE html>
                <html>
                <head>
                    <title>Fehler - GentleBook</title>
                    <style>
                        body { font-family: Arial, sans-serif; text-align: center; padding: 50px; }
                        .error { color: #dc3545; }
                    </style>
                </head>
                <body>
                    <h1 class='error'>❌ Ungültiger Bestätigungslink</h1>
                    <p>Der Bestätigungslink ist ungültig oder abgelaufen.</p>
                    <p><a href='https://gentlebook.app'>Zurück zur Website</a></p>
                </body>
                </html>", "text/html");
            }

            var booking = await _bookingService.ConfirmBookingAsync(bookingId);

            return Content($@"
            <!DOCTYPE html>
            <html>
            <head>
                <title>Buchung bestätigt - GentleBook</title>
                <style>
                    body {{ font-family: Arial, sans-serif; text-align: center; padding: 50px; }}
                    .success {{ color: #28a745; }}
                    .details {{ 
                        margin: 30px auto; 
                        max-width: 500px; 
                        text-align: left; 
                        background: #f8f9fa; 
                        padding: 20px; 
                        border-radius: 10px;
                        border-left: 4px solid #28a745;
                    }}
                </style>
            </head>
            <body>
                <h1 class='success'> Buchung erfolgreich bestätigt!</h1>
                <p>Vielen Dank für die Bestätigung Ihrer Buchung.</p>
                <div class='details'>
                    <h3>Ihre Buchungsdetails:</h3>
                    <p><strong>Buchungsnummer:</strong> {booking.BookingNumber}</p>
                    <p><strong>Service:</strong> {booking.Booking.ServiceName}</p>
                    <p><strong>Datum:</strong> {booking.Booking.BookingDate}</p>
                    <p><strong>Uhrzeit:</strong> {booking.Booking.StartTime} - {booking.Booking.EndTime}</p>
                    <p><strong>Preis:</strong> {booking.Booking.Price:C}</p>
                    <p><strong>Status:</strong> <span style='color: #28a745; font-weight: bold;'>{booking.Status}</span></p>
                </div>
                <p><a href='https://gentlebook.app' style='color: #007bff; text-decoration: none;'>Zurück zur Website</a></p>
            </body>
            </html>", "text/html");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error confirming booking with token: {Token}", token);

            return Content($@"
            <!DOCTYPE html>
            <html>
            <head>
                <title>Fehler - GentleBook</title>
                <style>
                    body {{ font-family: Arial, sans-serif; text-align: center; padding: 50px; }}
                    .error {{ color: #dc3545; }}
                </style>
            </head>
            <body>
                <h1 class='error'> Fehler bei der Bestätigung</h1>
                <p><strong>Fehler:</strong> {ex.Message}</p>
                <p>Bitte kontaktieren Sie uns telefonisch oder per Email.</p>
                <p><a href='https://www.gentlebook.app/'>Zurück zur Website</a></p>
            </body>
            </html>", "text/html");
        }
    }

    /// <summary>Preview booking details by cancel token (no cancellation performed).</summary>
    [HttpGet("cancel/preview/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> PreviewCancelBooking(string token)
    {
        try
        {
            var (bookingId, action) = _emailService.DecodeToken(token);
            if (bookingId == Guid.Empty || action != "cancel")
                return BadRequest(new { message = "Ungültiger Stornierungslink." });

            var booking = await _bookingService.GetBookingByIdAsync(bookingId);
            if (booking == null)
                return NotFound(new { message = "Buchung nicht gefunden." });

            if (booking.Status == "Cancelled")
                return Ok(new { alreadyCancelled = true, message = "Diese Buchung wurde bereits storniert." });

            return Ok(new
            {
                alreadyCancelled = false,
                bookingNumber = booking.BookingNumber,
                serviceName = booking.Booking.ServiceName,
                bookingDate = booking.Booking.BookingDate,
                startTime = booking.Booking.StartTime,
                endTime = booking.Booking.EndTime,
                customerName = booking.Customer != null ? $"{booking.Customer.FirstName} {booking.Customer.LastName}" : null,
            });
        }
        catch
        {
            return BadRequest(new { message = "Ungültiger Stornierungslink." });
        }
    }

    [HttpGet("cancel/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> CancelBookingByToken(string token)
    {
        try
        {
            var (bookingId, action) = _emailService.DecodeToken(token);

            if (bookingId == Guid.Empty || action != "cancel")
            {
                return Content(@"
            <!DOCTYPE html>
            <html>
            <head>
                <title>Fehler - GentleBook</title>
                <meta charset='utf-8'>
                <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                <style>
                    body { 
                        font-family: 'Helvetica', 'Arial', sans-serif; 
                        text-align: center; 
                        padding: 0; 
                        margin: 0;
                        background-color: #F5EDEB;
                        color: #1E1E1E;
                        line-height: 1.6;
                    }
                    .container {
                        max-width: 600px;
                        margin: 40px auto;
                        background-color: #FFFFFF;
                        border-radius: 24px;
                        box-shadow: 0 20px 40px rgba(0,0,0,0.05);
                        overflow: hidden;
                    }
                    .header {
                        background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995;
                        padding: 40px 20px;
                    }
                    .content { padding: 40px; }
                    .error { color: #D8B0AC; font-size: 48px; margin-bottom: 20px; }
                    .title { font-size: 24px; font-weight: 700; color: #1E1E1E; margin-bottom: 20px; }
                    .message { color: #8A8A8A; margin-bottom: 30px; }
                    .button {
                        display: inline-block;
                        background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995;
                        color: #FFFFFF; text-decoration: none; padding: 14px 32px;
                        border-radius: 40px; font-weight: 600; font-size: 16px;
                        box-shadow: 0 4px 12px rgba(232,199,195,0.3);
                    }
                    .footer { background-color: #F5EDEB; padding: 24px; color: #8A8A8A; font-size: 14px; }
                </style>
            </head>
            <body>
                <div class='container'>
                    <div class='header'>
                        <div style='color: #C09995; font-size: 44px; font-weight: 300; margin-bottom: 12px;'>✧</div>
                        <h1 style='color: #FFFFFF; font-size: 28px; font-weight: 600; margin: 0;'>GentleBook</h1>
                    </div>
                    <div class='content'>
                        <div class='error'>✧</div>
                        <h2 class='title'>Ungültiger Stornierungslink</h2>
                        <p class='message'>Der Stornierungslink ist ungültig oder abgelaufen.</p>
                        <p style='margin-top: 30px;'>
                            <a href='https://www.gentlebook.app/' class='button'>Zurück zur Website</a>
                        </p>
                    </div>
                    <div class='footer'><p style='margin: 0;'>Elisabethenstrasse 41, 4051 Basel, Schweiz</p></div>
                </div>
            </body>
            </html>", "text/html");
            }

            var booking = await _bookingService.GetBookingByIdAsync(bookingId);
            if (booking == null)
            {
                return Content(@"
            <!DOCTYPE html>
            <html>
            <head>
                <title>Fehler - GentleBook</title>
                <meta charset='utf-8'>
                <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                <style>
                    body { font-family: 'Helvetica', 'Arial', sans-serif; text-align: center; padding: 0; margin: 0; background-color: #F5EDEB; color: #1E1E1E; }
                    .container { max-width: 600px; margin: 40px auto; background-color: #FFFFFF; border-radius: 24px; box-shadow: 0 20px 40px rgba(0,0,0,0.05); overflow: hidden; }
                    .header { background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; padding: 40px 20px; }
                    .content { padding: 40px; }
                    .error { color: #D8B0AC; font-size: 48px; margin-bottom: 20px; }
                    .title { font-size: 24px; font-weight: 700; color: #1E1E1E; margin-bottom: 20px; }
                    .message { color: #8A8A8A; margin-bottom: 30px; }
                    .button { display: inline-block; background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; color: #FFFFFF; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; }
                    .footer { background-color: #F5EDEB; padding: 24px; color: #8A8A8A; font-size: 14px; }
                </style>
            </head>
            <body>
                <div class='container'>
                    <div class='header'><h1 style='color: #FFFFFF; font-size: 28px; font-weight: 600; margin: 0;'>GentleBook</h1></div>
                    <div class='content'>
                        <div class='error'>✧</div>
                        <h2 class='title'>Buchung nicht gefunden</h2>
                        <p class='message'>Die Buchung konnte nicht gefunden werden.</p>
                        <p style='margin-top: 30px;'><a href='https://www.gentlebook.app/' class='button'>Zurück zur Website</a></p>
                    </div>
                    <div class='footer'><p style='margin: 0;'>Elisabethenstrasse 41, 4051 Basel, Schweiz</p></div>
                </div>
            </body>
            </html>", "text/html");
            }

            if (booking.Status == "Cancelled")
            {
                return Content($@"
            <!DOCTYPE html>
            <html>
            <head>
                <title>Bereits storniert - GentleBook</title>
                <meta charset='utf-8'>
                <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                <style>
                    body {{ font-family: 'Helvetica', 'Arial', sans-serif; text-align: center; padding: 0; margin: 0; background-color: #F5EDEB; color: #1E1E1E; line-height: 1.6; }}
                    .container {{ max-width: 600px; margin: 40px auto; background-color: #FFFFFF; border-radius: 24px; box-shadow: 0 20px 40px rgba(0,0,0,0.05); overflow: hidden; }}
                    .header {{ background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; padding: 40px 20px; }}
                    .content {{ padding: 40px; }}
                    .title {{ font-size: 28px; font-weight: 700; color: #1E1E1E; margin-bottom: 10px; }}
                    .booking-number {{ background-color: #F5EDEB; padding: 12px 24px; border-radius: 40px; display: inline-block; color: #8A8A8A; font-size: 14px; font-weight: 600; margin-bottom: 30px; }}
                    .details-card {{ background-color: #F5EDEB; border-radius: 16px; padding: 30px; text-align: left; margin-bottom: 30px; }}
                    .detail-row {{ display: flex; padding: 12px 0; border-bottom: 1px solid #E8C7C3; }}
                    .detail-row:last-child {{ border-bottom: none; }}
                    .detail-label {{ width: 120px; color: #8A8A8A; font-weight: 500; }}
                    .detail-value {{ flex: 1; color: #1E1E1E; font-weight: 600; }}
                    .status-cancelled {{ color: #D8B0AC; font-weight: 700; }}
                    .button {{ display: inline-block; background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; color: #FFFFFF; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; box-shadow: 0 4px 12px rgba(232,199,195,0.3); }}
                    .footer {{ background-color: #F5EDEB; padding: 24px; color: #8A8A8A; font-size: 14px; }}
                </style>
            </head>
            <body>
                <div class='container'>
                    <div class='header'>
                        <div style='color: #C09995; font-size: 44px; font-weight: 300; margin-bottom: 12px;'>✧</div>
                        <h1 style='color: #FFFFFF; font-size: 28px; font-weight: 600; margin: 0;'>GentleBook</h1>
                    </div>
                    <div class='content'>
                        <div style='color: #8A8A8A; font-size: 48px; margin-bottom: 20px;'>✧</div>
                        <h2 class='title'>Bereits storniert</h2>
                        <div class='booking-number'>Buchungsnummer: {booking.BookingNumber}</div>
                        <div class='details-card'>
                            <div class='detail-row'><span class='detail-label'>Leistung</span><span class='detail-value'>{booking.Booking.ServiceName}</span></div>
                            <div class='detail-row'><span class='detail-label'>Datum</span><span class='detail-value'>{booking.Booking.BookingDate}</span></div>
                            <div class='detail-row'><span class='detail-label'>Uhrzeit</span><span class='detail-value'>{booking.Booking.StartTime} - {booking.Booking.EndTime}</span></div>
                            <div class='detail-row'><span class='detail-label'>Preis</span><span class='detail-value'>{booking.Booking.Price:0.00} CHF</span></div>
                            <div class='detail-row'><span class='detail-label'>Status</span><span class='detail-value'><span class='status-cancelled'>Storniert</span></span></div>
                        </div>
                        <p style='margin-top: 30px;'><a href='https://www.gentlebook.app/' class='button'>Zurück zur Website</a></p>
                    </div>
                    <div class='footer'><p style='margin: 0;'>Elisabethenstrasse 41, 4051 Basel, Schweiz</p></div>
                </div>
            </body>
            </html>", "text/html");
            }

            if (booking.Status == "Completed")
            {
                return Content($@"
            <!DOCTYPE html>
            <html>
            <head>
                <title>Buchung abgeschlossen - GentleBook</title>
                <meta charset='utf-8'>
                <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                <style>
                    body {{ font-family: 'Helvetica', 'Arial', sans-serif; text-align: center; padding: 0; margin: 0; background-color: #F5EDEB; color: #1E1E1E; line-height: 1.6; }}
                    .container {{ max-width: 600px; margin: 40px auto; background-color: #FFFFFF; border-radius: 24px; box-shadow: 0 20px 40px rgba(0,0,0,0.05); overflow: hidden; }}
                    .header {{ background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; padding: 40px 20px; }}
                    .content {{ padding: 40px; }}
                    .title {{ font-size: 28px; font-weight: 700; color: #1E1E1E; margin-bottom: 10px; }}
                    .booking-number {{ background-color: #F5EDEB; padding: 12px 24px; border-radius: 40px; display: inline-block; color: #8A8A8A; font-size: 14px; font-weight: 600; margin-bottom: 30px; }}
                    .details-card {{ background-color: #F5EDEB; border-radius: 16px; padding: 30px; text-align: left; margin-bottom: 30px; }}
                    .detail-row {{ display: flex; padding: 12px 0; border-bottom: 1px solid #E8C7C3; }}
                    .detail-row:last-child {{ border-bottom: none; }}
                    .detail-label {{ width: 120px; color: #8A8A8A; font-weight: 500; }}
                    .detail-value {{ flex: 1; color: #1E1E1E; font-weight: 600; }}
                    .status-completed {{ color: #C09995; font-weight: 700; }}
                    .button {{ display: inline-block; background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; color: #FFFFFF; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; box-shadow: 0 4px 12px rgba(232,199,195,0.3); }}
                    .footer {{ background-color: #F5EDEB; padding: 24px; color: #8A8A8A; font-size: 14px; }}
                </style>
            </head>
            <body>
                <div class='container'>
                    <div class='header'><h1 style='color: #FFFFFF; font-size: 28px; font-weight: 600; margin: 0;'>GentleBook</h1></div>
                    <div class='content'>
                        <div style='color: #C09995; font-size: 48px; margin-bottom: 20px;'>✓</div>
                        <h2 class='title'>Buchung abgeschlossen</h2>
                        <div class='booking-number'>Buchungsnummer: {booking.BookingNumber}</div>
                        <div class='details-card'>
                            <div class='detail-row'><span class='detail-label'>Leistung</span><span class='detail-value'>{booking.Booking.ServiceName}</span></div>
                            <div class='detail-row'><span class='detail-label'>Datum</span><span class='detail-value'>{booking.Booking.BookingDate}</span></div>
                            <div class='detail-row'><span class='detail-label'>Uhrzeit</span><span class='detail-value'>{booking.Booking.StartTime} - {booking.Booking.EndTime}</span></div>
                            <div class='detail-row'><span class='detail-label'>Status</span><span class='detail-value'><span class='status-completed'>Abgeschlossen</span></span></div>
                        </div>
                        <p style='margin-top: 30px;'><a href='https://www.gentlebook.app/' class='button'>Zurück zur Website</a></p>
                    </div>
                    <div class='footer'><p style='margin: 0;'>Elisabethenstrasse 41, 4051 Basel, Schweiz</p></div>
                </div>
            </body>
            </html>", "text/html");
            }

            var cancelDto = new CancelBookingDto("Storniert vom Kunden per E-Mail-Link", true);
            var result = await _bookingService.CancelBookingAsync(bookingId, cancelDto);

            if (!result.Success)
            {
                return Content($@"
            <!DOCTYPE html>
            <html>
            <head>
                <title>Fehler - GentleBook</title>
                <meta charset='utf-8'>
                <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                <style>
                    body {{ font-family: 'Helvetica', 'Arial', sans-serif; text-align: center; padding: 0; margin: 0; background-color: #F5EDEB; color: #1E1E1E; line-height: 1.6; }}
                    .container {{ max-width: 600px; margin: 40px auto; background-color: #FFFFFF; border-radius: 24px; overflow: hidden; }}
                    .header {{ background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; padding: 40px 20px; }}
                    .content {{ padding: 40px; }}
                    .button {{ display: inline-block; background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; color: #FFFFFF; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; }}
                    .footer {{ background-color: #F5EDEB; padding: 24px; color: #8A8A8A; font-size: 14px; }}
                </style>
            </head>
            <body>
                <div class='container'>
                    <div class='header'><h1 style='color: #FFFFFF; font-size: 28px; font-weight: 600; margin: 0;'>GentleBook</h1></div>
                    <div class='content'>
                        <div style='color: #D8B0AC; font-size: 48px; margin-bottom: 20px;'>✧</div>
                        <h2 style='font-size: 24px; font-weight: 700; color: #1E1E1E;'>Stornierung fehlgeschlagen</h2>
                        <p style='color: #8A8A8A; margin-bottom: 30px;'>{result.Message}</p>
                        <p><a href='https://www.gentlebook.app/' class='button'>Zurück zur Website</a></p>
                    </div>
                    <div class='footer'><p style='margin: 0;'>Elisabethenstrasse 41, 4051 Basel, Schweiz</p></div>
                </div>
            </body>
            </html>", "text/html");
            }

            return Content($@"
        <!DOCTYPE html>
        <html>
        <head>
            <title>Buchung storniert - GentleBook</title>
            <meta charset='utf-8'>
            <meta name='viewport' content='width=device-width, initial-scale=1.0'>
            <style>
                body {{ font-family: 'Helvetica', 'Arial', sans-serif; text-align: center; padding: 0; margin: 0; background-color: #F5EDEB; color: #1E1E1E; line-height: 1.6; }}
                .container {{ max-width: 600px; margin: 40px auto; background-color: #FFFFFF; border-radius: 24px; box-shadow: 0 20px 40px rgba(0,0,0,0.05); overflow: hidden; }}
                .header {{ background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; padding: 40px 20px; }}
                .content {{ padding: 40px; }}
                .title {{ font-size: 28px; font-weight: 700; color: #1E1E1E; margin-bottom: 10px; }}
                .booking-number {{ background-color: #F5EDEB; padding: 12px 24px; border-radius: 40px; display: inline-block; color: #8A8A8A; font-size: 14px; font-weight: 600; margin-bottom: 30px; }}
                .details-card {{ background-color: #F5EDEB; border-radius: 16px; padding: 30px; text-align: left; margin-bottom: 30px; }}
                .detail-row {{ display: flex; padding: 12px 0; border-bottom: 1px solid #E8C7C3; }}
                .detail-row:last-child {{ border-bottom: none; }}
                .detail-label {{ width: 120px; color: #8A8A8A; font-weight: 500; }}
                .detail-value {{ flex: 1; color: #1E1E1E; font-weight: 600; }}
                .status-cancelled {{ color: #D8B0AC; font-weight: 700; }}
                .button {{ display: inline-block; background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; color: #FFFFFF; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; box-shadow: 0 4px 12px rgba(232,199,195,0.3); margin: 10px; }}
                .button-secondary {{ display: inline-block; background-color: #F5EDEB; color: #1E1E1E; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; border: 2px solid #E8C7C3; margin: 10px; }}
                .footer {{ background-color: #F5EDEB; padding: 24px; color: #8A8A8A; font-size: 14px; }}
                .price {{ color: #1E1E1E; font-size: 20px; font-weight: 700; }}
                .email-notice {{ color: #8A8A8A; font-size: 12px; margin-top: 30px; }}
            </style>
        </head>
        <body>
            <div class='container'>
                <div class='content'>
                    <div style='color: #E8C7C3; font-size: 64px; margin-bottom: 20px;'>✓</div>
                    <h2 class='title'>{result.Message}</h2>
                    <div class='booking-number'>Buchungsnummer: {booking.BookingNumber}</div>
                    <div class='details-card'>
                        <div class='detail-row'><span class='detail-label'>Leistung</span><span class='detail-value'>{booking.Booking.ServiceName}</span></div>
                        <div class='detail-row'><span class='detail-label'>Datum</span><span class='detail-value'>{booking.Booking.BookingDate}</span></div>
                        <div class='detail-row'><span class='detail-label'>Uhrzeit</span><span class='detail-value'>{booking.Booking.StartTime} - {booking.Booking.EndTime}</span></div>
                        <div class='detail-row'><span class='detail-label'>Preis</span><span class='detail-value'><span class='price'>{booking.Booking.Price:0.00} CHF</span></span></div>
                        <div class='detail-row'><span class='detail-label'>Status</span><span class='detail-value'><span class='status-cancelled'>Storniert</span></span></div>
                        <div class='detail-row'><span class='detail-label'>Grund</span><span class='detail-value'>Storniert vom Kunden per E-Mail-Link</span></div>
                    </div>
                    <div style='margin: 30px 0;'>
                        <a href='https://gentlebook.runasp.net/booking' class='button'>Neuen Termin buchen</a>
                        <a href='https://www.gentlebook.app/' class='button-secondary'>Zurück zur Website</a>
                    </div>
                    <p class='email-notice'>* Sie erhalten eine Bestätigungs-Email an: {booking.Customer.Email}</p>
                </div>
                <div class='footer'>
                    <p style='margin: 0;'>Elisabethenstrasse 41, 4051 Basel, Schweiz</p>
                    <p style='margin: 10px 0 0 0;'>www.gentlebook.app</p>
                    <p style='margin: 10px 0 0 0;'>© 2026 GentleBook</p>
                </div>
            </div>
        </body>
        </html>", "text/html");
        }
        catch (InvalidOperationException ex)
        {
            return Content($@"
        <!DOCTYPE html>
        <html>
        <head>
            <title>Fehler - GentleBook</title>
            <meta charset='utf-8'>
            <style>
                body {{ font-family: 'Helvetica', 'Arial', sans-serif; text-align: center; padding: 0; margin: 0; background-color: #F5EDEB; color: #1E1E1E; }}
                .container {{ max-width: 600px; margin: 40px auto; background-color: #FFFFFF; border-radius: 24px; overflow: hidden; }}
                .header {{ background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; padding: 40px 20px; }}
                .content {{ padding: 40px; }}
                .button {{ display: inline-block; background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; color: #FFFFFF; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; }}
                .footer {{ background-color: #F5EDEB; padding: 24px; color: #8A8A8A; font-size: 14px; }}
            </style>
        </head>
        <body>
            <div class='container'>
                <div class='header'><h1 style='color: #FFFFFF; font-size: 28px; font-weight: 600; margin: 0;'>GentleBook</h1></div>
                <div class='content'>
                    <div style='color: #D8B0AC; font-size: 48px; margin-bottom: 20px;'>✧</div>
                    <h2 style='font-size: 24px; font-weight: 700; color: #1E1E1E; margin-bottom: 20px;'>Fehler bei der Stornierung</h2>
                    <p style='color: #8A8A8A; margin-bottom: 30px;'>{ex.Message}</p>
                    <p><a href='https://www.gentlebook.app/' class='button'>Zurück zur Website</a></p>
                </div>
                <div class='footer'><p style='margin: 0;'>Elisabethenstrasse 41, 4051 Basel, Schweiz</p></div>
            </div>
        </body>
        </html>", "text/html");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling booking with token: {Token}", token);

            return Content($@"
        <!DOCTYPE html>
        <html>
        <head>
            <title>Fehler - GentleBook</title>
            <meta charset='utf-8'>
            <style>
                body {{ font-family: 'Helvetica', 'Arial', sans-serif; text-align: center; padding: 0; margin: 0; background-color: #F5EDEB; color: #1E1E1E; }}
                .container {{ max-width: 600px; margin: 40px auto; background-color: #FFFFFF; border-radius: 24px; overflow: hidden; }}
                .header {{ background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; padding: 40px 20px; }}
                .content {{ padding: 40px; }}
                .button {{ display: inline-block; background: linear-gradient(135deg, #1a1a1a 0%, #2d2824 50%, #1a1a1a 100%); border-bottom: 3px solid #C09995; color: #FFFFFF; text-decoration: none; padding: 14px 32px; border-radius: 40px; font-weight: 600; font-size: 16px; }}
                .footer {{ background-color: #F5EDEB; padding: 24px; color: #8A8A8A; font-size: 14px; }}
            </style>
        </head>
        <body>
            <div class='container'>
                <div class='header'><h1 style='color: #FFFFFF; font-size: 28px; font-weight: 600; margin: 0;'>GentleBook</h1></div>
                <div class='content'>
                    <div style='color: #D8B0AC; font-size: 48px; margin-bottom: 20px;'>✧</div>
                    <h2 style='font-size: 24px; font-weight: 700; color: #1E1E1E; margin-bottom: 20px;'>Unerwarteter Fehler</h2>
                    <p style='color: #8A8A8A; margin-bottom: 30px;'>Ein unerwarteter Fehler ist aufgetreten. Bitte versuchen Sie es später erneut oder kontaktieren Sie uns.</p>
                    <p><a href='https://www.gentlebook.app/' class='button'>Zurück zur Website</a></p>
                </div>
                <div class='footer'><p style='margin: 0;'>Elisabethenstrasse 41, 4051 Basel, Schweiz</p></div>
            </div>
        </body>
        </html>", "text/html");
        }
    }
}
