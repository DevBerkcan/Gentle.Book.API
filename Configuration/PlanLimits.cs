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
}
