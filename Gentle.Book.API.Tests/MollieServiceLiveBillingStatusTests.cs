using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the new SuperAdmin billing overview feature: GetLiveBillingStatusAsync fetches
// subscription/mandate/payment state directly from Mollie (not from GentleBook's local DB) so a
// SuperAdmin doesn't have to open Mollie's own dashboard to check on a customer.
public class MollieServiceLiveBillingStatusTests
{
    private static MollieService BuildMollieService(GentleBook.Api.Data.GentleBookDbContext db, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var auditService = new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions());
        var mollieClient = new MollieClient(
            new HttpClient(new FakeHttpMessageHandler(responder)) { BaseAddress = new Uri("https://api.mollie.com/v2/") },
            new StaticOptionsMonitor<MollieOptions>(new MollieOptions { ApiKey = "test_x" }));
        return new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), auditService, emailService, NullLogger<MollieService>.Instance);
    }

    private static (Tenant tenant, Subscription subscription) SeedActiveSubscription(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Billing", Slug = "salon-billing-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Plan = SubscriptionPlan.Agency,
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

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task GetLiveBillingStatusAsync_HappyPath_ReturnsMaskedIbanAndRecentPayments()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedActiveSubscription(db);

        var mollieService = BuildMollieService(db, req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains("/subscriptions/"))
                return Json("{\"id\":\"sub_test\",\"status\":\"active\",\"nextPaymentDate\":\"2026-09-30\"}");
            if (path.Contains("/mandates/"))
                return Json("{\"id\":\"mdt_test\",\"status\":\"valid\",\"details\":{\"consumerName\":\"Berk-Can Atesoglu\",\"consumerAccount\":\"DE32330500000001855311\"}}");
            if (path.Contains("/payments"))
                return Json("{\"_embedded\":{\"payments\":[" +
                    "{\"id\":\"tr_2\",\"status\":\"paid\",\"description\":\"GentleBook Agency-Abonnement\",\"createdAt\":\"2026-08-31T00:48:55+00:00\",\"amount\":{\"currency\":\"EUR\",\"value\":\"1.00\"}}," +
                    "{\"id\":\"tr_1\",\"status\":\"paid\",\"description\":\"GentleBook Starter-Plan – Einrichtung SEPA-Mandat\",\"createdAt\":\"2026-07-31T14:41:30+00:00\",\"amount\":{\"currency\":\"EUR\",\"value\":\"1.00\"}}" +
                    "]}}");
            throw new InvalidOperationException($"Unexpected request path: {path}");
        });

        var result = await mollieService.GetLiveBillingStatusAsync(tenant.Id);

        Assert.True(result.Available);
        Assert.Null(result.Error);
        Assert.Equal("active", result.SubscriptionStatus);
        Assert.Equal(new DateTime(2026, 9, 30), result.NextPaymentDate);
        Assert.Equal("valid", result.MandateStatus);
        Assert.Equal("Berk-Can Atesoglu", result.ConsumerName);
        Assert.Equal("DE32 •••• •••• •••• 5311", result.ConsumerAccountMasked);
        Assert.Equal(2, result.RecentPayments.Count);
        Assert.Equal("tr_2", result.RecentPayments[0].Id); // newest first
        Assert.Equal(1.00m, result.RecentPayments[0].Amount);
    }

    [Fact]
    public async Task GetLiveBillingStatusAsync_NoMollieCustomer_ReturnsUnavailableWithoutCallingMollie()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Ohne Mollie", Slug = "salon-ohne-mollie-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Plan = SubscriptionPlan.Trial,
            Status = SubscriptionStatus.Trial,
        };
        tenant.Subscription = subscription;
        db.AddRange(tenant, subscription);
        db.SaveChanges();

        var mollieService = BuildMollieService(db, _ => throw new InvalidOperationException("Should never call Mollie without a MollieCustomerId."));

        var result = await mollieService.GetLiveBillingStatusAsync(tenant.Id);

        Assert.False(result.Available);
        Assert.NotNull(result.Error);
        Assert.Empty(result.RecentPayments);
    }

    [Fact]
    public async Task GetLiveBillingStatusAsync_MollieOutage_ReturnsUnavailableInsteadOfThrowing()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedActiveSubscription(db);

        var mollieService = BuildMollieService(db, _ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

        var result = await mollieService.GetLiveBillingStatusAsync(tenant.Id);

        Assert.False(result.Available);
        Assert.NotNull(result.Error);
    }
}
