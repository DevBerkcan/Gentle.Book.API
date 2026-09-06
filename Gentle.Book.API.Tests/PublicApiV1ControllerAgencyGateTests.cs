using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the "Public API access" Agency-exclusive feature (PublicApiV1Controller.cs) — had zero
// test coverage before this pass.
public class PublicApiV1ControllerAgencyGateTests
{
    private static PublicApiV1Controller BuildController(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test-tenant", role: "TenantAdmin");

        var serviceService = new ServiceService(db, NullLogger<ServiceService>.Instance, tenantContext);
        var employeeService = new EmployeeService(db, NullLogger<EmployeeService>.Instance, tenantContext);
        var voucherService = TestServiceFactory.CreateVoucherService(db);
        var bookingService = new BookingService(db, NullLogger<BookingService>.Instance, TestServiceFactory.CreateEmailService(db), new FakeBackgroundJobClient(), voucherService);

        return new PublicApiV1Controller(db, tenantContext, serviceService, employeeService, bookingService);
    }

    [Fact]
    public async Task GetServices_NonAgencyPlan_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Professional);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetServices();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetServices_AgencyPlan_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetServices();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetEmployees_AgencyPlan_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetEmployees();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetBookings_NonAgencyPlan_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetBookings(null, null);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }
}
