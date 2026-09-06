using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the "Digital intake forms" Agency-exclusive feature (AdminIntakeFormController.cs) —
// had zero test coverage before this pass. Also covers IntakeFormIndustryGate, the additional
// industry restriction layered on top of the plan gate.
public class AdminIntakeFormControllerAgencyGateTests
{
    private static AdminIntakeFormController BuildController(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test-tenant", role: "TenantAdmin");

        return new AdminIntakeFormController(db, tenantContext, NullLogger<AdminIntakeFormController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenantId) },
            },
        };
    }

    [Fact]
    public async Task GetFields_NonAgencyPlan_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Professional, IndustryType.Hairdresser);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetFields();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetFields_AgencyPlanDisallowedIndustry_Returns403()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency, IndustryType.Physio);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetFields();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetFields_AgencyPlanAllowedIndustry_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency, IndustryType.Hairdresser);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetFields();

        Assert.IsType<OkObjectResult>(result);
    }
}
