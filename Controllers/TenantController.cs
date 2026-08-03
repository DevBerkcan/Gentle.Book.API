// Controllers/TenantController.cs
// TenantAdmin self-service endpoints: settings + subscription info
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/tenant")]
[Authorize]
public class TenantController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly EmailService _emailService;
    private readonly ILogger<TenantController> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly AuditService _audit;
    private readonly MollieService _mollieService;
    private readonly IOptions<MollieOptions> _mollieOptions;

    public TenantController(GentleBookDbContext db, ITenantContext tenantContext, EmailService emailService, ILogger<TenantController> logger, IWebHostEnvironment env, AuditService audit, MollieService mollieService, IOptions<MollieOptions> mollieOptions)
    {
        _db = db;
        _tenantContext = tenantContext;
        _emailService = emailService;
        _logger = logger;
        _env = env;
        _audit = audit;
        _mollieService = mollieService;
        _mollieOptions = mollieOptions;
    }

    // Mollie test-mode keys always start with "test_"; live keys with "live_".
    // Real customers must never be routed to a Mollie test-mode checkout page.
    private bool IsMollieLiveMode() =>
        _mollieOptions.Value.ApiKey.StartsWith("live_", StringComparison.Ordinal);

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
        if (!_tenantContext.TenantId.HasValue)
            return Unauthorized(new { message = "Kein Tenant im Token" });

        var tenantId = _tenantContext.TenantId.Value;

        var settings = await _db.TenantSettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);

        if (settings == null)
            return Ok(new { data = (object?)null, message = "Keine Einstellungen vorhanden" });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);

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
                settings.LegalCompanyName,
                settings.BillingStreet,
                settings.BillingZipCode,
                settings.BillingCity,
                settings.BillingCountry,
                settings.VatId,
                settings.HasCompleteBillingProfile,
                settings.BookingIntervalMinutes,
                settings.MaxAdvanceBookingDays,
                settings.TimeZone,
                settings.DefaultCurrency,
                settings.WelcomeMessage,
                settings.CancellationPolicy,
                settings.CancellationHoursNotice,
                settings.CancellationFeePercent,
                settings.LinktreeStyle,
                settings.LinktreeConfig,
                Slug = tenant?.Slug,
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

        // The frontend already greys out/locks templates the tenant's plan doesn't cover
        // (app/admin/links/page.tsx), but that's client-side only and trivially bypassable
        // via a direct call to this endpoint — mirror the same plan check here.
        if (!string.IsNullOrWhiteSpace(dto.LinktreeConfig))
        {
            string? requestedTemplate = null;
            try
            {
                using var parsed = System.Text.Json.JsonDocument.Parse(dto.LinktreeConfig);
                if (parsed.RootElement.TryGetProperty("pageTemplate", out var templateProp) && templateProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    requestedTemplate = templateProp.GetString();
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed JSON is caught below by the existing free-form assignment — not this check's job.
            }

            if (requestedTemplate != null)
            {
                var tenantPlan = await _db.Subscriptions
                    .Where(s => s.TenantId == tenantId)
                    .Select(s => s.Plan)
                    .FirstOrDefaultAsync();

                var requiredPlanName = LinkPageTemplates.ValidateTemplateForPlan(requestedTemplate, tenantPlan);
                if (requiredPlanName != null)
                    return StatusCode(402, new { message = $"Dieses Template ist ab dem Tarif \"{requiredPlanName}\" verfügbar.", feature = "linkPageTemplate", upgrade = true, requiredPlan = requiredPlanName });
            }
        }

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
        settings.LegalCompanyName = dto.LegalCompanyName?.Trim() ?? settings.LegalCompanyName;
        settings.BillingStreet = dto.BillingStreet?.Trim() ?? settings.BillingStreet;
        settings.BillingZipCode = dto.BillingZipCode?.Trim() ?? settings.BillingZipCode;
        settings.BillingCity = dto.BillingCity?.Trim() ?? settings.BillingCity;
        settings.BillingCountry = dto.BillingCountry?.Trim().ToUpper() ?? settings.BillingCountry;
        settings.VatId = dto.VatId?.Trim() ?? settings.VatId;
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
        {
            var currency = dto.DefaultCurrency.Trim().ToUpperInvariant();
            if (currency.Length != 3)
                return BadRequest(new { message = "Die Währung muss ein gültiger dreistelliger ISO-Code sein." });

            settings.DefaultCurrency = currency;

            var defaultLocation = await _db.BusinessLocations
                .FirstOrDefaultAsync(location => location.TenantId == tenantId && location.IsDefault);
            if (defaultLocation != null)
            {
                defaultLocation.Currency = currency;
                defaultLocation.UpdatedAt = DateTime.UtcNow;
            }

            // The legacy setting represents the default location. Other locations
            // retain their own currencies.
            await _db.Services
                .Where(s => s.TenantId == tenantId
                    && (s.LocationId == null || (defaultLocation != null && s.LocationId == defaultLocation.Id))
                    && s.Currency != currency)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(s => s.Currency, currency)
                    .SetProperty(s => s.UpdatedAt, DateTime.UtcNow));
        }
        if (dto.CancellationHoursNotice.HasValue && dto.CancellationHoursNotice.Value >= 0)
            settings.CancellationHoursNotice = dto.CancellationHoursNotice.Value;
        if (dto.CancellationFeePercent.HasValue && dto.CancellationFeePercent.Value >= 0)
            settings.CancellationFeePercent = dto.CancellationFeePercent.Value;

        var validStyles = new[] { "gradient", "dark", "minimal", "bold", "glass" };
        if (!string.IsNullOrWhiteSpace(dto.LinktreeStyle) && validStyles.Contains(dto.LinktreeStyle))
            settings.LinktreeStyle = dto.LinktreeStyle;

        if (dto.LinktreeConfig != null)
            settings.LinktreeConfig = dto.LinktreeConfig;

        settings.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await _audit.LogAsync("settings.updated", "TenantSettings", settings.Id.ToString(), "Studio-Einstellungen geändert");

        return Ok(new { message = "Einstellungen gespeichert" });
    }

    // GET /api/tenant/locations
    [HttpGet("locations")]
    public async Task<IActionResult> GetLocations()
    {
        if (!_tenantContext.TenantId.HasValue)
            return Unauthorized(new { message = "Kein Tenant im Token" });

        var tenantId = _tenantContext.TenantId.Value;
        var locations = await _db.BusinessLocations
            .Where(location => location.TenantId == tenantId)
            .OrderByDescending(location => location.IsDefault)
            .ThenBy(location => location.Name)
            .Select(location => new BusinessLocationResponse(
                location.Id,
                location.Name,
                location.Street,
                location.PostalCode,
                location.City,
                location.CountryCode,
                location.Currency,
                location.TimeZone,
                location.IsDefault,
                location.IsActive,
                location.Services.Count))
            .ToListAsync();

        return Ok(new { data = locations });
    }

    // POST /api/tenant/locations
    [HttpPost("locations")]
    public async Task<IActionResult> CreateLocation([FromBody] UpsertBusinessLocationRequest dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var validation = ValidateLocation(dto);
        if (validation != null) return validation;

        var tenantId = _tenantContext.TenantId!.Value;
        var hasLocations = await _db.BusinessLocations.AnyAsync(location => location.TenantId == tenantId);
        var makeDefault = dto.IsDefault || !hasLocations;

        if (makeDefault)
        {
            await _db.BusinessLocations
                .Where(location => location.TenantId == tenantId && location.IsDefault)
                .ExecuteUpdateAsync(updates => updates.SetProperty(location => location.IsDefault, false));
        }

        var location = new BusinessLocation
        {
            TenantId = tenantId,
            Name = dto.Name.Trim(),
            Street = NullIfWhiteSpace(dto.Street),
            PostalCode = NullIfWhiteSpace(dto.PostalCode),
            City = dto.City.Trim(),
            CountryCode = dto.CountryCode.Trim().ToUpperInvariant(),
            Currency = dto.Currency.Trim().ToUpperInvariant(),
            TimeZone = dto.TimeZone.Trim(),
            IsDefault = makeDefault,
            IsActive = dto.IsActive,
        };

        _db.BusinessLocations.Add(location);
        await ApplyDefaultLocationSettingsAsync(location);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetLocations), new { id = location.Id }, new { data = location.Id });
    }

    // PUT /api/tenant/locations/{id}
    [HttpPut("locations/{id:guid}")]
    public async Task<IActionResult> UpdateLocation(Guid id, [FromBody] UpsertBusinessLocationRequest dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var validation = ValidateLocation(dto);
        if (validation != null) return validation;

        var tenantId = _tenantContext.TenantId!.Value;
        var location = await _db.BusinessLocations
            .FirstOrDefaultAsync(item => item.Id == id && item.TenantId == tenantId);
        if (location == null) return NotFound(new { message = "Standort nicht gefunden." });

        var makeDefault = dto.IsDefault || location.IsDefault;
        if (makeDefault)
        {
            await _db.BusinessLocations
                .Where(item => item.TenantId == tenantId && item.Id != id && item.IsDefault)
                .ExecuteUpdateAsync(updates => updates.SetProperty(item => item.IsDefault, false));
        }

        location.Name = dto.Name.Trim();
        location.Street = NullIfWhiteSpace(dto.Street);
        location.PostalCode = NullIfWhiteSpace(dto.PostalCode);
        location.City = dto.City.Trim();
        location.CountryCode = dto.CountryCode.Trim().ToUpperInvariant();
        location.Currency = dto.Currency.Trim().ToUpperInvariant();
        location.TimeZone = dto.TimeZone.Trim();
        location.IsDefault = makeDefault;
        location.IsActive = dto.IsActive;
        location.UpdatedAt = DateTime.UtcNow;

        await _db.Services
            .Where(service => service.TenantId == tenantId && service.LocationId == location.Id)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(service => service.Currency, location.Currency)
                .SetProperty(service => service.UpdatedAt, DateTime.UtcNow));

        await ApplyDefaultLocationSettingsAsync(location);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // DELETE /api/tenant/locations/{id}
    [HttpDelete("locations/{id:guid}")]
    public async Task<IActionResult> DeleteLocation(Guid id)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var tenantId = _tenantContext.TenantId!.Value;
        var location = await _db.BusinessLocations
            .FirstOrDefaultAsync(item => item.Id == id && item.TenantId == tenantId);
        if (location == null) return NotFound(new { message = "Standort nicht gefunden." });
        if (location.IsDefault)
            return BadRequest(new { message = "Der Standardstandort kann nicht gelöscht werden." });
        if (await _db.Services.AnyAsync(service => service.LocationId == id))
            return BadRequest(new { message = "Der Standort ist noch Services zugeordnet." });

        _db.BusinessLocations.Remove(location);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private IActionResult? ValidateLocation(UpsertBusinessLocationRequest dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.City))
            return BadRequest(new { message = "Name und Ort sind erforderlich." });
        if (string.IsNullOrWhiteSpace(dto.CountryCode) || dto.CountryCode.Trim().Length != 2)
            return BadRequest(new { message = "Das Land muss ein zweistelliger ISO-Code sein." });
        if (string.IsNullOrWhiteSpace(dto.Currency) || dto.Currency.Trim().Length != 3)
            return BadRequest(new { message = "Die Währung muss ein dreistelliger ISO-Code sein." });
        if (string.IsNullOrWhiteSpace(dto.TimeZone))
            return BadRequest(new { message = "Die Zeitzone ist erforderlich." });
        return null;
    }

    private async Task ApplyDefaultLocationSettingsAsync(BusinessLocation location)
    {
        if (!location.IsDefault) return;
        var settings = await _db.TenantSettings.FirstOrDefaultAsync(item => item.TenantId == location.TenantId);
        if (settings == null) return;

        settings.DefaultCurrency = location.Currency;
        settings.TimeZone = location.TimeZone;
        settings.Address = string.Join(", ", new[]
        {
            location.Street,
            string.Join(" ", new[] { location.PostalCode, location.City }.Where(value => !string.IsNullOrWhiteSpace(value)))
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        settings.UpdatedAt = DateTime.UtcNow;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // POST /api/tenant/logo
    [HttpPost("logo")]
    public async Task<IActionResult> UploadLogo(IFormFile logo)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

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
        // client-supplied FileName — see SuperAdminController.UploadLogo for the same fix.
        var tenantId = _tenantContext.TenantId!.Value;
        var fileName = $"{tenantId}{ext}";
        var uploadDir = Path.Combine(_env.WebRootPath, "uploads", "logos");

        if (!Directory.Exists(uploadDir))
            Directory.CreateDirectory(uploadDir);

        var filePath = Path.Combine(uploadDir, fileName);
        try
        {
            using (var stream = new FileStream(filePath, FileMode.Create))
                await logo.CopyToAsync(stream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Logo upload failed for tenant {TenantId}, path {FilePath}", tenantId, filePath);
            return StatusCode(500, new { message = "Datei konnte nicht gespeichert werden. Bitte versuchen Sie es erneut." });
        }

        var logoUrl = $"/uploads/logos/{fileName}";

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (settings == null)
        {
            settings = new TenantSettings { TenantId = tenantId, CreatedAt = DateTime.UtcNow };
            _db.TenantSettings.Add(settings);
        }
        settings.LogoUrl = logoUrl;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { logoUrl });
    }

    // DELETE /api/tenant/logo
    [HttpDelete("logo")]
    public async Task<IActionResult> DeleteLogo()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var tenantId = _tenantContext.TenantId!.Value;
        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (settings == null || string.IsNullOrEmpty(settings.LogoUrl))
            return Ok(new { message = "Kein Logo vorhanden." });

        // Delete file from disk
        if (!string.IsNullOrEmpty(settings.LogoUrl))
        {
            var filePath = Path.Combine(_env.WebRootPath, settings.LogoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(filePath))
            {
                try { System.IO.File.Delete(filePath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete logo file {Path}", filePath); }
            }
        }

        settings.LogoUrl = null;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Logo gelöscht." });
    }

    // GET /api/tenant/business-hours
    [HttpGet("business-hours")]
    public async Task<IActionResult> GetBusinessHours()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var hours = await _db.BusinessHours
            .OrderBy(bh => bh.DayOfWeek)
            .ToListAsync();

        var result = hours.Select(bh => new
        {
            bh.Id,
            DayOfWeek = (int)bh.DayOfWeek,
            bh.DayName,
            bh.IsOpen,
            OpenTime = bh.OpenTime.ToString("HH:mm"),
            CloseTime = bh.CloseTime.ToString("HH:mm"),
            BreakStartTime = bh.BreakStartTime?.ToString("HH:mm"),
            BreakEndTime = bh.BreakEndTime?.ToString("HH:mm"),
        });

        return Ok(new { data = result });
    }

    // PUT /api/tenant/business-hours
    [HttpPut("business-hours")]
    public async Task<IActionResult> UpdateBusinessHours([FromBody] List<UpdateBusinessHoursItemDto> dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var tenantId = _tenantContext.TenantId!.Value;

        foreach (var item in dto)
        {
            if (item.DayOfWeek < 0 || item.DayOfWeek > 6)
                return BadRequest(new { message = "Ungültiger Wochentag" });

            if (!TimeOnly.TryParse(item.OpenTime ?? "09:00", out var openTime))
                return BadRequest(new { message = "Ungültige Öffnungszeit" });

            if (!TimeOnly.TryParse(item.CloseTime ?? "18:00", out var closeTime))
                return BadRequest(new { message = "Ungültige Schließzeit" });

            TimeOnly? breakStartTime = null;
            TimeOnly? breakEndTime = null;

            if (!string.IsNullOrWhiteSpace(item.BreakStartTime))
            {
                if (!TimeOnly.TryParse(item.BreakStartTime, out var parsedBreakStart))
                    return BadRequest(new { message = "Ungültiger Pausenbeginn" });
                breakStartTime = parsedBreakStart;
            }

            if (!string.IsNullOrWhiteSpace(item.BreakEndTime))
            {
                if (!TimeOnly.TryParse(item.BreakEndTime, out var parsedBreakEnd))
                    return BadRequest(new { message = "Ungültiges Pausenende" });
                breakEndTime = parsedBreakEnd;
            }

            if (item.IsOpen && closeTime <= openTime)
                return BadRequest(new { message = "Schließzeit muss nach der Öffnungszeit liegen" });

            if (breakStartTime.HasValue && breakEndTime.HasValue && breakEndTime.Value <= breakStartTime.Value)
                return BadRequest(new { message = "Pausenende muss nach dem Pausenbeginn liegen" });

            var existing = await _db.BusinessHours
                .FirstOrDefaultAsync(bh => bh.DayOfWeek == (DayOfWeek)item.DayOfWeek);

            if (existing == null)
            {
                existing = new BusinessHours { TenantId = tenantId, DayOfWeek = (DayOfWeek)item.DayOfWeek };
                _db.BusinessHours.Add(existing);
            }

            existing.IsOpen = item.IsOpen;
            existing.OpenTime = openTime;
            existing.CloseTime = closeTime;
            existing.BreakStartTime = breakStartTime;
            existing.BreakEndTime = breakEndTime;
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Öffnungszeiten gespeichert" });
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
                sub.Interval,
                sub.TrialStartedAt,
                sub.TrialEndsAt,
                sub.TrialDaysRemaining,
                sub.IsInTrial,
                sub.IsAccessAllowed,
                sub.CurrentPeriodStart,
                sub.CurrentPeriodEnd,
                sub.CancelledAt,
                sub.CancelRequestedAt,
                sub.MollieSubscriptionId,
            }
        });
    }

    // POST /api/tenant/subscription/cancel
    [HttpPost("subscription/cancel")]
    public async Task<IActionResult> CancelSubscription([FromBody] CancelSubscriptionRequestDto dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var result = await _mollieService.CancelSubscriptionAsync(_tenantContext.TenantId!.Value, dto.Reason);
        if (!result.Success)
            return BadRequest(new { message = result.Error });

        return Ok(new { cancelRequestedAt = result.CancelRequestedAt, currentPeriodEnd = result.CurrentPeriodEnd, message = result.Message });
    }

    // POST /api/tenant/subscription/mollie/start
    [HttpPost("subscription/mollie/start")]
    public async Task<IActionResult> StartMollieMandateFlow([FromBody] MollieStartRequestDto dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (!dto.BusinessConfirmed || !dto.TermsAccepted || !dto.BillingTermsAccepted)
            return BadRequest(new { message = "Alle Vertragsbestätigungen sind erforderlich." });

        var result = await _mollieService.StartMandateFlowAsync(_tenantContext.TenantId!.Value, dto.Plan, dto.Interval);
        if (!result.Success)
            return BadRequest(new { message = result.Error });

        await _audit.LogAsync("subscription.contract_confirmed", "Subscription", _tenantContext.TenantId!.Value.ToString(),
            $"Tarif {dto.Plan}, Intervall {dto.Interval ?? "Monthly"}, AGB {LegalDocumentVersions.Terms}, Unternehmer/Abrechnung bestätigt",
            _tenantContext.TenantId.Value);

        return Ok(new { checkoutUrl = result.CheckoutUrl });
    }

    // POST /api/tenant/subscription/change-plan
    // Upgrade/downgrade for a tenant that already has an active Mollie subscription —
    // StartMollieMandateFlow above only handles first-time signup. Updates the existing
    // Mollie subscription in place (no new mandate/checkout); downgrades are rejected if
    // current usage wouldn't fit the target plan's limits.
    [HttpPost("subscription/change-plan")]
    public async Task<IActionResult> ChangeSubscriptionPlan([FromBody] ChangeSubscriptionPlanDto dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var result = await _mollieService.ChangePlanAsync(_tenantContext.TenantId!.Value, dto.Plan, dto.Interval);
        if (!result.Success)
            return BadRequest(new
            {
                message = result.Error,
                currentEmployees = result.CurrentEmployees,
                employeeLimit = result.EmployeeLimit,
                currentServices = result.CurrentServices,
                serviceLimit = result.ServiceLimit,
            });

        return Ok(new { message = "Plan gewechselt.", plan = result.Plan, interval = result.Interval });
    }

    // GET /api/tenant/subscription/mollie/status
    [HttpGet("subscription/mollie/status")]
    public async Task<IActionResult> GetMollieStatus()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == _tenantContext.TenantId!.Value);
        if (sub == null) return NotFound(new { message = "Kein Abonnement gefunden" });

        return Ok(new
        {
            isLiveMode = IsMollieLiveMode(),
            plan = sub.Plan.ToString(),
            status = sub.Status.ToString(),
            hasMollieSubscription = sub.MollieSubscriptionId != null,
        });
    }

    // GET /api/tenant/plan-pricing
    // Current, live plan prices — reads the same PlanLimits cache the SuperAdmin pricing
    // editor writes to, so the checkout page never shows a stale price.
    [HttpGet("plan-pricing")]
    public IActionResult GetPlanPricing()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

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

    // GET /api/tenant/usage
    [HttpGet("usage")]
    public async Task<IActionResult> GetUsage()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var tenantId = _tenantContext.TenantId;
        if (!tenantId.HasValue) return Unauthorized();

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId.Value);
        var limits = PlanLimits.Get(sub?.Plan ?? SubscriptionPlan.Trial);

        var employeeCount = await _db.Employees.CountAsync(e => e.IsActive);
        var serviceCount = await _db.Services.CountAsync(s => s.IsActive);

        static object BuildMeter(int current, int limit) => new
        {
            current,
            limit,
            isUnlimited = PlanLimits.IsUnlimited(limit),
            percentage = PlanLimits.IsUnlimited(limit) ? 0 : (int)Math.Round(current * 100.0 / limit),
        };

        return Ok(new
        {
            plan = sub?.Plan.ToString() ?? "Trial",
            planDisplayName = limits.DisplayName,
            employees = BuildMeter(employeeCount, limits.MaxEmployees),
            services = BuildMeter(serviceCount, limits.MaxServices),
        });
    }

    // POST /api/tenant/subscription-request
    [HttpPost("subscription-request")]
    public async Task<IActionResult> RequestSubscription([FromBody] SubscriptionRequestDto dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (!dto.BusinessConfirmed || !dto.TermsAccepted || !dto.BillingTermsAccepted)
            return BadRequest(new { message = "Alle Vertragsbestätigungen sind erforderlich." });

        var tenantId = _tenantContext.TenantId!.Value;

        var activeMollieSub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.MollieSubscriptionId != null);
        if (activeMollieSub != null)
            return BadRequest(new { message = "Sie haben bereits ein Mollie-Abonnement — bitte verwalten Sie Ihren Plan über die Zahlungsseite." });

        var existing = await _db.SubscriptionRequests
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Status == "Pending");
        if (existing != null)
            return BadRequest(new { message = "Es gibt bereits eine offene Anfrage für dieses System." });

        var validPlans = new[] { "Starter", "Professional", "Agency" };
        if (!validPlans.Contains(dto.Plan))
            return BadRequest(new { message = "Ungültiger Plan." });

        var tenant = await _db.Tenants.FindAsync(tenantId);
        var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                 ?? User.FindFirst("email")?.Value
                 ?? dto.ContactEmail;

        var intervalEnum = Enum.TryParse<SubscriptionInterval>(dto.Interval, out var parsedInterval)
            ? parsedInterval
            : SubscriptionInterval.Monthly;

        var request = new SubscriptionRequest
        {
            TenantId = tenantId,
            RequestedPlan = dto.Plan,
            Interval = intervalEnum,
            ContactEmail = email ?? "",
            Note = dto.Note,
        };

        _db.SubscriptionRequests.Add(request);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("subscription.contract_confirmed", "SubscriptionRequest", request.Id.ToString(),
            $"Tarif {dto.Plan}, Intervall {intervalEnum}, AGB {LegalDocumentVersions.Terms}, Unternehmer/Abrechnung bestätigt",
            tenantId);

        var firstName = User.FindFirst("given_name")?.Value
                     ?? User.FindFirst("name")?.Value
                     ?? "Kunde";

        try
        {
            await _emailService.SendSubscriptionRequestConfirmationAsync(email ?? "", firstName, dto.Plan, tenant?.Name ?? "Ihr System");
            await _emailService.SendSubscriptionRequestNotificationAsync(dto.Plan, tenant?.Name ?? tenantId.ToString(), email ?? "", tenant?.Slug ?? "");
        }
        catch { /* E-Mails sind nicht geschäftskritisch */ }

        return Ok(new { message = "Anfrage erfolgreich gesendet. Wir aktivieren Ihren Plan innerhalb von 24 Stunden.", requestId = request.Id });
    }

    // GET /api/tenant/subscription-request/status
    [HttpGet("subscription-request/status")]
    public async Task<IActionResult> GetSubscriptionRequestStatus()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var tenantId = _tenantContext.TenantId!.Value;

        var latest = await _db.SubscriptionRequests
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync();

        if (latest == null)
            return Ok(new { hasPendingRequest = false, request = (object?)null });

        return Ok(new
        {
            hasPendingRequest = latest.Status == "Pending",
            request = new
            {
                latest.Id,
                latest.RequestedPlan,
                latest.Status,
                latest.CreatedAt,
                latest.ProcessedAt,
            }
        });
    }

    // POST /api/tenant/support
    [HttpPost("support")]
    public async Task<IActionResult> SendSupportMessage([FromBody] SupportMessageRequest dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        if (string.IsNullOrWhiteSpace(dto.Subject) || string.IsNullOrWhiteSpace(dto.Message))
            return BadRequest(new { message = "Betreff und Nachricht sind erforderlich." });

        var tenantId = _tenantContext.TenantId!.Value;

        var tenant   = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId);

        var companyName = settings?.CompanyName ?? tenant?.Slug ?? "Unbekannt";
        var tenantSlug  = tenant?.Slug ?? tenantId.ToString();

        var senderEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                       ?? User.FindFirst("email")?.Value
                       ?? "noreply@gentlegroup.de";
        var senderName  = User.FindFirst("name")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                       ?? companyName;

        try
        {
            await _emailService.SendSupportMessageAsync(
                tenantSlug, companyName, senderEmail, senderName,
                dto.Subject.Trim(), dto.Message.Trim());

            return Ok(new { message = "Nachricht erfolgreich gesendet." });
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "E-Mail konnte nicht gesendet werden." });
        }
    }
}

