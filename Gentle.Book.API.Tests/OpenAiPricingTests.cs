using GentleBook.Api.Configuration;
using Xunit;

namespace Gentle.Book.API.Tests;

public class OpenAiPricingTests
{
    [Fact]
    public void EstimateCost_KnownModel_ComputesFromTokenRates()
    {
        // gpt-4o-mini: $0.15/1M input, $0.60/1M output
        var cost = OpenAiPricing.EstimateCost("gpt-4o-mini", inputTokens: 1_000_000, outputTokens: 1_000_000);

        Assert.Equal(0.75m, cost);
    }

    [Fact]
    public void EstimateCost_UnknownModel_FallsBackToDefaultRateInsteadOfThrowing()
    {
        var known = OpenAiPricing.EstimateCost("gpt-4o-mini", 500_000, 500_000);
        var unknown = OpenAiPricing.EstimateCost("some-future-model-xyz", 500_000, 500_000);

        Assert.Equal(known, unknown);
    }

    [Fact]
    public void EstimateCost_ZeroTokens_IsZero()
    {
        Assert.Equal(0m, OpenAiPricing.EstimateCost("gpt-4o-mini", 0, 0));
    }
}
