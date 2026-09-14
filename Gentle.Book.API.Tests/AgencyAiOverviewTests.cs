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

// Covers the platform-wide Agency AI cost overview (SuperAdminController.GetAgencyAiOverview) —
// the aggregate, "what is this costing us as Agency scales" view that sits alongside the existing
// per-tenant GetTenantAiUsage. Only Agency-plan tenants should ever appear here (no other plan
// ever triggers a real LLM call, per AgencyFeatureGate).
public class AgencyAiOverviewTests
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

    private static Tenant SeedAgencyTenant(GentleBook.Api.Data.GentleBookDbContext db, string name, decimal? negotiatedPrice)
    {
        var tenant = new Tenant { Name = name, Slug = name.ToLowerInvariant().Replace(" ", "-") + "-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = SubscriptionPlan.Agency, Status = SubscriptionStatus.Active, NegotiatedMonthlyPrice = negotiatedPrice });
        return tenant;
    }

    [Fact]
    public async Task GetAgencyAiOverview_NonAgencyTenants_AreExcluded()
    {
        using var db = TestDbContextFactory.Create();
        var starterTenant = new Tenant { Name = "Salon Starter", Slug = "salon-starter-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(starterTenant);
        db.Subscriptions.Add(new Subscription { TenantId = starterTenant.Id, Tenant = starterTenant, Plan = SubscriptionPlan.Starter, Status = SubscriptionStatus.Active });
        var agencyTenant = SeedAgencyTenant(db, "Salon Agency", 150m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.GetAgencyAiOverview();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("agencyTenantCount", json);
        Assert.Contains("Salon Agency", json);
        Assert.DoesNotContain("Salon Starter", json);
    }

    [Fact]
    public async Task GetAgencyAiOverview_SumsCostAcrossTenants()
    {
        using var db = TestDbContextFactory.Create();
        var t1 = SeedAgencyTenant(db, "Salon Eins", 100m);
        var t2 = SeedAgencyTenant(db, "Salon Zwei", 200m);
        await db.SaveChangesAsync();
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        db.AiUsages.Add(new AiUsage { TenantId = t1.Id, Feature = "ai_service_finder", Model = "gpt-4o-mini", EstimatedCost = 3.5m, CreatedOn = monthStart.AddDays(1) });
        db.AiUsages.Add(new AiUsage { TenantId = t2.Id, Feature = "ai_chatbot", Model = "gpt-4o-mini", EstimatedCost = 1.25m, CreatedOn = monthStart.AddDays(2) });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.GetAgencyAiOverview();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("4.75", json); // 3.5 + 1.25 totalSpendThisMonth
    }

    [Fact]
    public async Task GetAgencyAiOverview_WorstCase_UsesCustomBudgetOverPlatformDefault()
    {
        using var db = TestDbContextFactory.Create();
        var t1 = SeedAgencyTenant(db, "Salon Custom", 100m);
        var t2 = SeedAgencyTenant(db, "Salon Default", 100m);
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = t1.Id, MonthlyBudgetUsd = 75m });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.GetAgencyAiOverview();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        // 75 (custom) + 20 (platform default) = 95
        Assert.Contains("95", json);
    }

    [Fact]
    public async Task GetAgencyAiOverview_SortsByPercentUsedDescending()
    {
        using var db = TestDbContextFactory.Create();
        var low = SeedAgencyTenant(db, "Salon Niedrig", 100m);
        var high = SeedAgencyTenant(db, "Salon Hoch", 100m);
        await db.SaveChangesAsync();
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        db.AiUsages.Add(new AiUsage { TenantId = low.Id, Feature = "ai_service_finder", Model = "gpt-4o-mini", EstimatedCost = 1m, CreatedOn = monthStart.AddDays(1) });
        db.AiUsages.Add(new AiUsage { TenantId = high.Id, Feature = "ai_service_finder", Model = "gpt-4o-mini", EstimatedCost = 18m, CreatedOn = monthStart.AddDays(1) });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.GetAgencyAiOverview();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        // "Salon Hoch" (18/20=90%) must appear before "Salon Niedrig" (1/20=5%) in the serialized tenants array.
        Assert.True(json.IndexOf("Salon Hoch", StringComparison.Ordinal) < json.IndexOf("Salon Niedrig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAgencyAiOverview_NoAgencyTenants_ReturnsZeroedOverview()
    {
        using var db = TestDbContextFactory.Create();
        var controller = BuildController(db);

        var result = await controller.GetAgencyAiOverview();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"agencyTenantCount\":0", json);
    }
}
