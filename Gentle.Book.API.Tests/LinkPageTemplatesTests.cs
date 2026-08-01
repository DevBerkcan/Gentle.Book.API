using GentleBook.Api.Configuration;
using GentleBook.Api.Data.Entities;
using Xunit;

namespace Gentle.Book.API.Tests;

/// <summary>
/// The frontend already greys out/locks premium templates (app/admin/links/page.tsx), but
/// that alone is bypassable via a direct PUT /api/tenant/settings call. These tests cover the
/// server-side mirror of that same plan mapping.
/// </summary>
public class LinkPageTemplatesTests
{
    [Theory]
    [InlineData("classic", SubscriptionPlan.Trial)]
    [InlineData("classic", SubscriptionPlan.Starter)]
    [InlineData("beauty", SubscriptionPlan.Starter)]
    [InlineData("neon", SubscriptionPlan.Professional)]
    [InlineData("corporate", SubscriptionPlan.Agency)]
    [InlineData("portfolio", SubscriptionPlan.Agency)]
    public void ValidateTemplateForPlan_SufficientPlan_ReturnsNull(string template, SubscriptionPlan plan)
    {
        var result = LinkPageTemplates.ValidateTemplateForPlan(template, plan);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("neon", SubscriptionPlan.Trial)]
    [InlineData("neon", SubscriptionPlan.Starter)]
    [InlineData("corporate", SubscriptionPlan.Starter)]
    [InlineData("corporate", SubscriptionPlan.Professional)]
    [InlineData("portfolio", SubscriptionPlan.Professional)]
    public void ValidateTemplateForPlan_InsufficientPlan_ReturnsRequiredPlanDisplayName(string template, SubscriptionPlan plan)
    {
        var result = LinkPageTemplates.ValidateTemplateForPlan(template, plan);

        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateTemplateForPlan_UnknownTemplate_IsAcceptedNotRejected()
    {
        // Unknown/unmapped template keys are accepted rather than rejected — a new template
        // added on the frontend shouldn't break existing saves until the mapping here catches up.
        var result = LinkPageTemplates.ValidateTemplateForPlan("some-future-template", SubscriptionPlan.Trial);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateTemplateForPlan_NullOrEmptyTemplate_IsAccepted()
    {
        Assert.Null(LinkPageTemplates.ValidateTemplateForPlan(null, SubscriptionPlan.Trial));
        Assert.Null(LinkPageTemplates.ValidateTemplateForPlan("", SubscriptionPlan.Trial));
    }
}
