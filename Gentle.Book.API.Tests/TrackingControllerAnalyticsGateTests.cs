using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers.Admin;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the HasAnalytics plan gate (TrackingController.cs) — flagged as a gap in the July audit
// and still had zero test coverage until this pass.
public class TrackingControllerAnalyticsGateTests
{
    private static TrackingController BuildController(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test-tenant", role: "TenantAdmin");
        var trackingService = new TrackingService(db, NullLogger<TrackingService>.Instance);
        return new TrackingController(trackingService, db, tenantContext);
    }

    [Theory]
    [InlineData(SubscriptionPlan.Trial)]
    [InlineData(SubscriptionPlan.Starter)]
    public async Task GetTrackingStatistics_NoAnalyticsPlan_Returns402(SubscriptionPlan plan)
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, plan);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetTrackingStatistics(null, null);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Theory]
    [InlineData(SubscriptionPlan.Professional)]
    [InlineData(SubscriptionPlan.Agency)]
    public async Task GetTrackingStatistics_AnalyticsPlan_Succeeds(SubscriptionPlan plan)
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, plan);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetTrackingStatistics(null, null);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetRevenueStatistics_StarterPlan_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetRevenueStatistics();

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetRevenueStatistics_ProfessionalPlan_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Professional);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetRevenueStatistics();

        Assert.IsType<OkObjectResult>(result.Result);
    }
}
