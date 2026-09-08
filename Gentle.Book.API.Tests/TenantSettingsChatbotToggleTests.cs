using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gentle.Book.API.Tests;

// TenantController.UpdateSettings's IsChatbotEnabled handling: the Links/Design page already
// hides this toggle for non-Agency tenants, but the endpoint itself must still enforce it —
// same "frontend greys out, backend double-checks" idiom already used for LinktreeConfig's
// page-template plan check right above it in the same method.
public class TenantSettingsChatbotToggleTests
{
    private static UpdateTenantSettingsRequest Request(bool? isChatbotEnabled) => new(
        CompanyName: null, Tagline: null, Phone: null, Email: null, Website: null, Address: null,
        PrimaryColor: null, SecondaryColor: null, AccentColor: null, WelcomeMessage: null, CancellationPolicy: null,
        BookingIntervalMinutes: null, MaxAdvanceBookingDays: null, TimeZone: null, DefaultCurrency: null,
        LinktreeStyle: null, LinktreeConfig: null, CancellationHoursNotice: null, CancellationFeePercent: null,
        LegalCompanyName: null, BillingStreet: null, BillingZipCode: null, BillingCity: null, BillingCountry: null,
        VatId: null, IsChatbotEnabled: isChatbotEnabled);

    private static Tenant SeedTenant(GentleBook.Api.Data.GentleBookDbContext db, SubscriptionPlan plan)
    {
        var tenant = new Tenant { Name = "Salon Chatbot", Slug = "salon-chatbot-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = plan, Status = SubscriptionStatus.Active });
        db.SaveChanges();
        return tenant;
    }

    [Fact]
    public async Task UpdateSettings_EnableOnNonAgencyPlan_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Professional);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpdateSettings(Request(true));

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
        Assert.False((await db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenant.Id))?.IsChatbotEnabled ?? false);
    }

    [Fact]
    public async Task UpdateSettings_EnableOnAgencyPlan_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Agency);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpdateSettings(Request(true));

        Assert.IsType<OkObjectResult>(result);
        Assert.True((await db.TenantSettings.SingleAsync(s => s.TenantId == tenant.Id)).IsChatbotEnabled);
    }

    [Fact]
    public async Task UpdateSettings_DisableOnNonAgencyPlan_IsAlwaysAllowed()
    {
        // A tenant that downgrades away from Agency must still be able to turn the toggle off —
        // never get stuck unable to disable a feature it no longer has.
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Starter);
        db.TenantSettings.Add(new TenantSettings { TenantId = tenant.Id, CompanyName = "Salon Chatbot", IsChatbotEnabled = true });
        await db.SaveChangesAsync();
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpdateSettings(Request(false));

        Assert.IsType<OkObjectResult>(result);
        Assert.False((await db.TenantSettings.SingleAsync(s => s.TenantId == tenant.Id)).IsChatbotEnabled);
    }
}
