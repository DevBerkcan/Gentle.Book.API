// Controllers/TenantController.cs
// TenantAdmin self-service endpoints: settings + subscription info
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/tenant")]
[Authorize]
public class TenantController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;

    public TenantController(GentleBookDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    private IActionResult? RequireTenantAdmin()
    {
        var role = JwtService.GetRole(User);
        if (role != "TenantAdmin")
            return Forbid();
        if (!_tenantContext.TenantId.HasValue)
            return Unauthorized(new { message = "Kein Tenant im Token" });
        return null;
    }

    // GET /api/tenant/settings
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var settings = await _db.TenantSettings
            .FirstOrDefaultAsync(s => s.TenantId == _tenantContext.TenantId!.Value);

        if (settings == null)
            return Ok(new { data = (object?)null, message = "Keine Einstellungen vorhanden" });

        return Ok(new
        {
            data = new
            {
                settings.CompanyName,
                settings.Tagline,
                settings.LogoUrl,
                settings.PrimaryColor,
                settings.SecondaryColor,
                settings.AccentColor,
                settings.Phone,
                settings.Email,
                settings.Website,
                settings.Address,
                settings.BookingIntervalMinutes,
                settings.MaxAdvanceBookingDays,
                settings.TimeZone,
                settings.DefaultCurrency,
                settings.WelcomeMessage,
                settings.CancellationPolicy,
            }
        });
    }

    // PUT /api/tenant/settings
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateTenantSettingsRequest dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var tenantId = _tenantContext.TenantId!.Value;

        var settings = await _db.TenantSettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);

        if (settings == null)
        {
            settings = new TenantSettings
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CreatedAt = DateTime.UtcNow,
            };
            _db.TenantSettings.Add(settings);
        }

        if (!string.IsNullOrWhiteSpace(dto.CompanyName))
            settings.CompanyName = dto.CompanyName.Trim();
        settings.Tagline = dto.Tagline?.Trim() ?? settings.Tagline;
        settings.Phone = dto.Phone?.Trim() ?? settings.Phone;
        settings.Email = dto.Email?.Trim() ?? settings.Email;
        settings.Website = dto.Website?.Trim() ?? settings.Website;
        settings.Address = dto.Address?.Trim() ?? settings.Address;
        settings.WelcomeMessage = dto.WelcomeMessage?.Trim() ?? settings.WelcomeMessage;
        settings.CancellationPolicy = dto.CancellationPolicy?.Trim() ?? settings.CancellationPolicy;

        if (!string.IsNullOrWhiteSpace(dto.PrimaryColor))
            settings.PrimaryColor = dto.PrimaryColor.Trim();
        if (!string.IsNullOrWhiteSpace(dto.SecondaryColor))
            settings.SecondaryColor = dto.SecondaryColor.Trim();
        if (!string.IsNullOrWhiteSpace(dto.AccentColor))
            settings.AccentColor = dto.AccentColor.Trim();

        if (dto.BookingIntervalMinutes.HasValue && dto.BookingIntervalMinutes.Value >= 15)
            settings.BookingIntervalMinutes = dto.BookingIntervalMinutes.Value;
        if (dto.MaxAdvanceBookingDays.HasValue && dto.MaxAdvanceBookingDays.Value >= 1)
            settings.MaxAdvanceBookingDays = dto.MaxAdvanceBookingDays.Value;
        if (!string.IsNullOrWhiteSpace(dto.TimeZone))
            settings.TimeZone = dto.TimeZone.Trim();
        if (!string.IsNullOrWhiteSpace(dto.DefaultCurrency))
            settings.DefaultCurrency = dto.DefaultCurrency.Trim().ToUpper();

        settings.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new { message = "Einstellungen gespeichert" });
    }

    // GET /api/tenant/subscription
    [HttpGet("subscription")]
    public async Task<IActionResult> GetSubscription()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var sub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.TenantId == _tenantContext.TenantId!.Value);

        if (sub == null)
            return NotFound(new { message = "Kein Abonnement gefunden" });

        return Ok(new
        {
            data = new
            {
                sub.Plan,
                sub.Status,
                sub.TrialStartedAt,
                sub.TrialEndsAt,
                sub.TrialDaysRemaining,
                sub.IsInTrial,
                sub.IsAccessAllowed,
                sub.CurrentPeriodStart,
                sub.CurrentPeriodEnd,
                sub.CancelledAt,
            }
        });
    }
}

public record UpdateTenantSettingsRequest(
    string? CompanyName,
    string? Tagline,
    string? Phone,
    string? Email,
    string? Website,
    string? Address,
    string? PrimaryColor,
    string? SecondaryColor,
    string? AccentColor,
    string? WelcomeMessage,
    string? CancellationPolicy,
    int? BookingIntervalMinutes,
    int? MaxAdvanceBookingDays,
    string? TimeZone,
    string? DefaultCurrency
);
