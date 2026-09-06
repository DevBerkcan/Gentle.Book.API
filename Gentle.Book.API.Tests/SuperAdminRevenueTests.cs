using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the new /api/superadmin/revenue endpoint: realized (Mollie-collected) revenue, churn
// rate, and trial→paid conversion rate per period — the tracking gap the user asked to close,
// distinct from the MRR snapshot on /stats.
public class SuperAdminRevenueTests
{
    private static SuperAdminController BuildController(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var httpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.SuperAdmin(Guid.NewGuid()) };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var audit = new AuditService(db, accessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions());
        var mollieClient = new MollieClient(new HttpClient(), new StaticOptionsMonitor<MollieOptions>(new MollieOptions()));
        var mollieService = new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), audit, emailService, NullLogger<MollieService>.Instance);
        return new SuperAdminController(
            db, TestConfiguration.Build(), emailService, NullLogger<SuperAdminController>.Instance,
            new JwtService(TestConfiguration.Build()), new FakeWebHostEnvironment(), audit, mollieService, new FakeBackgroundJobClient())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static Tenant SeedTenant(GentleBook.Api.Data.GentleBookDbContext db, string name)
    {
        var tenant = new Tenant { Name = name, Slug = name.ToLowerInvariant() + "-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        return tenant;
    }

    [Fact]
    public async Task GetRevenue_Monthly_BucketsInvoicesByIssueMonth()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, "Salon Umsatz");
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant, Status = SubscriptionStatus.Active, Plan = SubscriptionPlan.Starter };
        db.Subscriptions.Add(subscription);
        db.Invoices.Add(new Invoice { TenantId = tenant.Id, SubscriptionId = subscription.Id, InvoiceNumber = "2026-0001", IssueDate = DateTime.UtcNow, Amount = 29m, PlanName = "Starter" });
        db.Invoices.Add(new Invoice { TenantId = tenant.Id, SubscriptionId = subscription.Id, InvoiceNumber = "2026-0002", IssueDate = DateTime.UtcNow.AddMonths(-2), Amount = 59m, PlanName = "Professional" });
        db.SaveChanges();

        var controller = BuildController(db);

        var result = await controller.GetRevenue("month", 3);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var buckets = doc.RootElement.GetProperty("Buckets").EnumerateArray().ToList();

        Assert.Equal(3, buckets.Count);
        Assert.Equal(29m, buckets[2].GetProperty("RealizedRevenue").GetDecimal()); // this month, oldest-first ordering
        Assert.Equal(59m, buckets[0].GetProperty("RealizedRevenue").GetDecimal()); // 2 months ago
        Assert.Equal(0m, buckets[1].GetProperty("RealizedRevenue").GetDecimal());  // 1 month ago, nothing booked
    }

    [Fact]
    public async Task GetRevenue_Monthly_ComputesChurnRateForCurrentPeriod()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, "Salon Churn");
        db.Subscriptions.Add(new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Status = SubscriptionStatus.Cancelled,
            Plan = SubscriptionPlan.Starter,
            MollieMandateSignedAt = DateTime.UtcNow.AddMonths(-2),
            CancelledAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var controller = BuildController(db);

        var result = await controller.GetRevenue("month", 1);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var bucket = doc.RootElement.GetProperty("Buckets").EnumerateArray().Single();

        Assert.Equal(1, bucket.GetProperty("ChurnedTenants").GetInt32());
        Assert.Equal(1, bucket.GetProperty("ActiveTenantsAtStart").GetInt32());
        Assert.Equal(100.0m, bucket.GetProperty("ChurnRatePercent").GetDecimal());
    }

    [Fact]
    public async Task GetRevenue_Monthly_ComputesTrialConversionRateForCurrentPeriod()
    {
        using var db = TestDbContextFactory.Create();
        var converted = SeedTenant(db, "Salon Konvertiert");
        db.Subscriptions.Add(new Subscription
        {
            TenantId = converted.Id, Tenant = converted, Status = SubscriptionStatus.Active, Plan = SubscriptionPlan.Starter,
            TrialEndsAt = DateTime.UtcNow, MollieMandateSignedAt = DateTime.UtcNow,
        });
        var notConverted = SeedTenant(db, "Salon Abgesprungen");
        db.Subscriptions.Add(new Subscription
        {
            TenantId = notConverted.Id, Tenant = notConverted, Status = SubscriptionStatus.Expired, Plan = SubscriptionPlan.Trial,
            TrialEndsAt = DateTime.UtcNow, MollieMandateSignedAt = null,
        });
        db.SaveChanges();

        var controller = BuildController(db);

        var result = await controller.GetRevenue("month", 1);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var bucket = doc.RootElement.GetProperty("Buckets").EnumerateArray().Single();

        Assert.Equal(2, bucket.GetProperty("TrialsEnded").GetInt32());
        Assert.Equal(1, bucket.GetProperty("Converted").GetInt32());
        Assert.Equal(50.0m, bucket.GetProperty("ConversionRatePercent").GetDecimal());
    }

    [Fact]
    public async Task GetRevenue_Weekly_ReturnsRequestedNumberOfBuckets()
    {
        using var db = TestDbContextFactory.Create();

        var controller = BuildController(db);

        var result = await controller.GetRevenue("week", 4);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal("week", doc.RootElement.GetProperty("Granularity").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("Buckets").GetArrayLength());
    }
}
