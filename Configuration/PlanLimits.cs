using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Configuration;

public static class PlanLimits
{
    public record Limits(
        int MaxEmployees,
        int MaxServices,
        int MaxBookingsPerMonth,
        bool HasAnalytics,
        bool HasApiAccess,
        string DisplayName,
        decimal MonthlyPrice
    );

    private static readonly Dictionary<SubscriptionPlan, Limits> _limits = new()
    {
        [SubscriptionPlan.Trial]        = new(2,   10,  100,        false, false, "Trial",    0m),
        [SubscriptionPlan.Starter]      = new(2,   15,  200,        false, false, "Starter",  29m),
        [SubscriptionPlan.Professional] = new(10,  50,  int.MaxValue, true, false, "Pro",     59m),
        [SubscriptionPlan.Agency]       = new(int.MaxValue, int.MaxValue, int.MaxValue, true, true, "Business", 99m),
    };

    public static Limits Get(SubscriptionPlan plan) =>
        _limits.TryGetValue(plan, out var limits) ? limits : _limits[SubscriptionPlan.Trial];

    public static bool IsUnlimited(int value) => value == int.MaxValue;

    public static IReadOnlyDictionary<SubscriptionPlan, Limits> All => _limits;

    /// <summary>
    /// Overrides a plan's MonthlyPrice at runtime (in-memory, single-process). Called once at
    /// startup for every persisted Data.Entities.PlanPrice row, and again whenever a SuperAdmin
    /// edits a price via the plan-pricing endpoint — so the very next signup uses the new price
    /// immediately. Existing Mollie subscriptions are unaffected: their amount was fixed at
    /// creation time and Mollie never re-reads GentleBook's price on recurring collections.
    /// </summary>
    public static void SetPrice(SubscriptionPlan plan, decimal monthlyPrice)
    {
        if (_limits.TryGetValue(plan, out var existing))
            _limits[plan] = existing with { MonthlyPrice = monthlyPrice };
    }
}
