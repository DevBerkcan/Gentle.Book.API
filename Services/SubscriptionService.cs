using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class SubscriptionService
{
    public const int RetentionPeriodDays = 30;
    private const int RetentionWarningDays = 7;
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
            sub.AccessRestrictedAt = now;
            sub.RetentionEndsAt = now.AddDays(RetentionPeriodDays);
            sub.RetentionWarningEmailSent = false;
            sub.UpdatedAt = now;
            if (sub.Tenant != null)
            {
                sub.Tenant.IsActive = false;
                sub.Tenant.UpdatedAt = now;
            }
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

    /// <summary>
    /// Runs daily: finalizes subscriptions whose tenant-initiated cancellation period has
    /// ended (Status -> Cancelled). Access stays available until this point.
    /// </summary>
    public async Task ProcessPendingCancellationsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();

        var now = DateTime.UtcNow;

        var due = await db.Subscriptions
            .Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .Where(s => s.CancelRequestedAt != null
                     && s.Status != SubscriptionStatus.Cancelled
                     && (s.CurrentPeriodEnd == null || s.CurrentPeriodEnd <= now))
            .ToListAsync();

        if (due.Count == 0)
        {
            _logger.LogInformation("Pending cancellations: none due.");
            return;
        }

        foreach (var sub in due)
        {
            sub.Status = SubscriptionStatus.Cancelled;
            sub.CancelledAt = now;
            sub.RetentionEndsAt = now.AddDays(RetentionPeriodDays);
            sub.RetentionWarningEmailSent = false;
            sub.UpdatedAt = now;
            sub.Tenant.IsActive = false;
            sub.Tenant.UpdatedAt = now;

            try
            {
                var admin = await db.PlatformUsers
                    .Where(u => u.TenantId == sub.TenantId && u.Role == PlatformRole.TenantAdmin)
                    .OrderBy(u => u.CreatedAt)
                    .FirstOrDefaultAsync();
                if (admin != null)
                {
                    var limits = PlanLimits.Get(sub.Plan);
                    await emailService.SendSubscriptionCancelledConfirmationAsync(admin.Email, admin.FirstName, limits.DisplayName, null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send final cancellation email for TenantId={TenantId}", sub.TenantId);
            }
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("Pending cancellations: {Count} subscription(s) finalized.", due.Count);
    }

    private const int DunningGracePeriodDays = 7;

    /// <summary>
    /// Runs daily: warns tenants on their first day PastDue, then auto-cancels the Mollie
    /// subscription and deactivates the account once the grace period has elapsed without
    /// a successful payment. Time-based (PastDueSince), not failure-count-based, because
    /// MollieReconciliationJob replays the same still-failing payment every hour.
    /// </summary>
    public async Task ProcessDunningAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
        var mollie = scope.ServiceProvider.GetRequiredService<MollieClient>();
        var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();

        var now = DateTime.UtcNow;

        var pastDue = await db.Subscriptions
            .Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .Where(s => s.Status == SubscriptionStatus.PastDue && s.PastDueSince != null)
            .ToListAsync();

        if (pastDue.Count == 0)
        {
            _logger.LogInformation("Dunning: no PastDue subscriptions.");
            return;
        }

        var warned = 0;
        var cancelled = 0;

        foreach (var sub in pastDue)
        {
            var daysPastDue = (now - sub.PastDueSince!.Value).TotalDays;

            var admin = await db.PlatformUsers
                .Where(u => u.TenantId == sub.TenantId && u.Role == PlatformRole.TenantAdmin)
                .OrderBy(u => u.CreatedAt)
                .FirstOrDefaultAsync();
            var limits = PlanLimits.Get(sub.Plan);

            if (!sub.DunningWarningEmailSent)
            {
                sub.DunningWarningEmailSent = true;
                await db.SaveChangesAsync();
                warned++;

                if (admin != null)
                {
                    try { await emailService.SendDunningWarningEmailAsync(admin.Email, admin.FirstName, limits.DisplayName, DunningGracePeriodDays); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to send dunning warning email for TenantId={TenantId}", sub.TenantId); }
                }
                continue; // give the tenant until the next day's run before considering cancel
            }

            if (daysPastDue < DunningGracePeriodDays) continue;

            try
            {
                if (!string.IsNullOrEmpty(sub.MollieSubscriptionId) && !string.IsNullOrEmpty(sub.MollieCustomerId))
                    await mollie.CancelSubscriptionAsync(sub.MollieCustomerId, sub.MollieSubscriptionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dunning auto-cancel: Mollie DELETE failed for Subscription {Id}, finalizing locally anyway.", sub.Id);
                // A PastDue subscription past its grace period must stop regardless of whether
                // Mollie's API is reachable right now.
            }

            sub.Status = SubscriptionStatus.Cancelled;
            sub.CancelledAt = now;
            sub.RetentionEndsAt = now.AddDays(RetentionPeriodDays);
            sub.RetentionWarningEmailSent = false;
            sub.UpdatedAt = now;
            sub.Tenant.IsActive = false;
            sub.Tenant.UpdatedAt = now;
            await db.SaveChangesAsync();
            cancelled++;

            await audit.LogAsync("subscription.dunning_auto_cancelled", "Subscription", sub.Id.ToString(),
                $"{sub.FailedPaymentCount} failed payment(s), {daysPastDue:F0} days past due", sub.TenantId);

            if (admin != null)
            {
                try { await emailService.SendDunningFinalCancellationEmailAsync(admin.Email, admin.FirstName, limits.DisplayName); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to send dunning final-cancellation email for TenantId={TenantId}", sub.TenantId); }
            }
        }

        _logger.LogInformation("Dunning: {Warned} warned, {Cancelled} auto-cancelled.", warned, cancelled);
    }

    /// <summary>
    /// Runs daily. Warns seven days before the retention deadline and irreversibly removes
    /// tenant-operational data after 30 days. The anonymized tenant/subscription shell and
    /// invoices remain so statutory accounting records and payment references stay intact.
    /// </summary>
    public async Task ProcessRetentionExpirationsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();
        var environment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var now = DateTime.UtcNow;

        var candidates = await db.Subscriptions
            .Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .Where(s => (s.Status == SubscriptionStatus.Cancelled || s.Status == SubscriptionStatus.Expired)
                     && s.RetentionEndsAt != null
                     && s.OperationalDataDeletedAt == null)
            .ToListAsync();

        foreach (var sub in candidates)
        {
            var remaining = sub.RetentionEndsAt!.Value - now;
            if (remaining > TimeSpan.Zero)
            {
                if (!sub.RetentionWarningEmailSent && remaining <= TimeSpan.FromDays(RetentionWarningDays))
                {
                    var admin = await db.PlatformUsers
                        .Where(u => u.TenantId == sub.TenantId && u.Role == PlatformRole.TenantAdmin && u.IsActive)
                        .OrderBy(u => u.CreatedAt)
                        .FirstOrDefaultAsync();

                    if (admin != null)
                    {
                        try
                        {
                            await emailService.SendRetentionDeletionWarningAsync(
                                admin.Email, admin.FirstName, sub.RetentionEndsAt.Value);
                            sub.RetentionWarningEmailSent = true;
                            sub.UpdatedAt = now;
                            await db.SaveChangesAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Retention warning failed for TenantId={TenantId}", sub.TenantId);
                        }
                    }
                }
                continue;
            }

            try
            {
                await PurgeOperationalTenantDataAsync(db, environment, sub, now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Retention purge failed for TenantId={TenantId}; remaining tenants will still be processed.",
                    sub.TenantId);
            }
        }
    }

    private async Task PurgeOperationalTenantDataAsync(
        GentleBookDbContext db,
        IWebHostEnvironment environment,
        Subscription sub,
        DateTime now)
    {
        var tenantId = sub.TenantId;
        var logoUrl = sub.Tenant.Settings?.LogoUrl;

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            // AI and brand-import data, children before parents.
            await db.AiMessages.Where(m => db.AiConversations
                .Where(c => c.TenantId == tenantId).Select(c => c.Id).Contains(m.ConversationId))
                .ExecuteDeleteAsync();
            await db.AiActions.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.AiUsages.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.AiKnowledgeChunks.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.AiKnowledgeDocuments.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.AiKnowledgeSources.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.AiConversations.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ServiceFinderBookingDrafts.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ServiceGuidances.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ServiceFinderRules.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ServiceFinderQuestions.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.TenantIndustryCapabilities.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.TenantIndustrySettings.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();

            await db.BrandAssetCandidates.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.BrandThemeProposals.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.BrandImportResults.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.BrandImportJobs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();

            // Booking and business data, restrictive dependants first.
            await db.EmailLogs.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.WaitlistEntries.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.EmployeeNotes.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.Bookings.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.BlockedTimeSlots.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.EmployeeVacations.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.EmployeeSchedules.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ServiceEmployees.IgnoreQueryFilters()
                .Where(x => x.Service.TenantId == tenantId || x.Employee.TenantId == tenantId)
                .ExecuteDeleteAsync();
            await db.Services.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.BusinessLocations.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ServiceCategories.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.Customers.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.BusinessHours.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.TenantLinks.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();

            await db.PasswordResetTokens
                .Where(x => x.User.TenantId == tenantId)
                .ExecuteDeleteAsync();
            await db.PlatformUsers.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.SubscriptionRequests.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.TenantSettings.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.AuditLogs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();

            sub.Tenant.Name = $"Gelöschter Mandant {tenantId:N}";
            sub.Tenant.Slug = $"deleted-{tenantId:N}";
            sub.Tenant.IndustryType = IndustryType.Other;
            sub.Tenant.IsActive = false;
            sub.Tenant.UpdatedAt = now;
            sub.CancelReason = null;
            sub.OperationalDataDeletedAt = now;
            sub.UpdatedAt = now;

            db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId,
                ActorType = "System",
                Action = "tenant.operational_data_deleted",
                EntityType = "Tenant",
                EntityId = tenantId.ToString(),
                Details = "Operative Daten nach Ablauf der 30-tägigen Aufbewahrungsfrist gelöscht oder anonymisiert.",
                CreatedAt = now
            });

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            DeleteUploadedLogo(environment.WebRootPath, logoUrl);
            _logger.LogInformation("Retention purge completed for TenantId={TenantId}", tenantId);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Retention purge failed for TenantId={TenantId}", tenantId);
            throw;
        }
    }

    private void DeleteUploadedLogo(string? webRootPath, string? logoUrl)
    {
        if (string.IsNullOrWhiteSpace(webRootPath) || string.IsNullOrWhiteSpace(logoUrl) ||
            !logoUrl.StartsWith("/uploads/logos/", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var uploadsRoot = Path.GetFullPath(Path.Combine(webRootPath, "uploads", "logos"));
            var candidate = Path.GetFullPath(Path.Combine(webRootPath, logoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (candidate.StartsWith(uploadsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                File.Delete(candidate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete retained logo {LogoUrl}", logoUrl);
        }
    }
}
