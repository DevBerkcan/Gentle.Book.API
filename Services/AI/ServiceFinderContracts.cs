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
        CancellationToken cancellationToken);
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
}

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
        CancellationToken cancellationToken);
}

public interface IAiUsageMeter
{
    Task TrackAsync(Guid tenantId, string feature, string model, int inputTokens, int outputTokens, decimal estimatedCost, CancellationToken cancellationToken);
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
