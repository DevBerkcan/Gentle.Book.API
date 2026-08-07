// Services/AdminDigestService.cs
// Daily/weekly team-report digest (Agency). Reuses AdminService.GetDashboardStatisticsAsync by
// briefly pointing the shared, scoped ITenantContext at each tenant in turn before calling it —
// same DI scope, so the DbContext's global query filter (CurrentTenantId) picks it up correctly.
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class AdminDigestService
{
    private readonly GentleBookDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly AdminService _adminService;
    private readonly EmailService _emailService;
    private readonly ILogger<AdminDigestService> _logger;

    public AdminDigestService(GentleBookDbContext context, ITenantContext tenantContext, AdminService adminService, EmailService emailService, ILogger<AdminDigestService> logger)
    {
        _context = context;
        _tenantContext = tenantContext;
        _adminService = adminService;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task SendDigestsAsync()
    {
        var isMonday = DateTime.UtcNow.DayOfWeek == DayOfWeek.Monday;

        var agencyTenantIds = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.Plan == SubscriptionPlan.Agency)
            .Select(s => s.TenantId)
            .ToListAsync();
        if (agencyTenantIds.Count == 0) return;

        var dueTenants = await _context.TenantSettings.IgnoreQueryFilters()
            .Where(t => agencyTenantIds.Contains(t.TenantId) &&
                (t.DigestFrequency == "Daily" || (t.DigestFrequency == "Weekly" && isMonday)))
            .ToListAsync();
        if (dueTenants.Count == 0) return;

        var sent = 0;
        foreach (var settings in dueTenants)
        {
            var admin = await _context.PlatformUsers
                .Where(u => u.TenantId == settings.TenantId && u.Role == PlatformRole.TenantAdmin)
                .OrderBy(u => u.CreatedAt)
                .FirstOrDefaultAsync();
            var recipientEmail = admin?.Email ?? settings.Email;
            if (string.IsNullOrWhiteSpace(recipientEmail)) continue;

            _tenantContext.Set(settings.TenantId, null);
            var stats = await _adminService.GetDashboardStatisticsAsync(null);

            var frequencyLabel = settings.DigestFrequency == "Weekly" ? "Wochen" : "Tages";
            var ok = await _emailService.SendAdminDigestEmailAsync(
                settings.TenantId, recipientEmail, settings.CompanyName, settings.LogoUrl, settings.PrimaryColor, frequencyLabel, stats);

            if (ok) sent++;
        }

        _logger.LogInformation("Admin digests: {Sent}/{Total} sent across {TenantCount} Agency tenant(s).",
            sent, dueTenants.Count, agencyTenantIds.Count);
    }
}
