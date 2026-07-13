using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CustomersController : ControllerBase
{
    private readonly CustomerService _customerService;
    private readonly ILogger<CustomersController> _logger;

    public CustomersController(
        CustomerService customerService,
        ILogger<CustomersController> logger)
    {
        _customerService = customerService;
        _logger = logger;
    }

    // ── Helpers ───────────────────────────────────────────────────
    private Guid? GetCurrentEmployeeId() => JwtService.GetEmployeeId(User);

    private bool IsTenantAdmin() => JwtService.GetRole(User) == "TenantAdmin";

    private bool IsSuperAdmin() => JwtService.IsSuperAdmin(User);

    private bool IsAdminRequest() => IsTenantAdmin() || IsSuperAdmin();

    /// <summary>
    /// Get all customers. TenantAdmin sees all tenant customers; employees see their own.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponseDto<CustomerListItemDto>>> GetCustomers(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool all = false)
    {
        // TenantAdmin and SuperAdmin see all customers; employees see only their own
        Guid? filterEmployeeId = (IsTenantAdmin() || IsSuperAdmin()) ? null : GetCurrentEmployeeId();

        var result = await _customerService.GetCustomersAsync(filterEmployeeId, search, page, pageSize);
        return Ok(result);
    }

    /// <summary>
    /// Get customer by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerDetailDto>> GetCustomer(Guid id)
    {
        var employeeId = GetCurrentEmployeeId();
        var isAdmin = IsAdminRequest();

        var customer = await _customerService.GetCustomerByIdAsync(id, employeeId, isAdmin);

        if (customer == null)
            return NotFound(new { message = "Kunde nicht gefunden" });

        return Ok(customer);
    }

    /// <summary>
    /// Create a new customer
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerResponseDto>> CreateCustomer([FromBody] CreateCustomerRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.FirstName) || string.IsNullOrWhiteSpace(dto.LastName))
            return BadRequest(new { message = "Vor- und Nachname sind erforderlich" });

        // TenantAdmins don't have an EmployeeId — pass null so the FK isn't violated
        var role = JwtService.GetRole(User);
        var employeeId = (role == "TenantAdmin") ? (Guid?)null : GetCurrentEmployeeId();

        if (employeeId == null && role != "TenantAdmin")
            return Unauthorized(new { message = "Nicht angemeldet" });

        try
        {
            var customer = await _customerService.CreateCustomerAsync(dto, employeeId);

            return CreatedAtAction(
                nameof(GetCustomer),
                new { id = customer.Id },
                customer
            );
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (DbUpdateException ex) when (
            ex.InnerException?.Message.Contains("IX_Customers_Email", StringComparison.OrdinalIgnoreCase) == true ||
            ex.InnerException?.Message.Contains("IX_Customers_TenantId_Email", StringComparison.OrdinalIgnoreCase) == true ||
            ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true ||
            ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogWarning(ex, "CreateCustomer duplicate constraint failed");
            return Conflict(new { message = "Ein Kunde mit dieser E-Mail existiert bereits oder die Datenbank nutzt noch einen alten Kunden-E-Mail-Index." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateCustomer failed");
            return StatusCode(500, new { message = "Interner Fehler beim Anlegen des Kunden." });
        }
    }

    /// <summary>
    /// Update a customer
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerResponseDto>> UpdateCustomer(
        Guid id,
        [FromBody] UpdateCustomerRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.FirstName) || string.IsNullOrWhiteSpace(dto.LastName))
            return BadRequest(new { message = "Vor- und Nachname sind erforderlich" });

        var employeeId = GetCurrentEmployeeId();
        var isAdmin = IsAdminRequest();

        try
        {
            var customer = await _customerService.UpdateCustomerAsync(id, dto, employeeId, isAdmin);
            return Ok(customer);
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Delete a customer (only if they have no bookings)
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteCustomer(Guid id)
    {
        var employeeId = GetCurrentEmployeeId();
        var isAdmin = IsAdminRequest();

        try
        {
            await _customerService.DeleteCustomerAsync(id, employeeId, isAdmin);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Search customers (for dropdown/autocomplete)
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<CustomerListItemDto>>> SearchCustomers(
        [FromQuery] string q,
        [FromQuery] int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(new List<CustomerListItemDto>());

        // TenantAdmin searches all customers in the tenant; employees search their own
        Guid? employeeId = IsTenantAdmin() ? null : GetCurrentEmployeeId();

        var results = await _customerService.SearchCustomersAsync(employeeId, q, limit);
        return Ok(results);
    }
}
