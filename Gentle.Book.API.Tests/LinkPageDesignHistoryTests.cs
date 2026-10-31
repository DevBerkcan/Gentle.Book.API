using System.Linq;
using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gentle.Book.API.Tests;

// The /admin/links design editor had no way to undo a design change or see when something was
// changed — every save silently overwrote TenantSettings.LinktreeConfig with no way back
// (user-reported: "...das Design, was ich vorher gemacht hatte, nochmal darauf zurückspringen...
// eine Verlaufshistory..."). TenantController.UpdateSettings now snapshots the PRE-change design
// into LinkPageDesignVersions before every save that actually changes it, and exposes
// GET design-history / POST design-history/{id}/restore to read it back.
public class LinkPageDesignHistoryTests
{
    private static UpdateTenantSettingsRequest Request(string? primaryColor = null, string? linktreeStyle = null, string? linktreeConfig = null) => new(
        CompanyName: null, Tagline: null, Phone: null, Email: null, Website: null, Address: null,
        PrimaryColor: primaryColor, SecondaryColor: null, AccentColor: null, WelcomeMessage: null, CancellationPolicy: null,
        BookingIntervalMinutes: null, MaxAdvanceBookingDays: null, TimeZone: null, DefaultCurrency: null,
        LinktreeStyle: linktreeStyle, LinktreeConfig: linktreeConfig, CancellationHoursNotice: null, CancellationFeePercent: null,
        LegalCompanyName: null, BillingStreet: null, BillingZipCode: null, BillingCity: null, BillingCountry: null,
        VatId: null, IsChatbotEnabled: null);

    private static Tenant SeedTenant(GentleBook.Api.Data.GentleBookDbContext db, SubscriptionPlan plan)
    {
        var tenant = new Tenant { Name = "Salon Verlauf", Slug = "salon-verlauf-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = plan, Status = SubscriptionStatus.Active });
        db.SaveChanges();
        return tenant;
    }

    [Fact]
    public async Task UpdateSettings_FirstEverDesignSave_DoesNotCreateHistoryEntry()
    {
        // Nothing to go "back" to yet — a fresh tenant's very first save shouldn't produce a
        // history row with nothing meaningful in it.
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Professional);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpdateSettings(Request(primaryColor: "#112233", linktreeConfig: "{\"pageTemplate\":\"classic\"}"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(await db.LinkPageDesignVersions.Where(v => v.TenantId == tenant.Id).ToListAsync());
    }

    [Fact]
    public async Task UpdateSettings_DesignChange_SnapshotsThePreviousDesign()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Professional);
        db.TenantSettings.Add(new TenantSettings
        {
            TenantId = tenant.Id, CompanyName = "Salon Verlauf",
            PrimaryColor = "#AAAAAA", LinktreeStyle = "gradient", LinktreeConfig = "{\"pageTemplate\":\"classic\"}",
        });
        await db.SaveChangesAsync();
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpdateSettings(Request(primaryColor: "#BBBBBB", linktreeConfig: "{\"pageTemplate\":\"soft\"}"));

        Assert.IsType<OkObjectResult>(result);
        var version = await db.LinkPageDesignVersions.SingleAsync(v => v.TenantId == tenant.Id);
        Assert.Equal("#AAAAAA", version.PrimaryColor);
        Assert.Equal("{\"pageTemplate\":\"classic\"}", version.LinktreeConfig);
        // ...and the settings row itself now holds the new design.
        var settings = await db.TenantSettings.SingleAsync(s => s.TenantId == tenant.Id);
        Assert.Equal("#BBBBBB", settings.PrimaryColor);
    }

    [Fact]
    public async Task UpdateSettings_SaveWithNoActualDesignChange_DoesNotCreateHistoryEntry()
    {
        // Every field tweak in the editor goes through this endpoint (silent auto-save) — a
        // request that re-sends the exact same values shouldn't pollute the history.
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Professional);
        db.TenantSettings.Add(new TenantSettings
        {
            TenantId = tenant.Id, CompanyName = "Salon Verlauf",
            PrimaryColor = "#AAAAAA", LinktreeStyle = "gradient", LinktreeConfig = "{\"pageTemplate\":\"classic\"}",
        });
        await db.SaveChangesAsync();
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpdateSettings(Request(primaryColor: "#AAAAAA", linktreeStyle: "gradient", linktreeConfig: "{\"pageTemplate\":\"classic\"}"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(await db.LinkPageDesignVersions.Where(v => v.TenantId == tenant.Id).ToListAsync());
    }

    [Fact]
    public async Task GetDesignHistory_ReturnsOwnTenantEntriesNewestFirst()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Professional);
        var otherTenant = SeedTenant(db, SubscriptionPlan.Professional);
        db.LinkPageDesignVersions.AddRange(
            new LinkPageDesignVersion { TenantId = tenant.Id, PrimaryColor = "#111111", CreatedAt = DateTime.UtcNow.AddMinutes(-10) },
            new LinkPageDesignVersion { TenantId = tenant.Id, PrimaryColor = "#222222", CreatedAt = DateTime.UtcNow.AddMinutes(-1) },
            new LinkPageDesignVersion { TenantId = otherTenant.Id, PrimaryColor = "#999999", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.GetDesignHistory();

        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            ok.Value!.GetType().GetProperty("data")!.GetValue(ok.Value));
        var items = data.Cast<object>().ToList();
        Assert.Equal(2, items.Count); // never the other tenant's entry
        var firstColor = items[0].GetType().GetProperty("PrimaryColor")!.GetValue(items[0]);
        Assert.Equal("#222222", firstColor); // newest first
    }

