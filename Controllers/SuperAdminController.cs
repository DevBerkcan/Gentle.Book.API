// Controllers/SuperAdminController.cs
// Full tenant management for the Super Admin.
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/superadmin")]
[Authorize]
public class SuperAdminController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly IConfiguration _config;

    public SuperAdminController(GentleBookDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private IActionResult ForbidIfNotSuperAdmin()
    {
        if (!JwtService.IsSuperAdmin(User))
            return Forbid();
        return null!;
    }

    // ── Tenants ───────────────────────────────────────────────────────────

    /// <summary>List all tenants with subscription status.</summary>
    [HttpGet("tenants")]
    public async Task<IActionResult> GetTenants([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var query = _db.Tenants
            .Include(t => t.Subscription)
            .Include(t => t.Settings)
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt);

        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.Slug,
                t.IndustryType,
                t.IsActive,
                t.CreatedAt,
                CompanyName = t.Settings != null ? t.Settings.CompanyName : t.Name,
                LogoUrl = t.Settings != null ? t.Settings.LogoUrl : null,
                PrimaryColor = t.Settings != null ? t.Settings.PrimaryColor : null,
                EmployeeCount = _db.Employees.IgnoreQueryFilters().Count(e => e.TenantId == t.Id),
                BookingCount = _db.Bookings.IgnoreQueryFilters().Count(b => b.TenantId == t.Id),
                Subscription = t.Subscription == null ? null : new
                {
                    t.Subscription.Plan,
                    t.Subscription.Status,
                    t.Subscription.TrialEndsAt,
                    t.Subscription.TrialDaysRemaining,
                    t.Subscription.IsInTrial,
                    t.Subscription.IsAccessAllowed,
                }
            })
            .ToListAsync();

        return Ok(new { items, totalCount = total, page, pageSize });
    }

    /// <summary>Get single tenant details.</summary>
    [HttpGet("tenants/{id:guid}")]
    public async Task<IActionResult> GetTenant(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var tenant = await _db.Tenants
            .Include(t => t.Settings)
            .Include(t => t.Subscription)
            .Include(t => t.Users)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null) return NotFound();
        return Ok(tenant);
    }

    /// <summary>Create a new tenant (triggers 14-day trial automatically).</summary>
    [HttpPost("tenants")]
    public async Task<IActionResult> CreateTenant([FromBody] CreateTenantDto dto)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var slug = dto.Slug.ToLowerInvariant().Trim();
        if (await _db.Tenants.AnyAsync(t => t.Slug == slug))
            return Conflict(new { message = $"Slug '{slug}' is already taken." });

        var trialDays = int.TryParse(_config["Platform:DefaultTrialDays"], out var d) ? d : 14;

        var tenant = new Tenant
        {
            Name = dto.Name,
            Slug = slug,
            IndustryType = dto.IndustryType,
        };

        var settings = new TenantSettings
        {
            TenantId = tenant.Id,
            CompanyName = dto.Name,
            DefaultCurrency = dto.Currency ?? "EUR",
            TimeZone = dto.TimeZone ?? "Europe/Berlin",
        };

        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            TrialStartedAt = DateTime.UtcNow,
            TrialEndsAt = DateTime.UtcNow.AddDays(trialDays),
            Plan = SubscriptionPlan.Trial,
            Status = SubscriptionStatus.Trial,
        };

        _db.Tenants.Add(tenant);
        _db.TenantSettings.Add(settings);
        _db.Subscriptions.Add(subscription);

        // Optionally create the first TenantAdmin user
        if (!string.IsNullOrWhiteSpace(dto.AdminEmail) && !string.IsNullOrWhiteSpace(dto.AdminPassword))
        {
            var adminUser = new PlatformUser
            {
                TenantId = tenant.Id,
                Email = dto.AdminEmail.ToLowerInvariant(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.AdminPassword, workFactor: 12),
                FirstName = dto.AdminFirstName ?? "Admin",
                LastName = dto.AdminLastName ?? tenant.Name,
                Role = PlatformRole.TenantAdmin,
            };
            _db.PlatformUsers.Add(adminUser);
        }

        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetTenant), new { id = tenant.Id }, new
        {
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            TrialEndsAt = subscription.TrialEndsAt,
        });
    }

    /// <summary>Update tenant metadata.</summary>
    [HttpPut("tenants/{id:guid}")]
    public async Task<IActionResult> UpdateTenant(Guid id, [FromBody] UpdateTenantDto dto)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(dto.Name)) tenant.Name = dto.Name;
        if (dto.IndustryType.HasValue) tenant.IndustryType = dto.IndustryType.Value;
        tenant.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Activate a tenant.</summary>
    [HttpPatch("tenants/{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;
        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();
        tenant.IsActive = true;
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Deactivate a tenant (blocks all access).</summary>
    [HttpPatch("tenants/{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;
        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();
        tenant.IsActive = false;
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Delete a tenant and all its data.</summary>
    [HttpDelete("tenants/{id:guid}")]
    public async Task<IActionResult> DeleteTenant(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        _db.Tenants.Remove(tenant);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── Settings / Branding ───────────────────────────────────────────────

    /// <summary>Update branding and settings for a tenant.</summary>
    [HttpPut("tenants/{id:guid}/settings")]
    public async Task<IActionResult> UpdateSettings(Guid id, [FromBody] UpdateTenantSettingsDto dto)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == id);
        if (settings == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(dto.CompanyName)) settings.CompanyName = dto.CompanyName;
        if (!string.IsNullOrWhiteSpace(dto.PrimaryColor)) settings.PrimaryColor = dto.PrimaryColor;
        if (!string.IsNullOrWhiteSpace(dto.SecondaryColor)) settings.SecondaryColor = dto.SecondaryColor;
        if (!string.IsNullOrWhiteSpace(dto.AccentColor)) settings.AccentColor = dto.AccentColor;
        if (dto.Tagline != null) settings.Tagline = dto.Tagline;
        if (dto.Phone != null) settings.Phone = dto.Phone;
        if (dto.Email != null) settings.Email = dto.Email;
        if (dto.Website != null) settings.Website = dto.Website;
        if (dto.Address != null) settings.Address = dto.Address;
        if (dto.WelcomeMessage != null) settings.WelcomeMessage = dto.WelcomeMessage;
        if (dto.CancellationPolicy != null) settings.CancellationPolicy = dto.CancellationPolicy;
        if (dto.DefaultCurrency != null) settings.DefaultCurrency = dto.DefaultCurrency;
        if (dto.TimeZone != null) settings.TimeZone = dto.TimeZone;
        if (dto.BookingIntervalMinutes.HasValue) settings.BookingIntervalMinutes = dto.BookingIntervalMinutes.Value;
        if (dto.MaxAdvanceBookingDays.HasValue) settings.MaxAdvanceBookingDays = dto.MaxAdvanceBookingDays.Value;

        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── Users ─────────────────────────────────────────────────────────────

    /// <summary>Add a TenantAdmin user to a tenant.</summary>
    [HttpPost("tenants/{id:guid}/users")]
    public async Task<IActionResult> CreateTenantUser(Guid id, [FromBody] CreateTenantUserDto dto)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var tenantExists = await _db.Tenants.AnyAsync(t => t.Id == id);
        if (!tenantExists) return NotFound();

        if (await _db.PlatformUsers.AnyAsync(u => u.Email == dto.Email.ToLowerInvariant()))
            return Conflict(new { message = "Email already in use." });

        var user = new PlatformUser
        {
            TenantId = id,
            Email = dto.Email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password, workFactor: 12),
            FirstName = dto.FirstName,
            LastName = dto.LastName,
            Role = PlatformRole.TenantAdmin,
        };

        _db.PlatformUsers.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new { user.Id, user.Email, user.FirstName, user.LastName });
    }

    // ── Trial / Subscription ─────────────────────────────────────────────

    /// <summary>Extend trial by N days.</summary>
    [HttpPost("tenants/{id:guid}/trial/extend")]
    public async Task<IActionResult> ExtendTrial(Guid id, [FromBody] ExtendTrialDto dto)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == id);
        if (subscription == null) return NotFound();

        subscription.TrialEndsAt = subscription.TrialEndsAt.AddDays(dto.Days);
        if (subscription.Status == SubscriptionStatus.Expired)
            subscription.Status = SubscriptionStatus.Trial;

        subscription.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { subscription.TrialEndsAt, subscription.TrialDaysRemaining });
    }

    /// <summary>Platform-wide stats for SuperAdmin dashboard.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var stats = new
        {
            TotalTenants = await _db.Tenants.CountAsync(),
            ActiveTenants = await _db.Tenants.CountAsync(t => t.IsActive),
            TrialTenants = await _db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Trial),
            ActiveSubscriptions = await _db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Active),
            ExpiredTenants = await _db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Expired),
            TotalBookings = await _db.Bookings.IgnoreQueryFilters().CountAsync(),
        };

        return Ok(stats);
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public record CreateTenantDto(
    string Name,
    string Slug,
    IndustryType IndustryType,
    string? Currency,
    string? TimeZone,
    string? AdminEmail,
    string? AdminPassword,
    string? AdminFirstName,
    string? AdminLastName
);

public record UpdateTenantDto(
    string? Name,
    IndustryType? IndustryType
);

public record UpdateTenantSettingsDto(
    string? CompanyName,
    string? PrimaryColor,
    string? SecondaryColor,
    string? AccentColor,
    string? Tagline,
    string? Phone,
    string? Email,
    string? Website,
    string? Address,
    string? WelcomeMessage,
    string? CancellationPolicy,
    string? DefaultCurrency,
    string? TimeZone,
    int? BookingIntervalMinutes,
    int? MaxAdvanceBookingDays
);

public record CreateTenantUserDto(string Email, string Password, string FirstName, string LastName);
public record ExtendTrialDto(int Days);
