using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GentleBook.Api.Services;

public record MollieFlowResult(bool Success, string? Error, string? CheckoutUrl);

// Orchestrates DB state + MollieClient calls. Scoped — used directly by controllers/webhook,
// each within a single request's DbContext lifetime. The Hangfire-invoked reconciliation logic
// lives separately in MollieReconciliationJob (singleton, its own IServiceScopeFactory-based
// scope per run) so this class never has to reconcile the Scoped-vs-Singleton DbContext wrinkle.
public class MollieService
{
    private readonly GentleBookDbContext _db;
    private readonly MollieClient _mollie;
    private readonly IOptions<MollieOptions> _options;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly AuditService _audit;
    private readonly ILogger<MollieService> _logger;

    public MollieService(
        GentleBookDbContext db,
        MollieClient mollie,
        IOptions<MollieOptions> options,
        IBackgroundJobClient backgroundJobClient,
        AuditService audit,
        ILogger<MollieService> logger)
    {
        _db = db;
        _mollie = mollie;
        _options = options;
        _backgroundJobClient = backgroundJobClient;
        _audit = audit;
        _logger = logger;
    }

    public async Task<MollieFlowResult> StartMandateFlowAsync(Guid tenantId, string plan)
    {
        var tenant = await _db.Tenants.Include(t => t.Settings).FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant?.Settings == null)
            return new MollieFlowResult(false, "Tenant nicht gefunden.", null);

        if (!tenant.Settings.HasCompleteBillingProfile)
            return new MollieFlowResult(false, "Bitte zuerst die Rechnungsadresse (Firma, Straße, PLZ, Ort, Land) in den Einstellungen vervollständigen.", null);

        if (!Enum.TryParse<SubscriptionPlan>(plan, out var planEnum) || planEnum == SubscriptionPlan.Trial)
            return new MollieFlowResult(false, "Ungültiger Plan.", null);

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (sub == null)
            return new MollieFlowResult(false, "Kein Abonnement gefunden.", null);

        if (!string.IsNullOrEmpty(sub.MollieSubscriptionId))
            return new MollieFlowResult(false, "Es besteht bereits ein aktives Mollie-Abonnement.", null);

        // If there's an unresolved in-flight first payment, re-check it instead of creating a new one.
        if (!string.IsNullOrEmpty(sub.LastMolliePaymentId))
        {
            var pending = await _mollie.GetPaymentAsync(sub.LastMolliePaymentId);
            if (pending.Status is "open" or "pending")
                return new MollieFlowResult(false, "Es läuft bereits ein Zahlungsvorgang für dieses Abonnement. Bitte schließe diesen zuerst ab oder warte kurz.", null);
        }

