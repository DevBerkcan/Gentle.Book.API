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
[Route("api/admin/services")]
[Authorize] 
public class AdminServicesController : ControllerBase
{
    private readonly ServiceService _serviceService;
    private readonly GentleBookDbContext _db;
    private readonly ILogger<AdminServicesController> _logger;

    public AdminServicesController(ServiceService serviceService, GentleBookDbContext db, ILogger<AdminServicesController> logger)
    {
        _serviceService = serviceService;
        _db = db;
        _logger = logger;
    }

    // ── Helper to get current employee from JWT ──────────────────
    private Guid? GetCurrentEmployeeId() => JwtService.GetEmployeeId(User);

    // ── SERVICES ─────────────────────────────────────────────────

    /// <summary>
    /// Get all services (including inactive) for admin management
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AdminServiceDto>>> GetAllServices()
    {
        var employeeId = GetCurrentEmployeeId();
        _logger.LogInformation("Employee {EmployeeId} is fetching all services", employeeId);

        var services = await _serviceService.GetAllServicesAdminAsync();
        return Ok(services);
    }

    /// <summary>
    /// Get service by ID for editing
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminServiceDto>> GetService(Guid id)
    {
        var employeeId = GetCurrentEmployeeId();
        _logger.LogInformation("Employee {EmployeeId} is fetching service {ServiceId}", employeeId, id);

        var service = await _serviceService.GetServiceByIdAdminAsync(id);
        if (service == null)
            return NotFound(new { message = "Service not found" });
        return Ok(service);
    }

    /// <summary>
    /// Create a new service
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AdminServiceDto>> CreateService([FromBody] CreateServiceDto dto)
    {
        var employeeId = GetCurrentEmployeeId();

        // Plan-Limit prüfen
        var tenantContext = HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        var tenantId = tenantContext.TenantId;
        if (tenantId.HasValue)
        {
            var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId.Value);
            var limits = PlanLimits.Get(sub?.Plan ?? SubscriptionPlan.Trial);
            var serviceCount = await _db.Services.CountAsync(s => s.TenantId == tenantId.Value && s.IsActive);
            if (serviceCount >= limits.MaxServices)
                return StatusCode(402, new
                {
                    message = $"Ihr Plan erlaubt maximal {limits.MaxServices} Services. Bitte upgraden Sie Ihren Plan.",
                    limit = limits.MaxServices,
                    current = serviceCount,
                    upgrade = true
                });
        }

