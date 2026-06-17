// Controllers/PublicController.cs
// Public endpoints for the booking widget (no auth required).
using GentleBook.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/booking")]
[AllowAnonymous]
public class PublicController : ControllerBase
{
    private readonly GentleBookDbContext _db;

    public PublicController(GentleBookDbContext db)
    {
        _db = db;
    }

    /// <summary>Verifies a customer's email via double opt-in token.</summary>
    [HttpGet("/api/public/verify-email")]
    public async Task<IActionResult> VerifyEmail([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new { message = "Token fehlt." });

        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.EmailVerificationToken == token
                                   && c.EmailVerificationTokenExpiry > DateTime.UtcNow);

        if (customer == null)
            return BadRequest(new { message = "Link ungültig oder abgelaufen." });

        customer.IsEmailVerified = true;
        customer.ConsentGivenAt = DateTime.UtcNow;
        customer.EmailVerificationToken = null;
        customer.EmailVerificationTokenExpiry = null;
        await _db.SaveChangesAsync();

        return Ok(new { message = "E-Mail erfolgreich bestätigt. Vielen Dank!" });
    }

    /// <summary>Diagnostics — no DB required.</summary>
    [HttpGet("ping")]
    public IActionResult Ping() => Ok(new { ok = true, time = DateTime.UtcNow });

    /// <summary>Returns public branding info for the booking widget.</summary>
    [HttpGet("{slug}/info")]
    public async Task<IActionResult> GetTenantInfo(string slug)
    {
        try
        {
            var tenant = await _db.Tenants
                .Include(t => t.Settings)
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive);

            if (tenant == null) return NotFound();

            return Ok(new
            {
                tenant.Name,
                CompanyName = tenant.Settings?.CompanyName ?? tenant.Name,
                tenant.Slug,
                PrimaryColor = tenant.Settings?.PrimaryColor,
                Tagline = tenant.Settings?.Tagline,
                WelcomeMessage = tenant.Settings?.WelcomeMessage,
                IndustryType = tenant.IndustryType.ToString(),
                LogoUrl = tenant.Settings?.LogoUrl,
                LinktreeStyle = tenant.Settings?.LinktreeStyle ?? "gradient",
                LinktreeConfig = tenant.Settings?.LinktreeConfig,
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message, inner = ex.InnerException?.Message });
        }
    }

    /// <summary>Returns active profile links for the Linktree page.</summary>
    [HttpGet("{slug}/links")]
    public async Task<IActionResult> GetTenantLinks(string slug)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive);

        if (tenant == null) return NotFound();

        var links = await _db.TenantLinks
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenant.Id && l.IsActive)
            .OrderBy(l => l.DisplayOrder)
            .Select(l => new { l.Id, l.Title, l.Url, l.IconType, l.DisplayOrder })
            .ToListAsync();

        return Ok(links);
    }
}
