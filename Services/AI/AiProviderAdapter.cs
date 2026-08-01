namespace GentleBook.Api.Services.AI;

public sealed class NullAiProviderAdapter : IAiProviderAdapter
{
    public Task<AiProviderMessage?> ExplainFinderResultAsync(
        Guid tenantId,
        string? freeText,
        ServiceFinderResult result,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<AiProviderMessage?>(null);
    }
}
