using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Configuration;

// Shared gate for features that are Agency-exclusive (public API access, the real
// LLM-backed AI Service Finder explanation, AI tagline suggestions). Same null-means-allowed
// convention as AiFinderPlanGate.cs/LinkPageTemplates.cs.
public static class AgencyFeatureGate
{
    public static string? ValidateForPlan(SubscriptionPlan tenantPlan) =>
        tenantPlan == SubscriptionPlan.Agency ? null : PlanLimits.Get(SubscriptionPlan.Agency).DisplayName;

    public static bool IsAgency(SubscriptionPlan tenantPlan) => tenantPlan == SubscriptionPlan.Agency;
}
