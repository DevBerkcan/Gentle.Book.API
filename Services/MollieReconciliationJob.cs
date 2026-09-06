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

        // First-time signups: mandate/first-payment initiated but never resolved locally — most
        // likely a dropped/missed webhook for the "paid" status. Self-heals a customer who paid
        // but never got activated, without needing them to retry the checkout themselves.
        var stuckSignups = await db.Subscriptions
            .Where(s => s.MollieSubscriptionId == null
                     && s.LastMolliePaymentId != null
                     && s.Status != SubscriptionStatus.Cancelled)
            .ToListAsync();

        foreach (var sub in stuckSignups)
        {
            try
            {
                var payment = await mollie.GetPaymentAsync(sub.LastMolliePaymentId!);
                await mollieService.ProcessPaymentEventAsync(payment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mollie reconciliation (stuck signup) failed for Subscription {SubscriptionId}", sub.Id);
            }
        }

        var mandateChecked = await CheckActiveMandatesAsync(db, mollie, scope.ServiceProvider.GetRequiredService<AuditService>());

        _logger.LogInformation("Mollie reconciliation: checked {Count} subscription(s).", overdue.Count + pastDue.Count + stuckSignups.Count + mandateChecked);
    }

    // Actively verifies each Active subscription's SEPA mandate is still valid at Mollie —
    // previously GentleBook only found out about a revoked mandate at the next failed charge,
    // which could be up to a full billing cycle late. Reuses the existing PastDue/dunning
    // machinery (SubscriptionService.ProcessDunningAsync) instead of a separate cancellation
    // path, so a revoked mandate is handled exactly like any other failed-payment episode.
    public async Task<int> CheckActiveMandatesAsync(GentleBookDbContext db, MollieClient mollie, AuditService audit)
    {
        var toCheck = await db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active
                     && s.MollieCustomerId != null
                     && s.MollieMandateId != null)
            .ToListAsync();

        foreach (var sub in toCheck)
        {
            try
            {
                var mandate = await mollie.GetMandateAsync(sub.MollieCustomerId!, sub.MollieMandateId!);
                if (mandate != null && mandate.Status != "valid")
                {
                    sub.Status = SubscriptionStatus.PastDue;
                    sub.PastDueSince ??= DateTime.UtcNow;
                    sub.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();

                    _logger.LogWarning("Mollie mandate {MandateId} for Subscription {SubscriptionId} is {Status} — flagged PastDue.", sub.MollieMandateId, sub.Id, mandate.Status);
                    await audit.LogAsync("mollie.mandate_invalid_detected", "Subscription", sub.Id.ToString(), mandate.Status, sub.TenantId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mollie mandate check failed for Subscription {SubscriptionId}", sub.Id);
            }
        }

        return toCheck.Count;
    }
}
