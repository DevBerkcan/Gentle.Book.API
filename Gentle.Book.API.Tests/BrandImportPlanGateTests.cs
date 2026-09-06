using GentleBook.Api.Configuration;
using GentleBook.Api.Data.Entities;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the "Brand-import re-analysis" Agency-exclusive gate (BrandImportPlanGate.cs) — had
// zero test coverage before this pass. The pipeline itself (real website fetch + AI analysis)
// is already covered by the existing BrandImport*Tests files; this covers only the plan-rank
// gate, which is pure logic and the part that was untested.
public class BrandImportPlanGateTests
{
    [Theory]
    [InlineData(SubscriptionPlan.Trial)]
    [InlineData(SubscriptionPlan.Starter)]
    [InlineData(SubscriptionPlan.Professional)]
    public void ValidateReanalysisForPlan_BelowAgency_IsBlocked(SubscriptionPlan plan)
    {
        var result = BrandImportPlanGate.ValidateReanalysisForPlan(plan);

        Assert.NotNull(result);
        Assert.Equal("Agency", result);
    }

    [Fact]
    public void ValidateReanalysisForPlan_Agency_IsAllowed()
    {
        var result = BrandImportPlanGate.ValidateReanalysisForPlan(SubscriptionPlan.Agency);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(SubscriptionPlan.Trial)]
    [InlineData(SubscriptionPlan.Starter)]
    public void ValidateAnalysisForPlan_BelowProfessional_IsBlocked(SubscriptionPlan plan)
    {
        Assert.NotNull(BrandImportPlanGate.ValidateAnalysisForPlan(plan));
    }

    [Theory]
    [InlineData(SubscriptionPlan.Professional)]
    [InlineData(SubscriptionPlan.Agency)]
    public void ValidateAnalysisForPlan_ProfessionalOrAbove_IsAllowed(SubscriptionPlan plan)
    {
        Assert.Null(BrandImportPlanGate.ValidateAnalysisForPlan(plan));
    }

    [Theory]
    [InlineData(SubscriptionPlan.Trial)]
    [InlineData(SubscriptionPlan.Starter)]
    public void ValidateContentExtractionForPlan_BelowProfessional_IsBlocked(SubscriptionPlan plan)
    {
        Assert.NotNull(BrandImportPlanGate.ValidateContentExtractionForPlan(plan));
    }

    [Theory]
    [InlineData(SubscriptionPlan.Professional)]
    [InlineData(SubscriptionPlan.Agency)]
    public void ValidateContentExtractionForPlan_ProfessionalOrAbove_IsAllowed(SubscriptionPlan plan)
    {
        Assert.Null(BrandImportPlanGate.ValidateContentExtractionForPlan(plan));
    }
}
