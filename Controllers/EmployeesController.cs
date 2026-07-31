using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Middleware;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/employees")]
[Authorize]
public class EmployeesController : ControllerBase
{
    private readonly EmployeeService _employeeService;
    private readonly GentleBookDbContext _db;
    private readonly IConfiguration _config;
    private readonly AuditService _audit;

    public EmployeesController(EmployeeService employeeService, GentleBookDbContext db, IConfiguration config, AuditService audit)
    {
        _employeeService = employeeService;
        _db = db;
        _config = config;
        _audit = audit;
    }

    // ── Helper to get current employee ────────────────────────────
    private Guid? GetCurrentEmployeeId() => JwtService.GetEmployeeId(User);

    // Mutating employee management is TenantAdmin-only.
    private IActionResult? RequireTenantAdmin()
    {
        var role = JwtService.GetRole(User);
        if (role != "TenantAdmin" && role != "SuperAdmin")
            return StatusCode(403, new { message = "Nur Administratoren dürfen Mitarbeiter verwalten." });
        return null;
    }

    // ── GET /api/employees ────────────────────────────────────────
    // Updated to support service filtering
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(
        [FromQuery] bool activeOnly = true,
        [FromQuery] Guid? serviceId = null,
        [FromQuery] string? tenantSlug = null)
    {
        var employees = await _employeeService.GetAllAsync(activeOnly, serviceId, tenantSlug);
        return Ok(employees);
    }

    // ── NEW: GET /api/employees/by-service/{serviceId} ─────────────
    // Get employees that can perform a specific service
    [HttpGet("by-service/{serviceId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEmployeesByService(
        Guid serviceId,
        [FromQuery] bool activeOnly = true,
        [FromQuery] string? tenantSlug = null)
    {
        var employees = await _employeeService.GetEmployeesByServiceAsync(serviceId, activeOnly, tenantSlug);
        return Ok(employees);
    }

    // ── NEW: GET /api/employees/with-services ──────────────────────
    // Get employees with their assigned services (for admin panel)
    [HttpGet("with-services")]
    [Authorize] // Requires authentication
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetEmployeesWithServices(
        [FromQuery] bool activeOnly = true)
    {
        var employees = await _employeeService.GetEmployeesWithServicesAsync(activeOnly);
        return Ok(employees);
    }

    // ── GET /api/employees/{id} ───────────────────────────────────
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(Guid id)
    {
        var employee = await _employeeService.GetByIdAsync(id);
        if (employee == null) return NotFound();
        return Ok(employee);
    }

    // ── GET /api/employees/{id}/stats ─────────────────────────────
    [HttpGet("{id:guid}/stats")]
    public async Task<IActionResult> GetStats(Guid id,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        var currentUserId = GetCurrentEmployeeId();
        var role = JwtService.GetRole(User);
        var isAdmin = role == "TenantAdmin" || role == "SuperAdmin";
        if (!isAdmin && currentUserId != id)
            return StatusCode(403, new { message = "Sie dürfen nur Ihre eigenen Statistiken einsehen." });

        var stats = await _employeeService.GetStatsAsync(id, from, to);

        if (stats == null) return NotFound();
        return Ok(stats);
    }

    // ── POST /api/employees ───────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateEmployeeRequest request)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;

        // Plan-Limit prüfen
        var tenantContext = HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        var tenantId = tenantContext.TenantId;
        if (tenantId.HasValue)
        {
            var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId.Value);
            var limits = PlanLimits.Get(sub?.Plan ?? SubscriptionPlan.Trial);
            var activeCount = await _db.Employees.CountAsync(e => e.TenantId == tenantId.Value && e.IsActive);
            if (activeCount >= limits.MaxEmployees)
                return StatusCode(402, new
                {
                    message = $"Ihr Plan erlaubt maximal {limits.MaxEmployees} Mitarbeiter. Bitte upgraden Sie Ihren Plan.",
                    limit = limits.MaxEmployees,
                    current = activeCount,
                    upgrade = true
                });
        }

        var (success, employee, errorMessage) = await _employeeService.CreateAsync(request);

        if (!success)
            return BadRequest(new { message = errorMessage });

        await _audit.LogAsync("employee.created", "Employee", ((dynamic)employee!).Id.ToString(),
            $"Mitarbeiter \"{request.Name}\" angelegt");

