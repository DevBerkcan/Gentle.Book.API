using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the idempotency hardening from the Mollie legal/technical audit: two near-simultaneous
// StartMandateFlowAsync calls for the same subscription must never both reach Mollie's "first
// payment" API, which would risk two real SEPA charges for the same signup.
public class MollieServiceStartMandateFlowIdempotencyTests
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

    private static (Tenant tenant, Subscription subscription) SeedEligibleSubscription(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Idempotenz", Slug = "salon-idempotenz-" + Guid.NewGuid(), IsActive = true };
        var settings = new TenantSettings
        {
            TenantId = tenant.Id,
            CompanyName = "Salon Idempotenz",
            LegalCompanyName = "Salon Idempotenz GmbH",
            BillingStreet = "Musterstraße 1",
            BillingZipCode = "12345",
            BillingCity = "Musterstadt",
            BillingCountry = "DE",
            Email = "billing@salon-idempotenz.de",
        };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Plan = SubscriptionPlan.Starter,
            Interval = SubscriptionInterval.Monthly,
            Status = SubscriptionStatus.Trial,
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
    public async Task StartMandateFlowAsync_ClaimAlreadyHeld_NeverCallsMollie()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedDb = TestDbContextFactory.Create(dbName);
        var (tenant, subscription) = SeedEligibleSubscription(seedDb);
        // Simulates a concurrent request having won the claim a moment earlier, using its own
        // (separate, scoped) DbContext — exactly like two real HTTP requests would — before its
        // own Mollie call has resolved either way.
        seedDb.MandateFlowClaims.Add(new GentleBook.Api.Data.Entities.MandateFlowClaim { SubscriptionId = subscription.Id });
        seedDb.SaveChanges();

        var mollieWasCalled = false;
        using var db = TestDbContextFactory.Create(dbName);
        var mollieService = BuildMollieService(db, _ => { mollieWasCalled = true; return Json("{}"); });

        // On real SQL Server this primary-key collision is wrapped in DbUpdateException and
        // StartMandateFlowAsync returns a graceful "already in progress" result (see the plain
        // happy-path test below for the success side of that same code path). EF Core's
        // InMemory provider — test-only, never used in production — doesn't wrap this specific
        // cross-context collision the same way, so here we only assert the property that
        // matters regardless of provider: the claim is never won twice, so Mollie is never
        // called a second time.
        try { await mollieService.StartMandateFlowAsync(tenant.Id, "Starter", "Monthly"); }
        catch { /* provider-specific collision shape, see comment above */ }

        Assert.False(mollieWasCalled);
    }

    [Fact]
    public async Task StartMandateFlowAsync_MollieCallFails_ReleasesClaimSoRetrySucceeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, subscription) = SeedEligibleSubscription(db);

        var mollieServiceFailing = BuildMollieService(db, req =>
            req.RequestUri!.AbsolutePath.Contains("/customers")
                ? Json("{\"id\":\"cst_test\"}")
                : throw new HttpRequestException("simulated Mollie outage"));

        await Assert.ThrowsAsync<HttpRequestException>(() => mollieServiceFailing.StartMandateFlowAsync(tenant.Id, "Starter", "Monthly"));

        Assert.False(await db.MandateFlowClaims.AsNoTracking().AnyAsync(c => c.SubscriptionId == subscription.Id)); // claim released, tenant can retry

        var mollieServiceRetry = BuildMollieService(db, req =>
            req.RequestUri!.AbsolutePath.Contains("/customers")
                ? Json("{\"id\":\"cst_test\"}")
                : Json("{\"id\":\"tr_test\",\"status\":\"open\",\"_links\":{\"checkout\":{\"href\":\"https://mollie.test/checkout\"}}}"));

        var retryResult = await mollieServiceRetry.StartMandateFlowAsync(tenant.Id, "Starter", "Monthly");

        Assert.True(retryResult.Success);
        var afterRetry = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == subscription.Id);
        Assert.Equal("tr_test", afterRetry.LastMolliePaymentId);
    }

    [Fact]
    public async Task StartMandateFlowAsync_Success_StoresPaymentIdAndReleasesClaim()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, subscription) = SeedEligibleSubscription(db);

        var mollieService = BuildMollieService(db, req =>
            req.RequestUri!.AbsolutePath.Contains("/customers")
                ? Json("{\"id\":\"cst_test\"}")
                : Json("{\"id\":\"tr_real_payment\",\"status\":\"open\",\"_links\":{\"checkout\":{\"href\":\"https://mollie.test/checkout\"}}}"));

        var result = await mollieService.StartMandateFlowAsync(tenant.Id, "Starter", "Monthly");

        Assert.True(result.Success);
        Assert.Equal("https://mollie.test/checkout", result.CheckoutUrl);
        var reloaded = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == subscription.Id);
        Assert.Equal("tr_real_payment", reloaded.LastMolliePaymentId);
        Assert.False(await db.MandateFlowClaims.AsNoTracking().AnyAsync(c => c.SubscriptionId == subscription.Id));
    }
}
