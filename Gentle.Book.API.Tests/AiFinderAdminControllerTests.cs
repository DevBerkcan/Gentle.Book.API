using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Gentle.Book.API.Tests;

/// <summary>
/// Covers the Copilot-audit findings on AiFinderAdminController: Preview was missing the
/// IsAdmin() role check every other write/preview action already had, and enabling the AI
/// Finder had no relationship to the tenant's plan at all (any Trial tenant could turn it on).
/// </summary>
public class AiFinderAdminControllerTests
{
    private static (Tenant tenant, IndustryProfile profile) SeedTenant(
        GentleBook.Api.Data.GentleBookDbContext db, SubscriptionPlan plan)
    {
        var tenant = new Tenant { Name = "Barber Wagner", Slug = "barber-wagner", IsActive = true };
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = plan, Status = SubscriptionStatus.Active };
        tenant.Subscription = subscription;
        var profile = new IndustryProfile { Key = "barbershop", Name = "Barbershop", IsActive = true };

        db.Tenants.Add(tenant);
        db.Subscriptions.Add(subscription);
        db.IndustryProfiles.Add(profile);
        db.SaveChanges();

        return (tenant, profile);
    }

    private static UpsertTenantIndustrySettingsRequestDto Request(Guid profileId, bool isFinderEnabled) => new(
        profileId, isFinderEnabled, SettingsJson: null, EnabledCapabilities: Array.Empty<string>());

    [Fact]
    public async Task Preview_AsEmployee_IsForbidden()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionPlan.Agency);
        var controller = AiFinderAdminControllerFactory.Create(db, tenant.Id, "Employee");

        var result = await controller.Preview(
            new EvaluateFinderRequestDto(new List<FinderAnswerValueDto>(), null), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task UpsertIndustrySettings_EnableFinderOnStarterPlan_IsRejected()
    {
        // Direct API call — "neon"-template-style bypass check for the AI Finder toggle.
        // Starter is below the Finder's required Professional tier.
        using var db = TestDbContextFactory.Create();
        var (tenant, profile) = SeedTenant(db, SubscriptionPlan.Starter);
        var controller = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpsertIndustrySettings(Request(profile.Id, true), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task UpsertIndustrySettings_EnableFinderOnProfessionalPlan_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, profile) = SeedTenant(db, SubscriptionPlan.Professional);
        var controller = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpsertIndustrySettings(Request(profile.Id, true), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UpsertIndustrySettings_DisableFinderOnStarterPlan_Succeeds()
    {
        // Disabling must always be allowed, even for a tenant that could never have enabled it.
        using var db = TestDbContextFactory.Create();
        var (tenant, profile) = SeedTenant(db, SubscriptionPlan.Starter);
        var controller = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpsertIndustrySettings(Request(profile.Id, false), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }
}
