// Controllers/WaitlistController.cs
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

// ── Public: customer joins waitlist ──────────────────────────────────────────

[ApiController]
[Route("api/public/{slug}/waitlist")]
[AllowAnonymous]
public class PublicWaitlistController : ControllerBase
{
    private readonly GentleBookDbContext _db;

    public PublicWaitlistController(GentleBookDbContext db) => _db = db;

    [HttpPost]
    public async Task<IActionResult> Join(string slug, [FromBody] JoinWaitlistDto dto)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug.ToLowerInvariant() && t.IsActive);

        if (tenant == null) return NotFound(new { message = "Buchungssystem nicht gefunden." });

        if (string.IsNullOrWhiteSpace(dto.FirstName) || string.IsNullOrWhiteSpace(dto.LastName))
            return BadRequest(new { message = "Vor- und Nachname sind erforderlich." });

        if (string.IsNullOrWhiteSpace(dto.Email))
            return BadRequest(new { message = "E-Mail-Adresse ist erforderlich." });

        if (!dto.ServiceId.HasValue || !dto.PreferredDate.HasValue)
            return BadRequest(new { message = "Service und Wunschtag sind erforderlich." });

        if (dto.PreferredDate.Value < DateOnly.FromDateTime(DateTime.UtcNow))
            return BadRequest(new { message = "Der Wunschtag darf nicht in der Vergangenheit liegen." });

        var serviceIsValid = await _db.Services.AnyAsync(s =>
            s.Id == dto.ServiceId.Value && s.TenantId == tenant.Id && s.IsActive);
        if (!serviceIsValid)
            return BadRequest(new { message = "Der gewählte Service ist nicht verfügbar." });

        if (dto.EmployeeId.HasValue)
        {
            var employeeIsValid = await _db.Employees.AnyAsync(e =>
                e.Id == dto.EmployeeId.Value &&
                e.TenantId == tenant.Id &&
                e.IsActive &&
                e.ServiceEmployees.Any(se => se.ServiceId == dto.ServiceId.Value));
            if (!employeeIsValid)
                return BadRequest(new { message = "Der gewählte Mitarbeiter ist für diesen Service nicht verfügbar." });
        }

        TimeOnly? preferredStartTime = null;
        TimeOnly? preferredEndTime = null;
        if (!string.IsNullOrWhiteSpace(dto.PreferredStartTime))
        {
            if (!TimeOnly.TryParse(dto.PreferredStartTime, out var parsedStart))
                return BadRequest(new { message = "Ungültige Wunschzeit." });

            preferredStartTime = parsedStart;
            if (!string.IsNullOrWhiteSpace(dto.PreferredEndTime))
            {
                if (!TimeOnly.TryParse(dto.PreferredEndTime, out var parsedEnd) || parsedEnd <= parsedStart)
                    return BadRequest(new { message = "Ungültiges Ende der Wunschzeit." });
                preferredEndTime = parsedEnd;
            }
        }

        // Prevent duplicate pending entries for the same customer and preference.
        var exists = await _db.WaitlistEntries.AnyAsync(w =>
            w.TenantId == tenant.Id &&
            w.Email.ToLower() == dto.Email.ToLower() &&
            w.ServiceId == dto.ServiceId &&
            w.PreferredDate == dto.PreferredDate &&
            w.EmployeeId == dto.EmployeeId &&
            w.PreferredStartTime == preferredStartTime &&
            w.Status == WaitlistStatus.Pending);

        if (exists)
            return Conflict(new { message = "Sie stehen für diesen Termin bereits auf der Warteliste." });

        var entry = new WaitlistEntry
        {
            TenantId    = tenant.Id,
            ServiceId   = dto.ServiceId,
            EmployeeId  = dto.EmployeeId,
            PreferredDate = dto.PreferredDate,
            PreferredStartTime = preferredStartTime,
            PreferredEndTime = preferredEndTime,
            FirstName   = dto.FirstName.Trim(),
            LastName    = dto.LastName.Trim(),
            Email       = dto.Email.Trim().ToLower(),
            Phone       = dto.Phone?.Trim(),
            Notes       = dto.Notes?.Trim(),
            Status      = WaitlistStatus.Pending,
            CreatedAt   = DateTime.UtcNow,
        };

        _db.WaitlistEntries.Add(entry);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Sie wurden erfolgreich auf die Warteliste gesetzt.", id = entry.Id });
    }
}

public record JoinWaitlistDto(
    string FirstName,
    string LastName,
    string Email,
    string? Phone,
    string? Notes,
    Guid? ServiceId,
    Guid? EmployeeId,
    DateOnly? PreferredDate,
    string? PreferredStartTime,
    string? PreferredEndTime
);

// ── Admin: view & manage waitlist ────────────────────────────────────────────

[ApiController]
[Route("api/waitlist")]
[Authorize]
public class WaitlistController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;

    public WaitlistController(GentleBookDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    private IActionResult? RequireAdmin()
    {
        var role = _tenantContext.Role;
        if (role != "Owner" && role != "Admin" && role != "TenantAdmin")
            return Forbid();
        return null;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] WaitlistStatus? status, [FromQuery] string? date)
    {
        var check = RequireAdmin();
        if (check != null) return check;

        var tenantId = _tenantContext.TenantId!.Value;

        var query = _db.WaitlistEntries
            .Where(w => w.TenantId == tenantId)
            .Include(w => w.Service)
            .Include(w => w.Employee)
            .AsNoTracking();

        if (status.HasValue)
            query = query.Where(w => w.Status == status.Value);

        if (!string.IsNullOrEmpty(date) && DateOnly.TryParse(date, out var d))
            query = query.Where(w => w.PreferredDate == d);

        var entries = await query
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new
            {
                w.Id,
                w.FirstName,
                w.LastName,
                w.Email,
                w.Phone,
                w.Notes,
                w.Status,
                w.CreatedAt,
                w.NotifiedAt,
                w.ReservationExpiresAt,
                PreferredDate  = w.PreferredDate.HasValue ? w.PreferredDate.Value.ToString("yyyy-MM-dd") : null,
                PreferredStartTime = w.PreferredStartTime.HasValue ? w.PreferredStartTime.Value.ToString("HH:mm") : null,
                PreferredEndTime = w.PreferredEndTime.HasValue ? w.PreferredEndTime.Value.ToString("HH:mm") : null,
                ReservedStartTime = w.ReservedStartTime.HasValue ? w.ReservedStartTime.Value.ToString("HH:mm") : null,
                ServiceName    = w.Service != null ? w.Service.Name : null,
                EmployeeName   = w.Employee != null ? w.Employee.Name : null,
            })
            .ToListAsync();

        return Ok(entries);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var check = RequireAdmin();
        if (check != null) return check;

        var tenantId = _tenantContext.TenantId!.Value;
        var entry = await _db.WaitlistEntries.FirstOrDefaultAsync(w => w.Id == id && w.TenantId == tenantId);
        if (entry == null) return NotFound();

        _db.WaitlistEntries.Remove(entry);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Eintrag gelöscht." });
    }

    [HttpPatch("{id:guid}/mark-notified")]
    public async Task<IActionResult> MarkNotified(Guid id)
    {
        var check = RequireAdmin();
        if (check != null) return check;

        var tenantId = _tenantContext.TenantId!.Value;
        var entry = await _db.WaitlistEntries.FirstOrDefaultAsync(w => w.Id == id && w.TenantId == tenantId);
        if (entry == null) return NotFound();

        entry.Status     = WaitlistStatus.Notified;
        entry.NotifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Als benachrichtigt markiert." });
    }
}
