using System.Text;
using System.Text.Json;
using GentleBook.Api.Configuration;

namespace GentleBook.Api.Services.AI;

// Real, Agency-exclusive implementation of IAiProviderAdapter (AiOrchestrator only calls this
// for tenants on the Agency plan — see AiOrchestrator.BuildFinderResponseAsync). Builds the full
// prompt itself (the orchestrator does no prompt-building, per the interface contract), asks the
// model to answer with a single JSON object, and parses that back into an AiProviderMessage.
// Any failure — network, malformed JSON, missing fields — returns null, which the orchestrator
// treats identically to "no AI available" and falls back to the existing deterministic message.
public sealed class OpenAiProviderAdapter : IAiProviderAdapter
{
    private readonly OpenAiClient _client;
    private readonly ILogger<OpenAiProviderAdapter> _logger;

    public OpenAiProviderAdapter(OpenAiClient client, ILogger<OpenAiProviderAdapter> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AiProviderMessage?> ExplainFinderResultAsync(
        Guid tenantId,
        string? freeText,
        ServiceFinderResult result,
        IReadOnlyList<KnowledgeResult> knowledge,
        CancellationToken cancellationToken)
    {
        try
        {
            var systemPrompt =
                "Du bist der freundliche Buchungs-Assistent eines Dienstleistungsbetriebs (z. B. Salon, Studio, Praxis). " +
                "Erkläre der Kundschaft auf Deutsch in 2-4 kurzen Sätzen, warum die vorgeschlagenen Services passen. " +
                "Antworte AUSSCHLIESSLICH mit einem einzigen JSON-Objekt, ohne Markdown-Codeblock, exakt in dieser Form: " +
                "{\"message\": string, \"suggestedServiceIds\": string[], \"suggestedGuidanceIds\": string[], " +
                "\"suggestedEmployeeIds\": string[], \"requiresHumanConsultation\": boolean}. " +
                "Nutze für die suggested*Ids-Felder ausschließlich IDs aus den bereitgestellten Listen — erfinde niemals eigene IDs. " +
                "Setze requiresHumanConsultation auf true, wenn die Anfrage eine individuelle Beratung vor Ort braucht.";

            var userPrompt = BuildUserPrompt(freeText, result, knowledge);

            var completion = await _client.CreateChatCompletionAsync(systemPrompt, userPrompt, cancellationToken);
            var parsed = ParseJsonResponse(completion.Content);
            if (parsed == null)
            {
                _logger.LogWarning("OpenAI finder response was not valid JSON for tenant {TenantId}.", tenantId);
                return null;
            }

            var estimatedCost = OpenAiPricing.EstimateCost(completion.Model, completion.InputTokens, completion.OutputTokens);

            return new AiProviderMessage(
                Message: parsed.Value.Message,
                InputTokens: completion.InputTokens,
                OutputTokens: completion.OutputTokens,
                EstimatedCost: estimatedCost,
                Model: completion.Model,
                SuggestedServiceIds: parsed.Value.SuggestedServiceIds,
                SuggestedGuidanceIds: parsed.Value.SuggestedGuidanceIds,
                SuggestedEmployeeIds: parsed.Value.SuggestedEmployeeIds,
                RequiresHumanConsultation: parsed.Value.RequiresHumanConsultation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI call failed for tenant {TenantId} — falling back to deterministic explanation.", tenantId);
            return null;
        }
    }

    private static string BuildUserPrompt(string? freeText, ServiceFinderResult result, IReadOnlyList<KnowledgeResult> knowledge)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(freeText))
            sb.AppendLine($"Anliegen der Kundschaft: \"{freeText}\"");

        sb.AppendLine("Vorgeschlagene Services (nur diese IDs dürfen in suggestedServiceIds verwendet werden):");
        foreach (var r in result.Recommendations)
            sb.AppendLine($"- id={r.ServiceId}, name=\"{r.ServiceName}\", preis={r.Price} {r.Currency}, dauer={r.DurationMinutes}min");

        if (result.Guidance.Count > 0)
        {
            sb.AppendLine("Freigegebene Hinweise des Studios (nur diese IDs dürfen in suggestedGuidanceIds verwendet werden):");
            foreach (var g in result.Guidance)
                sb.AppendLine($"- id={g.GuidanceId}, titel=\"{g.Title}\", inhalt=\"{g.Content}\"");
        }

        if (result.SuggestedEmployeeIds.Count > 0)
            sb.AppendLine("Erlaubte Mitarbeiter-IDs für suggestedEmployeeIds: " + string.Join(", ", result.SuggestedEmployeeIds));

        if (knowledge.Count > 0)
        {
            sb.AppendLine("Zusätzliches Hintergrundwissen des Studios (nur zur Einordnung, nicht wörtlich zitieren):");
            foreach (var k in knowledge.Take(5))
                sb.AppendLine($"- \"{k.Title}\": {k.Excerpt}");
        }

        if (result.MissingQuestions.Count > 0)
            sb.AppendLine("Hinweis: es fehlen noch Angaben für eine genauere Empfehlung — das darf in der Nachricht erwähnt werden.");

        return sb.ToString();
    }

    private readonly record struct ParsedResponse(
        string Message,
        List<Guid> SuggestedServiceIds,
        List<Guid> SuggestedGuidanceIds,
        List<Guid> SuggestedEmployeeIds,
        bool RequiresHumanConsultation);

    private static ParsedResponse? ParseJsonResponse(string content)
    {
        // Models occasionally wrap JSON in a markdown code fence despite instructions not to —
        // trim to the outermost braces rather than failing outright.
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            var root = doc.RootElement;

            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? ""
                : "";

            return new ParsedResponse(
                message,
                ReadGuidArray(root, "suggestedServiceIds"),
                ReadGuidArray(root, "suggestedGuidanceIds"),
                ReadGuidArray(root, "suggestedEmployeeIds"),
                root.TryGetProperty("requiresHumanConsultation", out var rc) && rc.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<Guid> ReadGuidArray(JsonElement root, string propertyName)
    {
        var result = new List<Guid>();
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id))
                result.Add(id);
        }
        return result;
    }
}
