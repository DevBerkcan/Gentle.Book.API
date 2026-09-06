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

// Covers a bug found during the Mollie go-live audit: a tenant who clicks "cancel" and then
// later accepts a still-pending Agency offer ended up billed the new negotiated price while
// remaining scheduled for cancellation at period end, because ApplyAcceptedAgencyOfferAsync
// never cleared the pending-cancellation fields.
public class MollieServiceAgencyOfferCancellationTests
{
    private static MollieService BuildMollieService(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var auditService = new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions());
        var mollieClient = new MollieClient(
            new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"sub_test\",\"status\":\"active\"}", System.Text.Encoding.UTF8, "application/json")
            }))
            { BaseAddress = new Uri("https://api.mollie.com/v2/") },
            new StaticOptionsMonitor<MollieOptions>(new MollieOptions { ApiKey = "test_x" }));
        return new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), auditService, emailService, NullLogger<MollieService>.Instance);
    }

    private static (Tenant tenant, Subscription subscription) SeedCancellingSubscriptionWithPendingOffer(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Agency-Angebot", Slug = "salon-agency-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Plan = SubscriptionPlan.Professional,
            Interval = SubscriptionInterval.Monthly,
            Status = SubscriptionStatus.Active,
            MollieCustomerId = "cst_test",
            MollieSubscriptionId = "sub_test",
            CurrentPeriodStart = DateTime.UtcNow,
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(10),
            NegotiatedMonthlyPrice = 149m,
            NegotiatedAnnualPrice = 1490m,
            // Tenant already clicked "cancel" before the Agency offer was accepted.
            CancelRequestedAt = DateTime.UtcNow.AddDays(-1),
            CancelReason = "zu teuer",
        };
        tenant.Subscription = subscription;
        db.AddRange(tenant, subscription);
        db.SaveChanges();
        return (tenant, subscription);
    }

    [Fact]
    public async Task ApplyAcceptedAgencyOfferAsync_PendingCancellation_IsClearedOnSuccess()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedCancellingSubscriptionWithPendingOffer(db);
        var mollieService = BuildMollieService(db);

        var result = await mollieService.ApplyAcceptedAgencyOfferAsync(tenant.Id, SubscriptionInterval.Monthly);

        Assert.True(result.Success);
        Assert.Equal("Agency", result.Plan);

        var reloaded = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.TenantId == tenant.Id);
        Assert.Equal(SubscriptionPlan.Agency, reloaded.Plan);
        Assert.Null(reloaded.CancelRequestedAt);
        Assert.Null(reloaded.CancelledAt);
        Assert.Null(reloaded.CancelReason);
    }
}
