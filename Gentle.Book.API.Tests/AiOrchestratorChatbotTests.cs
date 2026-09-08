using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// AiOrchestrator.BuildChatbotTurnAsync mirrors BuildFinderResponseAsync's safety pattern (plan
// gate, real-time budget gate, sanitize AI-suggested service IDs against the deterministic
// engine's own allowed set, fall back gracefully on any failure) — these tests exercise that
// mirror directly rather than through the controller.
public class AiOrchestratorChatbotTests
{
    private static (AiOrchestrator orchestrator, SpyAiProviderAdapter spy) Build(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var spy = new SpyAiProviderAdapter();
        var usageMeter = new AiUsageMeter(db);
        var usageGate = new AiUsageGate(db);
        var knowledge = new KnowledgeRetrievalService(db);
        var orchestrator = new AiOrchestrator(spy, usageMeter, usageGate, knowledge, db, NullLogger<AiOrchestrator>.Instance);
        return (orchestrator, spy);
    }

    private static ServiceFinderResult ResultWithOneService(Guid serviceId) => new(
        Recommendations: new List<ServiceRecommendation> { new(serviceId, "Haarschnitt", 100, 50m, "EUR", 30, new List<Guid>()) },
        MissingQuestions: new List<RequiredQuestion>(),
        Guidance: new List<CustomerGuidance>(),
        RequiresHumanConsultation: false,
        SuggestedEmployeeIds: new List<Guid>());

    [Fact]
    public async Task BuildChatbotTurnAsync_NonAgencyPlan_NeverCallsProvider()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "T", Slug = "t-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = SubscriptionPlan.Professional, Status = SubscriptionStatus.Active });
        await db.SaveChangesAsync();
        var (orchestrator, spy) = Build(db);
        var conversationId = Guid.NewGuid();

        var response = await orchestrator.BuildChatbotTurnAsync(
            tenant.Id, conversationId, Array.Empty<(string, string)>(), ResultWithOneService(Guid.NewGuid()), "Hallo", CancellationToken.None);

        Assert.Equal(0, spy.ChatTurnCallCount);
        Assert.True(response.UsedAiFallback);
        Assert.Equal(conversationId, response.ConversationId);
    }

    [Fact]
    public async Task BuildChatbotTurnAsync_AgencyPlanBudgetExhausted_NeverCallsProviderAndFallsBack()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "T", Slug = "t-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = SubscriptionPlan.Agency, Status = SubscriptionStatus.Active });
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenant.Id, CurrentPeriodCostUsd = GentleBook.Api.Configuration.OpenAiPricing.PlatformDefaultMonthlyBudgetUsd });
        await db.SaveChangesAsync();
        var (orchestrator, spy) = Build(db);

        var response = await orchestrator.BuildChatbotTurnAsync(
            tenant.Id, Guid.NewGuid(), Array.Empty<(string, string)>(), ResultWithOneService(Guid.NewGuid()), "Hallo", CancellationToken.None);

        Assert.Equal(0, spy.ChatTurnCallCount);
        Assert.True(response.UsedAiFallback);
    }

    [Fact]
    public async Task BuildChatbotTurnAsync_AgencyPlanWithinBudget_CallsProviderAndUsesItsMessage()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "T", Slug = "t-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = SubscriptionPlan.Agency, Status = SubscriptionStatus.Active });
        await db.SaveChangesAsync();
        var (orchestrator, spy) = Build(db);

        var response = await orchestrator.BuildChatbotTurnAsync(
            tenant.Id, Guid.NewGuid(), Array.Empty<(string, string)>(), ResultWithOneService(Guid.NewGuid()), "Hallo", CancellationToken.None);

        Assert.Equal(1, spy.ChatTurnCallCount);
        Assert.False(response.UsedAiFallback);
        Assert.Equal("Spy chat response", response.Reply);
    }

    [Fact]
    public async Task BuildChatbotTurnAsync_ProviderSuggestsDisallowedServiceId_IsFilteredOut()
    {
        // Spy always echoes back result.Recommendations' own service IDs (see SpyAiProviderAdapter),
        // so to prove sanitization actually filters, this constructs the mismatch explicitly: the
        // deterministic result the orchestrator receives has service X as "allowed", but the model
        // (via a purpose-built fake here) tries to suggest a different, disallowed ID.
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "T", Slug = "t-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = SubscriptionPlan.Agency, Status = SubscriptionStatus.Active });
        await db.SaveChangesAsync();

        var allowedServiceId = Guid.NewGuid();
        var disallowedServiceId = Guid.NewGuid();
        var fakeProvider = new HallucinatingAiProviderAdapter(disallowedServiceId);
        var orchestrator = new AiOrchestrator(fakeProvider, new AiUsageMeter(db), new AiUsageGate(db), new KnowledgeRetrievalService(db), db, NullLogger<AiOrchestrator>.Instance);

        var response = await orchestrator.BuildChatbotTurnAsync(
            tenant.Id, Guid.NewGuid(), Array.Empty<(string, string)>(), ResultWithOneService(allowedServiceId), "Hallo", CancellationToken.None);

        Assert.DoesNotContain(disallowedServiceId, response.RecommendedServiceIds);
    }

    private sealed class HallucinatingAiProviderAdapter : IAiProviderAdapter
    {
        private readonly Guid _hallucinatedServiceId;
        public HallucinatingAiProviderAdapter(Guid hallucinatedServiceId) => _hallucinatedServiceId = hallucinatedServiceId;

        public Task<AiProviderMessage?> ExplainFinderResultAsync(
            Guid tenantId, string? freeText, ServiceFinderResult result,
            IReadOnlyList<KnowledgeResult> knowledge, CancellationToken cancellationToken)
            => Task.FromResult<AiProviderMessage?>(null);

        public Task<AiProviderMessage?> ChatTurnAsync(
            Guid tenantId, IReadOnlyList<(string Role, string Content)> history, string latestMessage,
            ServiceFinderResult result, CancellationToken cancellationToken)
        {
            return Task.FromResult<AiProviderMessage?>(new AiProviderMessage(
                Message: "Hier ist ein erfundener Service!",
                InputTokens: 10, OutputTokens: 5, EstimatedCost: 0.001m, Model: "fake",
                SuggestedServiceIds: new List<Guid> { _hallucinatedServiceId },
                SuggestedGuidanceIds: new List<Guid>(),
                SuggestedEmployeeIds: new List<Guid>(),
                RequiresHumanConsultation: false));
        }
    }
}
