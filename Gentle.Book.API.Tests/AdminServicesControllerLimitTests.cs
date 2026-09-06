using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers server-side plan-limit enforcement on service creation (Starter=15, Professional=50) —
// had zero test coverage before this pass.
public class AdminServicesControllerLimitTests
{
    private static (Tenant tenant, ServiceCategory category) SeedTenantWithCategory(
        GentleBook.Api.Data.GentleBookDbContext db, SubscriptionPlan plan)
    {
        var (tenant, _) = AgencyTenantFactory.Seed(db, plan);
        db.BusinessLocations.Add(new BusinessLocation { TenantId = tenant.Id, Name = "Hauptstandort", City = "Musterstadt", CountryCode = "DE", IsDefault = true, IsActive = true });
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        db.SaveChanges();
        return (tenant, category);
    }

    private static AdminServicesController BuildController(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test-tenant", role: "TenantAdmin");
        var serviceService = new ServiceService(db, NullLogger<ServiceService>.Instance, tenantContext);

        var controller = new AdminServicesController(serviceService, db, NullLogger<AdminServicesController>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(tenantContext);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider(),
                User = ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenantId),
            },
        };
        return controller;
    }

    private static void SeedActiveServices(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId, Guid categoryId, int count)
    {
        for (var i = 0; i < count; i++)
            db.Services.Add(new Service { TenantId = tenantId, CategoryId = categoryId, Name = $"Service {i}", DurationMinutes = 30, Price = 20m, IsActive = true });
        db.SaveChanges();
    }

    private static CreateServiceDto NewServiceDto(Guid categoryId) =>
        new("Neuer Service", null, 30, 0, 25m, 0, categoryId, "EUR");

    [Fact]
    public async Task CreateService_StarterAtServiceLimit_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, category) = SeedTenantWithCategory(db, SubscriptionPlan.Starter);
        SeedActiveServices(db, tenant.Id, category.Id, count: 15); // Starter MaxServices = 15
        var controller = BuildController(db, tenant.Id);

        var result = await controller.CreateService(NewServiceDto(category.Id));

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(402, objectResult.StatusCode);
        Assert.Equal(15, await db.Services.CountAsync(s => s.TenantId == tenant.Id));
    }

    [Fact]
    public async Task CreateService_StarterUnderServiceLimit_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, category) = SeedTenantWithCategory(db, SubscriptionPlan.Starter);
        SeedActiveServices(db, tenant.Id, category.Id, count: 14);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.CreateService(NewServiceDto(category.Id));

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(15, await db.Services.CountAsync(s => s.TenantId == tenant.Id));
    }

    [Fact]
    public async Task CreateService_ProfessionalAtSameCountAsStarterLimit_StillSucceeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, category) = SeedTenantWithCategory(db, SubscriptionPlan.Professional);
        SeedActiveServices(db, tenant.Id, category.Id, count: 15); // Would block Starter, fine on Professional (50)
        var controller = BuildController(db, tenant.Id);

        var result = await controller.CreateService(NewServiceDto(category.Id));

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }
}
