using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

// Mollie calls this with nothing but a bare resource id — by design, so implementers are
// forced to re-fetch the real status server-to-server rather than trust the payload.
// SECURITY: never resolve tenant/subscription identity from anything in the request body —
// only from GentleBook's own DB mapping (LastMolliePaymentId / MollieSubscriptionId). This is
// the same class of bug documented in AUDIT_REPORT.md (EF tenant filter is OFF for anonymous
// requests) — here there isn't even a tenant slug in the payload, so the DB lookup is the only
// resolution path, full stop.
[ApiController]
[Route("api/webhooks/mollie")]
[AllowAnonymous]
public class MollieWebhookController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly MollieClient _mollie;
    private readonly MollieService _mollieService;
    private readonly ILogger<MollieWebhookController> _logger;

    public MollieWebhookController(GentleBookDbContext db, MollieClient mollie, MollieService mollieService, ILogger<MollieWebhookController> logger)
    {
        _db = db;
        _mollie = mollie;
        _mollieService = mollieService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Handle([FromForm] string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Ok(); // nothing to do; never give Mollie a reason to retry a malformed call

        var resourceType = id.StartsWith("sub_") ? "subscription" : "payment";

        if (resourceType == "payment")
            return await HandlePaymentAsync(id) ?? Ok();

        // Subscription-resource webhooks carry a bare subscription id with no customerId,
        // so the owning Subscription row (and thus its MollieCustomerId) must be resolved
        // from our own DB before we can call Mollie's customer-scoped subscription endpoint.
        // Not dedup-guarded: ProcessSubscriptionEventAsync is already idempotent by design.
        MollieSubscription subscription;
        try
        {
            var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.MollieSubscriptionId == id);
            if (sub?.MollieCustomerId == null)
                return Ok(); // no matching local subscription — nothing we can or should do

            subscription = await _mollie.GetSubscriptionAsync(sub.MollieCustomerId, id);
        }
        catch (Exception ex)
        {
            // Distinct from a processing failure below: we couldn't even fetch the resource, so
            // Mollie's own (fast, bounded) retry should fire instead of waiting up to an hour for
            // the reconciliation job to notice — a flat 200 here would silently swallow the delivery.
            _logger.LogError(ex, "Mollie webhook: failed to fetch subscription {Id}", id);
            return StatusCode(502);
        }

        var eventRow = new MollieWebhookEvent { MollieResourceId = id, ResourceType = "subscription", ResultStatus = subscription.Status };
        _db.MollieWebhookEvents.Add(eventRow);
        try
        {
            await _mollieService.ProcessSubscriptionEventAsync(subscription);
            eventRow.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // The resource itself was fetched fine — this is a local processing bug, not a
            // delivery problem. Still 200: a persistent bug here must not turn into an endless
            // Mollie retry storm; the hourly reconciliation job is the backstop for this case.
            _logger.LogError(ex, "Mollie webhook processing failed for subscription {Id}", id);
            eventRow.Notes = ex.Message;
            try { await _db.SaveChangesAsync(); } catch { /* best effort */ }
        }

        return Ok();
    }

    // Fetches the payment's real, current status/amounts from Mollie first — the dedup key is
    // then computed FROM that live state (status, plus a marker if a chargeback/refund has
    // appeared), not assumed up front. This is what lets a later webhook call for the same
    // payment id (e.g. pending -> paid, or a chargeback arriving weeks after "paid") still get
    // processed instead of being dropped as a duplicate of an earlier, different-status call.
    // Returns a non-null IActionResult only when the caller should return something other than
    // the default 200 (i.e. the fetch itself failed and Mollie should retry delivery).
    private async Task<IActionResult?> HandlePaymentAsync(string id)
    {
        MolliePayment payment;
        try
        {
            payment = await _mollie.GetPaymentAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mollie webhook: failed to fetch payment {Id}", id);
            return StatusCode(502); // let Mollie's own faster retry fire instead of waiting on the hourly reconciliation job
        }

        var dedupStatus = ComputeDedupStatus(payment);

        var existing = await _db.MollieWebhookEvents.FirstOrDefaultAsync(e =>
            e.MollieResourceId == id && e.ResourceType == "payment" && e.ResultStatus == dedupStatus);
        if (existing?.ProcessedAt != null)
            return null; // genuinely already handled

        MollieWebhookEvent eventRow;
        if (existing != null)
        {
            // A previous delivery for this exact (id, status) got as far as recording the dedup
            // row but never finished processing (e.g. it crashed mid-way) — resume it instead of
            // inserting a second row, which would hit the unique index and be misread as "another
            // delivery is already handling this," permanently skipping the payment until the
            // hourly reconciliation job happens to pick it up.
            eventRow = existing;
        }
        else
        {
            eventRow = new MollieWebhookEvent { MollieResourceId = id, ResourceType = "payment", ResultStatus = dedupStatus };
            try
            {
                _db.MollieWebhookEvents.Add(eventRow);
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Unique-index race: another concurrent delivery just inserted the same row a
                // moment ago — safe to stop, that request (or the next redelivery) handles it.
                return null;
            }
        }

        try
        {
            await _mollieService.ProcessPaymentEventAsync(payment);
            eventRow.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mollie webhook processing failed for payment {Id}", id);
            eventRow.Notes = ex.Message;
            try { await _db.SaveChangesAsync(); } catch { /* best effort */ }
        }

        return null;
    }

    private static string ComputeDedupStatus(MolliePayment payment)
    {
        var parts = new List<string> { payment.Status };
        if (payment.HasChargeback) parts.Add("chargedback");
        if (payment.HasRefund) parts.Add("refunded");
        return string.Join("+", parts);
    }
}