public record SupportMessageRequest(string Subject, string Message);
public record SubscriptionRequestDto(
    string Plan,
    string ContactEmail,
    string? Note,
    string? Interval = null,
    bool BusinessConfirmed = false,
    bool TermsAccepted = false,
    bool BillingTermsAccepted = false);
public record MollieStartRequestDto(
    string Plan,
    string? Interval = null,
    bool BusinessConfirmed = false,
    bool TermsAccepted = false,
    bool BillingTermsAccepted = false);
public record ChangeSubscriptionPlanDto(string Plan, string? Interval = null);
public record CancelSubscriptionRequestDto(string? Reason);

public record UpdateBusinessHoursItemDto(int DayOfWeek, bool IsOpen, string? OpenTime, string? CloseTime, string? BreakStartTime, string? BreakEndTime);
public record BusinessLocationResponse(
    Guid Id,
    string Name,
    string? Street,
    string? PostalCode,
    string City,
    string CountryCode,
    string Currency,
    string TimeZone,
    bool IsDefault,
    bool IsActive,
    int ServiceCount);
public record UpsertBusinessLocationRequest(
    string Name,
    string? Street,
    string? PostalCode,
    string City,
    string CountryCode,
    string Currency,
    string TimeZone,
    bool IsDefault,
    bool IsActive);

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
    string? DefaultCurrency,
    string? LinktreeStyle,
    string? LinktreeConfig,
    int? CancellationHoursNotice,
    decimal? CancellationFeePercent,
    string? LegalCompanyName,
    string? BillingStreet,
    string? BillingZipCode,
    string? BillingCity,
    string? BillingCountry,
    string? VatId
);
