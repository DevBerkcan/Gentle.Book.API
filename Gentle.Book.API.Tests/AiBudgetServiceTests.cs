using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using GentleBook.Api.Services.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// AiBudgetService backs the two new daily Hangfire jobs (reset-ai-budget-period,
// check-ai-budget-thresholds). It uses IServiceScopeFactory the same way SubscriptionService's
// dunning job does, so these tests wire a small real DI container around the same in-memory
// GentleBookDbContext rather than mocking the scope machinery.
public class AiBudgetServiceTests
{
    private static AiBudgetService Build(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(TestServiceFactory.CreateEmailService(db));
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        services.AddSingleton(new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance));
        var provider = services.BuildServiceProvider();

        return new AiBudgetService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AiBudgetService>.Instance);
    }

    [Fact]
    public async Task ResetPeriodsAsync_StalePeriod_ResetsCostAndFlags()
    {
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        db.TenantAiBudgets.Add(new TenantAiBudget
        {
            TenantId = tenantId,
            CurrentPeriodStart = DateTime.UtcNow.AddMonths(-1),
            CurrentPeriodCostUsd = 18m,
            IsHardCapped = true,
            HardCapWarningEmailSent = true,
        });
        await db.SaveChangesAsync();

        var service = Build(db);
        await service.ResetPeriodsAsync();

        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenantId);
        Assert.Equal(0m, budget.CurrentPeriodCostUsd);
        Assert.False(budget.IsHardCapped);
        Assert.False(budget.HardCapWarningEmailSent);
    }

    [Fact]
    public async Task ResetPeriodsAsync_CurrentPeriod_LeavesUntouched()
    {
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var currentMonthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenantId, CurrentPeriodStart = currentMonthStart, CurrentPeriodCostUsd = 5m });
        await db.SaveChangesAsync();

        var service = Build(db);
        await service.ResetPeriodsAsync();

        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenantId);
        Assert.Equal(5m, budget.CurrentPeriodCostUsd);
    }

    [Fact]
    public async Task CheckThresholdsAsync_At100Percent_SetsHardCappedAndWritesAudit()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Budget", Slug = "salon-budget-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenant.Id, MonthlyBudgetUsd = 10m, CurrentPeriodCostUsd = 10m });
        await db.SaveChangesAsync();

        var service = Build(db);
        await service.CheckThresholdsAsync();

        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenant.Id);
        Assert.True(budget.IsHardCapped);
        Assert.Contains(db.AuditLogs, a => a.Action == "ai_budget.hard_capped" && a.TenantId == tenant.Id);
    }

    [Fact]
    public async Task CheckThresholdsAsync_AlreadyHardCapped_DoesNotDuplicateAudit()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Budget", Slug = "salon-budget-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenant.Id, MonthlyBudgetUsd = 10m, CurrentPeriodCostUsd = 10m, IsHardCapped = true, HardCapWarningEmailSent = true });
        await db.SaveChangesAsync();

        var service = Build(db);
        await service.CheckThresholdsAsync();
        await service.CheckThresholdsAsync();

        Assert.DoesNotContain(db.AuditLogs, a => a.Action == "ai_budget.hard_capped" && a.TenantId == tenant.Id);
    }

    [Fact]
    public async Task CheckThresholdsAsync_At80Percent_SetsWarningFlagIdempotently()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Budget", Slug = "salon-budget-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.PlatformUsers.Add(new PlatformUser { TenantId = tenant.Id, Email = "admin@salon.test", FirstName = "Admin", LastName = "User", Role = PlatformRole.TenantAdmin, PasswordHash = "x" });
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenant.Id, MonthlyBudgetUsd = 10m, CurrentPeriodCostUsd = 8m });
        await db.SaveChangesAsync();

        var service = Build(db);
        await service.CheckThresholdsAsync();

        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenant.Id);
        Assert.True(budget.HardCapWarningEmailSent);
        Assert.False(budget.IsHardCapped); // 80% warns, does not cap
    }

    [Fact]
    public async Task CheckThresholdsAsync_UnderWarnThreshold_TouchesNothing()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Budget", Slug = "salon-budget-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenant.Id, MonthlyBudgetUsd = 10m, CurrentPeriodCostUsd = 1m });
        await db.SaveChangesAsync();

        var service = Build(db);
        await service.CheckThresholdsAsync();

        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenant.Id);
        Assert.False(budget.HardCapWarningEmailSent);
        Assert.False(budget.IsHardCapped);
    }
}
