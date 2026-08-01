using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Gentle.Book.API.Tests;

/// <summary>
/// Covers Phase 3: sensitive tenant endpoints must be blocked server-side for the Employee
/// role, not just hidden from the admin nav — and the new server-side plan check for
/// PUT /tenant/settings template selection (previously frontend-only and bypassable).
/// </summary>
public class TenantControllerAuthorizationTests
{
    private static (Tenant tenant, Subscription subscription) SeedTenant(
        GentleBook.Api.Data.GentleBookDbContext db, SubscriptionPlan plan)
    {
        var tenant = new Tenant { Name = "Barber Wagner", Slug = "barber-wagner", IsActive = true };
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = plan, Status = SubscriptionStatus.Active };
        tenant.Subscription = subscription;

        db.Tenants.Add(tenant);
        db.Subscriptions.Add(subscription);
        db.SaveChanges();

        return (tenant, subscription);
    }

    [Fact]
    public void GetPlanPricing_AsEmployee_IsForbidden()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionPlan.Starter);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "Employee");

        var result = controller.GetPlanPricing();

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public void GetPlanPricing_AsTenantAdmin_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionPlan.Starter);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = controller.GetPlanPricing();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetUsage_AsEmployee_IsForbidden()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionPlan.Starter);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "Employee");

        var result = await controller.GetUsage();

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetUsage_AsTenantAdmin_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionPlan.Starter);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.GetUsage();

        Assert.IsType<OkObjectResult>(result);
    }

    private static UpdateTenantSettingsRequest RequestWithLinktreeConfig(string? linktreeConfig) => new(
        CompanyName: null, Tagline: null, Phone: null, Email: null, Website: null, Address: null,
        PrimaryColor: null, SecondaryColor: null, AccentColor: null, WelcomeMessage: null, CancellationPolicy: null,
        BookingIntervalMinutes: null, MaxAdvanceBookingDays: null, TimeZone: null, DefaultCurrency: null,
        LinktreeStyle: null, LinktreeConfig: linktreeConfig, CancellationHoursNotice: null, CancellationFeePercent: null,
        LegalCompanyName: null, BillingStreet: null, BillingZipCode: null, BillingCity: null, BillingCountry: null, VatId: null);

    [Fact]
    public async Task UpdateSettings_AsEmployee_IsForbidden()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionPlan.Starter);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "Employee");

        var result = await controller.UpdateSettings(RequestWithLinktreeConfig(null));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task UpdateSettings_PremiumTemplateOnStarterPlan_IsRejected()
    {
        // Direct API call bypassing the frontend's greyed-out template picker — must still
        // be blocked server-side. "neon" requires Professional; tenant is on Starter.
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionPlan.Starter);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpdateSettings(RequestWithLinktreeConfig("{\"pageTemplate\":\"neon\"}"));

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task UpdateSettings_AllowedTemplateOnStarterPlan_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionPlan.Starter);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpdateSettings(RequestWithLinktreeConfig("{\"pageTemplate\":\"classic\"}"));

        Assert.IsType<OkObjectResult>(result);
    }
}
