using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers AiOrchestrator.BuildDesignAssistantTurnAsync (the "Meine Links" design editor's AI
// assistant) at two layers: the orchestrator's plan/budget gating (via SpyAiProviderAdapter,
// same convention as AiOrchestratorPlanGatingTests/AiOrchestratorChatbotTests), and the real
// OpenAiProviderAdapter's sanitization of pageTemplate/colorPaletteKey against the hardcoded
// allow-lists (via FakeHttpMessageHandler, same convention as EmployeesControllerAiTaglineTests).
public class AiOrchestratorDesignAssistantTests
{
    private static (AiOrchestrator orchestrator, SpyAiProviderAdapter spy) BuildOrchestrator(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var spy = new SpyAiProviderAdapter();
        var usageMeter = new AiUsageMeter(db);
        var usageGate = new AiUsageGate(db);
        var knowledge = new KnowledgeRetrievalService(db);
        var orchestrator = new AiOrchestrator(spy, usageMeter, usageGate, knowledge, db, NullLogger<AiOrchestrator>.Instance);
        return (orchestrator, spy);
    }

    [Theory]
    [InlineData(SubscriptionPlan.Trial)]
    [InlineData(SubscriptionPlan.Starter)]
    [InlineData(SubscriptionPlan.Professional)]
    public async Task BuildDesignAssistantTurnAsync_NonAgencyPlan_NeverCallsProvider(SubscriptionPlan plan)
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, plan);

        var (orchestrator, spy) = BuildOrchestrator(db);

        var response = await orchestrator.BuildDesignAssistantTurnAsync(
            tenant.Id, Array.Empty<(string, string)>(), "Ich bin ein Barbershop, modern, dunkel.", "Barbershop", CancellationToken.None);

        Assert.Equal(0, spy.SuggestDesignCallCount);
        Assert.True(response.UsedAiFallback);
        Assert.Null(response.Suggestion);
    }

    [Fact]
    public async Task BuildDesignAssistantTurnAsync_AgencyPlanBudgetExhausted_NeverCallsProviderAndFallsBack()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency);
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenant.Id, CurrentPeriodCostUsd = GentleBook.Api.Configuration.OpenAiPricing.PlatformDefaultMonthlyBudgetUsd });
        await db.SaveChangesAsync();

        var (orchestrator, spy) = BuildOrchestrator(db);

        var response = await orchestrator.BuildDesignAssistantTurnAsync(
            tenant.Id, Array.Empty<(string, string)>(), "Ich bin ein Barbershop, modern, dunkel.", "Barbershop", CancellationToken.None);

        Assert.Equal(0, spy.SuggestDesignCallCount);
        Assert.True(response.UsedAiFallback);
    }

    [Fact]
    public async Task BuildDesignAssistantTurnAsync_AgencyPlanWithinBudget_UsesProviderSuggestion()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency);

        var (orchestrator, spy) = BuildOrchestrator(db);

        var response = await orchestrator.BuildDesignAssistantTurnAsync(
            tenant.Id, Array.Empty<(string, string)>(), "Ich bin ein Barbershop, modern, dunkel.", "Barbershop", CancellationToken.None);

        Assert.Equal(1, spy.SuggestDesignCallCount);
        Assert.False(response.UsedAiFallback);
        Assert.Equal("Spy design response", response.Reply);
    }

    private static HttpResponseMessage ChatCompletionResponse(string jsonPayload)
    {
        var escaped = jsonPayload.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var body = "{\"model\":\"gpt-4o-mini\",\"choices\":[{\"message\":{\"content\":\"" + escaped + "\"}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":4}}";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private static OpenAiProviderAdapter BuildRealProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var aiOptions = new AiProviderOptions { ApiKey = "test-key" };
        var client = new OpenAiClient(
            new HttpClient(new FakeHttpMessageHandler(responder)) { BaseAddress = new Uri("https://api.openai.com/v1/") },
            new StaticOptionsMonitor<AiProviderOptions>(aiOptions));
        return new OpenAiProviderAdapter(client, NullLogger<OpenAiProviderAdapter>.Instance);
    }

    [Fact]
    public async Task SuggestDesignAsync_ProviderHallucinatesUnknownKeys_AreSanitizedToNull()
    {
        // The model is told to pick exclusively from the hardcoded DesignPageTemplateKeys /
        // DesignColorPaletteKeys allow-lists, but nothing stops a misbehaving or hallucinating
        // completion from returning values outside them — this proves OpenAiProviderAdapter
        // drops those rather than passing them straight through to the frontend's apply logic.
        var provider = BuildRealProvider(_ => ChatCompletionResponse(
            "{\"message\":\"Alles klar!\",\"pageTemplate\":\"totally-made-up\",\"colorPaletteKey\":\"not-a-real-palette\",\"ctaText\":\"Jetzt buchen\",\"ctaBadge\":\"Neu\"}"));

        var result = await provider.SuggestDesignAsync(
            Guid.NewGuid(), Array.Empty<(string, string)>(), "Ich bin ein Barbershop, modern, dunkel.", "Barbershop", CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result!.Suggestion);
        Assert.Null(result.Suggestion!.PageTemplate);
        Assert.Null(result.Suggestion!.ColorPaletteKey);
        Assert.Equal("Jetzt buchen", result.Suggestion!.CtaText);
        Assert.Equal("Neu", result.Suggestion!.CtaBadge);
    }

    [Fact]
    public async Task SuggestDesignAsync_ProviderReturnsAllowedKeys_ArePassedThrough()
    {
        var provider = BuildRealProvider(_ => ChatCompletionResponse(
            "{\"message\":\"Perfekt, hier ist mein Vorschlag!\",\"pageTemplate\":\"barbershop\",\"colorPaletteKey\":\"midnight\",\"ctaText\":\"Termin buchen\",\"ctaBadge\":null}"));

        var result = await provider.SuggestDesignAsync(
            Guid.NewGuid(), Array.Empty<(string, string)>(), "Ich bin ein Barbershop, modern, dunkel.", "Barbershop", CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result!.Suggestion);
        Assert.Equal("barbershop", result.Suggestion!.PageTemplate);
        Assert.Equal("midnight", result.Suggestion!.ColorPaletteKey);
        Assert.Equal("Termin buchen", result.Suggestion!.CtaText);
        Assert.Null(result.Suggestion!.CtaBadge);
    }
}
