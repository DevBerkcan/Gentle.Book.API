using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

public class MollieServiceChangePlanTests
{
    private static MollieService BuildMollieService(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var auditService = new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions());
        // No BaseAddress configured — if ChangePlanAsync ever attempted a real Mollie HTTP call
        // despite the fit-check, SendAsync would throw (relative URI, no base address), proving
        // the downgrade-blocked test below is actually blocked by the fit-check, not by a
        // coincidental HTTP failure.
        var mollieClient = new MollieClient(new HttpClient(), new StaticOptionsMonitor<MollieOptions>(new MollieOptions()));
        return new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), auditService, emailService, NullLogger<MollieService>.Instance);
    }

    private static MollieService BuildWorkingMollieService(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var auditService = new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions());
        var mollieClient = new MollieClient(
            new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"sub_test\",\"status\":\"active\"}", System.Text.Encoding.UTF8, "application/json")
            }))
            { BaseAddress = new Uri("https://api.mollie.com/v2/") },
            new StaticOptionsMonitor<MollieOptions>(new MollieOptions { ApiKey = "test_x" }));
        return new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), auditService, emailService, NullLogger<MollieService>.Instance);
    }

    private static (Tenant tenant, Subscription subscription) SeedActiveSubscription(GentleBook.Api.Data.GentleBookDbContext db, SubscriptionPlan plan)
    {
        var tenant = new Tenant { Name = "Salon Wechsel", Slug = "salon-wechsel-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Plan = plan,
            Interval = SubscriptionInterval.Monthly,
            Status = SubscriptionStatus.Active,
            MollieCustomerId = "cst_test",
            MollieSubscriptionId = "sub_test",
            CurrentPeriodStart = DateTime.UtcNow,
            CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1),
        };
        tenant.Subscription = subscription;
        db.AddRange(tenant, subscription);
        db.SaveChanges();
        return (tenant, subscription);
    }

    [Fact]
    public async Task ChangePlanAsync_DowngradeOverEmployeeLimit_IsBlockedWithoutTouchingMollieOrDb()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, subscription) = SeedActiveSubscription(db, SubscriptionPlan.Professional);

        for (var i = 0; i < 8; i++)
            db.Employees.Add(new Employee { TenantId = tenant.Id, Name = $"Mitarbeiter {i}", IsActive = true });
        await db.SaveChangesAsync();

        var mollieService = BuildMollieService(db);

        var result = await mollieService.ChangePlanAsync(tenant.Id, "Starter", "Monthly");

        Assert.False(result.Success);
        Assert.Contains("aktive Mitarbeiter", result.Error);
        Assert.Equal(8, result.CurrentEmployees);
        Assert.Equal(2, result.EmployeeLimit);

        var reloaded = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.TenantId == tenant.Id);
        Assert.Equal(SubscriptionPlan.Professional, reloaded.Plan);
    }

    [Fact]
    public async Task ChangePlanAsync_DowngradeWithProOnlyTemplateSelected_IsBlockedWithoutTouchingMollieOrDb()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedActiveSubscription(db, SubscriptionPlan.Professional);

        db.TenantSettings.Add(new TenantSettings
        {
            TenantId = tenant.Id,
            CompanyName = "Salon Wechsel",
            LinktreeConfig = "{\"pageTemplate\":\"neon\"}",
        });
        await db.SaveChangesAsync();

        var mollieService = BuildMollieService(db);

        var result = await mollieService.ChangePlanAsync(tenant.Id, "Starter", "Monthly");

        Assert.False(result.Success);
        Assert.Contains("Pro", result.Error);
        Assert.Contains("Vorlage", result.Error);

        var reloaded = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.TenantId == tenant.Id);
        Assert.Equal(SubscriptionPlan.Professional, reloaded.Plan);
    }

    [Fact]
    public async Task ChangePlanAsync_SamePlanAndInterval_IsNoOpSuccess()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedActiveSubscription(db, SubscriptionPlan.Professional);
        var mollieService = BuildMollieService(db);

        var result = await mollieService.ChangePlanAsync(tenant.Id, "Professional", "Monthly");

        Assert.True(result.Success);
        Assert.Equal("Professional", result.Plan);
        Assert.Equal("Monthly", result.Interval);
    }

    [Fact]
    public async Task ChangePlanAsync_TargetAgency_IsBlockedWithoutTouchingMollieOrDb()
    {
        // Agency has no fixed price ("Preis auf Anfrage") — self-service upgrade must be
        // rejected so billing never falls back to the internal PlanLimits list price instead
        // of an individually negotiated amount.
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedActiveSubscription(db, SubscriptionPlan.Professional);
        var mollieService = BuildMollieService(db);

        var result = await mollieService.ChangePlanAsync(tenant.Id, "Agency", "Monthly");

        Assert.False(result.Success);
        Assert.Contains("individuell", result.Error);

        var reloaded = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.TenantId == tenant.Id);
        Assert.Equal(SubscriptionPlan.Professional, reloaded.Plan);
    }

    [Fact]
    public async Task ChangePlanAsync_DowngradeOverServiceLimit_IsBlockedWithoutTouchingMollieOrDb()
    {
        // Deliberately within Starter's employee limit (2) so the employee check at
        // MollieService.cs:475-478 passes and the service-count branch at :480-483 — never
        // reached by any prior test — is the one that actually fires here.
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedActiveSubscription(db, SubscriptionPlan.Professional);

        for (var i = 0; i < 2; i++)
            db.Employees.Add(new Employee { TenantId = tenant.Id, Name = $"Mitarbeiter {i}", IsActive = true });
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        for (var i = 0; i < 16; i++) // Starter MaxServices = 15
            db.Services.Add(new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = $"Service {i}", DurationMinutes = 30, Price = 20m, IsActive = true });
        await db.SaveChangesAsync();

        var mollieService = BuildMollieService(db);

        var result = await mollieService.ChangePlanAsync(tenant.Id, "Starter", "Monthly");

        Assert.False(result.Success);
        Assert.Contains("aktive Services", result.Error);

        var reloaded = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.TenantId == tenant.Id);
        Assert.Equal(SubscriptionPlan.Professional, reloaded.Plan);
    }

    [Fact]
    public async Task ChangePlanAsync_UpgradeWithinLimits_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedActiveSubscription(db, SubscriptionPlan.Starter);
        var mollieService = BuildWorkingMollieService(db);

        var result = await mollieService.ChangePlanAsync(tenant.Id, "Professional", "Monthly");

        Assert.True(result.Success);
        Assert.Equal("Professional", result.Plan);
        var reloaded = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.TenantId == tenant.Id);
        Assert.Equal(SubscriptionPlan.Professional, reloaded.Plan);
    }

    [Fact]
    public async Task ChangePlanAsync_DowngradeWithinLimits_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedActiveSubscription(db, SubscriptionPlan.Professional);
        // Well within Starter's limits (2 employees, 15 services) — nothing to block this downgrade.
        db.Employees.Add(new Employee { TenantId = tenant.Id, Name = "Einzige Kraft", IsActive = true });
        await db.SaveChangesAsync();
        var mollieService = BuildWorkingMollieService(db);

        var result = await mollieService.ChangePlanAsync(tenant.Id, "Starter", "Monthly");

        Assert.True(result.Success);
        Assert.Equal("Starter", result.Plan);
        var reloaded = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.TenantId == tenant.Id);
        Assert.Equal(SubscriptionPlan.Starter, reloaded.Plan);
    }
}
