using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the "Loyalty points program" Agency-exclusive feature (CustomersController.cs) — had
// zero test coverage before this pass.
public class CustomersControllerLoyaltyTests
{
    private static CustomersController BuildController(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test-tenant", role: "TenantAdmin");
        var emailService = TestServiceFactory.CreateEmailService(db);
        var customerService = new CustomerService(db, NullLogger<CustomerService>.Instance, tenantContext, emailService);
        var loyaltyService = new LoyaltyService(db, NullLogger<LoyaltyService>.Instance);

        var controller = new CustomersController(customerService, db, tenantContext, loyaltyService, NullLogger<CustomersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenantId) },
            },
        };
        return controller;
    }

    private static Customer SeedCustomer(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId)
    {
        var customer = new Customer { TenantId = tenantId, FirstName = "Kim", LastName = "Kunde", Email = "kim@example.test" };
        db.Customers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    [Fact]
    public async Task GetLoyalty_NonAgencyPlan_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Professional);
        var customer = SeedCustomer(db, tenant.Id);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetLoyalty(customer.Id);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetLoyalty_AgencyPlan_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency);
        var customer = SeedCustomer(db, tenant.Id);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.GetLoyalty(customer.Id);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task AdjustLoyalty_AgencyPlan_UpdatesBalance()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency);
        var customer = SeedCustomer(db, tenant.Id);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.AdjustLoyalty(customer.Id, new AdjustLoyaltyRequestDto(10, "Willkommensbonus"));

        Assert.IsType<OkObjectResult>(result);
        var reloaded = await db.Customers.FindAsync(customer.Id);
        Assert.Equal(10, reloaded!.LoyaltyPoints);
    }
}
