using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: empty service categories (active, but with zero active
// services in them) showed up on the public booking page as selectable-looking sections with
// nothing in them. The "drop empty categories" filter only ran when an employeeId was supplied
// (an authenticated-employee booking context) — the plain anonymous public booking page, the
// common case, never got it.
public class ServiceServicePublicCategoryFilterTests
{
    private static ServiceService Build(GentleBook.Api.Data.GentleBookDbContext db, Guid? tenantId) =>
        new(db, NullLogger<ServiceService>.Instance, BuildTenantContext(tenantId));

    private static ITenantContext BuildTenantContext(Guid? tenantId)
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, "test-tenant");
        return ctx;
    }

    private static Tenant SeedTenantWithCategories(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Kategorien", Slug = "salon-kategorien-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);

        var withService = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt", IsActive = true, DisplayOrder = 0 };
        var empty = new ServiceCategory { TenantId = tenant.Id, Name = "Farbe", IsActive = true, DisplayOrder = 1 };
        db.ServiceCategories.AddRange(withService, empty);

        db.Services.Add(new Service { TenantId = tenant.Id, CategoryId = withService.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true });
        // "Farbe" category intentionally gets no Service row at all.

        db.SaveChanges();
        return tenant;
    }

    [Fact]
    public async Task GetServiceCategoriesAsync_NoEmployeeFilter_ExcludesCategoryWithNoServices()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenantWithCategories(db);
        var service = Build(db, tenant.Id);

        var categories = await service.GetServiceCategoriesAsync(employeeId: null);

        Assert.Single(categories);
        Assert.Equal("Haarschnitt", categories[0].Name);
    }

    [Fact]
    public async Task GetServiceCategoriesAsync_CategoryHasAnActiveService_IsReturned()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenantWithCategories(db);
        var service = Build(db, tenant.Id);

        var categories = await service.GetServiceCategoriesAsync(employeeId: null);

        var kept = Assert.Single(categories);
        Assert.Single(kept.Services);
    }
}