        // Starting the Mollie flow supersedes any pending manual request.
        var pendingManual = await _db.SubscriptionRequests
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Status == "Pending");
        if (pendingManual != null)
        {
            pendingManual.Status = "Superseded";
            pendingManual.ProcessedAt = DateTime.UtcNow;
            pendingManual.Note = (pendingManual.Note ?? "") + " [Ersetzt durch Mollie-Zahlungsfluss]";
        }

        var limits = PlanLimits.Get(planEnum);

        var email = tenant.Settings.Email ?? "";
        if (string.IsNullOrEmpty(sub.MollieCustomerId))
        {
            var customer = await _mollie.CreateCustomerAsync(
                tenant.Settings.LegalCompanyName ?? tenant.Name, email, "de_DE");
            sub.MollieCustomerId = customer.Id;
        }

        var redirectUrl = $"{_options.Value.RedirectUrlBase.TrimEnd('/')}/admin/subscription?mollieReturn=1";
        var payment = await _mollie.CreateFirstPaymentAsync(
            sub.MollieCustomerId!,
            limits.MonthlyPrice,
            "EUR",
            $"GentleBook {limits.DisplayName}-Plan – Einrichtung SEPA-Mandat",
            redirectUrl,
            _options.Value.WebhookUrl,
            new Dictionary<string, string> { ["tenantId"] = tenantId.ToString(), ["plan"] = planEnum.ToString() });

        sub.LastMolliePaymentId = payment.Id;
        sub.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return new MollieFlowResult(true, null, payment.CheckoutUrl());
    }

    /// <summary>
    /// Called only by the webhook after it has independently re-fetched and confirmed the
    /// payment's real status from Mollie — never from unverified webhook payload data.
    /// </summary>
    public async Task ProcessPaymentEventAsync(MolliePayment payment)
    {
        // First-payment (mandate setup) path: matched by the payment id we stored ourselves.
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.LastMolliePaymentId == payment.Id);
        if (sub != null)
        {
            await HandleFirstPaymentResultAsync(sub, payment);
            return;
        }

        // Recurring-cycle path: matched by the Mollie subscription id already on file.
        if (!string.IsNullOrEmpty(payment.SubscriptionId))
        {
            sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.MollieSubscriptionId == payment.SubscriptionId);
            if (sub != null)
            {
                await HandleRecurringPaymentResultAsync(sub, payment);
                return;
            }
        }

        _logger.LogWarning("Mollie webhook: payment {PaymentId} did not match any known Subscription.", payment.Id);
        await _audit.LogAsync("mollie.webhook_unmatched_payment", "MolliePayment", payment.Id);
    }

    public async Task ProcessSubscriptionEventAsync(MollieSubscription subscription)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.MollieSubscriptionId == subscription.Id);
        if (sub == null)
        {
            _logger.LogWarning("Mollie webhook: subscription {SubscriptionId} did not match any known Subscription.", subscription.Id);
            return;
        }

        if (subscription.Status is "canceled" or "suspended" or "completed")
        {
            sub.Status = SubscriptionStatus.Cancelled;
            sub.CancelledAt = DateTime.UtcNow;
            sub.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _audit.LogAsync("mollie.subscription_ended", "Subscription", sub.Id.ToString(), subscription.Status, sub.TenantId);
        }
    }

    private async Task HandleFirstPaymentResultAsync(Subscription sub, MolliePayment payment)
    {
        if (payment.IsPaid)
        {
            if (string.IsNullOrEmpty(sub.MollieSubscriptionId) && !string.IsNullOrEmpty(payment.MandateId))
            {
                var limits = PlanLimits.Get(sub.Plan == SubscriptionPlan.Trial
                    ? ParsePlanFromMetadata(payment) : sub.Plan);

                var mollieSub = await _mollie.CreateSubscriptionAsync(
                    sub.MollieCustomerId!, payment.MandateId!, limits.MonthlyPrice, "EUR", "1 month",
                    $"GentleBook {limits.DisplayName}-Abonnement", _options.Value.WebhookUrl,
                    new Dictionary<string, string> { ["tenantId"] = sub.TenantId.ToString() });

                sub.Plan = ParsePlanFromMetadata(payment);
                sub.MollieMandateId = payment.MandateId;
                sub.MollieSubscriptionId = mollieSub.Id;
                sub.MollieMandateSignedAt = DateTime.UtcNow;
                sub.Status = SubscriptionStatus.Active;
                sub.CurrentPeriodStart = DateTime.UtcNow;
                sub.CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1);
                sub.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                await _audit.LogAsync("mollie.subscription_activated", "Subscription", sub.Id.ToString(), sub.Plan.ToString(), sub.TenantId);
                EnqueueCrmPush(sub.Id, payment.Id);
            }
        }
        else if (payment.IsFailedOrExpired)
        {
            await _audit.LogAsync("mollie.first_payment_failed", "Subscription", sub.Id.ToString(), payment.Status, sub.TenantId);
        }
    }

    private async Task HandleRecurringPaymentResultAsync(Subscription sub, MolliePayment payment)
    {
        if (payment.IsPaid)
        {
            sub.Status = SubscriptionStatus.Active;
            sub.CurrentPeriodStart = DateTime.UtcNow;
            sub.CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1);
            sub.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await _audit.LogAsync("mollie.recurring_payment_paid", "Subscription", sub.Id.ToString(), payment.Id, sub.TenantId);
            EnqueueCrmPush(sub.Id, payment.Id);
        }
        else if (payment.IsFailedOrExpired)
        {
            // Mollie retries failed SEPA collections itself — reflect the risk, don't cancel yet.
            sub.Status = SubscriptionStatus.PastDue;
            sub.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _audit.LogAsync("mollie.recurring_payment_failed", "Subscription", sub.Id.ToString(), payment.Id, sub.TenantId);
        }
    }

    private static SubscriptionPlan ParsePlanFromMetadata(MolliePayment payment) =>
        payment.Metadata != null && payment.Metadata.TryGetValue("plan", out var p) && Enum.TryParse<SubscriptionPlan>(p, out var parsed)
            ? parsed
            : SubscriptionPlan.Starter;

    private void EnqueueCrmPush(Guid subscriptionId, string molliePaymentId) =>
        _backgroundJobClient.Enqueue<CrmPushService>(s => s.PushSubscriptionPaymentAsync(subscriptionId, molliePaymentId, CancellationToken.None));
}
