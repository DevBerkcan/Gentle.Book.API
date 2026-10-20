using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers a gap left open by the Mollie legal/technical audit (LEGAL_AUDIT_2026-09-06.md): the
// first-payment-confirmed path that calls Mollie's CreateSubscriptionAsync is reachable from two
// independent pathways for the same payment — a real-time webhook delivery
// (MollieWebhookController.HandlePaymentAsync) and the hourly MollieReconciliationJob, which calls
// ProcessPaymentEventAsync directly and never goes through the webhook's own (id, status) dedup
// table. Without a lock, two overlapping calls could both see MollieSubscriptionId as still-null
// and both call Mollie, creating two live recurring subscriptions (and two real monthly charges)
// for the same tenant. HandleFirstPaymentResultAsync now reuses the MandateFlowClaim row
// (SubscriptionId as PK) as a DB-level lock for this step too.
public class MollieServiceHandleFirstPaymentIdempotencyTests
{
    private static MollieService BuildMollieService(GentleBook.Api.Data.GentleBookDbContext db, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var auditService = new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions { WebhookUrl = "https://api.example.test/webhooks/mollie", RedirectUrlBase = "https://app.example.test" });
        var mollieClient = new MollieClient(
            new HttpClient(new FakeHttpMessageHandler(responder)) { BaseAddress = new Uri("https://api.mollie.com/v2/") },
            new StaticOptionsMonitor<MollieOptions>(new MollieOptions { ApiKey = "test_x" }));
        return new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), auditService, emailService, NullLogger<MollieService>.Instance);
    }

    private static (Tenant tenant, Subscription subscription) SeedPendingFirstPayment(GentleBook.Api.Data.GentleBookDbContext db, string paymentId)
    {
        var tenant = new Tenant { Name = "Salon Erstzahlung", Slug = "salon-erstzahlung-" + Guid.NewGuid(), IsActive = false };
        var settings = new TenantSettings
        {
            TenantId = tenant.Id,
            CompanyName = "Salon Erstzahlung",
            LegalCompanyName = "Salon Erstzahlung GmbH",
            BillingStreet = "Musterstraße 1",
            BillingZipCode = "12345",
            BillingCity = "Musterstadt",
            BillingCountry = "DE",
            Email = "billing@salon-erstzahlung.de",
        };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Plan = SubscriptionPlan.Trial,
            Interval = SubscriptionInterval.Monthly,
            Status = SubscriptionStatus.Trial,
            MollieCustomerId = "cst_test",
            LastMolliePaymentId = paymentId,
        };
        tenant.Subscription = subscription;
        tenant.Settings = settings;
        db.AddRange(tenant, settings, subscription);
        db.SaveChanges();
        return (tenant, subscription);
    }

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task ProcessPaymentEventAsync_ClaimAlreadyHeld_NeverCreatesSecondMollieSubscription()
    {
        const string paymentId = "tr_test_concurrent";
        var dbName = Guid.NewGuid().ToString();
        using var seedDb = TestDbContextFactory.Create(dbName);
        var (_, subscription) = SeedPendingFirstPayment(seedDb, paymentId);
        // Simulates a concurrent call (webhook delivery racing the hourly reconciliation job, or
        // two overlapping reconciliation runs) already holding the claim, using its own separate
        // DbContext — exactly like two real processes would.
        seedDb.MandateFlowClaims.Add(new MandateFlowClaim { SubscriptionId = subscription.Id });
        seedDb.SaveChanges();

        var mollieSubscriptionCreateCalled = false;
        using var db = TestDbContextFactory.Create(dbName);
        var mollieService = BuildMollieService(db, req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.ToString().Contains("/subscriptions"))
                mollieSubscriptionCreateCalled = true;
            return Json("{\"id\":\"sub_should_never_be_created\",\"status\":\"active\"}");
        });

        var payment = new MolliePayment
        {
            Id = paymentId,
            Status = "paid",
            MandateId = "mdt_test",
            Metadata = new Dictionary<string, string> { ["plan"] = "Starter", ["interval"] = "Monthly" },
        };

        // Same InMemory-provider caveat as MollieServiceStartMandateFlowIdempotencyTests: real SQL
        // Server wraps this primary-key collision in DbUpdateException and the method returns
        // gracefully; EF Core's InMemory provider doesn't reproduce that exact cross-context
        // collision shape. The property under test either way: Mollie's subscription-create
        // endpoint is never reached while another call holds the claim.
        try { await mollieService.ProcessPaymentEventAsync(payment); }
        catch { /* provider-specific collision shape, see comment above */ }

        Assert.False(mollieSubscriptionCreateCalled);
    }

    [Fact]
    public async Task ProcessPaymentEventAsync_NoCompetingClaim_CreatesSubscriptionAndReleasesClaim()
    {
        const string paymentId = "tr_test_normal";
        var dbName = Guid.NewGuid().ToString();
        using var db = TestDbContextFactory.Create(dbName);
        var (_, subscription) = SeedPendingFirstPayment(db, paymentId);

        var mollieSubscriptionCreateCalled = false;
        var mollieService = BuildMollieService(db, req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.ToString().Contains("/subscriptions"))
            {
                mollieSubscriptionCreateCalled = true;
                return Json("{\"id\":\"sub_real\",\"status\":\"active\"}");
            }
            return Json("{}");
        });

        var payment = new MolliePayment
        {
            Id = paymentId,
            Status = "paid",
            MandateId = "mdt_test",
            Metadata = new Dictionary<string, string> { ["plan"] = "Starter", ["interval"] = "Monthly" },
        };

        await mollieService.ProcessPaymentEventAsync(payment);

        Assert.True(mollieSubscriptionCreateCalled);
        var reloaded = db.Subscriptions.First(s => s.Id == subscription.Id);
        Assert.Equal("sub_real", reloaded.MollieSubscriptionId);
        // The claim must be released on success — otherwise the tenant could never change plan or
        // have any later payment event processed for this subscription again.
        Assert.False(db.MandateFlowClaims.Any(c => c.SubscriptionId == subscription.Id));
    }
}