    [Fact]
    public async Task RestoreDesignVersion_BringsBackOldDesignAndSnapshotsCurrentOneForward()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Professional);
        db.TenantSettings.Add(new TenantSettings
        {
            TenantId = tenant.Id, CompanyName = "Salon Verlauf",
            PrimaryColor = "#CURRENT1", LinktreeStyle = "dark", LinktreeConfig = "{\"pageTemplate\":\"soft\"}",
        });
        var oldVersion = new LinkPageDesignVersion
        {
            TenantId = tenant.Id, PrimaryColor = "#OLDONE1", LinktreeStyle = "gradient", LinktreeConfig = "{\"pageTemplate\":\"classic\"}",
            CreatedAt = DateTime.UtcNow.AddHours(-2),
        };
        db.LinkPageDesignVersions.Add(oldVersion);
        await db.SaveChangesAsync();
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.RestoreDesignVersion(oldVersion.Id);

        Assert.IsType<OkObjectResult>(result);
        var settings = await db.TenantSettings.SingleAsync(s => s.TenantId == tenant.Id);
        Assert.Equal("#OLDONE1", settings.PrimaryColor);
        Assert.Equal("gradient", settings.LinktreeStyle);
        Assert.Equal("{\"pageTemplate\":\"classic\"}", settings.LinktreeConfig);

        // The design we just moved away from got snapshotted too, so the admin can go forward again.
        var forwardVersion = await db.LinkPageDesignVersions.SingleAsync(v => v.Id != oldVersion.Id);
        Assert.Equal("#CURRENT1", forwardVersion.PrimaryColor);
    }

    [Fact]
    public async Task RestoreDesignVersion_FromAnotherTenant_IsNotFound()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Professional);
        var otherTenant = SeedTenant(db, SubscriptionPlan.Professional);
        db.TenantSettings.Add(new TenantSettings { TenantId = tenant.Id, CompanyName = "Salon Verlauf" });
        var otherVersion = new LinkPageDesignVersion { TenantId = otherTenant.Id, PrimaryColor = "#SECRET1", LinktreeConfig = "{}" };
        db.LinkPageDesignVersions.Add(otherVersion);
        await db.SaveChangesAsync();
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.RestoreDesignVersion(otherVersion.Id);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task RestoreDesignVersion_TemplateAboveCurrentPlan_IsRejectedAndSettingsUnchanged()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Starter);
        db.TenantSettings.Add(new TenantSettings { TenantId = tenant.Id, CompanyName = "Salon Verlauf", PrimaryColor = "#CURRENT2", LinktreeConfig = "{\"pageTemplate\":\"classic\"}" });
        var proVersion = new LinkPageDesignVersion { TenantId = tenant.Id, PrimaryColor = "#PRO0001", LinktreeConfig = "{\"pageTemplate\":\"neon\"}" };
        db.LinkPageDesignVersions.Add(proVersion);
        await db.SaveChangesAsync();
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.RestoreDesignVersion(proVersion.Id);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
        Assert.Equal("#CURRENT2", (await db.TenantSettings.SingleAsync(s => s.TenantId == tenant.Id)).PrimaryColor);
    }

    [Fact]
    public async Task UpdateSettings_DesignHistory_IsCappedAtFortyEntriesPerTenant()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Professional);
        db.TenantSettings.Add(new TenantSettings { TenantId = tenant.Id, CompanyName = "Salon Verlauf", PrimaryColor = "#000000", LinktreeConfig = "{\"pageTemplate\":\"classic\"}" });
        for (var i = 0; i < 40; i++)
        {
            db.LinkPageDesignVersions.Add(new LinkPageDesignVersion
            {
                TenantId = tenant.Id, PrimaryColor = $"#{i:D6}", LinktreeConfig = "{}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-40 + i),
            });
        }
        await db.SaveChangesAsync();
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.UpdateSettings(Request(primaryColor: "#FFFFFF", linktreeConfig: "{\"pageTemplate\":\"soft\"}"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(40, await db.LinkPageDesignVersions.CountAsync(v => v.TenantId == tenant.Id));
    }
}
