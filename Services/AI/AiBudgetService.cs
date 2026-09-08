using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services.AI;

// Two daily Hangfire jobs (registered in Options/HangfireJobScheduler.cs), mirroring
// SubscriptionService's dunning jobs. TenantAiBudget.CurrentPeriodCostUsd is enforced live by
// AiUsageGate on every AI call — these jobs don't do the enforcing, they do the reporting: reset
// the running total into a fresh calendar month, and send the once-per-period warning email /
// audit entry when a tenant crosses the warn or hard-cap threshold.
public class AiBudgetService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiBudgetService> _logger;

    public AiBudgetService(IServiceScopeFactory scopeFactory, ILogger<AiBudgetService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Runs daily: any TenantAiBudget whose CurrentPeriodStart is no longer in the current
    /// calendar month gets its running total reset to 0 and its per-period flags cleared.
    /// AiUsageMeter.TrackAsync also does this inline defensively for the first call of a new
    /// month, so a slow/missed run of this job never lets stale spend block a tenant.
    /// </summary>
    public async Task ResetPeriodsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();

        var currentMonthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var stale = await db.TenantAiBudgets
            .Where(b => b.CurrentPeriodStart < currentMonthStart)
            .ToListAsync();

        if (stale.Count == 0)
        {
            _logger.LogInformation("AI budget reset: nothing to reset.");
            return;
        }

        foreach (var budget in stale)
        {
            budget.CurrentPeriodStart = currentMonthStart;
            budget.CurrentPeriodCostUsd = 0m;
            budget.IsHardCapped = false;
            budget.HardCapWarningEmailSent = false;
            budget.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("AI budget reset: {Count} tenant(s) rolled into a new period.", stale.Count);
    }

    /// <summary>
    /// Runs daily: warns a tenant admin once when their AI spend crosses 80% of their monthly
    /// budget, and flags IsHardCapped + writes an audit entry once when it crosses 100% — for
    /// SuperAdmin visibility/reporting. The actual LLM-call cutoff already happened in real time
    /// via AiUsageGate the moment CurrentPeriodCostUsd crossed the budget; this job never has to
    /// "catch" that before damage is done.
    /// </summary>
    public async Task CheckThresholdsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();

        var budgets = await db.TenantAiBudgets
            .Where(b => !b.HardCapWarningEmailSent || !b.IsHardCapped)
            .ToListAsync();

        var warned = 0;
        var capped = 0;

        foreach (var budget in budgets)
        {
            var effectiveBudget = budget.MonthlyBudgetUsd ?? OpenAiPricing.PlatformDefaultMonthlyBudgetUsd;
            if (effectiveBudget <= 0) continue;

            var usedFraction = budget.CurrentPeriodCostUsd / effectiveBudget;

            if (usedFraction >= 1m && !budget.IsHardCapped)
            {
                budget.IsHardCapped = true;
                budget.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                capped++;

                await audit.LogAsync(
                    "ai_budget.hard_capped",
                    "TenantAiBudget",
                    budget.Id.ToString(),
                    $"{budget.CurrentPeriodCostUsd:0.00}$ von {effectiveBudget:0.00}$ verbraucht — KI-Aufrufe pausiert bis zur nächsten Periode.",
                    budget.TenantId);
                continue;
            }

            if (usedFraction >= OpenAiPricing.WarningThresholdPercent && !budget.HardCapWarningEmailSent)
            {
                budget.HardCapWarningEmailSent = true;
                budget.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                warned++;

                var admin = await db.PlatformUsers
                    .Where(u => u.TenantId == budget.TenantId && u.Role == PlatformRole.TenantAdmin)
                    .OrderBy(u => u.CreatedAt)
                    .FirstOrDefaultAsync();
                if (admin != null)
                {
                    try
                    {
                        await emailService.SendAiBudgetWarningEmailAsync(
                            admin.Email, admin.FirstName, budget.CurrentPeriodCostUsd, effectiveBudget);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send AI budget warning email for TenantId={TenantId}", budget.TenantId);
                    }
                }
            }
        }

        _logger.LogInformation("AI budget thresholds: {Warned} warned, {Capped} newly capped.", warned, capped);
    }
}
