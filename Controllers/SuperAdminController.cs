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
    private readonly EmailService _emailService;
    private readonly ILogger<SuperAdminController> _logger;

    public SuperAdminController(GentleBookDbContext db, IConfiguration config, EmailService emailService, ILogger<SuperAdminController> logger)
    {
        _db = db;
        _config = config;
        _emailService = emailService;
        _logger = logger;
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
        string? plainPassword = null;
        string? adminFirstName = null;
        if (!string.IsNullOrWhiteSpace(dto.AdminEmail))
        {
            var emailLower = dto.AdminEmail.ToLowerInvariant();
            var passwordWasGenerated = string.IsNullOrWhiteSpace(dto.AdminPassword);
            plainPassword = passwordWasGenerated ? GeneratePassword() : dto.AdminPassword!;
            adminFirstName = dto.AdminFirstName ?? "Admin";
            var adminUser = new PlatformUser
            {
                TenantId = tenant.Id,
                Email = emailLower,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(plainPassword, workFactor: 12),
                FirstName = adminFirstName,
                LastName = dto.AdminLastName ?? tenant.Name,
                Role = PlatformRole.TenantAdmin,
                MustChangePassword = passwordWasGenerated,
            };
            _db.PlatformUsers.Add(adminUser);
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveChanges failed in CreateTenant");
            return StatusCode(500, new { message = "DB-Fehler beim Speichern.", detail = ex.Message, inner = ex.InnerException?.Message });
        }

        // Send welcome email with credentials after save
        if (!string.IsNullOrWhiteSpace(dto.AdminEmail) && dto.SendWelcomeEmail != false && plainPassword != null)
        {
            _ = _emailService.SendWelcomeEmailAsync(dto.AdminEmail.ToLowerInvariant(), adminFirstName!, slug, plainPassword);
        }

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
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteTenant failed for {TenantId}", id);
            return StatusCode(500, new { message = "Löschen fehlgeschlagen.", detail = ex.Message, inner = ex.InnerException?.Message });
        }
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

    // ── Logo Upload ───────────────────────────────────────────────────────

    /// <summary>Upload logo for a tenant (SuperAdmin).</summary>
    [HttpPost("tenants/{id:guid}/logo")]
    public async Task<IActionResult> UploadLogo(Guid id, IFormFile logo)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var tenant = await _db.Tenants.Include(t => t.Settings).FirstOrDefaultAsync(t => t.Id == id);
        if (tenant == null) return NotFound();

        if (logo == null || logo.Length == 0)
            return BadRequest(new { message = "Keine Datei hochgeladen." });

        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "image/gif" };
        if (!allowedTypes.Contains(logo.ContentType.ToLower()))
            return BadRequest(new { message = "Nur JPG, PNG, WebP und GIF sind erlaubt." });

        if (logo.Length > 5 * 1024 * 1024)
            return BadRequest(new { message = "Die Datei darf maximal 5 MB groß sein." });

        var ext = Path.GetExtension(logo.FileName).ToLower();
        var fileName = $"{id}{ext}";
        var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "logos");
        if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

        var filePath = Path.Combine(uploadDir, fileName);
        using (var stream = new FileStream(filePath, FileMode.Create))
            await logo.CopyToAsync(stream);

        var logoUrl = $"/uploads/logos/{fileName}";

        var settings = tenant.Settings;
        if (settings == null)
        {
            settings = new TenantSettings { TenantId = id, CreatedAt = DateTime.UtcNow };
            _db.TenantSettings.Add(settings);
        }
        settings.LogoUrl = logoUrl;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { logoUrl });
    }

    // ── Users ─────────────────────────────────────────────────────────────

    /// <summary>Add a TenantAdmin user to a tenant. Auto-generates password if none provided, sends welcome email.</summary>
    [HttpPost("tenants/{id:guid}/users")]
    public async Task<IActionResult> CreateTenantUser(Guid id, [FromBody] CreateTenantUserDto dto)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
        if (tenant == null) return NotFound();

        if (await _db.PlatformUsers.AnyAsync(u => u.Email == dto.Email.ToLowerInvariant()))
            return Conflict(new { message = "Email already in use." });

        // Auto-generate password if not provided
        var plainPassword = !string.IsNullOrWhiteSpace(dto.Password)
            ? dto.Password
            : GeneratePassword();

        var user = new PlatformUser
        {
            TenantId = id,
            Email = dto.Email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(plainPassword, workFactor: 12),
            FirstName = dto.FirstName,
            LastName = dto.LastName,
            Role = PlatformRole.TenantAdmin,
        };

        _db.PlatformUsers.Add(user);
        await _db.SaveChangesAsync();

        // Send welcome email with credentials (fire-and-forget; failure is logged, not thrown)
        if (dto.SendWelcomeEmail != false)
        {
            _ = _emailService.SendWelcomeEmailAsync(user.Email, user.FirstName, tenant.Slug, plainPassword);
        }

        return Ok(new { user.Id, user.Email, user.FirstName, user.LastName, passwordGenerated = string.IsNullOrWhiteSpace(dto.Password) });
    }

    private static string GeneratePassword()
    {
        const string chars = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var rng = new Random();
        return new string(Enumerable.Range(0, 10).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
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

    /// <summary>Platform-wide overview data: monthly charts, email health, top tenants.</summary>
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview()
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var now = DateTime.UtcNow;
        var sixMonthsAgo = now.AddMonths(-5).Date;

        // Monthly bookings across all tenants (last 6 months)
        var bookingsByMonth = await _db.Bookings
            .IgnoreQueryFilters()
            .Where(b => b.CreatedAt >= sixMonthsAgo)
            .GroupBy(b => new { b.CreatedAt.Year, b.CreatedAt.Month })
            .Select(g => new {
                g.Key.Year, g.Key.Month,
                Total     = g.Count(),
                Confirmed = g.Count(b => b.Status == BookingStatus.Confirmed),
                Cancelled = g.Count(b => b.Status == BookingStatus.Cancelled),
                Completed = g.Count(b => b.Status == BookingStatus.Completed),
            })
            .ToListAsync();

        // Monthly new tenants (last 6 months)
        var tenantsByMonth = await _db.Tenants
            .Where(t => t.CreatedAt >= sixMonthsAgo)
            .GroupBy(t => new { t.CreatedAt.Year, t.CreatedAt.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, NewTenants = g.Count() })
            .ToListAsync();

        // Email stats (all-time)
        var emailStats = await _db.EmailLogs
            .IgnoreQueryFilters()
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
            .ToListAsync();

        // Top 5 tenants by booking count
        var topTenantGroups = await _db.Bookings
            .IgnoreQueryFilters()
            .GroupBy(b => b.TenantId)
            .Select(g => new { TenantId = g.Key, BookingCount = g.Count() })
            .OrderByDescending(x => x.BookingCount)
            .Take(5)
            .ToListAsync();

        var topTenantIds = topTenantGroups.Select(t => t.TenantId).ToList();
        var topTenantDetails = await _db.Tenants
            .Include(t => t.Settings)
            .Where(t => topTenantIds.Contains(t.Id))
            .ToListAsync();

        // Fill all 6 months (including empty ones)
        var culture = new System.Globalization.CultureInfo("de-DE");
        var months = Enumerable.Range(0, 6)
            .Select(i => now.AddMonths(-5 + i))
            .Select(d => new {
                d.Year, d.Month,
                Label      = d.ToString("MMM yy", culture),
                Bookings   = bookingsByMonth.FirstOrDefault(b => b.Year == d.Year && b.Month == d.Month)?.Total ?? 0,
                Confirmed  = bookingsByMonth.FirstOrDefault(b => b.Year == d.Year && b.Month == d.Month)?.Confirmed ?? 0,
                Cancelled  = bookingsByMonth.FirstOrDefault(b => b.Year == d.Year && b.Month == d.Month)?.Cancelled ?? 0,
                NewTenants = tenantsByMonth.FirstOrDefault(t => t.Year == d.Year && t.Month == d.Month)?.NewTenants ?? 0,
            })
            .ToList();

        return Ok(new {
            MonthlyData = months,
            EmailStats = new {
                Sent    = emailStats.FirstOrDefault(e => e.Status == "Sent")?.Count    ?? 0,
                Failed  = emailStats.FirstOrDefault(e => e.Status == "Failed")?.Count  ?? 0,
                Pending = emailStats.FirstOrDefault(e => e.Status == "Pending")?.Count ?? 0,
            },
            TopTenants = topTenantGroups.Select(tt => {
                var t = topTenantDetails.FirstOrDefault(x => x.Id == tt.TenantId);
                return new {
                    TenantId    = tt.TenantId,
                    CompanyName = t?.Settings?.CompanyName ?? t?.Name ?? "Unbekannt",
                    Slug        = t?.Slug ?? "",
                    BookingCount = tt.BookingCount,
                };
            }),
        });
    }

    // ── Email Logs ────────────────────────────────────────────────────────

    /// <summary>Platform-wide email logs with optional filters.</summary>
    [HttpGet("email-logs")]
    public async Task<IActionResult> GetEmailLogs(
        [FromQuery] Guid? tenantId,
        [FromQuery] string? status,
        [FromQuery] string? emailType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var query = _db.EmailLogs
            .IgnoreQueryFilters()
            .Include(e => e.Tenant).ThenInclude(t => t.Settings)
            .AsNoTracking()
            .AsQueryable();

        if (tenantId.HasValue)
            query = query.Where(e => e.TenantId == tenantId.Value);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<EmailStatus>(status, out var parsedStatus))
            query = query.Where(e => e.Status == parsedStatus);

        if (!string.IsNullOrEmpty(emailType) && Enum.TryParse<EmailType>(emailType, out var parsedType))
            query = query.Where(e => e.EmailType == parsedType);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id,
                e.TenantId,
                CompanyName = e.Tenant.Settings != null ? e.Tenant.Settings.CompanyName : e.Tenant.Name,
                TenantSlug = e.Tenant.Slug,
                e.RecipientEmail,
                e.Subject,
                EmailType = e.EmailType.ToString(),
                Status = e.Status.ToString(),
                e.SentAt,
                e.ErrorMessage,
                e.CreatedAt,
            })
            .ToListAsync();

        var sentCount   = await _db.EmailLogs.IgnoreQueryFilters().CountAsync(e => e.Status == EmailStatus.Sent);
        var failedCount = await _db.EmailLogs.IgnoreQueryFilters().CountAsync(e => e.Status == EmailStatus.Failed);

        return Ok(new { items, totalCount = total, page, pageSize, sentCount, failedCount });
    }

    // ── Tenant Stats ──────────────────────────────────────────────────────

    /// <summary>Detailed booking + revenue stats for a single tenant (last 6 months).</summary>
    [HttpGet("tenants/{id:guid}/stats")]
    public async Task<IActionResult> GetTenantStats(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        var since = DateTime.UtcNow.AddMonths(-6);
        var sinceDate = DateOnly.FromDateTime(since);

        var bookings = await _db.Bookings
            .IgnoreQueryFilters()
            .Include(b => b.Service)
            .Where(b => b.TenantId == id && b.BookingDate >= sinceDate)
            .AsNoTracking()
            .ToListAsync();

        // Group by month
        var byMonth = bookings
            .GroupBy(b => new { b.BookingDate.Year, b.BookingDate.Month })
            .Select(g => new
            {
                Year  = g.Key.Year,
                Month = g.Key.Month,
                Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM yyyy"),
                Bookings = g.Count(),
                Revenue  = g.Where(b => b.Status != BookingStatus.Cancelled)
                            .Sum(b => b.Service?.Price ?? 0),
            })
            .OrderBy(x => x.Year).ThenBy(x => x.Month)
            .ToList();

        // Fill missing months so chart is always 6 bars
        var months = Enumerable.Range(0, 6)
            .Select(i => DateTime.UtcNow.AddMonths(-5 + i))
            .Select(d => new
            {
                Year  = d.Year,
                Month = d.Month,
                Label = d.ToString("MMM yyyy"),
                Bookings = byMonth.FirstOrDefault(m => m.Year == d.Year && m.Month == d.Month)?.Bookings ?? 0,
                Revenue  = byMonth.FirstOrDefault(m => m.Year == d.Year && m.Month == d.Month)?.Revenue ?? 0m,
            })
            .ToList();

        var allBookings = await _db.Bookings.IgnoreQueryFilters().Where(b => b.TenantId == id).AsNoTracking().ToListAsync();
        var customers   = await _db.Customers.IgnoreQueryFilters().CountAsync(c => c.TenantId == id);
        var employees   = await _db.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == id);

        return Ok(new
        {
            MonthlyStats   = months,
            TotalBookings  = allBookings.Count,
            TotalRevenue   = allBookings.Where(b => b.Status != BookingStatus.Cancelled)
                                        .Sum(b => _db.Services.IgnoreQueryFilters().Where(s => s.Id == b.ServiceId).Select(s => s.Price).FirstOrDefault()),
            TotalCustomers = customers,
            TotalEmployees = employees,
            ConfirmedCount = allBookings.Count(b => b.Status == BookingStatus.Confirmed),
            CancelledCount = allBookings.Count(b => b.Status == BookingStatus.Cancelled),
            CompletedCount = allBookings.Count(b => b.Status == BookingStatus.Completed),
        });
    }

    // ── Activity Feed ─────────────────────────────────────────────────────

    /// <summary>Platform-wide activity feed derived from recent tenant + booking events.</summary>
    [HttpGet("activity")]
    public async Task<IActionResult> GetActivity([FromQuery] int limit = 30)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var activities = new List<object>();

        // Recent tenant registrations
        var recentTenants = await _db.Tenants
            .Include(t => t.Settings)
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .Take(10)
            .ToListAsync();

        foreach (var t in recentTenants)
        {
            activities.Add(new
            {
                Type      = "TenantCreated",
                Icon      = "building",
                Title     = $"Neues System angelegt: {(t.Settings?.CompanyName ?? t.Name)}",
                Detail    = $"/{t.Slug}",
                TenantId  = t.Id,
                Timestamp = t.CreatedAt,
            });
        }

        // Recently deactivated/reactivated tenants
        var recentUpdated = await _db.Tenants
            .Include(t => t.Settings)
            .AsNoTracking()
            .Where(t => t.UpdatedAt > t.CreatedAt.AddMinutes(5))
            .OrderByDescending(t => t.UpdatedAt)
            .Take(10)
            .ToListAsync();

        foreach (var t in recentUpdated)
        {
            activities.Add(new
            {
                Type      = t.IsActive ? "TenantActivated" : "TenantDeactivated",
                Icon      = t.IsActive ? "check" : "ban",
                Title     = t.IsActive
                    ? $"System aktiviert: {(t.Settings?.CompanyName ?? t.Name)}"
                    : $"System deaktiviert: {(t.Settings?.CompanyName ?? t.Name)}",
                Detail    = $"/{t.Slug}",
                TenantId  = t.Id,
                Timestamp = t.UpdatedAt,
            });
        }

        // Recent trial extensions
        var recentSubs = await _db.Subscriptions
            .Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .AsNoTracking()
            .Where(s => s.UpdatedAt > s.CreatedAt.AddMinutes(5))
            .OrderByDescending(s => s.UpdatedAt)
            .Take(10)
            .ToListAsync();

        foreach (var s in recentSubs)
        {
            activities.Add(new
            {
                Type      = "TrialExtended",
                Icon      = "zap",
                Title     = $"Trial verlängert: {(s.Tenant?.Settings?.CompanyName ?? s.Tenant?.Name ?? "–")}",
                Detail    = $"Läuft bis {s.TrialEndsAt:dd.MM.yyyy}",
                TenantId  = s.TenantId,
                Timestamp = s.UpdatedAt,
            });
        }

        // Recent bookings (platform-wide)
        var recentBookings = await _db.Bookings
            .IgnoreQueryFilters()
            .Include(b => b.Tenant).ThenInclude(t => t.Settings)
            .Include(b => b.Customer)
            .AsNoTracking()
            .OrderByDescending(b => b.CreatedAt)
            .Take(15)
            .ToListAsync();

        foreach (var b in recentBookings)
        {
            activities.Add(new
            {
                Type      = "BookingCreated",
                Icon      = "calendar",
                Title     = $"Neue Buchung: {b.Customer?.FullName ?? "–"}",
                Detail    = $"{(b.Tenant?.Settings?.CompanyName ?? b.Tenant?.Name ?? "–")} · {b.BookingDate:dd.MM.yyyy}",
                TenantId  = b.TenantId,
                Timestamp = b.CreatedAt,
            });
        }

        // Recent new platform users (TenantAdmins)
        var recentUsers = await _db.PlatformUsers
            .Include(u => u.Tenant).ThenInclude(t => t!.Settings)
            .AsNoTracking()
            .Where(u => u.Role == PlatformRole.TenantAdmin)
            .OrderByDescending(u => u.CreatedAt)
            .Take(10)
            .ToListAsync();

        foreach (var u in recentUsers)
        {
            activities.Add(new
            {
                Type      = "UserCreated",
                Icon      = "user",
                Title     = $"Admin-User erstellt: {u.FirstName} {u.LastName}",
                Detail    = $"{u.Email} · {(u.Tenant?.Settings?.CompanyName ?? u.Tenant?.Name ?? "–")}",
                TenantId  = u.TenantId,
                Timestamp = u.CreatedAt,
            });
        }

        var sorted = activities
            .OrderByDescending(a => (DateTime)a.GetType().GetProperty("Timestamp")!.GetValue(a)!)
            .Take(limit)
            .ToList();

        return Ok(sorted);
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
    string? AdminLastName,
    bool? SendWelcomeEmail = true
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

public record CreateTenantUserDto(string Email, string? Password, string FirstName, string LastName, bool? SendWelcomeEmail = true);
public record ExtendTrialDto(int Days);
