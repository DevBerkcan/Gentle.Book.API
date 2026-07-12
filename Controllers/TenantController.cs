// Controllers/TenantController.cs
// TenantAdmin self-service endpoints: settings + subscription info
using GentleBook.Api.Configuration;
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
    private readonly EmailService _emailService;
    private readonly ILogger<TenantController> _logger;
    private readonly IWebHostEnvironment _env;

    public TenantController(GentleBookDbContext db, ITenantContext tenantContext, EmailService emailService, ILogger<TenantController> logger, IWebHostEnvironment env)
    {
        _db = db;
        _tenantContext = tenantContext;
        _emailService = emailService;
        _logger = logger;
        _env = env;
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

        var tenantId = _tenantContext.TenantId!.Value;

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

        return Ok(new { message = "Einstellungen gespeichert" });
    }

    // POST /api/tenant/logo
    [HttpPost("logo")]
    public async Task<IActionResult> UploadLogo(IFormFile logo)
    {
        var check = RequireTenantAdmin();
        if (check != null) return check;

        if (logo == null || logo.Length == 0)
            return BadRequest(new { message = "Keine Datei hochgeladen." });

        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "image/gif" };
        if (!allowedTypes.Contains(logo.ContentType.ToLower()))
            return BadRequest(new { message = "Nur JPG, PNG, WebP und GIF sind erlaubt." });

        if (logo.Length > 5 * 1024 * 1024)
            return BadRequest(new { message = "Die Datei darf maximal 5 MB groß sein." });

        var ext = Path.GetExtension(logo.FileName).ToLower();
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

    // GET /api/tenant/usage
    [HttpGet("usage")]
    public async Task<IActionResult> GetUsage()
    {
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

        var tenantId = _tenantContext.TenantId!.Value;

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

        var request = new SubscriptionRequest
        {
            TenantId = tenantId,
            RequestedPlan = dto.Plan,
            ContactEmail = email ?? "",
            Note = dto.Note,
        };

        _db.SubscriptionRequests.Add(request);
        await _db.SaveChangesAsync();

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
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "E-Mail konnte nicht gesendet werden.", detail = ex.Message });
        }
    }
}

public record SupportMessageRequest(string Subject, string Message);
public record SubscriptionRequestDto(string Plan, string ContactEmail, string? Note);

public record UpdateBusinessHoursItemDto(int DayOfWeek, bool IsOpen, string? OpenTime, string? CloseTime, string? BreakStartTime, string? BreakEndTime);

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
    decimal? CancellationFeePercent
);
