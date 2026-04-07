using GentleBook.Api.Data;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Skinbloom.Api.Services;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ServicesController : ControllerBase
{
    private readonly ServiceService _serviceService;
    private readonly ILogger<ServicesController> _logger;

    public ServicesController(ServiceService serviceService, ILogger<ServicesController> logger)
    {
        _serviceService = serviceService;
        _logger = logger;
    }

    // ── Helpers ───────────────────────────────────────────────────
    private Guid? GetCurrentEmployeeId() => JwtService.GetEmployeeId(User);

    /// <summary>
    /// Get all active services
    /// If employee is authenticated, returns their specific services
    /// Otherwise returns general services
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ServiceDto>>> GetServices()
    {
        var employeeId = GetCurrentEmployeeId();
        var services = await _serviceService.GetServicesAsync(employeeId);
        return Ok(services);
    }

    /// <summary>
    /// Get all active service categories with their services
    /// If employee is authenticated, returns categories with their specific services
    /// Otherwise returns general categories
    /// </summary>
    [HttpGet("categories")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ServiceCategoryDto>>> GetServiceCategories()
    {
        var employeeId = GetCurrentEmployeeId();
        var categories = await _serviceService.GetServiceCategoriesAsync(employeeId);
        return Ok(categories);
    }

    /// <summary>
    /// Get services by category ID
    /// If employee is authenticated, returns their specific services in that category
    /// Otherwise returns general services in that category
    /// </summary>
    [HttpGet("categories/{categoryId}/services")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ServiceDto>>> GetServicesByCategory(Guid categoryId)
    {
        var employeeId = GetCurrentEmployeeId();
        var (services, categoryExists) = await _serviceService.GetServicesByCategoryAsync(categoryId, employeeId);

        if (!categoryExists)
        {
            return NotFound(new { message = "Category not found" });
        }

        return Ok(services);
    }

    /// <summary>
    /// Get a specific service by ID with category information
    /// Respects employee-specific service assignments
    /// </summary>
    [HttpGet("{id}/details")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ServiceWithCategoryDto>> GetServiceDetails(Guid id)
    {
        var employeeId = GetCurrentEmployeeId();
        var service = await _serviceService.GetServiceDetailsAsync(id, employeeId);

        if (service == null)
        {
            return NotFound(new { message = "Service not found" });
        }

        return Ok(service);
    }

    /// <summary>
    /// Get services summary with category counts
    /// If employee is authenticated, returns summary for their services
    /// Otherwise returns general summary
    /// </summary>
    [HttpGet("summary")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetServicesSummary()
    {
        var employeeId = GetCurrentEmployeeId();
        var summary = await _serviceService.GetServicesSummaryAsync(employeeId);
        return Ok(summary);
    }

    /// <summary>
    /// Get all categories (including inactive ones) - Admin only
    /// </summary>
    [HttpGet("admin/categories")]
    [Authorize]  // Requires authentication
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<ServiceCategoryDto>>> GetAllCategories()
    {
        // TODO: Add admin authorization check
        var categories = await _serviceService.GetAllCategoriesAsync();
        return Ok(categories);
    }

    /// <summary>
    /// Get all services for the currently authenticated employee
    /// </summary>
    [HttpGet("my-services")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<ServiceDto>>> GetMyServices()
    {
        var employeeId = GetCurrentEmployeeId();
        if (!employeeId.HasValue)
        {
            return Unauthorized(new { message = "Employee not authenticated" });
        }

        var services = await _serviceService.GetEmployeeServicesAsync(employeeId.Value);
        return Ok(services);
    }

    /// <summary>
    /// Get all services for a specific employee (Admin only)
    /// </summary>
    [HttpGet("employees/{employeeId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ServiceDto>>> GetEmployeeServices(Guid employeeId)
    {
        var services = await _serviceService.GetEmployeeServicesAsync(employeeId);
        return Ok(services);
    }

    /// <summary>
    /// Assign a service to an employee (Admin only)
    /// </summary>
    [HttpPost("assign")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignServiceToEmployee([FromBody] AssignServiceToEmployeeDto dto)
    {
        try
        {
            var result = await _serviceService.AssignServiceToEmployeeAsync(dto.ServiceId, dto.EmployeeId);
            return Ok(new { message = "Service assigned to employee successfully" });
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Remove a service from an employee (Admin only)
    /// </summary>
    [HttpDelete("assign/{serviceId}/employee/{employeeId}")] // Fixed route
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveServiceFromEmployee(Guid serviceId, Guid employeeId)
    {
        try
        {
            var result = await _serviceService.RemoveServiceFromEmployeeAsync(serviceId, employeeId);
            return Ok(new { message = "Service removed from employee successfully" });
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}