// Controllers/AdminIntakeFormController.cs
// One shared intake/consultation form per tenant. TenantAdmin and LocationAdmin edit the same
// fields together (same role set as AdminServicesController); response *viewing* is
// location-scoped for LocationAdmin via the linked Booking, mirroring BookingsController.
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/admin/intake-form")]
[Authorize]
public class AdminIntakeFormController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<AdminIntakeFormController> _logger;

    public AdminIntakeFormController(GentleBookDbContext db, ITenantContext tenantContext, ILogger<AdminIntakeFormController> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    private ObjectResult? RequireTenantAdmin()
    {
        var role = JwtService.GetRole(User);
        if (role != "TenantAdmin" && role != "SuperAdmin" && role != "LocationAdmin")
            return StatusCode(403, new { message = "Nur Administratoren dürfen das Anamneseformular verwalten." });
        return null;
    }

    private async Task<IActionResult?> RequireAgencyPlanAsync()
    {
        if (_tenantContext.TenantId is not { } tenantId) return Unauthorized(new { message = "Kein Tenant im Token" });

        var currentPlan = await _db.Subscriptions
            .Where(s => s.TenantId == tenantId)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync() ?? SubscriptionPlan.Trial;

        var requiredPlanName = AgencyFeatureGate.ValidateForPlan(currentPlan);
        if (requiredPlanName != null)
        {
            return StatusCode(402, new
            {
                message = $"Anamneseformulare sind dem {requiredPlanName}-Plan vorbehalten.",
                feature = "intake_form",
                upgrade = true,
                currentPlan = PlanLimits.Get(currentPlan).DisplayName,
                requiredPlan = requiredPlanName,
            });
        }
        return null;
    }

    // ── Fields (the form) ────────────────────────────────────────

    [HttpGet("fields")]
    public async Task<IActionResult> GetFields()
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        var fields = await _db.IntakeFormFields
            .OrderBy(f => f.DisplayOrder)
            .Select(f => new
            {
                f.Id, f.Label, fieldType = f.FieldType.ToString(), f.OptionsJson,
                f.IsRequired, f.IsActive, f.DisplayOrder,
            })
            .ToListAsync();

        return Ok(fields);
    }

    [HttpPost("fields")]
    public async Task<IActionResult> CreateField([FromBody] IntakeFormFieldRequest dto)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        if (string.IsNullOrWhiteSpace(dto.Label))
            return BadRequest(new { message = "Ein Label ist erforderlich." });
        if (!Enum.TryParse<IntakeFormFieldType>(dto.FieldType, out var fieldType))
            return BadRequest(new { message = "Ungültiger Feldtyp." });

        var tenantId = _tenantContext.TenantId!.Value;
        var maxOrder = await _db.IntakeFormFields.Where(f => f.TenantId == tenantId).AnyAsync()
            ? await _db.IntakeFormFields.Where(f => f.TenantId == tenantId).MaxAsync(f => f.DisplayOrder)
            : -1;

        var field = new IntakeFormField
        {
            TenantId = tenantId,
            Label = dto.Label.Trim(),
            FieldType = fieldType,
            OptionsJson = fieldType == IntakeFormFieldType.MultipleChoice ? dto.OptionsJson : null,
            IsRequired = dto.IsRequired,
            DisplayOrder = maxOrder + 1,
        };
        _db.IntakeFormFields.Add(field);
        await _db.SaveChangesAsync();

        return Ok(new { field.Id, field.Label, fieldType = field.FieldType.ToString(), field.OptionsJson, field.IsRequired, field.IsActive, field.DisplayOrder });
    }

    [HttpPut("fields/{id:guid}")]
    public async Task<IActionResult> UpdateField(Guid id, [FromBody] IntakeFormFieldRequest dto)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        var field = await _db.IntakeFormFields.FirstOrDefaultAsync(f => f.Id == id);
        if (field == null) return NotFound();

        if (string.IsNullOrWhiteSpace(dto.Label))
            return BadRequest(new { message = "Ein Label ist erforderlich." });
        if (!Enum.TryParse<IntakeFormFieldType>(dto.FieldType, out var fieldType))
            return BadRequest(new { message = "Ungültiger Feldtyp." });

        field.Label = dto.Label.Trim();
        field.FieldType = fieldType;
        field.OptionsJson = fieldType == IntakeFormFieldType.MultipleChoice ? dto.OptionsJson : null;
        field.IsRequired = dto.IsRequired;
        if (dto.IsActive.HasValue) field.IsActive = dto.IsActive.Value;
        field.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { field.Id, field.Label, fieldType = field.FieldType.ToString(), field.OptionsJson, field.IsRequired, field.IsActive, field.DisplayOrder });
    }

    [HttpDelete("fields/{id:guid}")]
    public async Task<IActionResult> DeleteField(Guid id)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        var field = await _db.IntakeFormFields.FirstOrDefaultAsync(f => f.Id == id);
        if (field == null) return NotFound();

        _db.IntakeFormFields.Remove(field);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("fields/reorder")]
    public async Task<IActionResult> ReorderFields([FromBody] Guid[] orderedIds)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        var tenantId = _tenantContext.TenantId!.Value;
        var fields = await _db.IntakeFormFields.Where(f => f.TenantId == tenantId).ToListAsync();

        for (int i = 0; i < orderedIds.Length; i++)
        {
            var field = fields.FirstOrDefault(f => f.Id == orderedIds[i]);
            if (field != null)
            {
                field.DisplayOrder = i;
                field.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── Responses ─────────────────────────────────────────────────

    /// <summary>Answers for one booking's response, if any — for the booking detail view. LocationAdmin only sees it if the booking belongs to their location.</summary>
    [HttpGet("responses/by-booking/{bookingId:guid}")]
    public async Task<IActionResult> GetResponseForBooking(Guid bookingId)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        var scope = LocationScopeAuthorization.GetAccessScope(User);

        var response = await _db.IntakeFormResponses
            .Include(r => r.Booking)
            .Include(r => r.Answers).ThenInclude(a => a.Field)
            .FirstOrDefaultAsync(r => r.BookingId == bookingId);

        if (response == null) return Ok(new { hasResponse = false });
        if (!scope.IsFullTenantAccess && response.Booking.LocationId != scope.LocationId)
            return Ok(new { hasResponse = false });

        return Ok(new
        {
            hasResponse = true,
            submittedAt = response.SubmittedAt,
            answers = response.Answers
                .OrderBy(a => a.Field.DisplayOrder)
                .Select(a => new { label = a.Field.Label, value = a.Value }),
        });
    }
}

public record IntakeFormFieldRequest(string Label, string FieldType, string? OptionsJson, bool IsRequired, bool? IsActive);
