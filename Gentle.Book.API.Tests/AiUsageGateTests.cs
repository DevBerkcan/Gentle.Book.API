using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Configuration;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services.AI;
using Xunit;

namespace Gentle.Book.API.Tests;

// AiUsageGate is the real-time enforcement point AiOrchestrator/EmployeesController check before
// every LLM call — it must never depend on the daily reset/threshold jobs having already run, so
// these tests exercise it directly against TenantAiBudget rows in whatever state they're in.
public class AiUsageGateTests
{
    [Fact]
    public async Task IsWithinBudgetAsync_NoBudgetRowYet_ReturnsTrue()
    {
        using var db = TestDbContextFactory.Create();
        var gate = new AiUsageGate(db);

        var result = await gate.IsWithinBudgetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsWithinBudgetAsync_UnderPlatformDefault_ReturnsTrue()
    {
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenantId, CurrentPeriodCostUsd = 1.00m });
        await db.SaveChangesAsync();

        var gate = new AiUsageGate(db);
        var result = await gate.IsWithinBudgetAsync(tenantId, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsWithinBudgetAsync_AtOrOverPlatformDefault_ReturnsFalse()
    {
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenantId, CurrentPeriodCostUsd = OpenAiPricing.PlatformDefaultMonthlyBudgetUsd });
        await db.SaveChangesAsync();

        var gate = new AiUsageGate(db);
        var result = await gate.IsWithinBudgetAsync(tenantId, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsWithinBudgetAsync_CustomOverrideBelowSpend_ReturnsFalse()
    {
        // Spend that's well under the platform default but over this tenant's own lower,
        // individually-negotiated cap.
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenantId, MonthlyBudgetUsd = 2.00m, CurrentPeriodCostUsd = 2.50m });
        await db.SaveChangesAsync();

        var gate = new AiUsageGate(db);
        var result = await gate.IsWithinBudgetAsync(tenantId, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsWithinBudgetAsync_CustomOverrideAboveSpend_ReturnsTrue()
    {
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenantId, MonthlyBudgetUsd = 100m, CurrentPeriodCostUsd = 25m });
        await db.SaveChangesAsync();

        var gate = new AiUsageGate(db);
        var result = await gate.IsWithinBudgetAsync(tenantId, CancellationToken.None);

        Assert.True(result);
    }
}
