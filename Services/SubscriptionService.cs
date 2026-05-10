using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class SubscriptionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(IServiceScopeFactory scopeFactory, ILogger<SubscriptionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Runs daily: sets Status = Expired for any Trial subscription whose TrialEndsAt has passed,
    /// and sends a one-time expiration email to the TenantAdmin.
    /// </summary>
    public async Task ProcessExpiredTrialsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();

        var now = DateTime.UtcNow;

        var expired = await db.Subscriptions
            .Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .Where(s => s.Status == SubscriptionStatus.Trial && s.TrialEndsAt <= now)
            .ToListAsync();

        if (expired.Count == 0)
        {
            _logger.LogInformation("Trial expiration check: no expired trials.");
            return;
        }

        foreach (var sub in expired)
        {
            sub.Status = SubscriptionStatus.Expired;
            sub.UpdatedAt = now;
            _logger.LogInformation("Trial expired for TenantId={TenantId}", sub.TenantId);

            // Send expiration email to first TenantAdmin
            try
            {
                var admin = await db.PlatformUsers
                    .Where(u => u.TenantId == sub.TenantId && u.Role == PlatformRole.TenantAdmin)
                    .OrderBy(u => u.CreatedAt)
                    .FirstOrDefaultAsync();

                if (admin != null)
                {
                    var tenantSlug = sub.Tenant?.Slug ?? "";
                    await emailService.SendTrialExpiredEmailAsync(admin.Email, admin.FirstName, tenantSlug);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send trial expired email for TenantId={TenantId}", sub.TenantId);
            }
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("Trial expiration check: {Count} trial(s) set to Expired.", expired.Count);
    }

    /// <summary>
    /// Runs daily: sends warning emails to tenants whose trial ends in exactly 7 or 3 days.
    /// </summary>
    public async Task SendTrialWarningEmailsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();

        var today = DateTime.UtcNow.Date;
        var in7Days = today.AddDays(7);
        var in3Days = today.AddDays(3);

        // Find subscriptions expiring in exactly 7 or 3 days
        var upcoming = await db.Subscriptions
            .Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .Where(s => s.Status == SubscriptionStatus.Trial &&
                        (s.TrialEndsAt.Date == in7Days || s.TrialEndsAt.Date == in3Days))
            .ToListAsync();

        if (upcoming.Count == 0)
        {
            _logger.LogInformation("Trial warning emails: no trials expiring in 3 or 7 days.");
            return;
        }

        foreach (var sub in upcoming)
        {
            var daysLeft = (sub.TrialEndsAt.Date - today).Days;
            try
            {
                var admin = await db.PlatformUsers
                    .Where(u => u.TenantId == sub.TenantId && u.Role == PlatformRole.TenantAdmin)
                    .OrderBy(u => u.CreatedAt)
                    .FirstOrDefaultAsync();

                if (admin != null)
                {
                    var tenantSlug = sub.Tenant?.Slug ?? "";
                    await emailService.SendTrialExpiringEmailAsync(admin.Email, admin.FirstName, tenantSlug, daysLeft);
                    _logger.LogInformation("Trial warning email sent: TenantId={TenantId}, DaysLeft={DaysLeft}", sub.TenantId, daysLeft);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send trial warning email for TenantId={TenantId}", sub.TenantId);
            }
        }

        _logger.LogInformation("Trial warning emails: {Count} email(s) sent.", upcoming.Count);
    }
}
