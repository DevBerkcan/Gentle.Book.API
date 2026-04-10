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
    /// Runs daily: sets Status = Expired for any Trial subscription whose TrialEndsAt has passed.
    /// </summary>
    public async Task ProcessExpiredTrialsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();

        var now = DateTime.UtcNow;

        var expired = await db.Subscriptions
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
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("Trial expiration check: {Count} trial(s) set to Expired.", expired.Count);
    }
}
