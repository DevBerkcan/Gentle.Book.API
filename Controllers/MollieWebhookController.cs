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

        // Idempotency: a payment id only ever needs full processing once. Mollie delivers
        // webhooks at-least-once, so duplicate calls for the same payment are expected.
        if (resourceType == "payment")
        {
            var alreadyProcessed = await _db.MollieWebhookEvents
                .AnyAsync(e => e.MollieResourceId == id && e.ResourceType == "payment" && e.ProcessedAt != null);
            if (alreadyProcessed)
                return Ok();
        }

        var eventRow = new MollieWebhookEvent { MollieResourceId = id, ResourceType = resourceType };
        try
        {
            _db.MollieWebhookEvents.Add(eventRow);
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Unique-index race: another concurrent delivery is already handling this payment id.
            return Ok();
        }

        try
        {
            if (resourceType == "payment")
            {
                var payment = await _mollie.GetPaymentAsync(id);
                await _mollieService.ProcessPaymentEventAsync(payment);
                eventRow.ResultStatus = payment.Status;
            }
            else
            {
                // Subscription-resource webhooks carry a bare subscription id with no customerId,
                // so the owning Subscription row (and thus its MollieCustomerId) must be resolved
                // from our own DB before we can call Mollie's customer-scoped subscription endpoint.
                var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.MollieSubscriptionId == id);
                if (sub?.MollieCustomerId != null)
                {
                    var subscription = await _mollie.GetSubscriptionAsync(sub.MollieCustomerId, id);
                    await _mollieService.ProcessSubscriptionEventAsync(subscription);
                    eventRow.ResultStatus = subscription.Status;
                }
            }

            eventRow.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Log and still return 200 — Mollie retries indefinitely on non-2xx, and the hourly
            // reconciliation job is the real safety net for anything that fails here.
            _logger.LogError(ex, "Mollie webhook processing failed for {ResourceType} {Id}", resourceType, id);
            eventRow.Notes = ex.Message;
            try { await _db.SaveChangesAsync(); } catch { /* best effort */ }
        }

        return Ok();
    }
}
