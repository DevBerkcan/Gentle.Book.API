using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Services.AI;

public sealed class AiUsageMeter : IAiUsageMeter
{
    private readonly GentleBookDbContext _db;

    public AiUsageMeter(GentleBookDbContext db)
    {
        _db = db;
    }

    public async Task TrackAsync(
        Guid tenantId,
        string feature,
        string model,
        int inputTokens,
        int outputTokens,
        decimal estimatedCost,
        CancellationToken cancellationToken)
    {
        _db.AiUsages.Add(new AiUsage
        {
            TenantId = tenantId,
            Feature = feature,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCost = estimatedCost,
            CreatedOn = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);
    }
}
