using GentleBook.Api.Data;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/employees")]
[Authorize]
public class EmployeesController : ControllerBase
{
    private readonly EmployeeService _employeeService;
    private readonly IConfiguration _config;

    public EmployeesController(EmployeeService employeeService, IConfiguration config)
    {
        _employeeService = employeeService;
        _config = config;
    }

    // ── Helper to get current employee ────────────────────────────
    private Guid? GetCurrentEmployeeId() => JwtService.GetEmployeeId(User);

    // ── GET /api/employees ────────────────────────────────────────
    // Updated to support service filtering
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(
        [FromQuery] bool activeOnly = true,
        [FromQuery] Guid? serviceId = null) // Added serviceId parameter
    {
        var employees = await _employeeService.GetAllAsync(activeOnly, serviceId);
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
        [FromQuery] bool activeOnly = true)
    {
        var employees = await _employeeService.GetEmployeesByServiceAsync(serviceId, activeOnly);
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
        var stats = await _employeeService.GetStatsAsync(id, from, to);

        if (stats == null) return NotFound();
        return Ok(stats);
    }

    // ── POST /api/employees ───────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateEmployeeRequest request)
    {
        var (success, employee, errorMessage) = await _employeeService.CreateAsync(request);

        if (!success)
            return BadRequest(new { message = errorMessage });

        return CreatedAtAction(nameof(GetById), new { id = ((dynamic)employee).Id }, employee);
    }

    // ── PUT /api/employees/{id} ───────────────────────────────────
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateEmployeeRequest request)
    {
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
        var (success, result, errorMessage) = await _employeeService.ToggleActiveAsync(id);

        if (!success)
            return NotFound(new { message = errorMessage });

        return Ok(result);
    }

    // ── DELETE /api/employees/{id} ────────────────────────────────
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var (success, errorMessage) = await _employeeService.DeleteAsync(id);

        if (!success)
        {
            if (errorMessage == "Mitarbeiter nicht gefunden")
                return NotFound();
            return Conflict(new { message = errorMessage });
        }

        return NoContent();
    }
}