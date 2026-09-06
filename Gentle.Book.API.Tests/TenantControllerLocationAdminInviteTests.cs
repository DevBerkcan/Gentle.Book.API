using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the "Multi-location delegated LocationAdmin role" Agency-exclusive feature — the pure
// scoping logic (LocationScopeAuthorization) already has its own test file; this covers the
// invite endpoint's own Agency-plan gate, which had zero coverage before this pass.
public class TenantControllerLocationAdminInviteTests
{
    private static (Tenant tenant, BusinessLocation location) SeedTenantWithLocation(
        GentleBook.Api.Data.GentleBookDbContext db, SubscriptionPlan plan)
    {
        var (tenant, _) = AgencyTenantFactory.Seed(db, plan);
        var location = new BusinessLocation { TenantId = tenant.Id, Name = "Filiale Mitte", City = "Musterstadt", CountryCode = "DE" };
        db.BusinessLocations.Add(location);
        db.SaveChanges();
        return (tenant, location);
    }

    [Fact]
    public async Task InviteLocationAdmin_NonAgencyPlan_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, location) = SeedTenantWithLocation(db, SubscriptionPlan.Professional);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.InviteLocationAdmin(location.Id, new InviteLocationAdminDto("admin@filiale.de", "Ada"));

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
        Assert.False(await db.PlatformUsers.AnyAsync(u => u.Email == "admin@filiale.de"));
    }

    [Fact]
    public async Task InviteLocationAdmin_AgencyPlan_CreatesLocationAdminUser()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, location) = SeedTenantWithLocation(db, SubscriptionPlan.Agency);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.InviteLocationAdmin(location.Id, new InviteLocationAdminDto("admin@filiale.de", "Ada"));

        Assert.IsType<OkObjectResult>(result);
        var created = await db.PlatformUsers.FirstOrDefaultAsync(u => u.Email == "admin@filiale.de");
        Assert.NotNull(created);
        Assert.Equal(PlatformRole.LocationAdmin, created!.Role);
        Assert.Equal(location.Id, created.LocationId);
    }
}
