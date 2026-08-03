using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

// Generates and emails a GentleBook-issued invoice for a confirmed subscription payment.
// Replaces the Gentle.Suite CRM push (CrmPushService) — no external dependency, invoice
// number, PDF and delivery all happen inside GentleBook itself.
// Invoked only via Hangfire (BackgroundJob.Enqueue from MollieService), never inline in the
// webhook — invoicing must never block or revert GentleBook's own subscription state.
[AutomaticRetry(Attempts = 5, DelaysInSeconds = new[] { 60, 300, 900, 3600, 3600 })]
public class InvoiceService
{
    private readonly GentleBookDbContext _db;
    private readonly EmailService _emailService;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(GentleBookDbContext db, EmailService emailService, ILogger<InvoiceService> logger)
    {
        _db = db;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task GenerateAndSendInvoiceAsync(Guid subscriptionId, string molliePaymentId, CancellationToken ct)
    {
        // A webhook retry could enqueue the same payment twice — never issue two invoices for it.
        // But "already exists" must not mean "already sent": if a prior attempt generated the
        // invoice and then the email failed, EmailSent stays false and this retry (or a manual
        // resend) must still deliver it instead of silently no-op'ing forever.
        var existing = await _db.Invoices.FirstOrDefaultAsync(i => i.MolliePaymentId == molliePaymentId, ct);
        if (existing != null)
        {
            if (existing.EmailSent)
            {
                _logger.LogInformation("Invoice {InvoiceNumber} already sent for Mollie payment {PaymentId}, skipping.", existing.InvoiceNumber, molliePaymentId);
                return;
            }

            _logger.LogInformation("Invoice {InvoiceNumber} exists but was never emailed for Mollie payment {PaymentId}, retrying send.", existing.InvoiceNumber, molliePaymentId);
            await SendAndMarkAsync(existing, ct);
            return;
        }

        var sub = await _db.Subscriptions.Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
            ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found for invoicing.");

        var settings = sub.Tenant.Settings
            ?? throw new InvalidOperationException($"TenantSettings missing for tenant {sub.TenantId}, cannot invoice.");

        // Prefer the TenantAdmin's actual account email — same lookup the cancellation/dunning
        // emails already use — over TenantSettings.Email (an optional "Kontakt"-field that's
        // often unset or points somewhere else, and previously caused invoices to be marked
        // "sent" while going to an address nobody checks).
        var admin = await _db.PlatformUsers
            .Where(u => u.TenantId == sub.TenantId && u.Role == PlatformRole.TenantAdmin)
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync(ct);
        var recipientEmail = admin?.Email ?? settings.Email;

        var limits = PlanLimits.Get(sub.Plan);
        var periodStart = sub.CurrentPeriodStart ?? DateTime.UtcNow;
        var periodEnd = sub.CurrentPeriodEnd ?? sub.Interval.AddInterval(periodStart);
        var issueDate = DateTime.UtcNow;

        var invoice = new Invoice
        {
            TenantId = sub.TenantId,
            SubscriptionId = sub.Id,
            InvoiceNumber = await NextInvoiceNumberAsync(issueDate.Year, ct),
            IssueDate = issueDate,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            PlanName = limits.DisplayName,
            Amount = sub.Interval.PriceFor(sub, limits),
            Currency = "EUR",
            MolliePaymentId = molliePaymentId,
            RecipientName = settings.LegalCompanyName ?? settings.CompanyName,
            RecipientVatId = settings.VatId,
            RecipientStreet = settings.BillingStreet,
            RecipientZip = settings.BillingZipCode,
            RecipientCity = settings.BillingCity,
            RecipientCountry = settings.BillingCountry,
            RecipientEmail = recipientEmail,
        };
        invoice.PdfContent = InvoicePdfBuilder.Build(invoice);

        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(ct);

        await SendAndMarkAsync(invoice, ct);

        _logger.LogInformation("Invoice {InvoiceNumber} generated for Subscription {SubscriptionId}",
            invoice.InvoiceNumber, subscriptionId);
    }

    // Manual resend path for the SuperAdmin UI — same delivery logic as the generation path,
    // just without creating a new invoice. Throws (404-worthy) if the id doesn't exist so the
    // controller can translate that into a clean HTTP response.
    public async Task ResendInvoiceEmailAsync(Guid invoiceId, CancellationToken ct)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} not found.");

        await SendAndMarkAsync(invoice, ct);
    }

    // Sends the invoice email and marks it delivered. Throws on failure instead of swallowing
    // it — EmailService still returns false on SMTP errors rather than throwing itself, so this
    // is the one place that turns "false" into an exception, which is what actually makes
    // Hangfire's [AutomaticRetry] on this class engage for failed sends.
    private async Task SendAndMarkAsync(Invoice invoice, CancellationToken ct)
    {
        var settings = await _db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == invoice.TenantId, ct)
            ?? throw new InvalidOperationException($"TenantSettings missing for tenant {invoice.TenantId}, cannot send invoice email.");

        var sent = await _emailService.SendSubscriptionInvoiceAsync(invoice, settings);
        if (!sent)
            throw new InvalidOperationException($"Failed to email invoice {invoice.InvoiceNumber} to tenant {invoice.TenantId}.");

        invoice.EmailSent = true;
        invoice.EmailSentAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Invoice {InvoiceNumber} emailed to tenant {TenantId}", invoice.InvoiceNumber, invoice.TenantId);
    }

    // Simple year-scoped sequential numbering (e.g. "2026-0001"). A unique index on
    // InvoiceNumber backstops any race between concurrent Hangfire workers — a collision
    // fails the SaveChanges and Hangfire's AutomaticRetry recomputes the next number.
    private async Task<string> NextInvoiceNumberAsync(int year, CancellationToken ct)
    {
        var count = await _db.Invoices.CountAsync(i => i.IssueDate.Year == year, ct);
        return $"{year}-{count + 1:0000}";
    }
}
