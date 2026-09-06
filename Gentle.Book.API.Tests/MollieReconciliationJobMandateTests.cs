using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

public class MollieReconciliationJobMandateTests
{
    private static (Tenant tenant, Subscription subscription) SeedActiveSubscription(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Mandat", Slug = "salon-mandat-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Plan = SubscriptionPlan.Starter,
            Interval = SubscriptionInterval.Monthly,
            Status = SubscriptionStatus.Active,
            MollieCustomerId = "cst_test",
            MollieMandateId = "mdt_test",
            MollieSubscriptionId = "sub_test",
            CurrentPeriodStart = DateTime.UtcNow,
            CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1),
        };
        tenant.Subscription = subscription;
        db.AddRange(tenant, subscription);
        db.SaveChanges();
        return (tenant, subscription);
    }

    private static MollieClient BuildMollieClient(string mandateStatusJson) =>
        new(
            new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(mandateStatusJson, System.Text.Encoding.UTF8, "application/json")
            }))
            { BaseAddress = new Uri("https://api.mollie.com/v2/") },
            new StaticOptionsMonitor<MollieOptions>(new MollieOptions { ApiKey = "test_x" }));

    private static AuditService BuildAuditService(GentleBook.Api.Data.GentleBookDbContext db) =>
        new(db, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, NullLogger<AuditService>.Instance);

    // CheckActiveMandatesAsync never touches the job's own scope factory (it's given db/mollie/audit
    // directly) — an empty one is enough to satisfy the constructor.
    private static IServiceScopeFactory EmptyScopeFactory() =>
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public async Task CheckActiveMandatesAsync_MandateRevoked_FlagsSubscriptionPastDue()
    {
        using var db = TestDbContextFactory.Create();
        var (_, subscription) = SeedActiveSubscription(db);

        var job = new MollieReconciliationJob(EmptyScopeFactory(), NullLogger<MollieReconciliationJob>.Instance);
        var mollie = BuildMollieClient("{\"id\":\"mdt_test\",\"status\":\"invalid\"}");
        var audit = BuildAuditService(db);

        var checkedCount = await job.CheckActiveMandatesAsync(db, mollie, audit);

        Assert.Equal(1, checkedCount);
        var reloaded = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == subscription.Id);
        Assert.Equal(SubscriptionStatus.PastDue, reloaded.Status);
        Assert.NotNull(reloaded.PastDueSince);
    }

    [Fact]
    public async Task CheckActiveMandatesAsync_MandateStillValid_LeavesSubscriptionActive()
    {
        using var db = TestDbContextFactory.Create();
        var (_, subscription) = SeedActiveSubscription(db);

        var job = new MollieReconciliationJob(EmptyScopeFactory(), NullLogger<MollieReconciliationJob>.Instance);
        var mollie = BuildMollieClient("{\"id\":\"mdt_test\",\"status\":\"valid\"}");
        var audit = BuildAuditService(db);

        await job.CheckActiveMandatesAsync(db, mollie, audit);

        var reloaded = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == subscription.Id);
        Assert.Equal(SubscriptionStatus.Active, reloaded.Status);
        Assert.Null(reloaded.PastDueSince);
    }
}
