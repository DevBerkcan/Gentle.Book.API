// Controllers/AdminIntakeFormController.cs
// "Formulare": one combined form per tenant, fields optionally scoped to a service category and
// grouped by FormType (Anamnese/Einverständnis/Fragebogen/Nachsorge) for display purposes only —
// still a single form/token per booking, see Data/Entities/IntakeFormEntities.cs. TenantAdmin and
// LocationAdmin edit the same fields together (same role set as AdminServicesController);
// response *viewing* is location-scoped for LocationAdmin via the linked Booking.
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
            return StatusCode(403, new { message = "Nur Administratoren dürfen Formulare verwalten." });
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
                message = $"Formulare sind dem {requiredPlanName}-Plan vorbehalten.",
                feature = "intake_form",
                upgrade = true,
                currentPlan = PlanLimits.Get(currentPlan).DisplayName,
                requiredPlan = requiredPlanName,
            });
        }
        return null;
    }

    /// <summary>Loads the tenant's IndustryType and checks it against IntakeFormIndustryGate. Also returns the industry so the frontend can decide whether to show the nav entry at all.</summary>
    private async Task<(IActionResult? deny, IndustryType industry)> RequireAllowedIndustryAsync()
    {
        var tenantId = _tenantContext.TenantId!.Value;
        var industry = await _db.Tenants.Where(t => t.Id == tenantId).Select(t => t.IndustryType).FirstOrDefaultAsync();

        var message = IntakeFormIndustryGate.ValidateForIndustry(industry);
        if (message != null)
            return (StatusCode(403, new { message, feature = "intake_form_industry" }), industry);
        return (null, industry);
    }

    // ── Fields (the form) ────────────────────────────────────────

    [HttpGet("fields")]
    public async Task<IActionResult> GetFields()
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;
        var (industryDeny, _) = await RequireAllowedIndustryAsync();
        if (industryDeny != null) return industryDeny;

        var fields = await _db.IntakeFormFields
            .OrderBy(f => f.DisplayOrder)
            .Select(f => new
            {
                f.Id, f.Label, fieldType = f.FieldType.ToString(), formType = f.FormType.ToString(), f.OptionsJson,
                f.CategoryId, categoryName = f.Category != null ? f.Category.Name : null,
                f.ConditionalOnFieldId, f.ConditionalOnValue,
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
        var (industryDeny, _) = await RequireAllowedIndustryAsync();
        if (industryDeny != null) return industryDeny;

        if (string.IsNullOrWhiteSpace(dto.Label))
            return BadRequest(new { message = "Ein Label ist erforderlich." });
        if (!Enum.TryParse<IntakeFormFieldType>(dto.FieldType, out var fieldType))
            return BadRequest(new { message = "Ungültiger Feldtyp." });
        if (!Enum.TryParse<IntakeFormType>(dto.FormType ?? "Anamnese", out var formType))
            return BadRequest(new { message = "Ungültiger Formular-Typ." });

        var tenantId = _tenantContext.TenantId!.Value;
        var maxOrder = await _db.IntakeFormFields.Where(f => f.TenantId == tenantId).AnyAsync()
            ? await _db.IntakeFormFields.Where(f => f.TenantId == tenantId).MaxAsync(f => f.DisplayOrder)
            : -1;

        var field = new IntakeFormField
        {
            TenantId = tenantId,
            Label = dto.Label.Trim(),
            FieldType = fieldType,
            FormType = formType,
            OptionsJson = fieldType is IntakeFormFieldType.MultipleChoice or IntakeFormFieldType.Checkboxes ? dto.OptionsJson : null,
            CategoryId = dto.CategoryId,
            ConditionalOnFieldId = dto.ConditionalOnFieldId,
            ConditionalOnValue = dto.ConditionalOnFieldId.HasValue ? dto.ConditionalOnValue : null,
            IsRequired = dto.IsRequired,
            DisplayOrder = maxOrder + 1,
        };
        _db.IntakeFormFields.Add(field);
        await _db.SaveChangesAsync();

        return Ok(ToFieldDto(field));
    }

    [HttpPut("fields/{id:guid}")]
    public async Task<IActionResult> UpdateField(Guid id, [FromBody] IntakeFormFieldRequest dto)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;
        var (industryDeny, _) = await RequireAllowedIndustryAsync();
        if (industryDeny != null) return industryDeny;

        var field = await _db.IntakeFormFields.FirstOrDefaultAsync(f => f.Id == id);
        if (field == null) return NotFound();

        if (string.IsNullOrWhiteSpace(dto.Label))
            return BadRequest(new { message = "Ein Label ist erforderlich." });
        if (!Enum.TryParse<IntakeFormFieldType>(dto.FieldType, out var fieldType))
            return BadRequest(new { message = "Ungültiger Feldtyp." });
        if (!Enum.TryParse<IntakeFormType>(dto.FormType ?? "Anamnese", out var formType))
            return BadRequest(new { message = "Ungültiger Formular-Typ." });

        field.Label = dto.Label.Trim();
        field.FieldType = fieldType;
        field.FormType = formType;
        field.OptionsJson = fieldType is IntakeFormFieldType.MultipleChoice or IntakeFormFieldType.Checkboxes ? dto.OptionsJson : null;
        field.CategoryId = dto.CategoryId;
        field.ConditionalOnFieldId = dto.ConditionalOnFieldId;
        field.ConditionalOnValue = dto.ConditionalOnFieldId.HasValue ? dto.ConditionalOnValue : null;
        field.IsRequired = dto.IsRequired;
        if (dto.IsActive.HasValue) field.IsActive = dto.IsActive.Value;
        field.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(ToFieldDto(field));
    }

    [HttpDelete("fields/{id:guid}")]
    public async Task<IActionResult> DeleteField(Guid id)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;
        var (industryDeny, _) = await RequireAllowedIndustryAsync();
        if (industryDeny != null) return industryDeny;

        var field = await _db.IntakeFormFields.FirstOrDefaultAsync(f => f.Id == id);
        if (field == null) return NotFound();

        // Clear other fields' ConditionalOnFieldId if they pointed at this one — otherwise the
        // Restrict FK would reject the delete.
        var dependents = await _db.IntakeFormFields.Where(f => f.ConditionalOnFieldId == id).ToListAsync();
        foreach (var dependent in dependents)
        {
            dependent.ConditionalOnFieldId = null;
            dependent.ConditionalOnValue = null;
        }

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
        var (industryDeny, _) = await RequireAllowedIndustryAsync();
        if (industryDeny != null) return industryDeny;

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

    // ── Templates ─────────────────────────────────────────────────

    /// <summary>Curated starter field-sets for the tenant's industry (+ the industry-agnostic fallback).</summary>
    [HttpGet("fields/templates")]
    public async Task<IActionResult> GetTemplates()
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;
        var (industryDeny, industry) = await RequireAllowedIndustryAsync();
        if (industryDeny != null) return industryDeny;

        var templates = IntakeFormTemplates.ForIndustry(industry)
            .Select(t => new { t.Key, t.Label, fieldCount = t.Fields.Length });
        return Ok(templates);
    }

    /// <summary>Copies a template's fields into real, editable IntakeFormField rows for the tenant.</summary>
    [HttpPost("fields/templates/{key}/apply")]
    public async Task<IActionResult> ApplyTemplate(string key, [FromQuery] Guid? categoryId)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;
        var (industryDeny, _) = await RequireAllowedIndustryAsync();
        if (industryDeny != null) return industryDeny;

        var template = IntakeFormTemplates.Find(key);
        if (template == null) return NotFound(new { message = "Vorlage nicht gefunden." });

        var tenantId = _tenantContext.TenantId!.Value;

        if (categoryId.HasValue)
        {
            var categoryExists = await _db.ServiceCategories.AnyAsync(c => c.Id == categoryId.Value && c.TenantId == tenantId);
            if (!categoryExists) return BadRequest(new { message = "Kategorie nicht gefunden." });
        }

        var maxOrder = await _db.IntakeFormFields.Where(f => f.TenantId == tenantId).AnyAsync()
            ? await _db.IntakeFormFields.Where(f => f.TenantId == tenantId).MaxAsync(f => f.DisplayOrder)
            : -1;

        var created = new List<IntakeFormField>();
        foreach (var templateField in template.Fields)
        {
            maxOrder++;
            var field = new IntakeFormField
            {
                TenantId = tenantId,
                Label = templateField.Label,
                FieldType = templateField.Type,
                OptionsJson = templateField.Options != null ? System.Text.Json.JsonSerializer.Serialize(templateField.Options) : null,
                CategoryId = categoryId,
                IsRequired = templateField.Required,
                DisplayOrder = maxOrder,
            };
            _db.IntakeFormFields.Add(field);
            created.Add(field);
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Applied intake form template {Key} for tenant {TenantId}: {Count} fields", key, tenantId, created.Count);
        return Ok(created.Select(ToFieldDto));
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

    private static object ToFieldDto(IntakeFormField field) => new
    {
        field.Id, field.Label, fieldType = field.FieldType.ToString(), formType = field.FormType.ToString(),
        field.OptionsJson, field.CategoryId, field.ConditionalOnFieldId, field.ConditionalOnValue,
        field.IsRequired, field.IsActive, field.DisplayOrder,
    };
}

public record IntakeFormFieldRequest(
    string Label,
    string FieldType,
    string? FormType,
    string? OptionsJson,
    Guid? CategoryId,
    Guid? ConditionalOnFieldId,
    string? ConditionalOnValue,
    bool IsRequired,
    bool? IsActive);
