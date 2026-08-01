// Gentle.Book.API.Tests/BrandImportTenantIsolationTests.cs
// Covers spec section 19 "Tests" → Tenant: "Tenant A sieht keine Analyse von Tenant B" for the
// new BrandImportJob/BrandImportResult entities, following the same pattern as
// TenantIsolationTests.cs for the existing global query filter mechanism.
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gentle.Book.API.Tests;

public class BrandImportTenantIsolationTests
{
    [Fact]
    public async Task BrandImportJobs_QueriedWithTenantContext_OnlyReturnsOwnTenantsJobs()
    {
        var options = new DbContextOptionsBuilder<GentleBookDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();

        using (var seedContext = new GentleBookDbContext(options, tenantContext: null))
        {
            seedContext.BrandImportJobs.Add(new BrandImportJob { TenantId = tenantAId, SourceUrl = "https://salon-a.example/", CreatedBy = Guid.NewGuid() });
            seedContext.BrandImportJobs.Add(new BrandImportJob { TenantId = tenantBId, SourceUrl = "https://salon-b.example/", CreatedBy = Guid.NewGuid() });
            await seedContext.SaveChangesAsync();
        }

        var tenantAContext = new TenantContext();
        tenantAContext.Set(tenantAId, "tenant-a", isSuperAdmin: false, role: "TenantAdmin");

        using var scopedContext = new GentleBookDbContext(options, tenantAContext);
        var visibleJobs = await scopedContext.BrandImportJobs.ToListAsync();

        Assert.Single(visibleJobs);
        Assert.Equal(tenantAId, visibleJobs[0].TenantId);
    }

    [Fact]
    public async Task BrandImportResults_QueriedWithTenantContext_OnlyReturnsOwnTenantsResults()
    {
        var options = new DbContextOptionsBuilder<GentleBookDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        var resultAId = Guid.NewGuid();
        var resultBId = Guid.NewGuid();

        using (var seedContext = new GentleBookDbContext(options, tenantContext: null))
        {
            seedContext.BrandImportResults.Add(new BrandImportResult { Id = resultAId, TenantId = tenantAId, JobId = Guid.NewGuid(), WebsiteTitle = "Salon A" });
            seedContext.BrandImportResults.Add(new BrandImportResult { Id = resultBId, TenantId = tenantBId, JobId = Guid.NewGuid(), WebsiteTitle = "Salon B" });
            await seedContext.SaveChangesAsync();
        }

        var tenantAContext = new TenantContext();
        tenantAContext.Set(tenantAId, "tenant-a", isSuperAdmin: false, role: "TenantAdmin");

        using var scopedContext = new GentleBookDbContext(options, tenantAContext);

        // Simulates the controller's `.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId)`
        // guard: even trying to fetch Tenant B's result by its known id must not succeed once a
        // different tenant's context is active.
        var crossTenantLookup = await scopedContext.BrandImportResults.FirstOrDefaultAsync(r => r.Id == resultBId);
        Assert.Null(crossTenantLookup);

        var ownLookup = await scopedContext.BrandImportResults.FirstOrDefaultAsync(r => r.Id == resultAId);
        Assert.NotNull(ownLookup);
    }
}
