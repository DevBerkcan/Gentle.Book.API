using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Configuration;

// Mirrors the PAGE_TEMPLATES plan mapping in Gentle.Book.UI/app/admin/links/page.tsx —
// keep both in sync when templates are added/re-tiered. This is the server-side
// enforcement half: the frontend's greyed-out/locked template UI alone can be bypassed by
// calling PUT /api/tenant/settings directly, since that endpoint didn't previously
// validate the chosen pageTemplate against the tenant's actual plan at all.
public static class LinkPageTemplates
{
    private static readonly Dictionary<string, SubscriptionPlan> RequiredPlanByTemplate = new(StringComparer.OrdinalIgnoreCase)
    {
        ["classic"] = SubscriptionPlan.Starter,
        ["soft"] = SubscriptionPlan.Starter,
        ["hero"] = SubscriptionPlan.Starter,
        ["organic"] = SubscriptionPlan.Starter,
        ["tattoo"] = SubscriptionPlan.Starter,
        ["barbershop"] = SubscriptionPlan.Starter,
        ["beauty"] = SubscriptionPlan.Starter,
        ["clinic"] = SubscriptionPlan.Starter,
        ["neon"] = SubscriptionPlan.Professional,
        ["magazine"] = SubscriptionPlan.Professional,
        ["split"] = SubscriptionPlan.Professional,
        ["fitness"] = SubscriptionPlan.Professional,
        ["restaurant"] = SubscriptionPlan.Professional,
        ["corporate"] = SubscriptionPlan.Agency,
        ["portfolio"] = SubscriptionPlan.Agency,
    };

    // Trial and Starter share the same (lowest) template tier — mirrors the frontend's
    // own tenantPlan normalization (app/admin/links/page.tsx: anything that isn't
    // "pro"/"business" is treated as "starter"), so this never rejects something the
    // frontend itself currently allows.
    private static int Rank(SubscriptionPlan plan) => plan switch
    {
        SubscriptionPlan.Professional => 1,
        SubscriptionPlan.Agency => 2,
        _ => 0,
    };

    /// <summary>
    /// Returns null when the template is allowed (unknown/unmapped template keys are accepted
    /// so new templates don't need a matching deploy on both repos at the exact same moment).
    /// Otherwise returns the display name of the plan actually required, for the error message.
    /// </summary>
    public static string? ValidateTemplateForPlan(string? pageTemplate, SubscriptionPlan tenantPlan)
    {
        if (string.IsNullOrWhiteSpace(pageTemplate)) return null;
        if (!RequiredPlanByTemplate.TryGetValue(pageTemplate, out var requiredPlan)) return null;
        if (Rank(tenantPlan) >= Rank(requiredPlan)) return null;

        return PlanLimits.Get(requiredPlan).DisplayName;
    }
}
