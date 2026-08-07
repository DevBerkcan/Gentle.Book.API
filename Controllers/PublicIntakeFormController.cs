// Controllers/PublicIntakeFormController.cs
// Public, anonymous endpoints for the digital intake/consultation form. Reached either via the
// "for-booking" check (plain bookingId, same trust level as the existing anonymous GET
// /bookings/{id}) or directly via the HMAC-signed token (EmailService.GenerateIntakeFormToken).
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/public/intake-form")]
[AllowAnonymous]
public class PublicIntakeFormController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly EmailService _emailService;
    private readonly ILogger<PublicIntakeFormController> _logger;

    public PublicIntakeFormController(GentleBookDbContext db, EmailService emailService, ILogger<PublicIntakeFormController> logger)
    {
        _db = db;
        _emailService = emailService;
        _logger = logger;
    }

    /// <summary>
    /// Checked from the booking-confirmation page: is there an intake form to fill out for this
    /// booking? Only true for Agency tenants with ≥1 active field and no existing response yet.
    /// </summary>
    [HttpGet("for-booking/{bookingId:guid}")]
    public async Task<IActionResult> CheckForBooking(Guid bookingId)
    {
        var booking = await _db.Bookings.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking == null) return Ok(new { available = false });

        var plan = await _db.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.TenantId == booking.TenantId)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync() ?? SubscriptionPlan.Trial;
        if (AgencyFeatureGate.ValidateForPlan(plan) != null) return Ok(new { available = false });

        var hasActiveFields = await _db.IntakeFormFields.IgnoreQueryFilters()
            .AnyAsync(f => f.TenantId == booking.TenantId && f.IsActive);
        if (!hasActiveFields) return Ok(new { available = false });

        var alreadySubmitted = await _db.IntakeFormResponses.IgnoreQueryFilters()
            .AnyAsync(r => r.BookingId == bookingId);
        if (alreadySubmitted) return Ok(new { available = false });

        return Ok(new { available = true, token = _emailService.GenerateIntakeFormToken(bookingId) });
    }

    /// <summary>Loads the active fields (the form) for the booking behind the token.</summary>
    [HttpGet("{token}")]
    public async Task<IActionResult> GetForm(string token)
    {
        Guid bookingId;
        try
        {
            var (id, action) = _emailService.DecodeToken(token);
            if (id == Guid.Empty || action != "intake") return BadRequest(new { message = "Ungültiger Link." });
            bookingId = id;
        }
        catch
        {
            return BadRequest(new { message = "Ungültiger Link." });
        }

        var booking = await _db.Bookings.IgnoreQueryFilters()
            .Include(b => b.Tenant)
            .FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking == null) return NotFound(new { message = "Buchung nicht gefunden." });

        var alreadySubmitted = await _db.IntakeFormResponses.IgnoreQueryFilters()
            .AnyAsync(r => r.BookingId == bookingId);

        var fields = await _db.IntakeFormFields.IgnoreQueryFilters()
            .Where(f => f.TenantId == booking.TenantId && f.IsActive)
            .OrderBy(f => f.DisplayOrder)
            .Select(f => new { f.Id, f.Label, fieldType = f.FieldType.ToString(), f.OptionsJson, f.IsRequired })
            .ToListAsync();

        return Ok(new { alreadySubmitted, tenantName = booking.Tenant.Name, fields });
    }

    /// <summary>Submits answers. One response per booking — a second submit is rejected.</summary>
    [HttpPost("{token}")]
    public async Task<IActionResult> Submit(string token, [FromBody] SubmitIntakeFormRequest request)
    {
        Guid bookingId;
        try
        {
            var (id, action) = _emailService.DecodeToken(token);
            if (id == Guid.Empty || action != "intake") return BadRequest(new { message = "Ungültiger Link." });
            bookingId = id;
        }
        catch
        {
            return BadRequest(new { message = "Ungültiger Link." });
        }

        var booking = await _db.Bookings.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking == null) return NotFound(new { message = "Buchung nicht gefunden." });

        var alreadySubmitted = await _db.IntakeFormResponses.IgnoreQueryFilters().AnyAsync(r => r.BookingId == bookingId);
        if (alreadySubmitted) return BadRequest(new { message = "Für diese Buchung wurde bereits ein Formular ausgefüllt." });

        var activeFields = await _db.IntakeFormFields.IgnoreQueryFilters()
            .Where(f => f.TenantId == booking.TenantId && f.IsActive)
            .ToListAsync();

        var answersByFieldId = request.Answers.ToDictionary(a => a.FieldId, a => a.Value);

        foreach (var field in activeFields.Where(f => f.IsRequired))
        {
            if (!answersByFieldId.TryGetValue(field.Id, out var value) || string.IsNullOrWhiteSpace(value))
                return BadRequest(new { message = $"Pflichtfeld „{field.Label}“ fehlt." });
        }

        var response = new IntakeFormResponse
        {
            TenantId = booking.TenantId,
            BookingId = booking.Id,
        };
        _db.IntakeFormResponses.Add(response);

        foreach (var field in activeFields)
        {
            if (!answersByFieldId.TryGetValue(field.Id, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            _db.IntakeFormAnswers.Add(new IntakeFormAnswer
            {
                TenantId = booking.TenantId,
                ResponseId = response.Id,
                FieldId = field.Id,
                Value = value.Trim(),
            });
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Intake form submitted for booking {BookingId}", bookingId);
        return Ok(new { message = "Vielen Dank! Ihr Formular wurde übermittelt." });
    }
}

public record SubmitIntakeFormAnswer(Guid FieldId, string Value);
public record SubmitIntakeFormRequest(List<SubmitIntakeFormAnswer> Answers);
