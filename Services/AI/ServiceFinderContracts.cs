using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Services.AI;

public sealed record ServiceFinderAnswers(IReadOnlyDictionary<string, System.Text.Json.JsonElement> Values);

public sealed record ServiceRecommendation(
    Guid ServiceId,
    string ServiceName,
    int Score,
    decimal Price,
    string Currency,
    int DurationMinutes,
    IReadOnlyList<Guid> SuggestedEmployeeIds);

public sealed record RequiredQuestion(
    string QuestionKey,
    string QuestionText,
    ServiceFinderAnswerType AnswerType);

public sealed record CustomerGuidance(
    Guid GuidanceId,
    ServiceGuidanceType GuidanceType,
    string Title,
    string Content,
    string SourceLabel);

public sealed record ServiceFinderResult(
    IReadOnlyList<ServiceRecommendation> Recommendations,
    IReadOnlyList<RequiredQuestion> MissingQuestions,
    IReadOnlyList<CustomerGuidance> Guidance,
    bool RequiresHumanConsultation,
    IReadOnlyList<Guid> SuggestedEmployeeIds);

public interface IServiceFinderEngine
{
    Task<ServiceFinderResult> FindServicesAsync(
        Guid tenantId,
        ServiceFinderAnswers answers,
        CancellationToken cancellationToken,
        bool bypassEnabledCheck = false);
}

public sealed record OrchestratedFinderResponse(
    string Message,
    IReadOnlyList<Guid> RecommendedServiceIds,
    IReadOnlyList<string> RequiredQuestionKeys,
    IReadOnlyList<Guid> GuidanceIds,
    IReadOnlyList<Guid> SuggestedEmployeeIds,
    bool RequiresConfirmation,
    bool RequiresHumanConsultation,
    bool UsedAiFallback,
    IReadOnlyList<string> Sources);

public interface IAiOrchestrator
{
    Task<OrchestratedFinderResponse> BuildFinderResponseAsync(
        Guid tenantId,
        ServiceFinderResult result,
        string? freeText,
        CancellationToken cancellationToken);

    Task<ChatbotTurnResponse> BuildChatbotTurnAsync(
        Guid tenantId,
        Guid conversationId,
        IReadOnlyList<(string Role, string Content)> history,
        ServiceFinderResult result,
        string latestMessage,
        CancellationToken cancellationToken);

    Task<DesignAssistantTurnResponse> BuildDesignAssistantTurnAsync(
        Guid tenantId,
        IReadOnlyList<(string Role, string Content)> history,
        string latestMessage,
        string industryType,
        CancellationToken cancellationToken);
}

// The chatbot's per-turn reply. Deliberately separate from OrchestratedFinderResponse (used by
// the existing click-wizard Finder) rather than reused — the two features render very
// differently (a single explanatory message vs. an ongoing back-and-forth) even though the
// underlying safety pattern (deterministic engine first, AI sanitized against its allowed IDs,
// fallback on any failure) is identical. See AiOrchestrator.BuildChatbotTurnAsync.
public sealed record ChatbotTurnResponse(
    Guid ConversationId,
    string Reply,
    IReadOnlyList<Guid> RecommendedServiceIds,
    bool RequiresHumanConsultation,
    bool UsedAiFallback);

public sealed record AiProviderMessage(
    string Message,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCost,
    string Model,
    IReadOnlyList<Guid> SuggestedServiceIds,
    IReadOnlyList<Guid> SuggestedGuidanceIds,
    IReadOnlyList<Guid> SuggestedEmployeeIds,
    bool RequiresHumanConsultation);

public interface IAiProviderAdapter
{
    Task<AiProviderMessage?> ExplainFinderResultAsync(
        Guid tenantId,
        string? freeText,
        ServiceFinderResult result,
        IReadOnlyList<KnowledgeResult> knowledge,
        CancellationToken cancellationToken);

    Task<AiProviderMessage?> ChatTurnAsync(
        Guid tenantId,
        IReadOnlyList<(string Role, string Content)> history,
        string latestMessage,
        ServiceFinderResult result,
        CancellationToken cancellationToken);

    Task<DesignAssistantProviderMessage?> SuggestDesignAsync(
        Guid tenantId,
        IReadOnlyList<(string Role, string Content)> history,
        string latestMessage,
        string industryType,
        CancellationToken cancellationToken);
}

public interface IAiUsageMeter
{
    Task TrackAsync(Guid tenantId, string feature, string model, int inputTokens, int outputTokens, decimal estimatedCost, CancellationToken cancellationToken);
}

// Per-tenant monthly AI spend ceiling — checked before every LLM call, right alongside the
// existing Agency-plan check in AiOrchestrator. "Not within budget" takes the same fallback path
// as any other AI failure (see AiOrchestrator.BuildFinderResponseAsync's catch block): the caller
// simply never calls the LLM for that tenant this period, and the deterministic response is
// returned instead — no error surfaces to the customer.
public interface IAiUsageGate
{
    Task<bool> IsWithinBudgetAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed record KnowledgeResult(
    string Title,
    string Excerpt,
    string SourceLabel,
    KnowledgeVisibility Visibility,
    Guid? ServiceId);

public interface IKnowledgeRetrievalService
{
    Task<IReadOnlyList<KnowledgeResult>> SearchAsync(
        Guid tenantId,
        KnowledgeVisibility visibility,
        string query,
        Guid? serviceId,
        CancellationToken cancellationToken);
}
