namespace GentleBook.Api.Configuration;

// Per-1M-token USD list prices — update here when OpenAI changes pricing or a new default
// model is picked (Services/AI/OpenAiClient.cs). Unknown/misconfigured model names fall back
// to the gpt-4o-mini rate rather than throwing — cost estimates just won't be perfectly
// accurate for that one call, but the feature never breaks over a pricing-table gap.
public static class OpenAiPricing
{
    private static readonly Dictionary<string, (decimal InputPerMillion, decimal OutputPerMillion)> Rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o-mini"] = (0.15m, 0.60m),
        ["gpt-4o"] = (2.50m, 10.00m),
        ["gpt-4.1-mini"] = (0.40m, 1.60m),
        ["gpt-4.1"] = (2.00m, 8.00m),
    };

    public static decimal EstimateCost(string model, int inputTokens, int outputTokens)
    {
        var (inRate, outRate) = Rates.TryGetValue(model, out var rate) ? rate : Rates["gpt-4o-mini"];
        return Math.Round((inputTokens / 1_000_000m) * inRate + (outputTokens / 1_000_000m) * outRate, 4);
    }
}
