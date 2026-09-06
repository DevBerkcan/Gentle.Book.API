using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the "Vouchers / discount codes" Agency-exclusive feature (AdminVoucherController.cs) —
// had zero test coverage before this pass.
public class AdminVoucherControllerAgencyGateTests
{
    private static AdminVoucherController BuildController(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test-tenant", role: "TenantAdmin");
        var voucherService = TestServiceFactory.CreateVoucherService(db);
        var emailService = TestServiceFactory.CreateEmailService(db);

        return new AdminVoucherController(db, tenantContext, voucherService, emailService, NullLogger<AdminVoucherController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenantId) },
            },
        };
    }

    [Fact]
    public async Task GetAll_NonAgencyPlan_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Professional);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetAll(null);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetAll_AgencyPlan_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetAll(null);

        Assert.IsType<OkObjectResult>(result);
    }
}