        return CreatedAtAction(nameof(GetById), new { id = ((dynamic)employee).Id }, employee);
    }

    // ── PUT /api/employees/{id} ───────────────────────────────────
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateEmployeeRequest request)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;

        var (success, employee, errorMessage) = await _employeeService.UpdateAsync(id, request);

        if (!success)
        {
            if (errorMessage == "Mitarbeiter nicht gefunden")
                return NotFound();
            return Conflict(new { message = errorMessage });
        }

        return Ok(employee);
    }

    // ── PATCH /api/employees/{id}/toggle-active ───────────────────
    [HttpPatch("{id:guid}/toggle-active")]
    public async Task<IActionResult> ToggleActive(Guid id)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;

        var (success, result, errorMessage, warning) = await _employeeService.ToggleActiveAsync(id);

        if (!success)
            return NotFound(new { message = errorMessage });

        if (warning != null)
            return Ok(new { result, warning });

        return Ok(result);
    }

    // ── DELETE /api/employees/{id} ────────────────────────────────
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;

        var (success, errorMessage) = await _employeeService.DeleteAsync(id);

        if (!success)
        {
            if (errorMessage == "Mitarbeiter nicht gefunden")
                return NotFound();
            return Conflict(new { message = errorMessage });
        }

        await _audit.LogAsync("employee.deleted", "Employee", id.ToString(), "Mitarbeiter gelöscht");

        return NoContent();
    }

    // ── GET /api/employees/{id}/schedule ─────────────────────────
    // TenantAdmin sieht alle; ein Mitarbeiter darf nur den eigenen Plan lesen.
    [HttpGet("{id:guid}/schedule")]
    public async Task<IActionResult> GetSchedule(Guid id)
    {
        var role = JwtService.GetRole(User);
        if (role != "TenantAdmin" && role != "SuperAdmin" && GetCurrentEmployeeId() != id)
            return StatusCode(403, new { message = "Kein Zugriff auf diesen Arbeitszeitplan." });

        var tenantId = HttpContext.RequestServices.GetRequiredService<ITenantContext>().TenantId;
        if (!tenantId.HasValue) return Unauthorized(new { message = "TenantId fehlt" });

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId.Value);
        if (employee == null) return NotFound();

        var schedules = await _db.EmployeeSchedules
            .Where(s => s.EmployeeId == id && s.TenantId == tenantId.Value)
            .OrderBy(s => s.DayOfWeek)
            .ToListAsync();

        var result = schedules.Select(s => new
        {
            s.Id,
            DayOfWeek = (int)s.DayOfWeek,
            s.DayName,
            s.IsWorkingDay,
            StartTime = s.StartTime.ToString("HH:mm"),
            EndTime = s.EndTime.ToString("HH:mm"),
        });

        return Ok(new { data = result });
    }

    // ── PUT /api/employees/{id}/schedule ─────────────────────────
    [HttpPut("{id:guid}/schedule")]
    public async Task<IActionResult> UpdateSchedule(Guid id, [FromBody] List<UpdateEmployeeScheduleItemDto> dto)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;

        var tenantId = HttpContext.RequestServices.GetRequiredService<ITenantContext>().TenantId;
        if (!tenantId.HasValue) return Unauthorized(new { message = "TenantId fehlt" });

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId.Value);
        if (employee == null) return NotFound();

        foreach (var item in dto)
        {
            if (item.DayOfWeek < 0 || item.DayOfWeek > 6)
                return BadRequest(new { message = "Ungültiger Wochentag" });

            if (!TimeOnly.TryParse(item.StartTime ?? "09:00", out var startTime))
                return BadRequest(new { message = "Ungültige Startzeit" });

            if (!TimeOnly.TryParse(item.EndTime ?? "18:00", out var endTime))
                return BadRequest(new { message = "Ungültige Endzeit" });

            if (item.IsWorkingDay && endTime <= startTime)
                return BadRequest(new { message = "Endzeit muss nach der Startzeit liegen" });

            var existing = await _db.EmployeeSchedules
                .FirstOrDefaultAsync(s => s.EmployeeId == id && s.TenantId == tenantId.Value && s.DayOfWeek == (DayOfWeek)item.DayOfWeek);

            if (existing == null)
            {
                existing = new EmployeeSchedule
                {
                    TenantId = employee.TenantId,
                    EmployeeId = id,
                    DayOfWeek = (DayOfWeek)item.DayOfWeek
                };
                _db.EmployeeSchedules.Add(existing);
            }

            existing.IsWorkingDay = item.IsWorkingDay;
            existing.StartTime = startTime;
            existing.EndTime = endTime;
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Arbeitszeiten gespeichert" });
    }
}

public record UpdateEmployeeScheduleItemDto(int DayOfWeek, bool IsWorkingDay, string? StartTime, string? EndTime);
