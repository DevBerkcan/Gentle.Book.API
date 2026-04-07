using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CustomersController : ControllerBase
{
    private readonly CustomerService _customerService;
    private readonly ILogger<CustomersController> _logger;
    private readonly IConfiguration _config;

    public CustomersController(
        CustomerService customerService,
        ILogger<CustomersController> logger,
        IConfiguration config)
    {
        _customerService = customerService;
        _logger = logger;
        _config = config;
    }

    // ── Helpers ───────────────────────────────────────────────────
    private Guid? GetCurrentEmployeeId() => JwtService.GetEmployeeId(User);

    private bool IsAdminRequest()
    {
        var secret = _config["AdminBootstrapSecret"]
            ?? throw new InvalidOperationException("AdminBootstrapSecret is not configured");
        return Request.Headers.TryGetValue("X-Admin-Secret", out var val) && val == secret;
    }

    /// <summary>
    /// Get all customers for the logged-in employee
    /// Pass ?all=true to see all customers (admin only)
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponseDto<CustomerListItemDto>>> GetCustomers(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool all = false)
    {
        var employeeId = GetCurrentEmployeeId();
        var isAdmin = IsAdminRequest();

        // Only admins can see all customers
        Guid? filterEmployeeId = (isAdmin && all) ? null : employeeId;

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

        var employeeId = GetCurrentEmployeeId();
        if (employeeId == null)
            return Unauthorized(new { message = "Nicht angemeldet" });

        try
        {
            var customer = await _customerService.CreateCustomerAsync(dto, employeeId.Value);

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

        var employeeId = GetCurrentEmployeeId();
        if (employeeId == null)
            return Unauthorized(new { message = "Nicht angemeldet" });

        var results = await _customerService.SearchCustomersAsync(employeeId, q, limit);
        return Ok(results);
    }
}