// Services/BrandImport/LlmServiceListExtractor.cs
// Best-effort, LLM-based extraction of a business's bookable services/treatments (name, price,
// duration) from page text. Unlike the deterministic HtmlBrandExtractor fields (colors, fonts,
// contact info), there is no reliable generic HTML pattern for price lists across arbitrary
// business websites — every failure mode (missing API key, malformed response, network error)
// degrades to an empty list rather than failing the analysis job, same convention as
// OpenAiProviderAdapter.ExplainFinderResultAsync.
using System.Text.Json;
using GentleBook.Api.Options;
using GentleBook.Api.Services.AI;
using Microsoft.Extensions.Options;

namespace GentleBook.Api.Services.BrandImport;

public interface IServiceListExtractor
{
    Task<List<DetectedServiceDto>> ExtractAsync(string pageText, CancellationToken cancellationToken);
}

public sealed class LlmServiceListExtractor : IServiceListExtractor
{
    private const int MaxInputChars = 8000;
    private const int MaxServicesReturned = 100;

    private readonly OpenAiClient _client;
    private readonly IOptionsMonitor<AiProviderOptions> _options;
    private readonly ILogger<LlmServiceListExtractor> _logger;

    public LlmServiceListExtractor(OpenAiClient client, IOptionsMonitor<AiProviderOptions> options, ILogger<LlmServiceListExtractor> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public async Task<List<DetectedServiceDto>> ExtractAsync(string pageText, CancellationToken cancellationToken)
    {
        // No platform key configured — same silent-skip convention as OpenAiProviderAdapter /
        // NullAiProviderAdapter, never a hard failure for the whole analysis job.
        if (string.IsNullOrWhiteSpace(_options.CurrentValue.ApiKey) || string.IsNullOrWhiteSpace(pageText))
            return new List<DetectedServiceDto>();

        try
        {
            const string systemPrompt =
                "Du liest den Text einer Unternehmens-Website (Salon, Studio, Praxis o. ä.) und extrahierst eine Liste " +
                "der buchbaren Dienstleistungen/Behandlungen. Antworte AUSSCHLIESSLICH mit einem JSON-Array, ohne " +
                "Markdown-Codeblock, jedes Element in genau dieser Form: " +
                "{\"name\": string, \"priceAmount\": number|null, \"currency\": string|null, \"durationMinutes\": number|null}. " +
                "Erfinde keine Preise oder Dauern, die nicht im Text stehen — setze sie dann auf null. " +
                "Ignoriere Navigation, Kundenstimmen, Blog-Artikel und Teambeschreibungen. Maximal 100 Einträge.";

            var truncated = pageText.Length > MaxInputChars ? pageText[..MaxInputChars] : pageText;
            var completion = await _client.CreateChatCompletionAsync(systemPrompt, truncated, cancellationToken);
            var services = ParseJsonResponse(completion.Content);

            _logger.LogInformation(
                "Brand import service extraction found {Count} candidate services ({Model}, {InputTokens}in/{OutputTokens}out)",
                services.Count, completion.Model, completion.InputTokens, completion.OutputTokens);

            return services;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Brand import service extraction failed — continuing without detected services.");
            return new List<DetectedServiceDto>();
        }
    }

    private static List<DetectedServiceDto> ParseJsonResponse(string content)
    {
        // Models occasionally wrap JSON in a markdown code fence despite instructions not to —
        // trim to the outermost brackets rather than failing outright (same defensive approach
        // as OpenAiProviderAdapter.ParseJsonResponse).
        var start = content.IndexOf('[');
        var end = content.LastIndexOf(']');
        if (start < 0 || end <= start) return new List<DetectedServiceDto>();

        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<DetectedServiceDto>();

            var result = new List<DetectedServiceDto>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;

                var name = nameEl.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                decimal? price = item.TryGetProperty("priceAmount", out var priceEl)
                    && priceEl.ValueKind == JsonValueKind.Number && priceEl.TryGetDecimal(out var p) ? p : null;
                string? currency = item.TryGetProperty("currency", out var curEl) && curEl.ValueKind == JsonValueKind.String
                    ? curEl.GetString() : null;
                int? duration = item.TryGetProperty("durationMinutes", out var durEl)
                    && durEl.ValueKind == JsonValueKind.Number && durEl.TryGetInt32(out var d) ? d : null;

                result.Add(new DetectedServiceDto(name, price, currency, duration));
                if (result.Count >= MaxServicesReturned) break;
            }
            return result;
        }
        catch (JsonException)
        {
            return new List<DetectedServiceDto>();
        }
    }
}
