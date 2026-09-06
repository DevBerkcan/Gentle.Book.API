using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the "Custom/white-label domain" Agency-exclusive feature (TenantController.cs domain
// endpoints) — had zero test coverage before this pass. Real DNS verification can't be
// meaningfully unit-tested (see AGENCY_FEATURE_QA_CHECKLIST.md); this covers the gate and the
// hostname-format validation, which are pure logic.
public class TenantControllerCustomDomainTests
{
    private static (Tenant tenant, TenantSettings settings) SeedTenantWithSettings(
        GentleBook.Api.Data.GentleBookDbContext db, SubscriptionPlan plan)
    {
        var (tenant, _) = AgencyTenantFactory.Seed(db, plan);
        var settings = new TenantSettings { TenantId = tenant.Id, CompanyName = tenant.Name };
        db.TenantSettings.Add(settings);
        db.SaveChanges();
        return (tenant, settings);
    }

    [Fact]
    public async Task GetDomain_NonAgencyPlan_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenantWithSettings(db, SubscriptionPlan.Professional);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.GetDomain();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetDomain_AgencyPlan_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenantWithSettings(db, SubscriptionPlan.Agency);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.GetDomain();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UpdateDomain_InvalidHostname_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenantWithSettings(db, SubscriptionPlan.Agency);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpdateDomain(new UpdateCustomDomainDto("not a valid host!!"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateDomain_ValidHostname_SetsPendingVerification()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, settings) = SeedTenantWithSettings(db, SubscriptionPlan.Agency);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpdateDomain(new UpdateCustomDomainDto("buchung.kunde-beispiel.de"));

        Assert.IsType<OkObjectResult>(result);
        var reloaded = await db.TenantSettings.FindAsync(settings.Id);
        Assert.Equal("buchung.kunde-beispiel.de", reloaded!.CustomDomain);
        Assert.Equal("PendingVerification", reloaded.CustomDomainStatus);
    }
}
