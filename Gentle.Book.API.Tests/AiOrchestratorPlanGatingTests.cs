using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Spy: records whether/how it was invoked, so tests can assert the real provider is never
// even called for non-Agency tenants (not just "returns null") — matches the cost/latency
// guarantee described in AiOrchestrator.BuildFinderResponseAsync.
public class SpyAiProviderAdapter : IAiProviderAdapter
{
    public int CallCount { get; private set; }

    public Task<AiProviderMessage?> ExplainFinderResultAsync(
        Guid tenantId, string? freeText, ServiceFinderResult result,
        IReadOnlyList<KnowledgeResult> knowledge, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult<AiProviderMessage?>(new AiProviderMessage(
            Message: "Spy response",
            InputTokens: 10,
            OutputTokens: 5,
            EstimatedCost: 0.001m,
            Model: "spy-model",
            SuggestedServiceIds: result.Recommendations.Select(r => r.ServiceId).ToList(),
            SuggestedGuidanceIds: new List<Guid>(),
            SuggestedEmployeeIds: new List<Guid>(),
            RequiresHumanConsultation: false));
    }
}

public class AiOrchestratorPlanGatingTests
{
    private static (AiOrchestrator orchestrator, SpyAiProviderAdapter spy) Build(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var spy = new SpyAiProviderAdapter();
        var usageMeter = new AiUsageMeter(db);
        var knowledge = new KnowledgeRetrievalService(db);
        var orchestrator = new AiOrchestrator(spy, usageMeter, knowledge, db, NullLogger<AiOrchestrator>.Instance);
        return (orchestrator, spy);
    }

    private static ServiceFinderResult EmptyResult() => new(
        Recommendations: new List<ServiceRecommendation>(),
        MissingQuestions: new List<RequiredQuestion>(),
        Guidance: new List<CustomerGuidance>(),
        RequiresHumanConsultation: false,
        SuggestedEmployeeIds: new List<Guid>());

    [Theory]
    [InlineData(SubscriptionPlan.Trial)]
    [InlineData(SubscriptionPlan.Starter)]
    [InlineData(SubscriptionPlan.Professional)]
    public async Task BuildFinderResponseAsync_NonAgencyPlan_NeverCallsProvider(SubscriptionPlan plan)
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "T", Slug = "t-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = plan, Status = SubscriptionStatus.Active });
        await db.SaveChangesAsync();

        var (orchestrator, spy) = Build(db);

        var response = await orchestrator.BuildFinderResponseAsync(tenant.Id, EmptyResult(), null, CancellationToken.None);

        Assert.Equal(0, spy.CallCount);
        Assert.True(response.UsedAiFallback);
    }

    [Fact]
    public async Task BuildFinderResponseAsync_AgencyPlan_CallsProviderAndUsesItsMessage()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "T", Slug = "t-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = SubscriptionPlan.Agency, Status = SubscriptionStatus.Active });
        await db.SaveChangesAsync();

        var (orchestrator, spy) = Build(db);

        var response = await orchestrator.BuildFinderResponseAsync(tenant.Id, EmptyResult(), null, CancellationToken.None);

        Assert.Equal(1, spy.CallCount);
        Assert.False(response.UsedAiFallback);
        Assert.Equal("Spy response", response.Message);
    }
}
