using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services.AI;

public sealed class AiUsageGate : IAiUsageGate
{
    private readonly GentleBookDbContext _db;

    public AiUsageGate(GentleBookDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsWithinBudgetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var budget = await _db.TenantAiBudgets.AsNoTracking()
            .FirstOrDefaultAsync(b => b.TenantId == tenantId, cancellationToken);

        // No budget row yet means the tenant has never made a chargeable AI call this period —
        // nothing spent, so it's always within budget. AiUsageMeter.TrackAsync creates the row
        // lazily on first use.
        if (budget == null) return true;

        var effectiveBudget = budget.MonthlyBudgetUsd ?? OpenAiPricing.PlatformDefaultMonthlyBudgetUsd;
        return budget.CurrentPeriodCostUsd < effectiveBudget;
    }
}
