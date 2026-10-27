using System.Collections.Concurrent;
using System.Text;
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services.AI;

public sealed class AiOrchestrator : IAiOrchestrator
{
    // Per-tenant serialization for the "check budget → call provider → record cost" sequence.
    // IAiUsageGate.IsWithinBudgetAsync and IAiUsageMeter.TrackAsync are two separate,
    // non-atomic DB round-trips with a slow provider call in between — without this lock, two
    // concurrent requests from the same tenant could both read "within budget" before either
    // writes its cost, letting the tenant's spend run past the cap. Keyed by tenant so different
    // tenants are never blocked by each other; static because a new lock per request would defeat
    // the purpose. Single-process deployment (see GO_LIVE_CHECKLISTE.md) — this only needs to
    // hold within one instance, not across a farm.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> TenantAiLocks = new();

    private static SemaphoreSlim GetTenantLock(Guid tenantId) =>
        TenantAiLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));

    private readonly IAiProviderAdapter _provider;
    private readonly IAiUsageMeter _usageMeter;
    private readonly IAiUsageGate _usageGate;
    private readonly IKnowledgeRetrievalService _knowledge;
    private readonly GentleBookDbContext _db;
    private readonly ILogger<AiOrchestrator> _logger;

    public AiOrchestrator(
        IAiProviderAdapter provider,
        IAiUsageMeter usageMeter,
        IAiUsageGate usageGate,
        IKnowledgeRetrievalService knowledge,
        GentleBookDbContext db,
        ILogger<AiOrchestrator> logger)
    {
        _provider = provider;
        _usageMeter = usageMeter;
        _usageGate = usageGate;
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
        var knowledgeResults = Array.Empty<KnowledgeResult>() as IReadOnlyList<KnowledgeResult>;
        if (!string.IsNullOrWhiteSpace(freeText))
        {
            knowledgeResults = await _knowledge.SearchAsync(tenantId, KnowledgeVisibility.Public, freeText, null, cancellationToken);
            sources.AddRange(knowledgeResults.Select(k => k.SourceLabel).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        // The real LLM-backed explanation is Agency-exclusive — Trial/Starter/Professional get
        // exactly today's deterministic behavior, and the provider is never even called for them
        // (not just "returns null"), so there's no accidental cost or latency on lower tiers.
        var tenantPlan = await _db.Subscriptions
            .Where(s => s.TenantId == tenantId)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync(cancellationToken);
        var isAgency = tenantPlan.HasValue && AgencyFeatureGate.IsAgency(tenantPlan.Value);
        // Budget check only needs to run for Agency tenants — no LLM call would happen for
        // anyone else anyway, and skipping it avoids an extra DB round-trip on every other plan.
        // The lock serializes check→call→track per tenant (see TenantAiLocks above) so two
        // concurrent requests from the same tenant can't both slip past the budget check before
        // either records its cost.
        var tenantLock = isAgency ? GetTenantLock(tenantId) : null;
        if (tenantLock != null) await tenantLock.WaitAsync(cancellationToken);
        try
        {
            var withinBudget = isAgency && await _usageGate.IsWithinBudgetAsync(tenantId, cancellationToken);

            try
            {
                var aiMessage = withinBudget
                    ? await _provider.ExplainFinderResultAsync(tenantId, freeText, result, knowledgeResults, cancellationToken)
                    : null;
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
        }
        finally
        {
            tenantLock?.Release();
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

    // Same shape as BuildFinderResponseAsync above (deterministic engine first, AI sanitized
    // against its allowed service IDs, real-time budget gate, graceful fallback on any failure)
    // but for the chatbot widget: takes/returns conversation history instead of a one-shot
    // explanation, and doesn't touch AiConversation/AiMessage itself — the calling controller
    // owns persisting the conversation (it also needs to store the user's own message, which
    // this method never sees written to disk).
    public async Task<ChatbotTurnResponse> BuildChatbotTurnAsync(
        Guid tenantId,
        Guid conversationId,
        IReadOnlyList<(string Role, string Content)> history,
        ServiceFinderResult result,
        string latestMessage,
        CancellationToken cancellationToken)
    {
        // The caller always runs the Finder engine with an empty answers dictionary for the
        // chatbot (no accumulated conversation state), so result.Recommendations is just the
        // engine's generic top-3 fallback pick — recomputed identically every turn, not "the
        // tenant's full catalog". Grounding the chat prompt (and the suggestedServiceIds
        // allow-list) against only those 3 made the bot flatly deny real services whenever they
        // weren't among that arbitrary subset — found during live pre-launch testing, where it
        // answered "no" to services that are in fact offered. The chat needs the full active
        // service list for "do you offer X"-style questions, not the Finder's narrower picks.
        // A service with no active employee assigned can't actually be booked — found during
        // live pre-launch testing: deactivating a service's only employee still left the chat
        // offering to book it ("Ja, wir bieten Laser-Haarentfernung an..."), since this grounding
        // query had no equivalent to the booking page's "hide unbookable services" rule
        // (ServiceService.GetServicesAsync et al.).
        var allServices = await _db.Services
            .Where(s => s.TenantId == tenantId && s.IsActive && s.ServiceEmployees.Any(se => se.Employee.IsActive))
            .Select(s => new ServiceRecommendation(s.Id, s.Name, 0, s.Price, s.Currency, s.DurationMinutes, new List<Guid>()))
            .ToListAsync(cancellationToken);
        var chatContext = result with { Recommendations = allServices };
        var allowedServiceIds = allServices.Select(r => r.ServiceId).ToHashSet();

        var tenantPlan = await _db.Subscriptions
            .Where(s => s.TenantId == tenantId)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync(cancellationToken);
        var isAgency = tenantPlan.HasValue && AgencyFeatureGate.IsAgency(tenantPlan.Value);

        // Same per-tenant lock as BuildFinderResponseAsync above — closes the same check→call→track
        // race for the chatbot's own budget-gated call.
        var tenantLock = isAgency ? GetTenantLock(tenantId) : null;
        if (tenantLock != null) await tenantLock.WaitAsync(cancellationToken);
        try
        {
            var withinBudget = isAgency && await _usageGate.IsWithinBudgetAsync(tenantId, cancellationToken);

            try
            {
                var aiMessage = withinBudget
                    ? await _provider.ChatTurnAsync(tenantId, history, latestMessage, chatContext, cancellationToken)
                    : null;
                if (aiMessage != null)
                {
                    await _usageMeter.TrackAsync(
                        tenantId,
                        feature: "ai_chatbot",
                        model: aiMessage.Model,
                        inputTokens: aiMessage.InputTokens,
                        outputTokens: aiMessage.OutputTokens,
                        estimatedCost: aiMessage.EstimatedCost,
                        cancellationToken: cancellationToken);

                    var safeServiceIds = aiMessage.SuggestedServiceIds.Where(allowedServiceIds.Contains).ToList();
                    var message = string.IsNullOrWhiteSpace(aiMessage.Message) ? BuildFallbackMessage(result) : aiMessage.Message;

                    await WriteAuditAsync(tenantId, "AiChatbotTurn", "Completed", cancellationToken);

                    return new ChatbotTurnResponse(
                        conversationId,
                        message,
                        safeServiceIds,
                        RequiresHumanConsultation: aiMessage.RequiresHumanConsultation || result.RequiresHumanConsultation,
                        UsedAiFallback: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI provider unavailable for chatbot turn. Falling back to deterministic message.");
            }
        }
        finally
        {
            tenantLock?.Release();
        }

        await WriteAuditAsync(tenantId, "AiChatbotTurn", "Fallback", cancellationToken);

        return new ChatbotTurnResponse(
            conversationId,
            BuildFallbackMessage(result),
            result.Recommendations.Select(r => r.ServiceId).ToList(),
            RequiresHumanConsultation: result.RequiresHumanConsultation,
            UsedAiFallback: true);
    }

    // Same budget-gated, per-tenant-locked, sanitize-then-fallback shape as BuildChatbotTurnAsync
    // above, for the "Meine Links" design editor's AI assistant instead of the booking chatbot.
    // No deterministic engine to fall back to here (there's no non-AI way to "suggest a design"),
    // so on any failure/over-budget the fallback is simply a plain message asking the admin to
    // pick a template manually — the editor's existing pickers are always still right there.
    public async Task<DesignAssistantTurnResponse> BuildDesignAssistantTurnAsync(
        Guid tenantId,
        IReadOnlyList<(string Role, string Content)> history,
        string latestMessage,
        string industryType,
        CancellationToken cancellationToken)
    {
        var tenantPlan = await _db.Subscriptions
            .Where(s => s.TenantId == tenantId)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync(cancellationToken);
        var isAgency = tenantPlan.HasValue && AgencyFeatureGate.IsAgency(tenantPlan.Value);

        var tenantLock = isAgency ? GetTenantLock(tenantId) : null;
        if (tenantLock != null) await tenantLock.WaitAsync(cancellationToken);
        try
        {
            var withinBudget = isAgency && await _usageGate.IsWithinBudgetAsync(tenantId, cancellationToken);

            try
            {
                var aiMessage = withinBudget
                    ? await _provider.SuggestDesignAsync(tenantId, history, latestMessage, industryType, cancellationToken)
                    : null;
                if (aiMessage != null)
                {
                    await _usageMeter.TrackAsync(
                        tenantId,
                        feature: "ai_design_assistant",
                        model: aiMessage.Model,
                        inputTokens: aiMessage.InputTokens,
                        outputTokens: aiMessage.OutputTokens,
                        estimatedCost: aiMessage.EstimatedCost,
                        cancellationToken: cancellationToken);

                    var message = string.IsNullOrWhiteSpace(aiMessage.Message)
                        ? "Erzähl mir etwas mehr über dein Business, damit ich eine passende Vorlage vorschlagen kann."
                        : aiMessage.Message;

                    await WriteAuditAsync(tenantId, "AiDesignAssistantTurn", "Completed", cancellationToken);

                    return new DesignAssistantTurnResponse(message, aiMessage.Suggestion, UsedAiFallback: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI provider unavailable for design assistant turn. Falling back.");
            }
        }
        finally
        {
            tenantLock?.Release();
        }

        await WriteAuditAsync(tenantId, "AiDesignAssistantTurn", "Fallback", cancellationToken);

        return new DesignAssistantTurnResponse(
            "Gerade nicht verfügbar — wähle unten direkt eine Vorlage oder ein Branchen-Paket aus, das geht genauso schnell.",
            Suggestion: null,
            UsedAiFallback: true);
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
