// Controllers/AdminVoucherController.cs
// Gutscheine/10er-Karten (Agency). TenantAdmin/SuperAdmin only — NOT LocationAdmin, since
// vouchers are tenant-wide credit, not location-bound (same scope decision as the plan's other
// ~10 TenantAdmin-only controllers).
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/admin/vouchers")]
[Authorize]
public class AdminVoucherController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly VoucherService _voucherService;
    private readonly EmailService _emailService;
    private readonly ILogger<AdminVoucherController> _logger;

    public AdminVoucherController(GentleBookDbContext db, ITenantContext tenantContext, VoucherService voucherService, EmailService emailService, ILogger<AdminVoucherController> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _voucherService = voucherService;
        _emailService = emailService;
        _logger = logger;
    }

    private ObjectResult? RequireTenantAdmin()
    {
        var role = JwtService.GetRole(User);
        if (role != "TenantAdmin" && role != "SuperAdmin")
            return StatusCode(403, new { message = "Nur Administratoren dürfen Gutscheine verwalten." });
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
                message = $"Gutscheine sind dem {requiredPlanName}-Plan vorbehalten.",
                feature = "vouchers",
                upgrade = true,
                currentPlan = PlanLimits.Get(currentPlan).DisplayName,
                requiredPlan = requiredPlanName,
            });
        }
        return null;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? search)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        var query = _db.Vouchers.Include(v => v.Customer).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(v =>
                v.Code.ToLower().Contains(term) ||
                (v.Customer != null && (v.Customer.FirstName + " " + v.Customer.LastName).ToLower().Contains(term)));
        }

        var vouchers = await query
            .OrderByDescending(v => v.IssuedAt)
            .Select(v => new
            {
                v.Id, v.Code, type = v.Type.ToString(), status = v.Status.ToString(),
                v.InitialAmount, v.RemainingAmount, v.InitialSessions, v.RemainingSessions,
                v.ExpiresAt, v.IssuedAt, v.Note,
                customerName = v.Customer != null ? v.Customer.FirstName + " " + v.Customer.LastName : null,
            })
            .ToListAsync();

        return Ok(vouchers);
    }

    [HttpPost]
    public async Task<IActionResult> Issue([FromBody] IssueVoucherRequest dto)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        if (!Enum.TryParse<VoucherType>(dto.Type, out var type))
            return BadRequest(new { message = "Ungültiger Gutschein-Typ." });

        var tenantId = _tenantContext.TenantId!.Value;
        var platformUserId = JwtService.GetUserId(User) ?? Guid.Empty;

        try
        {
            var voucher = await _voucherService.IssueAsync(tenantId, type, dto.CustomerId, dto.Amount, dto.Sessions, dto.ExpiresAt, dto.Note, platformUserId);

            if (dto.CustomerId.HasValue)
            {
                var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == dto.CustomerId.Value);
                if (customer != null && !string.IsNullOrWhiteSpace(customer.Email))
                {
                    var settings = await _db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId);
                    await _emailService.SendVoucherIssuedEmailAsync(
                        tenantId, customer.Email, customer.FullName, voucher.Code, type, voucher.RemainingAmount, voucher.RemainingSessions,
                        settings?.CompanyName ?? "GentleBook", settings?.LogoUrl, settings?.PrimaryColor ?? "#8B7BC7");
                }
            }

            return Ok(new
            {
                voucher.Id, voucher.Code, type = voucher.Type.ToString(), status = voucher.Status.ToString(),
                voucher.InitialAmount, voucher.RemainingAmount, voucher.InitialSessions, voucher.RemainingSessions,
                voucher.ExpiresAt, voucher.IssuedAt, voucher.Note,
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        try
        {
            await _voucherService.CancelAsync(_tenantContext.TenantId!.Value, id);
            return Ok(new { message = "Gutschein storniert." });
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}

public record IssueVoucherRequest(string Type, Guid? CustomerId, decimal? Amount, int? Sessions, DateTime? ExpiresAt, string? Note);
