using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
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
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly LoyaltyService _loyaltyService;
    private readonly ILogger<CustomersController> _logger;

    public CustomersController(
        CustomerService customerService,
        GentleBookDbContext db,
        ITenantContext tenantContext,
        LoyaltyService loyaltyService,
        ILogger<CustomersController> logger)
    {
        _customerService = customerService;
        _db = db;
        _tenantContext = tenantContext;
        _loyaltyService = loyaltyService;
        _logger = logger;
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
                message = $"Treuepunkte sind dem {requiredPlanName}-Plan vorbehalten.",
                feature = "loyalty_points",
                upgrade = true,
                currentPlan = PlanLimits.Get(currentPlan).DisplayName,
                requiredPlan = requiredPlanName,
            });
        }
        return null;
    }

    /// <summary>Point balance + ledger history for a customer (Agency).</summary>
    [HttpGet("{id:guid}/loyalty")]
    public async Task<IActionResult> GetLoyalty(Guid id)
    {
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null) return NotFound(new { message = "Kunde nicht gefunden" });

        var history = await _db.LoyaltyPointsTransactions
            .Where(l => l.CustomerId == id)
            .OrderByDescending(l => l.CreatedAt)
            .Take(50)
            .Select(l => new { l.Id, l.Points, l.Reason, l.CreatedAt })
            .ToListAsync();

        return Ok(new { points = customer.LoyaltyPoints, history });
    }

    /// <summary>Manual staff point adjustment, e.g. in-person redemption (Agency, TenantAdmin/SuperAdmin only).</summary>
    [HttpPost("{id:guid}/loyalty/adjust")]
    public async Task<IActionResult> AdjustLoyalty(Guid id, [FromBody] AdjustLoyaltyRequestDto dto)
    {
        if (!IsAdminRequest()) return Forbid();
        if (await RequireAgencyPlanAsync() is { } deny) return deny;
        if (dto.Points == 0) return BadRequest(new { message = "Die Punktzahl darf nicht 0 sein." });

        try
        {
            var newBalance = await _loyaltyService.AdjustPointsAsync(
                _tenantContext.TenantId!.Value, id, dto.Points, string.IsNullOrWhiteSpace(dto.Reason) ? "manual_redemption" : dto.Reason);
            return Ok(new { points = newBalance });
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

public record AdjustLoyaltyRequestDto(int Points, string? Reason);
