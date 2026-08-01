using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services.AI;

public sealed class KnowledgeRetrievalService : IKnowledgeRetrievalService
{
    private readonly GentleBookDbContext _db;

    public KnowledgeRetrievalService(GentleBookDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<KnowledgeResult>> SearchAsync(
        Guid tenantId,
        KnowledgeVisibility visibility,
        string query,
        Guid? serviceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<KnowledgeResult>();

        var normalized = query.Trim().ToLowerInvariant();

        var docs = await _db.AiKnowledgeDocuments
            .AsNoTracking()
            .Include(d => d.Source)
            .Where(d => d.TenantId == tenantId && d.IsActive)
            .Where(d => d.Source.Visibility == visibility)
            .Where(d => d.Source.ApprovalStatus == ApprovalStatus.Approved)
            .Where(d => d.Source.Status == KnowledgeSourceStatus.Ready)
            .Where(d => !serviceId.HasValue || d.Source.ServiceId == null || d.Source.ServiceId == serviceId)
            .Where(d => d.Title.ToLower().Contains(normalized) || d.Content.ToLower().Contains(normalized))
            .OrderByDescending(d => d.UpdatedAt)
            .Take(8)
            .Select(d => new KnowledgeResult(
                d.Title,
                d.Content.Length > 300 ? d.Content.Substring(0, 300) + "..." : d.Content,
                string.IsNullOrWhiteSpace(d.Source.Category)
                    ? "Unternehmenswissen"
                    : d.Source.Category!,
                d.Source.Visibility,
                d.Source.ServiceId))
            .ToListAsync(cancellationToken);

        return docs;
    }
}