        try
        {
            var service = await _serviceService.CreateServiceAsync(dto);
            _logger.LogInformation("Employee {EmployeeId} created new service: {ServiceName}", employeeId, service.Name);

            return CreatedAtAction(nameof(GetService), new { id = service.Id }, service);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to create service: {Error}", employeeId, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to create service: {Error}", employeeId, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing service
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminServiceDto>> UpdateService(Guid id, [FromBody] UpdateServiceDto dto)
    {
        var employeeId = GetCurrentEmployeeId();

        try
        {
            var service = await _serviceService.UpdateServiceAsync(id, dto);
            _logger.LogInformation("Employee {EmployeeId} updated service: {ServiceName}", employeeId, service.Name);

            return Ok(service);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to update service {ServiceId}: {Error}", employeeId, id, ex.Message);
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to update service {ServiceId}: {Error}", employeeId, id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Delete a service (hard delete - only if no bookings)
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteService(Guid id)
    {
        var employeeId = GetCurrentEmployeeId();

        try
        {
            var result = await _serviceService.DeleteServiceAsync(id);
            _logger.LogInformation("Employee {EmployeeId} deleted service {ServiceId}", employeeId, id);

            return Ok(new { message = "Service deleted successfully" });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to delete service {ServiceId}: {Error}", employeeId, id, ex.Message);
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to delete service {ServiceId}: {Error}", employeeId, id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Toggle service active status
    /// </summary>
    [HttpPatch("{id}/toggle-active")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleServiceActive(Guid id)
    {
        var employeeId = GetCurrentEmployeeId();

        try
        {
            var result = await _serviceService.ToggleServiceActiveAsync(id);
            _logger.LogInformation("Employee {EmployeeId} toggled service {ServiceId} active status to {IsActive}",
                employeeId, id, result);

            return Ok(new { message = "Service status updated", isActive = result });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to toggle service {ServiceId}: {Error}", employeeId, id, ex.Message);
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to toggle service {ServiceId}: {Error}", employeeId, id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    // ── CATEGORIES ───────────────────────────────────────────────

    /// <summary>
    /// Get all categories (including inactive)
    /// </summary>
    [HttpGet("categories")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AdminServiceCategoryDto>>> GetAllCategories()
    {
        var employeeId = GetCurrentEmployeeId();
        _logger.LogInformation("Employee {EmployeeId} is fetching all categories", employeeId);

        var categories = await _serviceService.GetAllCategoriesAdminAsync();
        return Ok(categories);
    }

    /// <summary>
    /// Get category by ID
    /// </summary>
    [HttpGet("categories/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminServiceCategoryDto>> GetCategory(Guid id)
    {
        var employeeId = GetCurrentEmployeeId();
        _logger.LogInformation("Employee {EmployeeId} is fetching category {CategoryId}", employeeId, id);

        var category = await _serviceService.GetCategoryByIdAdminAsync(id);
        if (category == null)
            return NotFound(new { message = "Category not found" });
        return Ok(category);
    }

    /// <summary>
    /// Create a new category
    /// </summary>
    [HttpPost("categories")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AdminServiceCategoryDto>> CreateCategory([FromBody] CreateCategoryDto dto)
    {
        var employeeId = GetCurrentEmployeeId();

        try
        {
            var category = await _serviceService.CreateCategoryAsync(dto);
            _logger.LogInformation("Employee {EmployeeId} created new category: {CategoryName}", employeeId, category.Name);

            return CreatedAtAction(nameof(GetCategory), new { id = category.Id }, category);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to create category: {Error}", employeeId, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Update a category
    /// </summary>
    [HttpPut("categories/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminServiceCategoryDto>> UpdateCategory(Guid id, [FromBody] UpdateCategoryDto dto)
    {
        var employeeId = GetCurrentEmployeeId();

        try
        {
            var category = await _serviceService.UpdateCategoryAsync(id, dto);
            _logger.LogInformation("Employee {EmployeeId} updated category: {CategoryName}", employeeId, category.Name);

            return Ok(category);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to update category {CategoryId}: {Error}", employeeId, id, ex.Message);
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Delete a category (only if no services)
    /// </summary>
    [HttpDelete("categories/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCategory(Guid id)
    {
        var employeeId = GetCurrentEmployeeId();

        try
        {
            var result = await _serviceService.DeleteCategoryAsync(id);
            _logger.LogInformation("Employee {EmployeeId} deleted category {CategoryId}", employeeId, id);

            return Ok(new { message = "Category deleted successfully" });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to delete category {CategoryId}: {Error}", employeeId, id, ex.Message);
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Employee {EmployeeId} failed to delete category {CategoryId}: {Error}", employeeId, id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    // ── EMPLOYEE ASSIGNMENT ─────────────────────────────────────

    /// <summary>
    /// Get all employees for assignment dropdown
    /// </summary>
    [HttpGet("employees")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<EmployeeForAssignmentDto>>> GetEmployeesForAssignment()
    {
        var employeeId = GetCurrentEmployeeId();
        _logger.LogInformation("Employee {EmployeeId} is fetching employees for assignment", employeeId);

        var employees = await _serviceService.GetEmployeesForAssignmentAsync();
        return Ok(employees);
    }

    /// <summary>
    /// Get all services for a specific employee
    /// </summary>
    [HttpGet("employees/{employeeId}/services")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AdminServiceDto>>> GetServicesByEmployee(Guid employeeId)
    {
        var currentEmployeeId = GetCurrentEmployeeId();
        _logger.LogInformation("Employee {CurrentEmployeeId} is fetching services for employee {TargetEmployeeId}",
            currentEmployeeId, employeeId);

        var services = await _serviceService.GetServicesByEmployeeAsync(employeeId);
        return Ok(services);
    }

    /// <summary>
    /// Bulk assign services to an employee
    /// </summary>
    [HttpPost("employees/{targetEmployeeId}/services/bulk")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkAssignServicesToEmployee(Guid targetEmployeeId, [FromBody] List<Guid> serviceIds)
    {
        var currentEmployeeId = GetCurrentEmployeeId();

        try
        {
            var result = await _serviceService.BulkAssignServicesToEmployeeAsync(targetEmployeeId, serviceIds);
            _logger.LogInformation("Employee {CurrentEmployeeId} assigned {Count} services to employee {TargetEmployeeId}",
                currentEmployeeId, serviceIds.Count, targetEmployeeId);

            return Ok(new { message = "Services assigned successfully", count = serviceIds.Count });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Employee {CurrentEmployeeId} failed to assign services: {Error}", currentEmployeeId, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }
}
