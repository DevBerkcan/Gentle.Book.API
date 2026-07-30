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
        if (await _db.Invoices.AnyAsync(i => i.MolliePaymentId == molliePaymentId, ct))
        {
            _logger.LogInformation("Invoice already exists for Mollie payment {PaymentId}, skipping.", molliePaymentId);
            return;
        }

        var sub = await _db.Subscriptions.Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
            ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found for invoicing.");

        var settings = sub.Tenant.Settings
            ?? throw new InvalidOperationException($"TenantSettings missing for tenant {sub.TenantId}, cannot invoice.");

        var limits = PlanLimits.Get(sub.Plan);
        var periodStart = sub.CurrentPeriodStart ?? DateTime.UtcNow;
        var periodEnd = sub.CurrentPeriodEnd ?? periodStart.AddMonths(1);
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
            Amount = limits.MonthlyPrice,
            Currency = "EUR",
            MolliePaymentId = molliePaymentId,
            RecipientName = settings.LegalCompanyName ?? settings.CompanyName,
            RecipientVatId = settings.VatId,
            RecipientStreet = settings.BillingStreet,
            RecipientZip = settings.BillingZipCode,
            RecipientCity = settings.BillingCity,
            RecipientCountry = settings.BillingCountry,
            RecipientEmail = settings.Email,
        };
        invoice.PdfContent = InvoicePdfBuilder.Build(invoice);

        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(ct);

        var sent = await _emailService.SendSubscriptionInvoiceAsync(invoice, settings);
        if (sent)
        {
            invoice.EmailSent = true;
            invoice.EmailSentAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Invoice {InvoiceNumber} generated for Subscription {SubscriptionId} (email sent: {EmailSent})",
            invoice.InvoiceNumber, subscriptionId, sent);
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
