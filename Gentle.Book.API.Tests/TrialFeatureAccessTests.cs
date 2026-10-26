using GentleBook.Api.Configuration;
using GentleBook.Api.Data.Entities;
using Xunit;

namespace Gentle.Book.API.Tests;

public class TrialFeatureAccessTests
{
    [Fact]
    public void Trial_IncludesNonAiFeaturesAndUnlimitedCapacity()
    {
        var limits = PlanLimits.Get(SubscriptionPlan.Trial);
        Assert.True(limits.HasAnalytics);
        Assert.True(limits.HasApiAccess);
        Assert.True(PlanLimits.IsUnlimited(limits.MaxEmployees));
        Assert.True(PlanLimits.IsUnlimited(limits.MaxServices));
        Assert.True(PlanLimits.IsUnlimited(limits.MaxBookingsPerMonth));
        Assert.Null(AgencyFeatureGate.ValidateNonAiForPlan(SubscriptionPlan.Trial));
        foreach (var template in LinkPageTemplates.AllTemplateIds)
            Assert.Null(LinkPageTemplates.ValidateTemplateForPlan(template, SubscriptionPlan.Trial));
    }

    [Fact]
    public void Trial_DoesNotUnlockAnyAiGate()
    {
        Assert.NotNull(AgencyFeatureGate.ValidateForPlan(SubscriptionPlan.Trial));
        Assert.False(AgencyFeatureGate.IsAgency(SubscriptionPlan.Trial));
        Assert.NotNull(AiFinderPlanGate.ValidateForPlan(SubscriptionPlan.Trial));
        Assert.NotNull(BrandImportPlanGate.ValidateAnalysisForPlan(SubscriptionPlan.Trial));
        Assert.NotNull(BrandImportPlanGate.ValidateReanalysisForPlan(SubscriptionPlan.Trial));
        Assert.NotNull(BrandImportPlanGate.ValidateContentExtractionForPlan(SubscriptionPlan.Trial));
    }
}
