// Controllers/TenantController.cs
// TenantAdmin self-service endpoints: settings + subscription info
using System.Security.Cryptography;
using System.Text;
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
    private readonly ApiKeyService _apiKeyService;

    public TenantController(GentleBookDbContext db, ITenantContext tenantContext, EmailService emailService, ILogger<TenantController> logger, IWebHostEnvironment env, AuditService audit, MollieService mollieService, IOptions<MollieOptions> mollieOptions, ApiKeyService apiKeyService)
    {
        _db = db;
        _tenantContext = tenantContext;
        _emailService = emailService;
        _logger = logger;
        _env = env;
        _audit = audit;
        _mollieService = mollieService;
        _mollieOptions = mollieOptions;
        _apiKeyService = apiKeyService;
    }

    // featureLabel/featureKey default to the original api-keys wording so every existing call
    // site (RequireAgencyPlanAsync() with no args) keeps its exact current behavior; new Agency
    // features just pass their own label/key instead of duplicating this whole method.
    private async Task<IActionResult?> RequireAgencyPlanAsync(string featureLabel = "Der API-Zugang", string featureKey = "api_access")
    {
        var tenantId = _tenantContext.TenantId;
        if (!tenantId.HasValue) return Unauthorized(new { message = "Kein Tenant im Token" });

        var currentPlan = await _db.Subscriptions
            .Where(s => s.TenantId == tenantId.Value)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync() ?? SubscriptionPlan.Trial;

        var requiredPlanName = AgencyFeatureGate.ValidateForPlan(currentPlan);
        if (requiredPlanName != null)
        {
            // Same 402 shape as TrackingController.RequireAnalyticsPlanAsync — the frontend's
            // established upsell-banner pattern reads message/feature/currentPlan/requiredPlan.
            return StatusCode(402, new
            {
                message = $"{featureLabel} ist dem {requiredPlanName}-Plan vorbehalten.",
                feature = featureKey,
                upgrade = true,
                currentPlan = PlanLimits.Get(currentPlan).DisplayName,
                requiredPlan = requiredPlanName,
            });
        }

        return null;
    }

    // ── GET /api/tenant/api-keys ──────────────────────────────────
    [HttpGet("api-keys")]
    public async Task<IActionResult> GetApiKeys()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        var keys = await _apiKeyService.ListAsync(_tenantContext.TenantId!.Value);
        return Ok(keys);
    }

    // ── POST /api/tenant/api-keys ─────────────────────────────────
    [HttpPost("api-keys")]
    public async Task<IActionResult> CreateApiKey([FromBody] CreateApiKeyDto dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        if (string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest(new { message = "Bitte einen Namen für den Key angeben." });

        var created = await _apiKeyService.GenerateAsync(_tenantContext.TenantId!.Value, dto.Name);
        await _audit.LogAsync("api_key.created", "ApiKey", created.Id.ToString(), created.Name);

        // rawKey is only ever returned here, right after creation — never again.
        return Ok(new { id = created.Id, name = created.Name, rawKey = created.RawKey, keyPrefix = created.KeyPrefix, createdAt = created.CreatedAt });
    }

    // ── DELETE /api/tenant/api-keys/{id} ──────────────────────────
    [HttpDelete("api-keys/{id:guid}")]
    public async Task<IActionResult> RevokeApiKey(Guid id)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var revoked = await _apiKeyService.RevokeAsync(_tenantContext.TenantId!.Value, id);
        if (!revoked) return NotFound(new { message = "Key nicht gefunden oder bereits widerrufen." });

        await _audit.LogAsync("api_key.revoked", "ApiKey", id.ToString(), "");
        return Ok(new { message = "Key widerrufen." });
    }

    // ── GET /api/tenant/domain/resolve ──────────────────────────────
    // Anonymous on purpose: called from Next.js middleware on every request to a custom domain,
    // before the visitor has any tenant context of their own, to map Host header → tenant slug.
    [HttpGet("domain/resolve")]
    [AllowAnonymous]
    public async Task<IActionResult> ResolveDomain([FromQuery] string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return NotFound();

        var normalized = host.Trim().ToLowerInvariant();
        var match = await _db.TenantSettings.AsNoTracking()
            .Where(s => s.CustomDomain == normalized && s.CustomDomainStatus == "Verified")
            .Select(s => new { s.TenantId })
            .FirstOrDefaultAsync();
        if (match == null) return NotFound();

        var slug = await _db.Tenants.AsNoTracking().Where(t => t.Id == match.TenantId).Select(t => t.Slug).FirstOrDefaultAsync();
        if (slug == null) return NotFound();

        return Ok(new { slug });
    }

    // ── GET /api/tenant/domain ──────────────────────────────────────
    [HttpGet("domain")]
    public async Task<IActionResult> GetDomain()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (await RequireAgencyPlanAsync("Eine eigene Domain", "custom_domain") is { } deny) return deny;

        var settings = await _db.TenantSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == _tenantContext.TenantId!.Value);
        if (settings == null) return NotFound();

        return Ok(new
        {
            domain = settings.CustomDomain,
            status = settings.CustomDomainStatus,
            requestedAt = settings.CustomDomainRequestedAt,
        });
    }

    // ── PUT /api/tenant/domain ──────────────────────────────────────
    // Only records the request — no automatic Vercel provisioning yet (see plan). A SuperAdmin
    // adds the domain by hand in the Vercel dashboard and then flips the status via the
    // SuperAdmin endpoint below.
    [HttpPut("domain")]
    public async Task<IActionResult> UpdateDomain([FromBody] UpdateCustomDomainDto dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (await RequireAgencyPlanAsync("Eine eigene Domain", "custom_domain") is { } deny) return deny;

        var domain = dto.Domain?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain) || !Uri.CheckHostName(domain).Equals(UriHostNameType.Dns))
            return BadRequest(new { message = "Bitte eine gültige Domain angeben (z. B. buchung.deinefirma.de)." });

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == _tenantContext.TenantId!.Value);
        if (settings == null) return NotFound();

        settings.CustomDomain = domain;
        settings.CustomDomainStatus = "PendingVerification";
        settings.CustomDomainRequestedAt = DateTime.UtcNow;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("custom_domain.requested", "TenantSettings", settings.Id.ToString(), domain);

        return Ok(new { domain = settings.CustomDomain, status = settings.CustomDomainStatus, requestedAt = settings.CustomDomainRequestedAt });
    }

    // ── DELETE /api/tenant/domain ────────────────────────────────────
    [HttpDelete("domain")]
    public async Task<IActionResult> RemoveDomain()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == _tenantContext.TenantId!.Value);
        if (settings == null) return NotFound();

        settings.CustomDomain = null;
        settings.CustomDomainStatus = "None";
        settings.CustomDomainRequestedAt = null;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("custom_domain.removed", "TenantSettings", settings.Id.ToString(), "");

        return Ok(new { message = "Domain entfernt." });
    }

    // ── GET /api/tenant/digest ───────────────────────────────────────
    [HttpGet("digest")]
    public async Task<IActionResult> GetDigestFrequency()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (await RequireAgencyPlanAsync("Team-Reports", "admin_digest") is { } deny) return deny;

        var settings = await _db.TenantSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == _tenantContext.TenantId!.Value);
        if (settings == null) return NotFound();

        return Ok(new { frequency = settings.DigestFrequency });
    }

    // ── PUT /api/tenant/digest ───────────────────────────────────────
    [HttpPut("digest")]
    public async Task<IActionResult> UpdateDigestFrequency([FromBody] UpdateDigestFrequencyDto dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (await RequireAgencyPlanAsync("Team-Reports", "admin_digest") is { } deny) return deny;

        if (dto.Frequency is not ("None" or "Daily" or "Weekly"))
            return BadRequest(new { message = "Ungültige Frequenz. Erlaubt: None, Daily, Weekly." });

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == _tenantContext.TenantId!.Value);
        if (settings == null) return NotFound();

        settings.DigestFrequency = dto.Frequency;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { frequency = settings.DigestFrequency });
    }

    // ── GET /api/tenant/loyalty ───────────────────────────────────────
    [HttpGet("loyalty")]
    public async Task<IActionResult> GetLoyaltySettings()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (await RequireAgencyPlanAsync("Treuepunkte", "loyalty_points") is { } deny) return deny;

        var settings = await _db.TenantSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == _tenantContext.TenantId!.Value);
        if (settings == null) return NotFound();

        return Ok(new
        {
            pointsPerBooking = settings.LoyaltyPointsPerBooking,
            rewardEveryNVisits = settings.LoyaltyRewardEveryNVisits,
            rewardType = settings.LoyaltyRewardType,
            rewardValue = settings.LoyaltyRewardValue,
        });
    }

    // ── PUT /api/tenant/loyalty ───────────────────────────────────────
    [HttpPut("loyalty")]
    public async Task<IActionResult> UpdateLoyaltySettings([FromBody] UpdateLoyaltySettingsDto dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (await RequireAgencyPlanAsync("Treuepunkte", "loyalty_points") is { } deny) return deny;

        if (dto.PointsPerBooking < 0)
            return BadRequest(new { message = "Die Punktzahl darf nicht negativ sein." });
        if (dto.RewardEveryNVisits < 0)
            return BadRequest(new { message = "Die Besuchsanzahl darf nicht negativ sein." });
        if (dto.RewardEveryNVisits > 0 && dto.RewardType is not ("MonetaryValue" or "PercentageDiscount" or "SessionPackage"))
            return BadRequest(new { message = "Ungültiger Belohnungstyp." });
        if (dto.RewardEveryNVisits > 0 && (dto.RewardValue is null or <= 0))
            return BadRequest(new { message = "Bitte einen gültigen Belohnungswert angeben." });

        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == _tenantContext.TenantId!.Value);
        if (settings == null) return NotFound();

        settings.LoyaltyPointsPerBooking = dto.PointsPerBooking;
        settings.LoyaltyRewardEveryNVisits = dto.RewardEveryNVisits;
        settings.LoyaltyRewardType = dto.RewardType ?? "MonetaryValue";
        settings.LoyaltyRewardValue = dto.RewardValue;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new
        {
            pointsPerBooking = settings.LoyaltyPointsPerBooking,
            rewardEveryNVisits = settings.LoyaltyRewardEveryNVisits,
            rewardType = settings.LoyaltyRewardType,
            rewardValue = settings.LoyaltyRewardValue,
        });
    }

    // ── GET /api/tenant/location-admins ───────────────────────────
    [HttpGet("location-admins")]
    public async Task<IActionResult> GetLocationAdmins()
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var admins = await _db.PlatformUsers
            .Where(u => u.TenantId == _tenantContext.TenantId!.Value && u.Role == PlatformRole.LocationAdmin)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FirstName,
                u.LastName,
                u.LocationId,
                LocationName = u.Location != null ? u.Location.Name : null,
                u.IsActive,
                u.LastLoginAt,
            })
            .ToListAsync();

        return Ok(admins);
    }

    // ── POST /api/tenant/locations/{locationId}/admin ─────────────
    // Invites a new LocationAdmin (Agency-exclusive): creates the account with an unusable
    // random password hash and immediately sends a password-reset email so they set their own —
    // same token/email pattern as AuthController.ForgotPassword.
    [HttpPost("locations/{locationId:guid}/admin")]
    public async Task<IActionResult> InviteLocationAdmin(Guid locationId, [FromBody] InviteLocationAdminDto dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        var tenantId = _tenantContext.TenantId!.Value;
        var location = await _db.BusinessLocations.FirstOrDefaultAsync(l => l.Id == locationId && l.TenantId == tenantId);
        if (location == null) return NotFound(new { message = "Standort nicht gefunden." });

        if (string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.FirstName))
            return BadRequest(new { message = "E-Mail und Vorname sind erforderlich." });

        var email = dto.Email.Trim().ToLowerInvariant();
        if (await _db.PlatformUsers.AnyAsync(u => u.TenantId == tenantId && u.Email == email))
            return Conflict(new { message = "Diese E-Mail-Adresse wird bereits verwendet." });

        var randomPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var user = new PlatformUser
        {
            TenantId = tenantId,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(randomPassword, workFactor: 12),
            FirstName = dto.FirstName.Trim(),
            LastName = dto.LastName?.Trim() ?? "",
            Role = PlatformRole.LocationAdmin,
            LocationId = locationId,
            IsActive = true,
            MustChangePassword = true,
        };
        _db.PlatformUsers.Add(user);

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = hash,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsUsed = false,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var resetUrl = $"{_emailService.FrontendUrl}/admin/reset-password?token={rawToken}";
        try
        {
            await _emailService.SendPasswordResetEmailAsync(user.Email, user.FirstName, resetUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send LocationAdmin invite email to {Email}", user.Email);
        }

        await _audit.LogAsync("location_admin.invited", "PlatformUser", user.Id.ToString(), $"{user.Email} → {location.Name}");
        return Ok(new { message = "Standort-Admin eingeladen.", id = user.Id });
    }

    // ── DELETE /api/tenant/location-admins/{id} ────────────────────
    [HttpDelete("location-admins/{id:guid}")]
    public async Task<IActionResult> RemoveLocationAdmin(Guid id)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var user = await _db.PlatformUsers
            .FirstOrDefaultAsync(u => u.Id == id && u.TenantId == _tenantContext.TenantId!.Value && u.Role == PlatformRole.LocationAdmin);
        if (user == null) return NotFound(new { message = "Standort-Admin nicht gefunden." });

        user.IsActive = false;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("location_admin.removed", "PlatformUser", id.ToString(), user.Email);
        return Ok(new { message = "Standort-Admin entfernt." });
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

    // ── GET /api/tenant/invoices ───────────────────────────────────
    // Tenant self-service view of their own GentleBook subscription invoices — previously
    // only reachable via the SuperAdmin-only endpoint, so a lost/spam-filtered invoice email
    // left tenants with no way to pull their own copy.
    [HttpGet("invoices")]
    public async Task<IActionResult> GetInvoices([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var tenantId = _tenantContext.TenantId!.Value;
        var query = _db.Invoices.AsNoTracking().Where(i => i.TenantId == tenantId);

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(i => i.IssueDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new
            {
                i.Id,
                i.InvoiceNumber,
                i.IssueDate,
                i.PeriodStart,
                i.PeriodEnd,
                i.PlanName,
                i.Amount,
                i.Currency,
                i.EmailSent,
            })
            .ToListAsync();

        return Ok(new { items, totalCount = total, page, pageSize });
    }

    // ── GET /api/tenant/invoices/{id}/pdf ──────────────────────────
    [HttpGet("invoices/{id:guid}/pdf")]
    public async Task<IActionResult> GetInvoicePdf(Guid id)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        var invoice = await _db.Invoices.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == _tenantContext.TenantId!.Value);
        if (invoice == null) return NotFound();

        return File(invoice.PdfContent, "application/pdf", $"Rechnung-{invoice.InvoiceNumber}.pdf");
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
                IndustryType = tenant?.IndustryType.ToString(),
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
        if (activeMollieSub != null && dto.Plan != "Agency")
            return BadRequest(new { message = "Sie haben bereits ein Mollie-Abonnement — bitte verwalten Sie Ihren Plan über die Zahlungsseite." });

        var existing = await _db.SubscriptionRequests
            .FirstOrDefaultAsync(r => r.TenantId == tenantId &&
                (r.Status == "Pending" || r.Status == "Offered" || r.Status == "Accepted"));
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

        return Ok(new { message = "Anfrage erfolgreich gesendet. Bei Agency erhalten Sie zuerst ein verbindliches Angebot mit Monats- und Jahrespreis.", requestId = request.Id });
    }

    // POST /api/tenant/subscription-request/{id}/accept
    [HttpPost("subscription-request/{id:guid}/accept")]
    public async Task<IActionResult> AcceptAgencyOffer(Guid id, [FromBody] AcceptAgencyOfferDto dto)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;
        if (!dto.BusinessConfirmed || !dto.TermsAccepted || !dto.BillingTermsAccepted || !dto.PriceAccepted)
            return BadRequest(new { message = "Alle Vertrags- und Preisbestätigungen sind erforderlich." });
        if (!Enum.TryParse<SubscriptionInterval>(dto.Interval, true, out var interval))
            return BadRequest(new { message = "Bitte monatliche oder jährliche Abrechnung wählen." });

        var tenantId = _tenantContext.TenantId!.Value;
        var request = await _db.SubscriptionRequests.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId);
        if (request == null) return NotFound(new { message = "Angebot nicht gefunden." });
        if (request.RequestedPlan != "Agency" || request.Status is not ("Offered" or "Accepted"))
            return BadRequest(new { message = "Dieses Agency-Angebot kann nicht angenommen werden." });
        if (request.OfferExpiresAt == null || request.OfferExpiresAt < DateTime.UtcNow)
            return BadRequest(new { message = "Dieses Angebot ist abgelaufen. Bitte fordern Sie ein neues Angebot an." });

        var selectedPrice = interval == SubscriptionInterval.Yearly
            ? request.OfferedAnnualPrice
            : request.OfferedMonthlyPrice;
        if (selectedPrice == null || selectedPrice <= 0)
            return BadRequest(new { message = "Für das gewählte Intervall ist kein gültiger Preis hinterlegt." });

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (sub == null) return BadRequest(new { message = "Kein Abonnement gefunden." });

        sub.NegotiatedMonthlyPrice = request.OfferedMonthlyPrice;
        sub.NegotiatedAnnualPrice = request.OfferedAnnualPrice;
        request.Status = "Accepted";
        request.AcceptedInterval = interval;
        request.AcceptedPrice = selectedPrice;
        request.AcceptedAt = DateTime.UtcNow;
        request.AcceptedTermsVersion = LegalDocumentVersions.Terms;
        request.AcceptedByUserId = JwtService.GetUserId(User);
        request.AcceptedByEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value;
        request.AcceptedIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        var hasExistingMollieSubscription = !string.IsNullOrEmpty(sub.MollieSubscriptionId) && sub.Status == SubscriptionStatus.Active;
        if (hasExistingMollieSubscription)
        {
            var change = await _mollieService.ApplyAcceptedAgencyOfferAsync(tenantId, interval);
            if (!change.Success) return BadRequest(new { message = change.Error });
            request.Status = "Activated";
            request.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        else
        {
            var flow = await _mollieService.StartMandateFlowAsync(tenantId, "Agency", interval.ToString());
            if (!flow.Success) return BadRequest(new { message = flow.Error });

            await _audit.LogAsync("subscription.agency_offer_accepted", "SubscriptionRequest", request.Id.ToString(),
                $"Agency {interval}, {selectedPrice:0.00} EUR, AGB {LegalDocumentVersions.Terms}, Preis/Unternehmer/Abrechnung bestätigt",
                tenantId);
            return Ok(new { checkoutUrl = flow.CheckoutUrl, activated = false });
        }

        await _audit.LogAsync("subscription.agency_offer_accepted", "SubscriptionRequest", request.Id.ToString(),
            $"Agency {interval}, {selectedPrice:0.00} EUR, AGB {LegalDocumentVersions.Terms}, Preis/Unternehmer/Abrechnung bestätigt; bestehendes Mollie-Mandat aktualisiert",
            tenantId);
        return Ok(new { checkoutUrl = (string?)null, activated = true });
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
            hasPendingRequest = latest.Status is "Pending" or "Offered" or "Accepted",
            request = new
            {
                latest.Id,
                latest.RequestedPlan,
                latest.Status,
                latest.Interval,
                latest.OfferedMonthlyPrice,
                latest.OfferedAnnualPrice,
                latest.OfferedAt,
                latest.OfferExpiresAt,
                latest.AcceptedInterval,
                latest.AcceptedPrice,
                latest.AcceptedAt,
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
public record AcceptAgencyOfferDto(
    string Interval,
    bool BusinessConfirmed = false,
    bool TermsAccepted = false,
    bool BillingTermsAccepted = false,
    bool PriceAccepted = false);
public record ChangeSubscriptionPlanDto(string Plan, string? Interval = null);
public record CreateApiKeyDto(string Name);
public record UpdateCustomDomainDto(string Domain);
public record UpdateDigestFrequencyDto(string Frequency);
public record UpdateLoyaltySettingsDto(int PointsPerBooking, int RewardEveryNVisits, string? RewardType, decimal? RewardValue);
public record InviteLocationAdminDto(string Email, string FirstName, string? LastName = null);
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
