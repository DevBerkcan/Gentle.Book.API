using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Configuration;

// The AI Service Finder had no relationship to SubscriptionPlan at all — any tenant on any
// plan (including Trial) could enable and use it. Mirrors the LinkPageTemplates.cs pattern:
// null return means allowed, otherwise the display name of the plan actually required.
// Required tier (Professional/"Pro") is a reasonable default for a premium AI feature; adjust
// here if product decides otherwise — nothing else needs to change.
public static class AiFinderPlanGate
{
    private static readonly SubscriptionPlan RequiredPlan = SubscriptionPlan.Professional;

    private static int Rank(SubscriptionPlan plan) => plan switch
    {
        SubscriptionPlan.Professional => 1,
        SubscriptionPlan.Agency => 2,
        _ => 0,
    };

    public static string? ValidateForPlan(SubscriptionPlan tenantPlan) =>
        Rank(tenantPlan) >= Rank(RequiredPlan) ? null : PlanLimits.Get(RequiredPlan).DisplayName;
}
