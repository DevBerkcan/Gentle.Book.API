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
}
