// Controllers/SuperAdminController.cs
// Full tenant management for the Super Admin.
using System.Security.Cryptography;
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Hangfire;
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
    private readonly JwtService _jwt;
    private readonly IWebHostEnvironment _env;
    private readonly AuditService _audit;
    private readonly MollieService _mollieService;
    private readonly IBackgroundJobClient _backgroundJobClient;

    public SuperAdminController(GentleBookDbContext db, IConfiguration config, EmailService emailService, ILogger<SuperAdminController> logger, JwtService jwt, IWebHostEnvironment env, AuditService audit, MollieService mollieService, IBackgroundJobClient backgroundJobClient)
    {
        _db = db;
        _config = config;
        _emailService = emailService;
        _logger = logger;
        _jwt = jwt;
        _env = env;
        _audit = audit;
        _mollieService = mollieService;
        _backgroundJobClient = backgroundJobClient;
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

        // Aggregate counts in a single query per type to avoid N+1
        var tenantIds = await query.Skip((page - 1) * pageSize).Take(pageSize).Select(t => t.Id).ToListAsync();
        var employeeCounts = await _db.Employees.IgnoreQueryFilters()
            .Where(e => tenantIds.Contains(e.TenantId))
            .GroupBy(e => e.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TenantId, x => x.Count);
        var bookingCounts = await _db.Bookings.IgnoreQueryFilters()
            .Where(b => tenantIds.Contains(b.TenantId))
            .GroupBy(b => b.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TenantId, x => x.Count);

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
                Subscription = t.Subscription == null ? null : new
                {
                    t.Subscription.Plan,
                    t.Subscription.Status,
                    t.Subscription.TrialEndsAt,
                    t.Subscription.TrialDaysRemaining,
                    t.Subscription.IsInTrial,
                    t.Subscription.IsAccessAllowed,
                    t.Subscription.CurrentPeriodEnd,
                    t.Subscription.PastDueSince,
                }
            })
            .ToListAsync();

        var result = items.Select(t => new
        {
            t.Id, t.Name, t.Slug, t.IndustryType, t.IsActive, t.CreatedAt,
            t.CompanyName, t.LogoUrl, t.PrimaryColor, t.Subscription,
            EmployeeCount = employeeCounts.GetValueOrDefault(t.Id, 0),
            BookingCount  = bookingCounts.GetValueOrDefault(t.Id, 0),
        });

        return Ok(new { items = result, totalCount = total, page, pageSize });
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

    /// <summary>Live Mollie billing snapshot for one tenant — mandate/subscription status and
    /// recent payment history fetched directly from Mollie, not from GentleBook's local DB, so
    /// the SuperAdmin doesn't need to open Mollie's own dashboard to check on a customer.</summary>
    [HttpGet("tenants/{id:guid}/billing")]
    public async Task<IActionResult> GetTenantBilling(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var exists = await _db.Tenants.AsNoTracking().AnyAsync(t => t.Id == id);
        if (!exists) return NotFound();

        var live = await _mollieService.GetLiveBillingStatusAsync(id);
        return Ok(live);
    }

    /// <summary>
    /// Prepares a tenant after the sales conversation. Legal acceptance is followed by a
    /// separate manual GentleBook release; only that release starts the 14-day trial.
    /// </summary>
    [HttpPost("tenants")]
    public async Task<IActionResult> CreateTenant([FromBody] CreateTenantDto dto)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var slug = dto.Slug.ToLowerInvariant().Trim();
        if (await _db.Tenants.AnyAsync(t => t.Slug == slug))
            return Conflict(new { message = $"Slug '{slug}' is already taken." });

        if (string.IsNullOrWhiteSpace(dto.AdminEmail))
            return BadRequest(new { message = "Eine E-Mail-Adresse für die Testfreischaltung ist erforderlich." });

        var trialDays = int.TryParse(_config["Platform:DefaultTrialDays"], out var d) ? d : 14;

        var tenant = new Tenant
        {
            Name = dto.Name,
            Slug = slug,
            IndustryType = dto.IndustryType,
            IsActive = false,
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
            Interval = SubscriptionInterval.Monthly,
            Status = SubscriptionStatus.PendingAcceptance,
        };
        var defaultLocation = new BusinessLocation
        {
            TenantId = tenant.Id,
            Name = "Hauptstandort",
            City = "Hauptstandort",
            CountryCode = string.Equals(settings.DefaultCurrency, "CHF", StringComparison.OrdinalIgnoreCase) ? "CH" : "DE",
            Currency = settings.DefaultCurrency.ToUpperInvariant(),
            TimeZone = settings.TimeZone,
            IsDefault = true,
            IsActive = true,
        };

        _db.Tenants.Add(tenant);
        _db.TenantSettings.Add(settings);
        _db.Subscriptions.Add(subscription);
        _db.BusinessLocations.Add(defaultLocation);

        // The user stays locked until all required trial documents have been confirmed.
        var adminFirstName = dto.AdminFirstName ?? "Admin";
        var adminUser = new PlatformUser
        {
            TenantId = tenant.Id,
            Email = dto.AdminEmail.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString(), workFactor: 4),
            FirstName = adminFirstName,
            LastName = dto.AdminLastName ?? tenant.Name,
            Role = PlatformRole.TenantAdmin,
            IsActive = false,
            MustChangePassword = true,
        };
        _db.PlatformUsers.Add(adminUser);

        var activationRawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
        var activationTokenHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(activationRawToken)));
        var activationExpiresAt = DateTime.UtcNow.AddDays(14);
        _db.TrialAccessInvitations.Add(new TrialAccessInvitation
        {
            TenantId = tenant.Id,
            UserId = adminUser.Id,
            Email = adminUser.Email,
            TokenHash = activationTokenHash,
            ExpiresAt = activationExpiresAt,
            TermsVersion = LegalDocumentVersions.Terms,
            PrivacyVersion = LegalDocumentVersions.Privacy,
            DpaVersion = LegalDocumentVersions.Dpa,
            PersonalNote = dto.PersonalNote,
        });

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveChanges failed in CreateTenant");
            return StatusCode(500, new { message = "DB-Fehler beim Speichern." });
        }

        // Send legal activation link. Credentials follow only after acceptance.
        if (dto.SendWelcomeEmail != false)
        {
            var activationUrl = $"{_emailService.FrontendUrl}/trial-activation?token={activationRawToken}";
            _ = _emailService.SendTrialActivationInvitationAsync(
                adminUser.Email, adminFirstName, tenant.Name, slug, activationUrl, activationExpiresAt);
        }

        await _audit.LogAsync("tenant.created", "Tenant", tenant.Id.ToString(),
            $"System \"{tenant.Name}\" ({slug}) vorbereitet; Testfreischaltung ausstehend", tenant.Id);

        return CreatedAtAction(nameof(GetTenant), new { id = tenant.Id }, new
        {
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            Status = subscription.Status.ToString(),
            ActivationExpiresAt = activationExpiresAt,
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
        var tenant = await _db.Tenants.Include(t => t.Subscription).FirstOrDefaultAsync(t => t.Id == id);
        if (tenant == null) return NotFound();
        if (tenant.Subscription?.Status is SubscriptionStatus.PendingAcceptance or SubscriptionStatus.PendingActivation)
            return Conflict(new { message = "Vorbereitete Testmandanten müssen über die gesonderte Testfreigabe aktiviert werden." });
        tenant.IsActive = true;
        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("tenant.activated", "Tenant", id.ToString(), $"System \"{tenant.Name}\" aktiviert", id);
        return NoContent();
    }

    /// <summary>Manually starts the 14-day trial after all legal confirmations were received.</summary>
    [HttpPost("tenants/{id:guid}/trial/activate")]
    public async Task<IActionResult> ActivatePreparedTrial(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var tenant = await _db.Tenants
            .Include(t => t.Subscription)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (tenant == null) return NotFound(new { message = "Mandant nicht gefunden." });
        if (tenant.Subscription?.Status != SubscriptionStatus.PendingActivation)
            return Conflict(new { message = "Der Test kann erst nach vollständiger Bestätigung von B2B-Eigenschaft, AGB, Datenschutz und AVV freigegeben werden." });

        var invitation = await _db.TrialAccessInvitations
            .Include(x => x.User)
            .Where(x => x.TenantId == id && x.AcceptedAt != null)
            .OrderByDescending(x => x.AcceptedAt)
            .FirstOrDefaultAsync();
        if (invitation?.User == null || !invitation.BusinessConfirmed || !invitation.TermsAccepted ||
            !invitation.PrivacyAcknowledged || !invitation.DpaAccepted ||
            !invitation.NoAutomaticPaidConversionAcknowledged)
        {
            return Conflict(new { message = "Die erforderlichen rechtlichen Bestätigungen sind nicht vollständig dokumentiert." });
        }

        var now = DateTime.UtcNow;
        var trialDays = int.TryParse(_config["Platform:DefaultTrialDays"], out var configuredDays) ? configuredDays : 14;
        var subscription = tenant.Subscription;
        subscription.Status = SubscriptionStatus.Trial;
        subscription.Plan = SubscriptionPlan.Trial;
        subscription.TrialStartedAt = now;
        subscription.TrialEndsAt = now.AddDays(trialDays);
        subscription.TrialActivatedByUserId = JwtService.GetUserId(User);
        subscription.AccessRestrictedAt = null;
        subscription.RetentionEndsAt = null;
        subscription.RetentionWarningEmailSent = false;
        subscription.OperationalDataDeletedAt = null;
        subscription.UpdatedAt = now;
        tenant.IsActive = true;
        tenant.UpdatedAt = now;
        invitation.User.IsActive = true;
        invitation.User.UpdatedAt = now;

        var existingTokens = await _db.PasswordResetTokens
            .Where(t => t.UserId == invitation.User.Id && !t.IsUsed)
            .ToListAsync();
        foreach (var existingToken in existingTokens) existingToken.IsUsed = true;

        var setupRawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
        var setupTokenHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(setupRawToken)));
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = invitation.User.Id,
            TokenHash = setupTokenHash,
            ExpiresAt = now.AddHours(72),
            IsUsed = false,
            CreatedAt = now,
        });

        await _db.SaveChangesAsync();

        var setupUrl = $"{_emailService.FrontendUrl}/admin/reset-password?token={setupRawToken}";
        _ = _emailService.SendWelcomeEmailAsync(
            invitation.User.Email,
            invitation.User.FirstName,
            setupUrl,
            tenant.Name,
            tenant.Slug,
            tenant.Id,
            tenant.IndustryType.ToString(),
            SubscriptionPlan.Trial.ToString(),
            invitation.PersonalNote,
            subscription.TrialStartedAt,
            subscription.TrialEndsAt);

        await _audit.LogAsync("subscription.trial_activated", "Subscription", subscription.Id.ToString(),
            $"Test manuell freigegeben; Beginn {subscription.TrialStartedAt:O}; Ende {subscription.TrialEndsAt:O}; bestätigt durch {invitation.AcceptedByName}", id);

        return Ok(new
        {
            status = subscription.Status.ToString(),
            subscription.TrialStartedAt,
            subscription.TrialEndsAt,
            activatedByUserId = subscription.TrialActivatedByUserId,
            sentTo = invitation.User.Email,
        });
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
        await _audit.LogAsync("tenant.deactivated", "Tenant", id.ToString(), $"System \"{tenant.Name}\" deaktiviert", id);
        return NoContent();
    }

    /// <summary>Delete a tenant and all its data.</summary>
    [HttpDelete("tenants/{id:guid}")]
    public async Task<IActionResult> DeleteTenant(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Dependents with optional/restrictive foreign keys must be removed
            // before their bookings, services or employees.
            await _db.EmailLogs.IgnoreQueryFilters().Where(e => e.TenantId == id).ExecuteDeleteAsync();
            await _db.WaitlistEntries.IgnoreQueryFilters().Where(w => w.TenantId == id).ExecuteDeleteAsync();
            await _db.EmployeeNotes.IgnoreQueryFilters().Where(n => n.TenantId == id).ExecuteDeleteAsync();
            await _db.Bookings.IgnoreQueryFilters().Where(b => b.TenantId == id).ExecuteDeleteAsync();

            await _db.BlockedTimeSlots.IgnoreQueryFilters().Where(b => b.TenantId == id).ExecuteDeleteAsync();
            await _db.EmployeeVacations.IgnoreQueryFilters().Where(v => v.TenantId == id).ExecuteDeleteAsync();
            await _db.EmployeeSchedules.IgnoreQueryFilters().Where(s => s.TenantId == id).ExecuteDeleteAsync();

            await _db.ServiceEmployees.IgnoreQueryFilters()
                .Where(se => se.Service.TenantId == id || se.Employee.TenantId == id)
                .ExecuteDeleteAsync();

            await _db.Services.IgnoreQueryFilters().Where(s => s.TenantId == id).ExecuteDeleteAsync();
            await _db.BusinessLocations.IgnoreQueryFilters().Where(l => l.TenantId == id).ExecuteDeleteAsync();
            await _db.ServiceCategories.IgnoreQueryFilters().Where(c => c.TenantId == id).ExecuteDeleteAsync();
            await _db.Customers.IgnoreQueryFilters().Where(c => c.TenantId == id).ExecuteDeleteAsync();
            await _db.BusinessHours.IgnoreQueryFilters().Where(h => h.TenantId == id).ExecuteDeleteAsync();
            await _db.TenantLinks.IgnoreQueryFilters().Where(l => l.TenantId == id).ExecuteDeleteAsync();

            await _db.PasswordResetTokens
                .Where(t => t.User.TenantId == id)
                .ExecuteDeleteAsync();
            await _db.PlatformUsers.Where(u => u.TenantId == id).ExecuteDeleteAsync();

            await _db.TenantSettings.Where(s => s.TenantId == id).ExecuteDeleteAsync();
            await _db.Subscriptions.Where(s => s.TenantId == id).ExecuteDeleteAsync();
            await _db.SubscriptionRequests.IgnoreQueryFilters().Where(r => r.TenantId == id).ExecuteDeleteAsync();
            await _db.Employees.IgnoreQueryFilters().Where(e => e.TenantId == id).ExecuteDeleteAsync();
            await _db.AuditLogs.Where(a => a.TenantId == id).ExecuteDeleteAsync();

            _db.Tenants.Remove(tenant);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "DeleteTenant failed for {TenantId}", id);
            return StatusCode(500, new { message = "Löschen fehlgeschlagen." });
        }
        await _audit.LogAsync("tenant.deleted", "Tenant", id.ToString(), $"System \"{tenant.Name}\" ({tenant.Slug}) samt aller Daten gelöscht");
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
        if (!string.IsNullOrWhiteSpace(dto.DefaultCurrency))
        {
            var currency = dto.DefaultCurrency.Trim().ToUpperInvariant();
            if (currency.Length != 3)
                return BadRequest(new { message = "Die Währung muss ein gültiger dreistelliger ISO-Code sein." });

            settings.DefaultCurrency = currency;
            var defaultLocation = await _db.BusinessLocations
                .FirstOrDefaultAsync(location => location.TenantId == id && location.IsDefault);
            if (defaultLocation != null)
            {
                defaultLocation.Currency = currency;
                defaultLocation.UpdatedAt = DateTime.UtcNow;
            }
            await _db.Services
                .Where(s => s.TenantId == id
                    && (s.LocationId == null || (defaultLocation != null && s.LocationId == defaultLocation.Id))
                    && s.Currency != currency)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(s => s.Currency, currency)
                    .SetProperty(s => s.UpdatedAt, DateTime.UtcNow));
        }
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

        var allowedExtensionsByType = new Dictionary<string, string>
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp",
            ["image/gif"] = ".gif",
        };
        if (!allowedExtensionsByType.TryGetValue(logo.ContentType.ToLower(), out var ext))
            return BadRequest(new { message = "Nur JPG, PNG, WebP und GIF sind erlaubt." });

        if (logo.Length > 5 * 1024 * 1024)
            return BadRequest(new { message = "Die Datei darf maximal 5 MB groß sein." });

        // Extension is derived from the validated ContentType above, never from the
        // client-supplied FileName — an attacker could otherwise upload e.g. "evil.html"
        // with a spoofed image/* content-type header and have it saved with a .html extension.
        var fileName = $"{id}{ext}";
        var uploadDir = Path.Combine(_env.WebRootPath, "uploads", "logos");
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

        var lockedHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString(), workFactor: 4);
        var user = new PlatformUser
        {
            TenantId = id,
            Email = dto.Email.ToLowerInvariant(),
            PasswordHash = lockedHash,
            FirstName = dto.FirstName,
            LastName = dto.LastName,
            Role = PlatformRole.TenantAdmin,
            MustChangePassword = true,
        };
        _db.PlatformUsers.Add(user);

        var setupRawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
        var tokenHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(setupRawToken)));
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id        = Guid.NewGuid(),
            UserId    = user.Id,
            TokenHash = tokenHash,
            ExpiresAt = DateTime.UtcNow.AddHours(72),
            IsUsed    = false,
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync();

        // Send welcome email with setup link (fire-and-forget; failure is logged, not thrown)
        if (dto.SendWelcomeEmail != false)
        {
            var setupUrl = $"{_emailService.FrontendUrl}/admin/reset-password?token={setupRawToken}";
            _ = _emailService.SendWelcomeEmailAsync(
                user.Email,
                user.FirstName,
                setupUrl,
                tenant.Name,
                tenant.Slug,
                tenant.Id,
                tenant.IndustryType.ToString(),
                "Trial");
        }

        return Ok(new { user.Id, user.Email, user.FirstName, user.LastName, passwordGenerated = true });
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

        await _audit.LogAsync("subscription.trial_extended", "Subscription", subscription.Id.ToString(),
            $"Trial um {dto.Days} Tage verlängert (neu bis {subscription.TrialEndsAt:dd.MM.yyyy})", id);

        return Ok(new { subscription.TrialEndsAt, subscription.TrialDaysRemaining });
    }

    // ── Eigene Domain (Agency) ───────────────────────────────────────
    // No automatic Vercel provisioning yet — a SuperAdmin adds the domain by hand in the Vercel
    // dashboard, then confirms it here. See Configuration/AgencyFeatureGate.cs and the
    // Agency-Ausbau-Runde-2 plan for why this stops short of a live API call.
    [HttpGet("tenants/{id:guid}/domain")]
    public async Task<IActionResult> GetTenantDomain(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var settings = await _db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == id);
        if (settings == null) return NotFound();

        return Ok(new
        {
            domain = settings.CustomDomain,
            status = settings.CustomDomainStatus,
            requestedAt = settings.CustomDomainRequestedAt,
        });
    }

    [HttpPost("tenants/{id:guid}/domain/status")]
    public async Task<IActionResult> SetTenantDomainStatus(Guid id, [FromBody] SetDomainStatusDto dto)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        if (dto.Status is not ("Verified" or "Failed" or "PendingVerification"))
            return BadRequest(new { message = "Status muss Verified, Failed oder PendingVerification sein." });

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == id);
        if (settings == null) return NotFound();
        if (string.IsNullOrWhiteSpace(settings.CustomDomain))
            return BadRequest(new { message = "Für diesen Tenant wurde keine Domain beantragt." });

        settings.CustomDomainStatus = dto.Status;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("custom_domain.status_changed", "TenantSettings", settings.Id.ToString(),
            $"{settings.CustomDomain} → {dto.Status}", id);

        return Ok(new { domain = settings.CustomDomain, status = settings.CustomDomainStatus });
    }

    /// <summary>Upgrade or change a tenant's subscription plan.</summary>
    [HttpPatch("tenants/{id:guid}/plan")]
    public async Task<IActionResult> ChangePlan(Guid id, [FromBody] ChangePlanDto dto)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == id);
        if (subscription == null) return NotFound(new { message = "Subscription nicht gefunden" });

        if (!Enum.TryParse<SubscriptionPlan>(dto.Plan, ignoreCase: true, out var newPlan))
            return BadRequest(new { message = $"Ungültiger Plan: {dto.Plan}. Erlaubt: Trial, Starter, Professional, Agency" });

        if (newPlan == SubscriptionPlan.Agency)
            return BadRequest(new { message = "Agency darf nicht direkt aktiviert werden. Bitte eine Agency-Anfrage öffnen und dem Kunden ein Angebot senden." });

        // Only overwrite the interval if the caller explicitly specified one — a plain plan
        // change must not silently reset an existing yearly subscriber back to monthly.
        if (!string.IsNullOrEmpty(dto.Interval) && Enum.TryParse<SubscriptionInterval>(dto.Interval, ignoreCase: true, out var newInterval))
            subscription.Interval = newInterval;

        // Agency has no fixed price ("Preis auf Anfrage") — only overwrite when the caller
        // explicitly provided a negotiated amount, so re-saving other fields never wipes an
        // already-agreed price back to null.
        if (newPlan == SubscriptionPlan.Agency)
        {
            if (dto.NegotiatedMonthlyPrice.HasValue) subscription.NegotiatedMonthlyPrice = dto.NegotiatedMonthlyPrice;
            if (dto.NegotiatedAnnualPrice.HasValue) subscription.NegotiatedAnnualPrice = dto.NegotiatedAnnualPrice;
        }

        subscription.Plan = newPlan;
        subscription.Status = newPlan == SubscriptionPlan.Trial
            ? SubscriptionStatus.Trial
            : SubscriptionStatus.Active;

        if (newPlan != SubscriptionPlan.Trial)
        {
            subscription.CurrentPeriodStart = DateTime.UtcNow;
            subscription.CurrentPeriodEnd = subscription.Interval.AddInterval(DateTime.UtcNow);
        }

        subscription.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("subscription.plan_changed", "Subscription", subscription.Id.ToString(),
            $"Plan geändert auf {newPlan}", id);

        // Best-effort — keeps a live Mollie subscription's price/interval in sync with this
        // manual override. No fit-check here (deliberate admin override), failures are logged
        // internally and never block the DB-level change.
        if (!string.IsNullOrEmpty(subscription.MollieSubscriptionId))
            await _mollieService.TrySyncPlanToMollieAsync(id);

        var limits = PlanLimits.Get(newPlan);
        return Ok(new
        {
            plan = subscription.Plan.ToString(),
            status = subscription.Status.ToString(),
            interval = subscription.Interval.ToString(),
            displayName = limits.DisplayName,
            maxEmployees = limits.MaxEmployees,
            maxServices = limits.MaxServices,
        });
    }

    /// <summary>Resend onboarding/welcome email with a fresh password-reset link (72h).</summary>
    [HttpPost("tenants/{id:guid}/resend-welcome")]
    public async Task<IActionResult> ResendWelcomeEmail(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var tenant = await _db.Tenants
            .Include(t => t.Subscription)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (tenant == null) return NotFound(new { message = "Tenant nicht gefunden." });

        var adminUser = await _db.PlatformUsers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.TenantId == id && u.Role == PlatformRole.TenantAdmin);
        if (adminUser == null) return BadRequest(new { message = "Kein Admin-User gefunden." });

        if (tenant.Subscription?.Status == SubscriptionStatus.PendingAcceptance)
        {
            var previousInvitations = await _db.TrialAccessInvitations
                .Where(x => x.TenantId == id && x.AcceptedAt == null && x.ExpiresAt > DateTime.UtcNow)
                .ToListAsync();
            foreach (var previous in previousInvitations) previous.ExpiresAt = DateTime.UtcNow;

            var activationRawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
                .Replace("+", "-").Replace("/", "_").Replace("=", "");
            var activationHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(activationRawToken)));
            var activationExpiresAt = DateTime.UtcNow.AddDays(14);
            var personalNote = previousInvitations.OrderByDescending(x => x.CreatedAt).FirstOrDefault()?.PersonalNote;

            _db.TrialAccessInvitations.Add(new TrialAccessInvitation
            {
                TenantId = id,
                UserId = adminUser.Id,
                Email = adminUser.Email,
                TokenHash = activationHash,
                ExpiresAt = activationExpiresAt,
                TermsVersion = LegalDocumentVersions.Terms,
                PrivacyVersion = LegalDocumentVersions.Privacy,
                DpaVersion = LegalDocumentVersions.Dpa,
                PersonalNote = personalNote,
            });
            await _db.SaveChangesAsync();

            var activationUrl = $"{_emailService.FrontendUrl}/trial-activation?token={activationRawToken}";
            _ = _emailService.SendTrialActivationInvitationAsync(
                adminUser.Email, adminUser.FirstName ?? "Admin", tenant.Name, tenant.Slug,
                activationUrl, activationExpiresAt);

            return Ok(new { sent = true, sentTo = adminUser.Email, type = "trial-activation" });
        }

        if (tenant.Subscription?.Status == SubscriptionStatus.PendingActivation)
        {
            return Conflict(new
            {
                message = "Die rechtlichen Bestätigungen liegen bereits vor. Geben Sie den Testzugang jetzt manuell über die Testfreigabe frei."
            });
        }

        // Generate a fresh password-reset token (72h)
        var rawToken  = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
        var tokenHash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawToken)));

        // Invalidate any existing unused tokens for this user
        var existing = await _db.PasswordResetTokens
            .Where(t => t.UserId == adminUser.Id && !t.IsUsed)
            .ToListAsync();
        foreach (var t in existing) t.IsUsed = true;

        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id        = Guid.NewGuid(),
            UserId    = adminUser.Id,
            TokenHash = tokenHash,
            ExpiresAt = DateTime.UtcNow.AddHours(72),
            IsUsed    = false,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var setupUrl = $"{_emailService.FrontendUrl}/admin/reset-password?token={rawToken}";
        var planStr  = tenant.Subscription?.Plan.ToString() ?? "Trial";

        _ = _emailService.SendWelcomeEmailAsync(
            adminUser.Email,
            adminUser.FirstName ?? "Admin",
            setupUrl,
            tenant.Name,
            tenant.Slug,
            tenant.Id,
            tenant.IndustryType.ToString(),
            planStr,
            personalNote: null);

        return Ok(new { sent = true, sentTo = adminUser.Email });
    }

    /// <summary>Platform-wide stats for SuperAdmin dashboard.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        // MRR: Summe der Monatspreise aller aktiven Abos, pro Subscription — Agency-Kunden mit
        // individuell verhandeltem Preis (NegotiatedMonthlyPrice) zählen mit ihrem tatsächlichen
        // Preis statt dem globalen PlanLimits-Fallback.
        var activeSubs = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active)
            .Select(s => new { s.Plan, s.NegotiatedMonthlyPrice })
            .ToListAsync();

        var activePlans = activeSubs
            .GroupBy(s => s.Plan)
            .Select(g => new { Plan = g.Key, Count = g.Count() })
            .ToList();

        var mrr = activeSubs.Sum(s => s.NegotiatedMonthlyPrice ?? PlanLimits.Get(s.Plan).MonthlyPrice);

        var stats = new
        {
            TotalTenants = await _db.Tenants.CountAsync(),
            ActiveTenants = await _db.Tenants.CountAsync(t => t.IsActive),
            TrialTenants = await _db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Trial),
            ActiveSubscriptions = await _db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Active),
            ExpiredTenants = await _db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Expired),
            TotalBookings = await _db.Bookings.IgnoreQueryFilters().CountAsync(),
            Mrr = mrr,
            PlanDistribution = activePlans.Select(p => new
            {
                Plan = PlanLimits.Get(p.Plan).DisplayName,
                p.Count,
                MonthlyPrice = PlanLimits.Get(p.Plan).MonthlyPrice,
            }),
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

    /// <summary>
    /// Realized (Mollie-collected) revenue, churn rate, and trial→paid conversion rate per
    /// week/month — deliberately separate from /stats' MRR, which is a forward-looking snapshot
    /// of currently-recurring prices, not money that has actually been collected.
    /// </summary>
    [HttpGet("revenue")]
    public async Task<IActionResult> GetRevenue([FromQuery] string granularity = "month", [FromQuery] int periods = 12)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var isWeekly = string.Equals(granularity, "week", StringComparison.OrdinalIgnoreCase);
        periods = Math.Clamp(periods, 1, 52);
        var now = DateTime.UtcNow;
        var culture = new System.Globalization.CultureInfo("de-DE");

        DateTime PeriodStart(int indexFromOldest)
        {
            if (isWeekly)
            {
                var daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
                var thisMonday = now.Date.AddDays(-daysSinceMonday);
                return thisMonday.AddDays(-7 * (periods - 1 - indexFromOldest));
            }
            var thisMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return thisMonthStart.AddMonths(-(periods - 1 - indexFromOldest));
        }
        DateTime PeriodEnd(DateTime start) => isWeekly ? start.AddDays(7) : start.AddMonths(1);

        var earliestStart = PeriodStart(0);

        var invoices = await _db.Invoices
            .Where(i => i.IssueDate >= earliestStart)
            .Select(i => new { i.IssueDate, i.Amount })
            .ToListAsync();

        var cancellations = await _db.Subscriptions
            .Where(s => s.CancelledAt != null && s.CancelledAt >= earliestStart)
            .Select(s => s.CancelledAt!.Value)
            .ToListAsync();

        // "Ever paying" = completed at least one real Mollie mandate signup — used both as the
        // churn denominator (who could churn at all) and the conversion-rate numerator.
        var everPaying = await _db.Subscriptions
            .Where(s => s.MollieMandateSignedAt != null)
            .Select(s => new { s.MollieMandateSignedAt, s.CancelledAt })
            .ToListAsync();

        var trialEndCohorts = await _db.Subscriptions
            .Where(s => s.TrialEndsAt >= earliestStart)
            .Select(s => new { s.TrialEndsAt, s.MollieMandateSignedAt })
            .ToListAsync();

        var buckets = new List<object>();
        for (var i = 0; i < periods; i++)
        {
            var start = PeriodStart(i);
            var end = PeriodEnd(start);

            var periodInvoices = invoices.Where(inv => inv.IssueDate >= start && inv.IssueDate < end).ToList();
            var realizedRevenue = periodInvoices.Sum(inv => inv.Amount);

            var churned = cancellations.Count(c => c >= start && c < end);
            // Zahlende Tenants zu Periodenbeginn: Mandat vor Periodenbeginn signiert und eine
            // etwaige Kündigung (falls vorhanden) liegt nicht vor Periodenbeginn.
            var activeAtStart = everPaying.Count(s => s.MollieMandateSignedAt < start && (s.CancelledAt == null || s.CancelledAt >= start));
            decimal? churnRate = activeAtStart > 0 ? Math.Round(100m * churned / activeAtStart, 1) : null;

            var cohort = trialEndCohorts.Where(t => t.TrialEndsAt >= start && t.TrialEndsAt < end).ToList();
            var converted = cohort.Count(t => t.MollieMandateSignedAt != null);
            decimal? conversionRate = cohort.Count > 0 ? Math.Round(100m * converted / cohort.Count, 1) : null;

            buckets.Add(new
            {
                PeriodStart = start,
                PeriodEnd = end,
                Label = isWeekly ? $"KW {System.Globalization.ISOWeek.GetWeekOfYear(start)}" : start.ToString("MMM yy", culture),
                RealizedRevenue = realizedRevenue,
                InvoiceCount = periodInvoices.Count,
                ChurnedTenants = churned,
                ActiveTenantsAtStart = activeAtStart,
                ChurnRatePercent = churnRate,
                TrialsEnded = cohort.Count,
                Converted = converted,
                ConversionRatePercent = conversionRate,
            });
        }

        return Ok(new { Granularity = isWeekly ? "week" : "month", Periods = periods, Buckets = buckets });
    }

    /// <summary>
    /// KI-Nutzungskosten der letzten 30 Tage, pro Tenant aufgeschlüsselt — Grundlage, um
    /// individuell verhandelte Agency-Preise (Subscription.NegotiatedMonthlyPrice) anhand der
    /// tatsächlich entstandenen OpenAI-Kosten zu kalibrieren/nachzuverhandeln.
    /// </summary>
    [HttpGet("ai-usage")]
    public async Task<IActionResult> GetAiUsage()
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var since = DateTime.UtcNow.AddDays(-30);
        var usageByTenant = await _db.AiUsages
            .Where(u => u.CreatedOn >= since)
            .GroupBy(u => u.TenantId)
            .Select(g => new
            {
                TenantId = g.Key,
                TotalCost = g.Sum(u => u.EstimatedCost),
                TotalCalls = g.Count(),
                InputTokens = g.Sum(u => u.InputTokens),
                OutputTokens = g.Sum(u => u.OutputTokens),
            })
            .OrderByDescending(x => x.TotalCost)
            .ToListAsync();

        var tenantIds = usageByTenant.Select(u => u.TenantId).ToList();
        var tenantNames = await _db.Tenants
            .Include(t => t.Settings)
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Settings?.CompanyName ?? t.Name);

        return Ok(new
        {
            since,
            totalCostLast30Days = usageByTenant.Sum(u => u.TotalCost),
            tenants = usageByTenant.Select(u => new
            {
                u.TenantId,
                TenantName = tenantNames.GetValueOrDefault(u.TenantId, "Unbekannt"),
                u.TotalCost,
                u.TotalCalls,
                u.InputTokens,
                u.OutputTokens,
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

    // ── Invoices ──────────────────────────────────────────────────────────

    /// <summary>All GentleBook-issued subscription invoices, across all tenants.</summary>
    [HttpGet("invoices")]
    public async Task<IActionResult> GetInvoices(
        [FromQuery] Guid? tenantId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var query = _db.Invoices.Include(i => i.Tenant).ThenInclude(t => t.Settings).AsNoTracking().AsQueryable();

        if (tenantId.HasValue)
            query = query.Where(i => i.TenantId == tenantId.Value);

        var total = await query.CountAsync();
        var totalAmount = await query.SumAsync(i => i.Amount);

        var items = await query
            .OrderByDescending(i => i.IssueDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new
            {
                i.Id,
                i.TenantId,
                CompanyName = i.Tenant.Settings != null ? i.Tenant.Settings.CompanyName : i.Tenant.Name,
                TenantSlug = i.Tenant.Slug,
                i.InvoiceNumber,
                i.IssueDate,
                i.PeriodStart,
                i.PeriodEnd,
                i.PlanName,
                i.Amount,
                i.Currency,
                i.MolliePaymentId,
                i.EmailSent,
                i.EmailSentAt,
            })
            .ToListAsync();

        return Ok(new { items, totalCount = total, page, pageSize, totalAmount });
    }

    /// <summary>Downloads the PDF of a single invoice.</summary>
    [HttpGet("invoices/{id:guid}/pdf")]
    public async Task<IActionResult> GetInvoicePdf(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (invoice == null) return NotFound();

        return File(invoice.PdfContent, "application/pdf", $"Rechnung-{invoice.InvoiceNumber}.pdf");
    }

    /// <summary>Re-enqueues delivery of an invoice that was never (successfully) emailed.</summary>
    [HttpPost("invoices/{id:guid}/resend")]
    public async Task<IActionResult> ResendInvoice(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (invoice == null) return NotFound();

        // Runs through InvoiceService, which carries the class-level [AutomaticRetry] — a
        // transient SMTP failure here gets Hangfire's normal backoff/retry, same as the
        // original send, instead of a one-shot inline attempt.
        _backgroundJobClient.Enqueue<InvoiceService>(s => s.ResendInvoiceEmailAsync(id, CancellationToken.None));
        await _audit.LogAsync("invoice.resend_requested", "Invoice", id.ToString(), invoice.InvoiceNumber);

        return Ok(new { message = "Rechnungsversand wurde erneut angestoßen." });
    }

    // ── Plan Pricing ──────────────────────────────────────────────────────

    /// <summary>Current monthly price per paid plan (Trial excluded — always free).</summary>
    [HttpGet("plan-pricing")]
    public IActionResult GetPlanPricing()
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var items = PlanLimits.All
            .Where(kv => kv.Key != SubscriptionPlan.Trial)
            .OrderBy(kv => kv.Value.MonthlyPrice)
            .Select(kv => new
            {
                Plan = kv.Key.ToString(),
                kv.Value.DisplayName,
                kv.Value.MonthlyPrice,
                kv.Value.AnnualPrice,
                kv.Value.MaxEmployees,
                kv.Value.MaxServices,
            });

        return Ok(items);
    }

    /// <summary>Updates a plan's monthly and annual price. Takes effect immediately for new
    /// signups; tenants with an existing Mollie subscription keep the price they signed up at.</summary>
    [HttpPut("plan-pricing/{plan}")]
    public async Task<IActionResult> UpdatePlanPricing(string plan, [FromBody] UpdatePlanPriceDto dto)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        if (!Enum.TryParse<SubscriptionPlan>(plan, ignoreCase: true, out var planEnum) || planEnum == SubscriptionPlan.Trial)
            return BadRequest(new { message = "Ungültiger Plan." });

        if (dto.MonthlyPrice < 0 || dto.AnnualPrice < 0)
            return BadRequest(new { message = "Preis darf nicht negativ sein." });

        var row = await _db.PlanPrices.FirstOrDefaultAsync(p => p.Plan == planEnum);
        var oldLimits = PlanLimits.Get(planEnum);

        if (row == null)
        {
            row = new PlanPrice { Plan = planEnum, MonthlyPrice = dto.MonthlyPrice, AnnualPrice = dto.AnnualPrice, UpdatedAt = DateTime.UtcNow };
            _db.PlanPrices.Add(row);
        }
        else
        {
            row.MonthlyPrice = dto.MonthlyPrice;
            row.AnnualPrice = dto.AnnualPrice;
            row.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        PlanLimits.SetPrice(planEnum, dto.MonthlyPrice, dto.AnnualPrice);

        await _audit.LogAsync("plan_pricing.changed", "PlanPrice", planEnum.ToString(),
            $"Monat {oldLimits.MonthlyPrice:0.00}€→{dto.MonthlyPrice:0.00}€, Jahr {oldLimits.AnnualPrice:0.00}€→{dto.AnnualPrice:0.00}€");

        return Ok(new { plan = planEnum.ToString(), monthlyPrice = dto.MonthlyPrice, annualPrice = dto.AnnualPrice });
    }

    /// <summary>Tenants that are about to churn: pending cancellation (access runs out at period
    /// end) or in a payment-failure dunning grace period (auto-cancels after ~7 days).</summary>
    [HttpGet("at-risk-subscriptions")]
    public async Task<IActionResult> GetAtRiskSubscriptions()
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var now = DateTime.UtcNow;

        var cancelling = await _db.Subscriptions
            .Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .Where(s => s.CancelRequestedAt != null && s.Status != SubscriptionStatus.Cancelled)
            .OrderBy(s => s.CurrentPeriodEnd)
            .Select(s => new
            {
                s.TenantId,
                CompanyName = s.Tenant.Settings != null ? s.Tenant.Settings.CompanyName : s.Tenant.Name,
                TenantSlug = s.Tenant.Slug,
                Plan = s.Plan.ToString(),
                s.CancelRequestedAt,
                s.CancelReason,
                s.CurrentPeriodEnd,
            })
            .ToListAsync();

        var pastDue = await _db.Subscriptions
            .Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .Where(s => s.Status == SubscriptionStatus.PastDue && s.PastDueSince != null)
            .OrderBy(s => s.PastDueSince)
            .Select(s => new
            {
                s.TenantId,
                CompanyName = s.Tenant.Settings != null ? s.Tenant.Settings.CompanyName : s.Tenant.Name,
                TenantSlug = s.Tenant.Slug,
                Plan = s.Plan.ToString(),
                s.PastDueSince,
                s.FailedPaymentCount,
                s.DunningWarningEmailSent,
            })
            .ToListAsync();

        const int gracePeriodDays = 7;
        var dunning = pastDue.Select(s => new
        {
            s.TenantId,
            s.CompanyName,
            s.TenantSlug,
            s.Plan,
            s.PastDueSince,
            s.FailedPaymentCount,
            s.DunningWarningEmailSent,
            DaysUntilAutoCancel = Math.Max(0, gracePeriodDays - (int)(now - s.PastDueSince!.Value).TotalDays),
        });

        return Ok(new { cancelling, dunning, totalAtRisk = cancelling.Count + pastDue.Count });
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

    // ── Impersonate ───────────────────────────────────────────────────────────

    /// <summary>Generate a TenantAdmin JWT for the first admin of the given tenant (Superadmin only).</summary>
    [HttpPost("tenants/{tenantId:guid}/impersonate")]
    public async Task<IActionResult> ImpersonateTenant(Guid tenantId)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var tenant = await _db.Tenants.FindAsync(tenantId);
        if (tenant == null) return NotFound(new { message = "System nicht gefunden" });
        if (!tenant.IsActive) return BadRequest(new { message = "System ist deaktiviert" });

        var adminUser = await _db.PlatformUsers
            .Where(u => u.TenantId == tenantId && u.Role == PlatformRole.TenantAdmin)
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync();

        if (adminUser == null)
            return NotFound(new { message = "Kein Admin-User für dieses System gefunden" });

        var superAdminEmail = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? "superadmin";
        var token = _jwt.GenerateTenantAdminToken(adminUser.Id, adminUser.Email, tenant.Id, tenant.Slug, impersonatedBy: superAdminEmail);

        _logger.LogInformation("SuperAdmin {SuperAdmin} impersonated tenant {TenantId} ({TenantName}) as {Email}", superAdminEmail, tenantId, tenant.Name, adminUser.Email);
        await _audit.LogAsync("tenant.impersonated", "Tenant", tenantId.ToString(),
            $"Impersonation als {adminUser.Email} für System \"{tenant.Name}\"", tenantId);

        return Ok(new
        {
            access_token = token,
            tenant_slug = tenant.Slug,
            tenant_name = tenant.Name,
            user_email = adminUser.Email,
            user_id = adminUser.Id,
        });
    }

    // GET /api/superadmin/subscription-requests
    [HttpGet("subscription-requests")]
    public async Task<IActionResult> GetSubscriptionRequests([FromQuery] string? status = null)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var query = _db.SubscriptionRequests
            .Include(r => r.Tenant)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(r => r.Status == status);

        var requests = await query
            .OrderByDescending(r => r.Status == "Pending")
            .ThenByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                r.RequestedPlan,
                r.ContactEmail,
                r.Status,
                r.Note,
                r.Interval,
                r.OfferedMonthlyPrice,
                r.OfferedAnnualPrice,
                r.OfferedAt,
                r.OfferExpiresAt,
                r.AcceptedInterval,
                r.AcceptedPrice,
                r.AcceptedAt,
                r.AcceptedByEmail,
                r.CreatedAt,
                r.ProcessedAt,
                TenantId = r.TenantId,
                TenantName = r.Tenant.Name,
                TenantSlug = r.Tenant.Slug,
            })
            .ToListAsync();

        var pendingCount = requests.Count(r => r.Status == "Pending");

        return Ok(new { data = requests, pendingCount });
    }

    // POST /api/superadmin/subscription-requests/{id}/offer
    [HttpPost("subscription-requests/{id:guid}/offer")]
    public async Task<IActionResult> SendAgencyOffer(Guid id, [FromBody] AgencyOfferDto dto)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;
        if (dto.MonthlyPrice <= 0 || dto.AnnualPrice <= 0)
            return BadRequest(new { message = "Monats- und Jahrespreis müssen größer als 0 sein." });

        var request = await _db.SubscriptionRequests
            .Include(r => r.Tenant)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (request == null) return NotFound(new { message = "Anfrage nicht gefunden" });
        if (request.RequestedPlan != "Agency")
            return BadRequest(new { message = "Ein individuelles Angebot ist nur für Agency-Anfragen vorgesehen." });
        if (request.Status is not ("Pending" or "Offered"))
            return BadRequest(new { message = "Diese Anfrage kann nicht mehr angeboten werden." });

        var now = DateTime.UtcNow;
        request.OfferedMonthlyPrice = decimal.Round(dto.MonthlyPrice, 2);
        request.OfferedAnnualPrice = decimal.Round(dto.AnnualPrice, 2);
        request.OfferedAt = now;
        request.OfferExpiresAt = now.AddDays(Math.Clamp(dto.ValidForDays ?? 14, 1, 90));
        request.Status = "Offered";
        request.Note = string.IsNullOrWhiteSpace(dto.Note) ? request.Note : dto.Note.Trim()[..Math.Min(dto.Note.Trim().Length, 500)];
        await _db.SaveChangesAsync();

        await _audit.LogAsync("subscription.offer_sent", "SubscriptionRequest", id.ToString(),
            $"Agency-Angebot: {request.OfferedMonthlyPrice:0.00} EUR/Monat oder {request.OfferedAnnualPrice:0.00} EUR/Jahr; gültig bis {request.OfferExpiresAt:O}", request.TenantId);

        try
        {
            var admin = await _db.PlatformUsers
                .Where(u => u.TenantId == request.TenantId && u.Role == PlatformRole.TenantAdmin)
                .OrderBy(u => u.CreatedAt)
                .FirstOrDefaultAsync();
            await _emailService.SendAgencyOfferEmailAsync(
                request.ContactEmail,
                admin?.FirstName ?? "Kunde",
                request.Tenant.Name,
                request.OfferedMonthlyPrice.Value,
                request.OfferedAnnualPrice.Value,
                request.OfferExpiresAt.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Agency offer email for request {Id}", id);
            return StatusCode(502, new { message = "Das Angebot wurde gespeichert, aber die E-Mail konnte nicht versendet werden. Bitte prüfen Sie SMTP und senden Sie das Angebot erneut." });
        }

        return Ok(new { message = "Agency-Angebot wurde gespeichert und per E-Mail versendet." });
    }

    // POST /api/superadmin/subscription-requests/{id}/activate
    [HttpPost("subscription-requests/{id:guid}/activate")]
    public async Task<IActionResult> ActivateSubscriptionRequest(Guid id, [FromBody] ActivateRequestDto? dto = null)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var request = await _db.SubscriptionRequests
            .Include(r => r.Tenant)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (request == null) return NotFound(new { message = "Anfrage nicht gefunden" });
        if (request.Status != "Pending") return BadRequest(new { message = "Anfrage ist nicht mehr offen" });

        var plan = request.RequestedPlan switch
        {
            "Starter" => SubscriptionPlan.Starter,
            "Professional" => SubscriptionPlan.Professional,
            "Agency" => SubscriptionPlan.Agency,
            _ => (SubscriptionPlan?)null,
        };

        if (plan == null) return BadRequest(new { message = "Unbekannter Plan" });

        if (plan == SubscriptionPlan.Agency)
            return BadRequest(new { message = "Agency wird erst nach Annahme des Angebots und erfolgreicher Mollie-Zahlung automatisch aktiviert. Bitte senden Sie stattdessen ein Angebot." });

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == request.TenantId);
        if (sub?.MollieSubscriptionId != null && dto?.ConfirmOverrideMollie != true)
        {
            return Conflict(new { message = "Dieser Tenant hat ein aktives Mollie-Abonnement. Bitte mit ConfirmOverrideMollie=true bestätigen, um bewusst manuell zu überschreiben." });
        }

        // Agency has no fixed price ("Preis auf Anfrage") — require an individually negotiated
        // amount at activation time so billing/invoicing never silently falls back to the
        // internal PlanLimits list price.
        if (plan == SubscriptionPlan.Agency)
        {
            var hasExistingPrice = sub?.NegotiatedMonthlyPrice != null || sub?.NegotiatedAnnualPrice != null;
            var hasNewPrice = dto?.NegotiatedMonthlyPrice != null || dto?.NegotiatedAnnualPrice != null;
            if (!hasExistingPrice && !hasNewPrice)
                return BadRequest(new { message = "Für den Agency-Plan muss ein individuell verhandelter Preis (Monat oder Jahr) angegeben werden." });
        }

        if (sub != null)
        {
            sub.Plan = plan.Value;
            sub.Interval = request.Interval;
            if (plan == SubscriptionPlan.Agency)
            {
                if (dto?.NegotiatedMonthlyPrice != null) sub.NegotiatedMonthlyPrice = dto.NegotiatedMonthlyPrice;
                if (dto?.NegotiatedAnnualPrice != null) sub.NegotiatedAnnualPrice = dto.NegotiatedAnnualPrice;
            }
            sub.Status = SubscriptionStatus.Active;
            sub.CurrentPeriodStart = DateTime.UtcNow;
            sub.CurrentPeriodEnd = sub.Interval.AddInterval(DateTime.UtcNow);
            sub.CancelledAt = null;
            sub.CancelRequestedAt = null;
            sub.CancelReason = null;
            sub.RetentionEndsAt = null;
            sub.RetentionWarningEmailSent = false;
            sub.OperationalDataDeletedAt = null;
            sub.PastDueSince = null;
            sub.FailedPaymentCount = 0;
            sub.LastFailedMolliePaymentId = null;
            sub.DunningWarningEmailSent = false;
            request.Tenant.IsActive = true;
            request.Tenant.UpdatedAt = DateTime.UtcNow;
        }

        request.Status = "Activated";
        request.ProcessedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        _logger.LogInformation("SuperAdmin activated plan {Plan} for tenant {TenantId}", request.RequestedPlan, request.TenantId);
        var activationDetails = $"Plan {request.RequestedPlan} für \"{request.Tenant.Name}\" aktiviert";
        if (sub != null && sub.MollieSubscriptionId != null)
            activationDetails += " [ACHTUNG: manuell trotz aktivem Mollie-Abo überschrieben]";
        await _audit.LogAsync("subscription.request_activated", "SubscriptionRequest", id.ToString(),
            activationDetails, request.TenantId);

        // Kunden benachrichtigen (best effort)
        try
        {
            var admin = await _db.PlatformUsers
                .Where(u => u.TenantId == request.TenantId && u.Role == PlatformRole.TenantAdmin)
                .OrderBy(u => u.CreatedAt)
                .FirstOrDefaultAsync();
            if (admin != null)
            {
                var limits = PlanLimits.Get(plan.Value);
                var price = sub != null ? sub.Interval.PriceFor(sub, limits) : limits.MonthlyPrice;
                await _emailService.SendPlanActivatedEmailAsync(admin.Email, admin.FirstName, limits.DisplayName, price);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send plan-activated email for tenant {TenantId}", request.TenantId);
        }

        return Ok(new { message = $"Plan {request.RequestedPlan} für {request.Tenant.Name} aktiviert." });
    }

    // POST /api/superadmin/subscription-requests/{id}/decline
    [HttpPost("subscription-requests/{id:guid}/decline")]
    public async Task<IActionResult> DeclineSubscriptionRequest(Guid id)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        var request = await _db.SubscriptionRequests.FindAsync(id);
        if (request == null) return NotFound(new { message = "Anfrage nicht gefunden" });
        if (request.Status != "Pending") return BadRequest(new { message = "Anfrage ist nicht mehr offen" });

        request.Status = "Declined";
        request.ProcessedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        _logger.LogInformation("SuperAdmin declined subscription request {Id}", id);
        await _audit.LogAsync("subscription.request_declined", "SubscriptionRequest", id.ToString(),
            $"Plan-Anfrage {request.RequestedPlan} abgelehnt", request.TenantId);

        // Kunden benachrichtigen (best effort)
        try
        {
            var admin = await _db.PlatformUsers
                .Where(u => u.TenantId == request.TenantId && u.Role == PlatformRole.TenantAdmin)
                .OrderBy(u => u.CreatedAt)
                .FirstOrDefaultAsync();
            if (admin != null)
                await _emailService.SendPlanDeclinedEmailAsync(admin.Email, admin.FirstName, request.RequestedPlan);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send plan-declined email for request {Id}", id);
        }

        return Ok(new { message = "Anfrage abgelehnt." });
    }

    // ── Audit-Log ────────────────────────────────────────────────────────

    /// <summary>Paginated audit trail of administrative and security-relevant actions.</summary>
    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? tenantId = null,
        [FromQuery] string? action = null)
    {
        if (ForbidIfNotSuperAdmin() is { } err) return err;

        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();
        if (tenantId.HasValue) query = query.Where(a => a.TenantId == tenantId.Value);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action.StartsWith(action));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id, a.TenantId, a.ActorType, a.ActorName,
                a.Action, a.EntityType, a.EntityId, a.Details,
                a.IpAddress, a.CreatedAt,
            })
            .ToListAsync();

        return Ok(new { items, totalCount = total, page, pageSize });
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
    bool? SendWelcomeEmail = true,
    string? Plan = null,
    string? PersonalNote = null,
    string? Interval = null
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
public record SetDomainStatusDto(string Status);
public record ActivateRequestDto(string? Note, bool ConfirmOverrideMollie = false, decimal? NegotiatedMonthlyPrice = null, decimal? NegotiatedAnnualPrice = null);
public record AgencyOfferDto(decimal MonthlyPrice, decimal AnnualPrice, int? ValidForDays = 14, string? Note = null);
public record ChangePlanDto(string Plan, string? Interval = null, decimal? NegotiatedMonthlyPrice = null, decimal? NegotiatedAnnualPrice = null);
public record UpdatePlanPriceDto(decimal MonthlyPrice, decimal AnnualPrice);
