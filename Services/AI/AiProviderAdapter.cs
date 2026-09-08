namespace GentleBook.Api.Services.AI;

public sealed class NullAiProviderAdapter : IAiProviderAdapter
{
    public Task<AiProviderMessage?> ExplainFinderResultAsync(
        Guid tenantId,
        string? freeText,
        ServiceFinderResult result,
        IReadOnlyList<KnowledgeResult> knowledge,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<AiProviderMessage?>(null);
    }

    public Task<AiProviderMessage?> ChatTurnAsync(
        Guid tenantId,
        IReadOnlyList<(string Role, string Content)> history,
        string latestMessage,
        ServiceFinderResult result,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<AiProviderMessage?>(null);
    }
}
