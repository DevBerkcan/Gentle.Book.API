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

    // Found during live pre-launch testing: the chatbot answered "no" to real services that just
    // weren't among the Finder engine's top-3 (the engine is always run with an empty answers
    // dictionary for the chatbot, so result.Recommendations is the same generic 3-service pick
    // every turn, not "everything the tenant offers"). This confirms the full active-service
    // catalog — not just that narrow subset — is what actually reaches the provider and the
    // suggestedServiceIds allow-list.
    [Fact]
    public async Task BuildChatbotTurnAsync_GroundsAgainstFullActiveCatalog_NotJustFinderTopThree()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "T", Slug = "t-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = SubscriptionPlan.Agency, Status = SubscriptionStatus.Active });
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Beauty", IsActive = true };
        db.ServiceCategories.Add(category);
        var laser = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Laser-Haarentfernung", DurationMinutes = 30, Price = 80m, Currency = "EUR", IsActive = true };
        db.Services.Add(laser);
        await db.SaveChangesAsync();

        var echoProvider = new EchoingPromptAiProviderAdapter();
        var orchestrator = new AiOrchestrator(echoProvider, new AiUsageMeter(db), new AiUsageGate(db), new KnowledgeRetrievalService(db), db, NullLogger<AiOrchestrator>.Instance);

        // result.Recommendations deliberately does NOT include "laser" — mirrors the real
        // controller always calling FindServicesAsync with empty answers, whose top-3 fallback
        // picks are unrelated to what the customer is actually asking about.
        var narrowResult = ResultWithOneService(Guid.NewGuid());

        await orchestrator.BuildChatbotTurnAsync(
            tenant.Id, Guid.NewGuid(), Array.Empty<(string, string)>(), narrowResult, "Bietet ihr Laser-Haarentfernung an?", CancellationToken.None);

        Assert.Contains("Laser-Haarentfernung", echoProvider.LastResultSeen!.Recommendations.Select(r => r.ServiceName));
    }

    private sealed class EchoingPromptAiProviderAdapter : IAiProviderAdapter
    {
        public ServiceFinderResult? LastResultSeen { get; private set; }

        public Task<AiProviderMessage?> ExplainFinderResultAsync(
            Guid tenantId, string? freeText, ServiceFinderResult result,
            IReadOnlyList<KnowledgeResult> knowledge, CancellationToken cancellationToken)
            => Task.FromResult<AiProviderMessage?>(null);

        public Task<AiProviderMessage?> ChatTurnAsync(
            Guid tenantId, IReadOnlyList<(string Role, string Content)> history, string latestMessage,
            ServiceFinderResult result, CancellationToken cancellationToken)
        {
            LastResultSeen = result;
            return Task.FromResult<AiProviderMessage?>(new AiProviderMessage(
                Message: "ok", InputTokens: 1, OutputTokens: 1, EstimatedCost: 0m, Model: "echo",
                SuggestedServiceIds: new List<Guid>(), SuggestedGuidanceIds: new List<Guid>(),
                SuggestedEmployeeIds: new List<Guid>(), RequiresHumanConsultation: false));
        }

        public Task<DesignAssistantProviderMessage?> SuggestDesignAsync(
            Guid tenantId, IReadOnlyList<(string Role, string Content)> history, string latestMessage,
            string industryType, CancellationToken cancellationToken)
            => Task.FromResult<DesignAssistantProviderMessage?>(null);
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

        public Task<DesignAssistantProviderMessage?> SuggestDesignAsync(
            Guid tenantId, IReadOnlyList<(string Role, string Content)> history, string latestMessage,
            string industryType, CancellationToken cancellationToken)
            => Task.FromResult<DesignAssistantProviderMessage?>(null);
    }
}
