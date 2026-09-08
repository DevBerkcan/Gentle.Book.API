using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services.AI;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gentle.Book.API.Tests;

// AiUsageMeter.TrackAsync does double duty: it still writes the per-call AiUsage row (existing
// behavior, covered elsewhere), and now also maintains TenantAiBudget.CurrentPeriodCostUsd — the
// running total AiUsageGate reads. These tests cover only the new budget-tracking half.
public class AiUsageMeterBudgetTrackingTests
{
    [Fact]
    public async Task TrackAsync_FirstCallForTenant_CreatesBudgetRowWithCost()
    {
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var meter = new AiUsageMeter(db);

        await meter.TrackAsync(tenantId, "ai_service_finder", "gpt-4o-mini", 100, 50, 0.05m, CancellationToken.None);

        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenantId);
        Assert.Equal(0.05m, budget.CurrentPeriodCostUsd);
    }

    [Fact]
    public async Task TrackAsync_SecondCallSamePeriod_AccumulatesCost()
    {
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var meter = new AiUsageMeter(db);

        await meter.TrackAsync(tenantId, "ai_service_finder", "gpt-4o-mini", 100, 50, 0.05m, CancellationToken.None);
        await meter.TrackAsync(tenantId, "employee_tagline_suggestion", "gpt-4o-mini", 40, 10, 0.02m, CancellationToken.None);

        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenantId);
        Assert.Equal(0.07m, budget.CurrentPeriodCostUsd);
    }

    [Fact]
    public async Task TrackAsync_StalePeriodFromPreviousMonth_ResetsBeforeAdding()
    {
        // Simulates the reset-ai-budget-period job not having run yet for a brand-new month —
        // TrackAsync must not silently pile this call's cost onto last month's already-reported
        // total, and must clear the per-period flags too (a new period is a fresh chance to warn).
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        db.TenantAiBudgets.Add(new TenantAiBudget
        {
            TenantId = tenantId,
            CurrentPeriodStart = DateTime.UtcNow.AddMonths(-1),
            CurrentPeriodCostUsd = 19.99m,
            IsHardCapped = true,
            HardCapWarningEmailSent = true,
        });
        await db.SaveChangesAsync();

        var meter = new AiUsageMeter(db);
        await meter.TrackAsync(tenantId, "ai_service_finder", "gpt-4o-mini", 100, 50, 0.05m, CancellationToken.None);

        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenantId);
        Assert.Equal(0.05m, budget.CurrentPeriodCostUsd);
        Assert.False(budget.IsHardCapped);
        Assert.False(budget.HardCapWarningEmailSent);
        Assert.True(budget.CurrentPeriodStart >= new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1));
    }

    [Fact]
    public async Task TrackAsync_StillWritesAiUsageRow()
    {
        // Regression guard: the pre-existing per-call ledger write must survive unchanged.
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var meter = new AiUsageMeter(db);

        await meter.TrackAsync(tenantId, "ai_service_finder", "gpt-4o-mini", 100, 50, 0.05m, CancellationToken.None);

        var usage = await db.AiUsages.SingleAsync(u => u.TenantId == tenantId);
        Assert.Equal("ai_service_finder", usage.Feature);
        Assert.Equal(0.05m, usage.EstimatedCost);
    }
}
