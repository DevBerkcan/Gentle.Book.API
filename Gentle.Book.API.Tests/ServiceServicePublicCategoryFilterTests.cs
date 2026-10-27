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

        var employee = new Employee { TenantId = tenant.Id, Name = "Mara Mitarbeiterin", IsActive = true };
        db.Employees.Add(employee);
        var bookableService = new Service { TenantId = tenant.Id, CategoryId = withService.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(bookableService);
        db.ServiceEmployees.Add(new ServiceEmployee { ServiceId = bookableService.Id, EmployeeId = employee.Id });
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

    // Found during further live pre-launch testing: deactivating a service's only assigned
    // employee left it still listed on the public booking page — the customer only discovered
    // in the next step that "Für diesen Service sind aktuell keine Mitarbeiter verfügbar". A
    // service with no active employee assigned (none at all, or its only one deactivated) can
    // never actually be booked, so it must be hidden the same way an empty category is.
    private static (Tenant tenant, Service unbookable) SeedTenantWithUnbookableService(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Unbuchbar", Slug = "salon-unbuchbar-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Gesicht", IsActive = true };
        db.ServiceCategories.Add(category);
        var inactiveEmployee = new Employee { TenantId = tenant.Id, Name = "Tea Test", IsActive = false };
        db.Employees.Add(inactiveEmployee);
        var unbookable = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Hautanalyse", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(unbookable);
        db.ServiceEmployees.Add(new ServiceEmployee { ServiceId = unbookable.Id, EmployeeId = inactiveEmployee.Id });
        db.SaveChanges();
        return (tenant, unbookable);
    }

    [Fact]
    public async Task GetServicesAsync_OnlyAssignedEmployeeIsInactive_ServiceIsHidden()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, unbookable) = SeedTenantWithUnbookableService(db);
        var service = Build(db, tenant.Id);

        var services = await service.GetServicesAsync(employeeId: null);

        Assert.DoesNotContain(services, s => s.Id == unbookable.Id);
    }

    [Fact]
    public async Task GetServiceCategoriesAsync_OnlyServiceHasNoActiveEmployee_CategoryIsHidden()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenantWithUnbookableService(db);
        var service = Build(db, tenant.Id);

        var categories = await service.GetServiceCategoriesAsync(employeeId: null);

        Assert.Empty(categories);
    }

    [Fact]
    public async Task GetServicesByCategoryAsync_OnlyAssignedEmployeeIsInactive_ServiceIsHidden()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, unbookable) = SeedTenantWithUnbookableService(db);
        var service = Build(db, tenant.Id);

        var (services, categoryExists) = await service.GetServicesByCategoryAsync(unbookable.CategoryId, employeeId: null);

        Assert.True(categoryExists);
        Assert.DoesNotContain(services!, s => s.Id == unbookable.Id);
    }
}
