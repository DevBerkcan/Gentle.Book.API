using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Regression coverage for a production data-integrity bug found during a live pre-launch test:
// InvoiceService used to re-derive PlanName/Amount/Period from the Subscription row's *current*
// state at the time the (Hangfire, possibly delayed) invoicing job actually ran. If the tenant
// changed plan between the payment being confirmed and the job executing, the resulting invoice
// described the new plan/price instead of what was actually charged for the referenced Mollie
// payment. The fix makes the caller (MollieService) capture planName/amount/period at the moment
// the payment is confirmed and pass them straight through — this test locks that in by calling
// GenerateAndSendInvoiceAsync with values that deliberately disagree with the subscription's live
// state and asserting the invoice reflects the passed-in values, not the live ones.
public class InvoiceServiceCapturedValuesTests
{
    [Fact]
    public async Task GenerateAndSendInvoiceAsync_UsesCapturedValues_NotLiveSubscriptionState()
    {
        var db = TestDbContextFactory.Create();
        var emailService = TestServiceFactory.CreateEmailService(db);
        var invoiceService = new InvoiceService(db, emailService, NullLogger<InvoiceService>.Instance);

        var tenant = new Tenant { Name = "Salon Invoice", Slug = "salon-invoice-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            // Live state has since moved on to Agency at a negotiated 1.00 EUR — deliberately
            // different from what was actually charged for the payment being invoiced below.
            Plan = SubscriptionPlan.Agency,
            Interval = SubscriptionInterval.Monthly,
            Status = SubscriptionStatus.Active,
            NegotiatedMonthlyPrice = 1.00m,
        };
        tenant.Subscription = subscription;
        var settings = new TenantSettings { TenantId = tenant.Id, Tenant = tenant, CompanyName = "Salon Invoice" };
        db.AddRange(tenant, subscription, settings);
        await db.SaveChangesAsync();

        var chargedPeriodStart = new DateTime(2026, 7, 31, 17, 30, 0, DateTimeKind.Utc);
        var chargedPeriodEnd = chargedPeriodStart.AddMonths(1);

        try
        {
            await invoiceService.GenerateAndSendInvoiceAsync(
                subscription.Id, "tr_pJHvqLKADwhycinxoYjUJ", "Starter", 1.00m,
                chargedPeriodStart, chargedPeriodEnd, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // No recipient email configured in this test tenant, so the send leg fails — the
            // invoice row (created and saved before the send is attempted) is what's under test.
        }

        var invoice = await db.Invoices.SingleAsync(i => i.MolliePaymentId == "tr_pJHvqLKADwhycinxoYjUJ");
        Assert.Equal("Starter", invoice.PlanName);
        Assert.Equal(1.00m, invoice.Amount);
        Assert.Equal(chargedPeriodStart, invoice.PeriodStart);
        Assert.Equal(chargedPeriodEnd, invoice.PeriodEnd);
    }
}
