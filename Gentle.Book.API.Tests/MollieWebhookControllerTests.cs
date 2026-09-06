using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the webhook-handling hardening from the Mollie legal/technical audit: a fetch failure
// must not be answered with 200 (which would stop Mollie's own retry), and a payment that
// errored out mid-processing must be resumable on the next delivery instead of being blocked
// forever by its own dedup row.
public class MollieWebhookControllerTests
{
    private static (MollieWebhookController controller, GentleBook.Api.Data.GentleBookDbContext db) BuildController(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var db = TestDbContextFactory.Create();
        var httpContextAccessor = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        var auditService = new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions());
        var mollieClient = new MollieClient(
            new HttpClient(new FakeHttpMessageHandler(responder)) { BaseAddress = new Uri("https://api.mollie.com/v2/") },
            new StaticOptionsMonitor<MollieOptions>(new MollieOptions { ApiKey = "test_x" }));
        var mollieService = new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), auditService, emailService, NullLogger<MollieService>.Instance);
        var controller = new MollieWebhookController(db, mollieClient, mollieService, NullLogger<MollieWebhookController>.Instance);
        return (controller, db);
    }

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private static (Tenant tenant, Subscription subscription) SeedActiveSubscription(GentleBook.Api.Data.GentleBookDbContext db, string lastMolliePaymentId)
    {
        var tenant = new Tenant { Name = "Salon Webhook", Slug = "salon-webhook-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Plan = SubscriptionPlan.Starter,
            Interval = SubscriptionInterval.Monthly,
            Status = SubscriptionStatus.Trial,
            LastMolliePaymentId = lastMolliePaymentId,
        };
        tenant.Subscription = subscription;
        db.AddRange(tenant, subscription);
        db.SaveChanges();
        return (tenant, subscription);
    }

    [Fact]
    public async Task Handle_PaymentFetchFails_Returns502NotOk()
    {
        var (controller, _) = BuildController(_ => throw new HttpRequestException("simulated network failure"));

        var result = await controller.Handle("tr_unreachable");

        var statusResult = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(502, statusResult.StatusCode);
    }

    [Fact]
    public async Task Handle_SubscriptionFetchFails_Returns502NotOk()
    {
        var (controller, db) = BuildController(_ => throw new HttpRequestException("simulated network failure"));
        var tenant = new Tenant { Name = "Salon Webhook Sub", Slug = "salon-webhook-sub-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant, MollieCustomerId = "cst_test", MollieSubscriptionId = "sub_test" };
        tenant.Subscription = subscription;
        db.AddRange(tenant, subscription);
        db.SaveChanges();

        var result = await controller.Handle("sub_test");

        var statusResult = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(502, statusResult.StatusCode);
    }

    [Fact]
    public async Task Handle_Payment_StuckIncompleteDedupRow_IsResumedNotSkipped()
    {
        // "failed" (not "paid") deliberately — HandleFirstPaymentResultAsync's paid-success
        // branch exercises an unrelated, pre-existing EF Core ExecuteUpdateAsync call that EF's
        // InMemory test provider can't run at all (a test-infra limitation, not something this
        // fix touches); the failed-payment branch only writes an audit log entry, which is
        // enough to prove the stuck row gets resumed and actually reprocessed here.
        var (controller, db) = BuildController(req =>
            Json("{\"id\":\"tr_stuck\",\"status\":\"failed\",\"customerId\":\"cst_test\"}"));
        var (tenant, subscription) = SeedActiveSubscription(db, "tr_stuck");

        // Simulate a previous delivery that recorded the dedup row but crashed before finishing
        // (ProcessedAt never set) — the exact state a mid-processing exception would leave behind.
        db.MollieWebhookEvents.Add(new MollieWebhookEvent
        {
            MollieResourceId = "tr_stuck",
            ResourceType = "payment",
            ResultStatus = "failed",
            ProcessedAt = null,
        });
        db.SaveChanges();

        var result = await controller.Handle("tr_stuck");

        Assert.IsType<OkResult>(result);
        var eventRows = await db.MollieWebhookEvents.Where(e => e.MollieResourceId == "tr_stuck").ToListAsync();
        Assert.Single(eventRows); // resumed the existing row, did not insert a second one
        Assert.NotNull(eventRows[0].ProcessedAt);

        Assert.Contains(db.AuditLogs, entry => entry.Action == "mollie.first_payment_failed" && entry.TenantId == tenant.Id);
    }

    [Fact]
    public async Task Handle_Payment_AlreadyFullyProcessed_IsSkipped()
    {
        var mollieCalls = 0;
        var (controller, db) = BuildController(req =>
        {
            mollieCalls++;
            return Json("{\"id\":\"tr_done\",\"status\":\"paid\",\"customerId\":\"cst_test\"}");
        });
        SeedActiveSubscription(db, "tr_done");
        db.MollieWebhookEvents.Add(new MollieWebhookEvent
        {
            MollieResourceId = "tr_done",
            ResourceType = "payment",
            ResultStatus = "paid",
            ProcessedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var result = await controller.Handle("tr_done");

        Assert.IsType<OkResult>(result);
        Assert.Equal(1, mollieCalls); // only the initial GetPaymentAsync fetch — no reprocessing
        Assert.Single(await db.MollieWebhookEvents.Where(e => e.MollieResourceId == "tr_done").ToListAsync());
    }
}
