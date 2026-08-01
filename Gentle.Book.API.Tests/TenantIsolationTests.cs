using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gentle.Book.API.Tests;

/// <summary>
/// GentleBookDbContext's global query filter (CurrentTenantId == null || x.TenantId ==
/// CurrentTenantId) is the platform's core cross-tenant defense — every tenant-scoped entity
/// relies on it. These tests prove it actually filters when a tenant context is set (the
/// AvailabilityService bug fixed earlier this session was exactly a code path that queried
/// without ever setting one).
/// </summary>
public class TenantIsolationTests
{
    [Fact]
    public async Task Employees_QueriedWithTenantContext_OnlyReturnsOwnTenantsRows()
    {
        var options = new DbContextOptionsBuilder<GentleBookDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();

        // Seed via a context with no tenant filter active, so both tenants' rows actually land in the DB.
        using (var seedContext = new GentleBookDbContext(options, tenantContext: null))
        {
            seedContext.Employees.Add(new Employee { Id = Guid.NewGuid(), TenantId = tenantAId, Name = "Anna A", IsActive = true });
            seedContext.Employees.Add(new Employee { Id = Guid.NewGuid(), TenantId = tenantBId, Name = "Ben B", IsActive = true });
            await seedContext.SaveChangesAsync();
        }

        var tenantAContext = new TenantContext();
        tenantAContext.Set(tenantAId, "tenant-a", isSuperAdmin: false, role: "TenantAdmin");

        using var scopedContext = new GentleBookDbContext(options, tenantAContext);
        var visibleEmployees = await scopedContext.Employees.ToListAsync();

        Assert.Single(visibleEmployees);
        Assert.Equal(tenantAId, visibleEmployees[0].TenantId);
        Assert.All(visibleEmployees, e => Assert.NotEqual(tenantBId, e.TenantId));
    }

    [Fact]
    public async Task Employees_QueriedWithNoTenantContext_ReturnsEveryTenantsRows()
    {
        // Documents the known, deliberate shape of the filter: CurrentTenantId == null is a
        // no-op, matching every tenant — this is exactly why unauthenticated/public code paths
        // (see AvailabilityService) must always resolve and apply an explicit TenantId filter
        // themselves rather than relying on this global filter alone.
        var options = new DbContextOptionsBuilder<GentleBookDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();

        using (var seedContext = new GentleBookDbContext(options, tenantContext: null))
        {
            seedContext.Employees.Add(new Employee { Id = Guid.NewGuid(), TenantId = tenantAId, Name = "Anna A", IsActive = true });
            seedContext.Employees.Add(new Employee { Id = Guid.NewGuid(), TenantId = tenantBId, Name = "Ben B", IsActive = true });
            await seedContext.SaveChangesAsync();
        }

        using var unscopedContext = new GentleBookDbContext(options, tenantContext: null);
        var allEmployees = await unscopedContext.Employees.ToListAsync();

        Assert.Equal(2, allEmployees.Count);
    }
}
