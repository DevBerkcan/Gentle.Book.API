using System.Text;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services.AI;

public sealed class AiOrchestrator : IAiOrchestrator
{
    private readonly IAiProviderAdapter _provider;
    private readonly IAiUsageMeter _usageMeter;
    private readonly IKnowledgeRetrievalService _knowledge;
    private readonly GentleBookDbContext _db;
    private readonly ILogger<AiOrchestrator> _logger;

    public AiOrchestrator(
        IAiProviderAdapter provider,
        IAiUsageMeter usageMeter,
        IKnowledgeRetrievalService knowledge,
        GentleBookDbContext db,
        ILogger<AiOrchestrator> logger)
    {
        _provider = provider;
        _usageMeter = usageMeter;
        _knowledge = knowledge;
        _db = db;
        _logger = logger;
    }

    public async Task<OrchestratedFinderResponse> BuildFinderResponseAsync(
        Guid tenantId,
        ServiceFinderResult result,
        string? freeText,
        CancellationToken cancellationToken)
    {
        var allowedServiceIds = result.Recommendations.Select(r => r.ServiceId).ToHashSet();
        var allowedGuidanceIds = result.Guidance.Select(g => g.GuidanceId).ToHashSet();
        var allowedEmployeeIds = result.SuggestedEmployeeIds.ToHashSet();

        var sources = new List<string>();
        if (!string.IsNullOrWhiteSpace(freeText))
        {
            var knowledge = await _knowledge.SearchAsync(tenantId, KnowledgeVisibility.Public, freeText, null, cancellationToken);
            sources.AddRange(knowledge.Select(k => k.SourceLabel).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        try
        {
            var aiMessage = await _provider.ExplainFinderResultAsync(tenantId, freeText, result, cancellationToken);
            if (aiMessage != null)
            {
                await _usageMeter.TrackAsync(
                    tenantId,
                    feature: "ai_service_finder",
                    model: aiMessage.Model,
                    inputTokens: aiMessage.InputTokens,
                    outputTokens: aiMessage.OutputTokens,
                    estimatedCost: aiMessage.EstimatedCost,
                    cancellationToken: cancellationToken);

                var safeServiceIds = aiMessage.SuggestedServiceIds.Where(allowedServiceIds.Contains).ToList();
                var safeGuidanceIds = aiMessage.SuggestedGuidanceIds.Where(allowedGuidanceIds.Contains).ToList();
                var safeEmployeeIds = aiMessage.SuggestedEmployeeIds.Where(allowedEmployeeIds.Contains).ToList();

                if (safeServiceIds.Count == 0)
                    safeServiceIds = result.Recommendations.Select(r => r.ServiceId).ToList();

                var message = string.IsNullOrWhiteSpace(aiMessage.Message)
                    ? BuildFallbackMessage(result)
                    : aiMessage.Message;

                await WriteAuditAsync(tenantId, "AiFinderResponse", "Completed", cancellationToken);

                return new OrchestratedFinderResponse(
                    message,
                    safeServiceIds,
                    result.MissingQuestions.Select(q => q.QuestionKey).ToList(),
                    safeGuidanceIds.Count > 0 ? safeGuidanceIds : result.Guidance.Select(g => g.GuidanceId).ToList(),
                    safeEmployeeIds.Count > 0 ? safeEmployeeIds : result.SuggestedEmployeeIds,
                    RequiresConfirmation: true,
                    RequiresHumanConsultation: aiMessage.RequiresHumanConsultation || result.RequiresHumanConsultation,
                    UsedAiFallback: false,
                    Sources: sources);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI provider unavailable. Falling back to deterministic explanation.");
        }

        await WriteAuditAsync(tenantId, "AiFinderResponse", "Fallback", cancellationToken);

        return new OrchestratedFinderResponse(
            BuildFallbackMessage(result),
            result.Recommendations.Select(r => r.ServiceId).ToList(),
            result.MissingQuestions.Select(q => q.QuestionKey).ToList(),
            result.Guidance.Select(g => g.GuidanceId).ToList(),
            result.SuggestedEmployeeIds,
            RequiresConfirmation: true,
            RequiresHumanConsultation: result.RequiresHumanConsultation,
            UsedAiFallback: true,
            Sources: sources);
    }

    private static string BuildFallbackMessage(ServiceFinderResult result)
    {
        if (result.Recommendations.Count == 0)
            return "Wir haben aktuell keine eindeutige Empfehlung. Bitte nutze die normale Buchungsansicht oder kontaktiere das Studio.";

        var sb = new StringBuilder();
        sb.Append("Basierend auf deinen Angaben passen am besten: ");
        sb.Append(string.Join(", ", result.Recommendations.Select(r => r.ServiceName)));

        if (result.MissingQuestions.Count > 0)
            sb.Append(". Für eine genauere Empfehlung benötigen wir noch ein paar Angaben.");

        if (result.Guidance.Count > 0)
            sb.Append(" Bitte beachte die freigegebenen Hinweise des Studios.");

        return sb.ToString();
    }

    private async Task WriteAuditAsync(Guid tenantId, string actionType, string status, CancellationToken cancellationToken)
    {
        _db.AiActions.Add(new AiAction
        {
            TenantId = tenantId,
            ActionType = actionType,
            Status = status,
            InputJson = "{}",
            OutputJson = "{}",
            CreatedOn = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);
    }
}
