using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

// Hangfire-invoked safety net: catches subscriptions whose Mollie webhook was missed (e.g.
// GentleBook was down, or Mollie's delivery genuinely failed) by re-polling Mollie directly.
// Reuses MollieService's own state-transition methods so there is exactly one code path for
// "a payment resolved to status X, update GentleBook state" — regardless of whether that
// status was learned via webhook or via this reconciliation poll.
public class MollieReconciliationJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MollieReconciliationJob> _logger;

    public MollieReconciliationJob(IServiceScopeFactory scopeFactory, ILogger<MollieReconciliationJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ReconcileAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
        var mollie = scope.ServiceProvider.GetRequiredService<MollieClient>();
        var mollieService = scope.ServiceProvider.GetRequiredService<MollieService>();

        var now = DateTime.UtcNow;

        // Active subscriptions whose period has already ended (missed a recurring-payment webhook).
        var overdue = await db.Subscriptions
            .Where(s => s.MollieSubscriptionId != null
                     && s.Status == SubscriptionStatus.Active
                     && s.CurrentPeriodEnd != null
                     && s.CurrentPeriodEnd < now.AddDays(-2))
            .ToListAsync();

        // PastDue subscriptions — check whether Mollie's own retry has since succeeded (or given up).
        var pastDue = await db.Subscriptions
            .Where(s => s.MollieSubscriptionId != null && s.Status == SubscriptionStatus.PastDue)
            .ToListAsync();

        foreach (var sub in overdue.Concat(pastDue).DistinctBy(s => s.Id))
        {
            try
            {
                var payments = await mollie.GetSubscriptionPaymentsAsync(sub.MollieCustomerId!, sub.MollieSubscriptionId!);
                var latest = payments.OrderByDescending(p => p.Id).FirstOrDefault();
                if (latest != null)
                    await mollieService.ProcessPaymentEventAsync(latest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mollie reconciliation failed for Subscription {SubscriptionId}", sub.Id);
            }
        }

        _logger.LogInformation("Mollie reconciliation: checked {Count} subscription(s).", overdue.Count + pastDue.Count);
    }
}
