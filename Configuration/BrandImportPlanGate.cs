using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Configuration;

// Feature keys are never hardcoded plan names — see spec section 14. Mirrors the
// AiFinderPlanGate / LinkPageTemplates "ValidateXForPlan" pattern: null return means allowed,
// otherwise the display name of the plan actually required (for the 402 error message).
public static class BrandImportPlanGate
{
    public const string FeatureWebsiteBrandImport = "website_brand_import";
    public const string FeatureAiBrandAnalysis = "ai_brand_analysis";
    public const string FeatureBrandImportReanalysis = "brand_import_reanalysis";
    public const string FeatureBrandImportContentExtraction = "brand_import_content_extraction";

    // Basis (all paid plans incl. Trial): store website URL + manually copy logo/colors — no
    // gating needed, that's just editing existing TenantSettings fields.

    // Automatic AI-driven analysis (colors/fonts/3 proposals/content suggestions): Professional+.
    private static readonly SubscriptionPlan AnalysisRequiredPlan = SubscriptionPlan.Professional;

    // Repeated/re-analysis + change detection: Agency (Business) only.
    private static readonly SubscriptionPlan ReanalysisRequiredPlan = SubscriptionPlan.Agency;

    // LLM-based services/pricing extraction: same threshold as base analysis today — one analyze
    // call covers Branding + Services + Links, no extra tier hurdle. Kept as its own gate (rather
    // than reusing ValidateAnalysisForPlan) so the threshold can be raised independently later
    // without touching the base branding-analysis gate.
    private static readonly SubscriptionPlan ContentExtractionRequiredPlan = SubscriptionPlan.Professional;

    private static int Rank(SubscriptionPlan plan) => plan switch
    {
        SubscriptionPlan.Professional => 1,
        SubscriptionPlan.Agency => 2,
        _ => 0,
    };

    public static string? ValidateAnalysisForPlan(SubscriptionPlan tenantPlan) =>
        Rank(tenantPlan) >= Rank(AnalysisRequiredPlan) ? null : PlanLimits.Get(AnalysisRequiredPlan).DisplayName;

    public static string? ValidateReanalysisForPlan(SubscriptionPlan tenantPlan) =>
        Rank(tenantPlan) >= Rank(ReanalysisRequiredPlan) ? null : PlanLimits.Get(ReanalysisRequiredPlan).DisplayName;

    public static string? ValidateContentExtractionForPlan(SubscriptionPlan tenantPlan) =>
        Rank(tenantPlan) >= Rank(ContentExtractionRequiredPlan) ? null : PlanLimits.Get(ContentExtractionRequiredPlan).DisplayName;
}
