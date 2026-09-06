using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Middleware;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers a bug found during the Mollie go-live audit: a PastDue tenant (failed SEPA collection)
// got a blanket 402 from every tenant-scoped route, including the cancel endpoint itself — so a
// customer in payment default had no self-service way to cancel until the dunning job gave up
// on them up to 7 days later. Billing paths must stay reachable while PastDue, exactly like the
// existing Expired exemption.
public class TenantMiddlewarePastDueTests
{
    private static (Tenant tenant, Subscription subscription) SeedPastDueSubscription(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Zahlungsverzug", Slug = "salon-pastdue-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Plan = SubscriptionPlan.Starter,
            Interval = SubscriptionInterval.Monthly,
            Status = SubscriptionStatus.PastDue,
            PastDueSince = DateTime.UtcNow.AddDays(-2),
        };
        tenant.Subscription = subscription;
        db.AddRange(tenant, subscription);
        db.SaveChanges();
        return (tenant, subscription);
    }

    private static HttpContext BuildContext(Guid tenantId, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenantId);
        return context;
    }

    [Fact]
    public async Task InvokeAsync_PastDueTenant_CancelEndpoint_IsReachable()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedPastDueSubscription(db);

        var nextCalled = false;
        var middleware = new TenantMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = BuildContext(tenant.Id, "/api/tenant/subscription/cancel");

        await middleware.InvokeAsync(context, new TenantContext(), db);

        Assert.True(nextCalled);
        Assert.NotEqual(402, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_PastDueTenant_NonBillingEndpoint_Is402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedPastDueSubscription(db);

        var nextCalled = false;
        var middleware = new TenantMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = BuildContext(tenant.Id, "/api/bookings");

        await middleware.InvokeAsync(context, new TenantContext(), db);

        Assert.False(nextCalled);
        Assert.Equal(402, context.Response.StatusCode);
    }
}
