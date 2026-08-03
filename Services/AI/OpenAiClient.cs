using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GentleBook.Api.Options;
using Microsoft.Extensions.Options;

namespace GentleBook.Api.Services.AI;

// Thin typed wrapper around the OpenAI Chat Completions API — mirrors the MollieClient.cs
// pattern (plain typed HttpClient, System.Net.Http.Json, no SDK). One platform-wide API key
// (Options/AiProviderOptions.cs, "Ai" config section) — GentleBook itself pays for usage across
// every Agency tenant, which is exactly why Agency pricing is negotiated per customer rather
// than fixed (see Subscription.NegotiatedMonthlyPrice).
public class OpenAiClient
{
    private const string DefaultModel = "gpt-4o-mini";

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<AiProviderOptions> _options;

    public OpenAiClient(HttpClient http, IOptionsMonitor<AiProviderOptions> options)
    {
        _http = http;
        _options = options;
    }

    private void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.CurrentValue.ApiKey);

    public async Task<OpenAiChatResult> CreateChatCompletionAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var model = string.IsNullOrWhiteSpace(_options.CurrentValue.Model) ? DefaultModel : _options.CurrentValue.Model;
        var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
                temperature = 0.4,
            })
        };
        Authorize(req);
        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<OpenAiChatResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty OpenAI response.");

        var content = body.Choices.FirstOrDefault()?.Message.Content ?? "";
        return new OpenAiChatResult(content, body.Model, body.Usage?.PromptTokens ?? 0, body.Usage?.CompletionTokens ?? 0);
    }
}

public record OpenAiChatResult(string Content, string Model, int InputTokens, int OutputTokens);

internal class OpenAiChatResponse
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("choices")] public List<OpenAiChoice> Choices { get; set; } = new();
    [JsonPropertyName("usage")] public OpenAiUsage? Usage { get; set; }
}

internal class OpenAiChoice
{
    [JsonPropertyName("message")] public OpenAiMessage Message { get; set; } = new();
}

internal class OpenAiMessage
{
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

internal class OpenAiUsage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
}
