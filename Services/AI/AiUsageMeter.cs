using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services.AI;

public sealed class AiUsageMeter : IAiUsageMeter
{
    private readonly GentleBookDbContext _db;

    public AiUsageMeter(GentleBookDbContext db)
    {
        _db = db;
    }

    public async Task TrackAsync(
        Guid tenantId,
        string feature,
        string model,
        int inputTokens,
        int outputTokens,
        decimal estimatedCost,
        CancellationToken cancellationToken)
    {
        _db.AiUsages.Add(new AiUsage
        {
            TenantId = tenantId,
            Feature = feature,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCost = estimatedCost,
            CreatedOn = DateTime.UtcNow
        });

        // Maintain the running per-period total the budget gate (IAiUsageGate) reads — kept
        // incremental here rather than recomputed from AiUsage on every gate check, so the gate
        // stays a single cheap indexed read even under heavy AI traffic.
        var currentMonthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var budget = await _db.TenantAiBudgets.FirstOrDefaultAsync(b => b.TenantId == tenantId, cancellationToken);
        if (budget == null)
        {
            budget = new TenantAiBudget { TenantId = tenantId, CurrentPeriodStart = currentMonthStart };
            _db.TenantAiBudgets.Add(budget);
        }
        else if (budget.CurrentPeriodStart < currentMonthStart)
        {
            // The daily reset job (reset-ai-budget-period) hasn't run yet for the new month —
            // reset inline so this call is never mistakenly counted against last month's spend.
            budget.CurrentPeriodStart = currentMonthStart;
            budget.CurrentPeriodCostUsd = 0m;
            budget.IsHardCapped = false;
            budget.HardCapWarningEmailSent = false;
        }

        budget.CurrentPeriodCostUsd += estimatedCost;
        budget.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }
}
