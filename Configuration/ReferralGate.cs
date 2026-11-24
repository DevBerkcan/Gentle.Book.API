using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Configuration;

// "Freunde machen Freude": Trial (codebase convention: Trial shows every non-AI feature),
// Professional and Agency. The personal Studio-Pass itself is available on every plan.
public static class ReferralGate
{
    public const string FeatureKey = "referrals";
    public const int MaxRewardTextLength = 300;

    public static bool IsAllowed(SubscriptionPlan plan) =>
        plan is SubscriptionPlan.Trial or SubscriptionPlan.Professional or SubscriptionPlan.Agency;
}
