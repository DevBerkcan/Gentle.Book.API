using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Configuration;

// Behandlungspakete + "Offene Folgetermine": Professional and Agency, plus Trial (the codebase
// convention — Trial shows every non-AI feature so a prospect can actually try it). Starter
// gets neither the management UI nor working personal package links. Data is kept on a
// downgrade; it simply becomes unreachable until the next upgrade.
public static class TreatmentPackageGate
{
    public const string FeatureKey = "treatment_packages";

    public static bool IsAllowed(SubscriptionPlan plan) =>
        plan is SubscriptionPlan.Trial or SubscriptionPlan.Professional or SubscriptionPlan.Agency;
}
