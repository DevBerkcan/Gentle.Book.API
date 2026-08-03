using System.Text.Json;
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
    private readonly EmailService _emailService;
    private readonly ILogger<MollieService> _logger;

    public MollieService(
        GentleBookDbContext db,
        MollieClient mollie,
        IOptions<MollieOptions> options,
        IBackgroundJobClient backgroundJobClient,
        AuditService audit,
        EmailService emailService,
        ILogger<MollieService> logger)
    {
        _db = db;
        _mollie = mollie;
        _options = options;
        _backgroundJobClient = backgroundJobClient;
        _audit = audit;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<MollieFlowResult> StartMandateFlowAsync(Guid tenantId, string plan, string? interval = null)
    {
        var intervalEnum = Enum.TryParse<SubscriptionInterval>(interval, out var parsedInterval)
            ? parsedInterval
            : SubscriptionInterval.Monthly;

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

        if (!string.IsNullOrEmpty(sub.MollieSubscriptionId) && sub.Status != SubscriptionStatus.Cancelled)
            return new MollieFlowResult(false, "Es besteht bereits ein aktives Mollie-Abonnement.", null);

        // If there's an unresolved in-flight first payment, re-check it instead of creating a new one.
        if (!string.IsNullOrEmpty(sub.LastMolliePaymentId))
        {
            var pending = await _mollie.GetPaymentAsync(sub.LastMolliePaymentId);
            if (pending.Status is "open" or "pending")
                return new MollieFlowResult(false, "Es läuft bereits ein Zahlungsvorgang für dieses Abonnement. Bitte schließe diesen zuerst ab oder warte kurz.", null);

            // The payment already succeeded at Mollie but never got processed locally (e.g. a
            // dropped/missed webhook) — self-heal right here instead of letting a second
            // mandate/first-payment be created for the same customer, which would risk a
            // real double SEPA charge.
            if (pending.IsPaid && string.IsNullOrEmpty(sub.MollieSubscriptionId))
            {
                await ProcessPaymentEventAsync(pending);
                if (!string.IsNullOrEmpty(sub.MollieSubscriptionId))
                    return new MollieFlowResult(false, "Dein Abonnement wurde soeben aktiviert. Bitte lade die Seite neu.", null);
            }
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
            intervalEnum.PriceFor(limits),
            "EUR",
            $"GentleBook {limits.DisplayName}-Plan – Einrichtung SEPA-Mandat",
            redirectUrl,
            _options.Value.WebhookUrl,
            new Dictionary<string, string> { ["tenantId"] = tenantId.ToString(), ["plan"] = planEnum.ToString(), ["interval"] = intervalEnum.ToString() });

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

    // Does NOT unilaterally finalize Status → Cancelled anymore. A tenant-initiated cancel
    // (CancelSubscriptionAsync below) or the dunning auto-cancel job calls Mollie's DELETE
    // themselves and Mollie echoes it back here as a webhook — finalizing on that echo would
    // deactivate the tenant immediately, violating "access continues until period end."
    // Real finalization is owned by SubscriptionService.ProcessPendingCancellationsAsync
    // (period-end) and SubscriptionService.ProcessDunningAsync (grace-period expiry).
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
            if (sub.Status == SubscriptionStatus.Cancelled)
            {
                // Our own DELETE call (cancel endpoint or dunning job) echoed back — audit only.
                await _audit.LogAsync("mollie.subscription_cancel_confirmed", "Subscription", sub.Id.ToString(), subscription.Status, sub.TenantId);
                return;
            }

            if (sub.CancelRequestedAt != null)
            {
                // Tenant-initiated cancellation already recorded; ProcessPendingCancellationsAsync
                // finalizes Status at period end, not this webhook.
                await _audit.LogAsync("mollie.subscription_cancel_confirmed_pending_period_end", "Subscription", sub.Id.ToString(), subscription.Status, sub.TenantId);
                return;
            }

            // Genuinely unprompted external cancellation (Mollie dashboard, invalidated mandate).
            // Treat like a tenant self-cancel so access still runs out gracefully at period end
            // instead of an abrupt cutoff, but flag it for ops via a warning log.
            sub.CancelRequestedAt = DateTime.UtcNow;
            sub.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            _logger.LogWarning("Mollie subscription {MollieSubscriptionId} was cancelled externally for Subscription {SubscriptionId}", subscription.Id, sub.Id);
            await _audit.LogAsync("mollie.subscription_cancelled_externally", "Subscription", sub.Id.ToString(), subscription.Status, sub.TenantId);
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
                var intervalEnum = ParseIntervalFromMetadata(payment);
                var mollieSub = await _mollie.CreateSubscriptionAsync(
                    sub.MollieCustomerId!, payment.MandateId!, intervalEnum.PriceFor(limits), "EUR", intervalEnum.ToMollieInterval(),
                    $"GentleBook {limits.DisplayName}-Abonnement", _options.Value.WebhookUrl,
                    new Dictionary<string, string> { ["tenantId"] = sub.TenantId.ToString() });

                sub.Plan = ParsePlanFromMetadata(payment);
                sub.Interval = intervalEnum;
                sub.MollieMandateId = payment.MandateId;
                sub.MollieSubscriptionId = mollieSub.Id;
                sub.MollieMandateSignedAt = DateTime.UtcNow;
                sub.Status = SubscriptionStatus.Active;
                sub.RetentionEndsAt = null;
                sub.RetentionWarningEmailSent = false;
                sub.OperationalDataDeletedAt = null;
                sub.CurrentPeriodStart = DateTime.UtcNow;
                sub.CurrentPeriodEnd = sub.Interval.AddInterval(DateTime.UtcNow);
                sub.UpdatedAt = DateTime.UtcNow;
                await _db.Tenants.Where(t => t.Id == sub.TenantId)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(t => t.IsActive, true)
                        .SetProperty(t => t.UpdatedAt, DateTime.UtcNow));
                await _db.SaveChangesAsync();

                await _audit.LogAsync("mollie.subscription_activated", "Subscription", sub.Id.ToString(), sub.Plan.ToString(), sub.TenantId);
                EnqueueInvoice(sub.Id, payment.Id);
            }
        }
        else if (payment.IsFailedOrExpired)
        {
            await _audit.LogAsync("mollie.first_payment_failed", "Subscription", sub.Id.ToString(), payment.Status, sub.TenantId);
        }
    }

    private async Task HandleRecurringPaymentResultAsync(Subscription sub, MolliePayment payment)
    {
        // A chargeback/refund can arrive weeks after the payment was originally marked "paid" —
        // Mollie sends a fresh webhook call for the same payment id when it happens. Flag the
        // subscription for review via the existing dunning/PastDue flow rather than silently
        // leaving it Active despite the reclaimed funds.
        if (payment.HasChargeback || payment.HasRefund)
        {
            sub.Status = SubscriptionStatus.PastDue;
            if (sub.PastDueSince == null)
                sub.PastDueSince = DateTime.UtcNow;
            sub.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            var kind = payment.HasChargeback ? "chargeback" : "refund";
            _logger.LogWarning("Mollie payment {PaymentId} for Subscription {SubscriptionId} has a {Kind} — flagged PastDue for review.", payment.Id, sub.Id, kind);
            await _audit.LogAsync($"mollie.payment_{kind}", "Subscription", sub.Id.ToString(), payment.Id, sub.TenantId);
            return;
        }

        if (payment.IsPaid)
        {
            sub.Status = SubscriptionStatus.Active;
            sub.RetentionEndsAt = null;
            sub.RetentionWarningEmailSent = false;
            sub.OperationalDataDeletedAt = null;
            sub.CurrentPeriodStart = DateTime.UtcNow;
            sub.CurrentPeriodEnd = sub.Interval.AddInterval(DateTime.UtcNow);
            // Clears any in-progress dunning episode — a recovered payment cancels the grace-period clock.
            sub.PastDueSince = null;
            sub.FailedPaymentCount = 0;
            sub.LastFailedMolliePaymentId = null;
            sub.DunningWarningEmailSent = false;
            sub.UpdatedAt = DateTime.UtcNow;
            await _db.Tenants.Where(t => t.Id == sub.TenantId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(t => t.IsActive, true)
                    .SetProperty(t => t.UpdatedAt, DateTime.UtcNow));
            await _db.SaveChangesAsync();

            await _audit.LogAsync("mollie.recurring_payment_paid", "Subscription", sub.Id.ToString(), payment.Id, sub.TenantId);
            EnqueueInvoice(sub.Id, payment.Id);
        }
        else if (payment.IsFailedOrExpired)
        {
            // Mollie retries failed SEPA collections itself — reflect the risk, don't cancel yet.
            // SubscriptionService.ProcessDunningAsync auto-cancels after a grace period.
            sub.Status = SubscriptionStatus.PastDue;
            if (sub.PastDueSince == null)
                sub.PastDueSince = DateTime.UtcNow;

            // Dedup against MollieReconciliationJob's hourly replay of the same still-failing
            // payment — only count a genuinely new failed payment id.
            if (payment.Id != sub.LastFailedMolliePaymentId)
            {
                sub.FailedPaymentCount++;
                sub.LastFailedMolliePaymentId = payment.Id;
            }
            sub.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _audit.LogAsync("mollie.recurring_payment_failed", "Subscription", sub.Id.ToString(), payment.Id, sub.TenantId);
        }
    }

    public record CancelResult(bool Success, string? Error, DateTime? CancelRequestedAt, DateTime? CurrentPeriodEnd, string Message);

    /// <summary>
    /// Tenant-initiated cancellation. Cancels the Mollie subscription (not the mandate)
    /// immediately so no further charge is even attempted, while access stays available
    /// until CurrentPeriodEnd — finalized later by ProcessPendingCancellationsAsync.
    /// </summary>
    public async Task<CancelResult> CancelSubscriptionAsync(Guid tenantId, string? reason)
    {
        var sub = await _db.Subscriptions.Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (sub == null)
            return new CancelResult(false, "Kein Abonnement gefunden.", null, null, "");

        if (sub.Status == SubscriptionStatus.Cancelled)
            return new CancelResult(true, null, sub.CancelRequestedAt, sub.CurrentPeriodEnd, "Das Abonnement ist bereits gekündigt.");

        if (sub.CancelRequestedAt != null)
            return new CancelResult(true, null, sub.CancelRequestedAt, sub.CurrentPeriodEnd, "Die Kündigung wurde bereits entgegengenommen.");

        var reasonTrimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        // Trial has no paid period to run out — cancel finalizes immediately.
        if (sub.Status == SubscriptionStatus.Trial)
        {
            sub.Status = SubscriptionStatus.Cancelled;
            sub.CancelledAt = DateTime.UtcNow;
            sub.RetentionEndsAt = sub.CancelledAt.Value.AddDays(30);
            sub.RetentionWarningEmailSent = false;
            sub.Tenant.IsActive = false;
            sub.CancelReason = reasonTrimmed;
            sub.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _audit.LogAsync("subscription.cancel_requested", "Subscription", sub.Id.ToString(), "Trial", sub.TenantId);
            return new CancelResult(true, null, sub.CancelledAt, null, "Testphase beendet.");
        }

        var now = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(sub.MollieSubscriptionId))
        {
            try
            {
                await _mollie.CancelSubscriptionAsync(sub.MollieCustomerId!, sub.MollieSubscriptionId!);
            }
            catch (Exception ex)
            {
                // Never let a Mollie outage silently drop the tenant's cancellation intent —
                // record it locally now, retry the Mollie-side DELETE via Hangfire.
                _logger.LogError(ex, "Cancel: Mollie DELETE failed for Subscription {SubscriptionId}, queuing retry.", sub.Id);
                sub.CancelRequestedAt = now;
                sub.CancelReason = reasonTrimmed;
                sub.UpdatedAt = now;
                await _db.SaveChangesAsync();
                _backgroundJobClient.Enqueue<MollieService>(s => s.RetryCancelMollieSubscriptionAsync(sub.Id, CancellationToken.None));
                await _audit.LogAsync("subscription.cancel_mollie_call_failed_queued_retry", "Subscription", sub.Id.ToString(), ex.Message, sub.TenantId);
                await SendCancelConfirmationEmailAsync(sub);
                return new CancelResult(true, null, sub.CancelRequestedAt, sub.CurrentPeriodEnd,
                    "Kündigung bestätigt. Ihr Zugang bleibt bis zum Ende der bezahlten Periode bestehen.");
            }
        }
        // else: no Mollie subscription on file (e.g. manually activated tenant) — nothing to
        // cancel at Mollie, the period-end job finalizes exactly the same way.

        sub.CancelRequestedAt = now;
        sub.CancelReason = reasonTrimmed;
        sub.UpdatedAt = now;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("subscription.cancel_requested", "Subscription", sub.Id.ToString(), reasonTrimmed ?? "", sub.TenantId);
        await SendCancelConfirmationEmailAsync(sub);

        return new CancelResult(true, null, sub.CancelRequestedAt, sub.CurrentPeriodEnd,
            "Kündigung bestätigt. Ihr Zugang bleibt bis zum Ende der bezahlten Periode bestehen.");
    }

    public record ChangePlanResult(
        bool Success, string? Error, string? Plan, string? Interval,
        int? CurrentEmployees = null, int? EmployeeLimit = null,
        int? CurrentServices = null, int? ServiceLimit = null);

    /// <summary>
    /// Self-service plan/interval change for a tenant that already has an active Mollie
    /// subscription — StartMandateFlowAsync explicitly refuses this case, and cancel-then-
    /// resubscribe doesn't work either (Status stays Active until period end). Uses Mollie's
    /// subscription PATCH to change amount/interval in place: no new mandate, no checkout
    /// redirect. No proration — the new price takes effect at the next Mollie-triggered charge;
    /// upgrades get the higher limits immediately, downgrades are rejected if current usage
    /// wouldn't fit (never auto-deletes employees/services — the admin reduces usage first).
    /// </summary>
    public async Task<ChangePlanResult> ChangePlanAsync(Guid tenantId, string plan, string? interval)
    {
        if (!Enum.TryParse<SubscriptionPlan>(plan, ignoreCase: true, out var newPlan) || newPlan == SubscriptionPlan.Trial)
            return new ChangePlanResult(false, "Ungültiger Plan.", null, null);

        var newInterval = Enum.TryParse<SubscriptionInterval>(interval, ignoreCase: true, out var parsedInterval)
            ? parsedInterval
            : SubscriptionInterval.Monthly;

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (sub == null)
            return new ChangePlanResult(false, "Kein Abonnement gefunden.", null, null);

        if (sub.Status != SubscriptionStatus.Active || string.IsNullOrEmpty(sub.MollieSubscriptionId))
            return new ChangePlanResult(false, "Kein aktives Mollie-Abonnement. Bitte zuerst abonnieren.", null, null);

        if (sub.Plan == newPlan && sub.Interval == newInterval)
            return new ChangePlanResult(true, null, sub.Plan.ToString(), sub.Interval.ToString());

        var newLimits = PlanLimits.Get(newPlan);
        var employeeCount = await _db.Employees.CountAsync(e => e.TenantId == tenantId && e.IsActive);
        var serviceCount = await _db.Services.CountAsync(s => s.TenantId == tenantId && s.IsActive);

        if (!PlanLimits.IsUnlimited(newLimits.MaxEmployees) && employeeCount > newLimits.MaxEmployees)
            return new ChangePlanResult(false,
                $"Ihr aktueller Bestand hat {employeeCount} aktive Mitarbeiter, der {newLimits.DisplayName}-Plan erlaubt maximal {newLimits.MaxEmployees}. Bitte deaktivieren Sie zuerst {employeeCount - newLimits.MaxEmployees} Mitarbeiter und versuchen Sie es erneut.",
                null, null, employeeCount, newLimits.MaxEmployees);

        if (!PlanLimits.IsUnlimited(newLimits.MaxServices) && serviceCount > newLimits.MaxServices)
            return new ChangePlanResult(false,
                $"Sie haben {serviceCount} aktive Services, der {newLimits.DisplayName}-Plan erlaubt maximal {newLimits.MaxServices}. Bitte deaktivieren Sie zuerst {serviceCount - newLimits.MaxServices} Services und versuchen Sie es erneut.",
                null, null, null, null, serviceCount, newLimits.MaxServices);

        // The currently selected booking-page template can be tier-locked (LinkPageTemplates)
        // — that's only enforced when SAVING a new template choice, never at render time, so a
        // downgrade would otherwise leave a higher-tier template silently live on the public
        // booking page forever. Block until the admin picks a compatible template first.
        var linktreeConfig = await _db.TenantSettings
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.LinktreeConfig)
            .FirstOrDefaultAsync();

        if (!string.IsNullOrWhiteSpace(linktreeConfig))
        {
            string? currentTemplate = null;
            try
            {
                using var parsed = JsonDocument.Parse(linktreeConfig);
                if (parsed.RootElement.TryGetProperty("pageTemplate", out var templateProp) && templateProp.ValueKind == JsonValueKind.String)
                    currentTemplate = templateProp.GetString();
            }
            catch (JsonException) { /* malformed config — nothing to validate, same as the save-time check */ }

            if (currentTemplate != null)
            {
                var requiredPlanName = LinkPageTemplates.ValidateTemplateForPlan(currentTemplate, newPlan);
                if (requiredPlanName != null)
                    return new ChangePlanResult(false,
                        $"Ihre aktuell gewählte Buchungsseiten-Vorlage erfordert mindestens den {requiredPlanName}-Plan. Bitte wählen Sie unter \"Meine Links\" zuerst eine andere Vorlage und versuchen Sie den Wechsel danach erneut.",
                        null, null);
            }
        }

        if (!await TryUpdateMollieSubscriptionAsync(sub, newLimits, newInterval))
            return new ChangePlanResult(false, "Preisänderung bei Mollie fehlgeschlagen. Bitte versuchen Sie es später erneut oder kontaktieren Sie den Support.", null, null);

        var oldPlan = sub.Plan;
        var oldInterval = sub.Interval;
        sub.Plan = newPlan;
        sub.Interval = newInterval;
        sub.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("subscription.plan_changed_self_service", "Subscription", sub.Id.ToString(),
            $"{oldPlan} ({oldInterval}) → {newPlan} ({newInterval})", tenantId);

        return new ChangePlanResult(true, null, sub.Plan.ToString(), sub.Interval.ToString());
    }

    /// <summary>
    /// Best-effort Mollie sync for SuperAdmin's manual ChangePlan override — pushes whatever
    /// Plan/Interval is already saved on the subscription to Mollie. No fit-check (an admin
    /// override is allowed to exceed limits deliberately, e.g. a support case); failures are
    /// logged, never thrown, so the DB-level override always succeeds regardless of Mollie.
    /// </summary>
    public async Task<bool> TrySyncPlanToMollieAsync(Guid tenantId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (sub == null || string.IsNullOrEmpty(sub.MollieSubscriptionId))
            return false;

        return await TryUpdateMollieSubscriptionAsync(sub, PlanLimits.Get(sub.Plan), sub.Interval);
    }

    private async Task<bool> TryUpdateMollieSubscriptionAsync(Subscription sub, PlanLimits.Limits limits, SubscriptionInterval interval)
    {
        try
        {
            await _mollie.UpdateSubscriptionAsync(
                sub.MollieCustomerId!, sub.MollieSubscriptionId!, interval.PriceFor(limits), "EUR", interval.ToMollieInterval(),
                $"GentleBook {limits.DisplayName}-Abonnement");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mollie subscription PATCH failed for Subscription {SubscriptionId}", sub.Id);
            return false;
        }
    }

    [AutomaticRetry(Attempts = 5, DelaysInSeconds = new[] { 60, 300, 900, 3600, 3600 })]
    public async Task RetryCancelMollieSubscriptionAsync(Guid subscriptionId, CancellationToken ct)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
        if (sub?.MollieSubscriptionId == null || sub.MollieCustomerId == null) return;

        try
        {
            await _mollie.CancelSubscriptionAsync(sub.MollieCustomerId, sub.MollieSubscriptionId, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            // Already cancelled/gone on Mollie's side — treat as success.
        }
        await _audit.LogAsync("subscription.cancel_mollie_retry_succeeded", "Subscription", sub.Id.ToString(), "", sub.TenantId);
    }

    private async Task SendCancelConfirmationEmailAsync(Subscription sub)
    {
        try
        {
            var admin = await _db.PlatformUsers
                .Where(u => u.TenantId == sub.TenantId && u.Role == PlatformRole.TenantAdmin)
                .OrderBy(u => u.CreatedAt)
                .FirstOrDefaultAsync();
            if (admin == null) return;

            var limits = PlanLimits.Get(sub.Plan);
            await _emailService.SendSubscriptionCancelledConfirmationAsync(
                admin.Email, admin.FirstName, limits.DisplayName, sub.CurrentPeriodEnd);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send cancellation confirmation email for Subscription {SubscriptionId}", sub.Id);
        }
    }

    private static SubscriptionPlan ParsePlanFromMetadata(MolliePayment payment) =>
        payment.Metadata != null && payment.Metadata.TryGetValue("plan", out var p) && Enum.TryParse<SubscriptionPlan>(p, out var parsed)
            ? parsed
            : SubscriptionPlan.Starter;

    private static SubscriptionInterval ParseIntervalFromMetadata(MolliePayment payment) =>
        payment.Metadata != null && payment.Metadata.TryGetValue("interval", out var i) && Enum.TryParse<SubscriptionInterval>(i, out var parsed)
            ? parsed
            : SubscriptionInterval.Monthly;

    // GentleBook generates and emails its own invoice — no Gentle.Suite CRM dependency.
    // CrmPushService remains in the codebase for a future CRM re-hook but is no longer called.
    private void EnqueueInvoice(Guid subscriptionId, string molliePaymentId) =>
        _backgroundJobClient.Enqueue<InvoiceService>(s => s.GenerateAndSendInvoiceAsync(subscriptionId, molliePaymentId, CancellationToken.None));
}
