using GentleBook.Api.Configuration;
using GentleBook.Api.Data.Entities;
using Xunit;

namespace Gentle.Book.API.Tests;

public class SubscriptionIntervalExtensionsTests
{
    [Fact]
    public void AddInterval_Monthly_AddsOneMonth()
    {
        var from = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var result = SubscriptionInterval.Monthly.AddInterval(from);

        Assert.Equal(new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void AddInterval_Yearly_AddsOneYear()
    {
        var from = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var result = SubscriptionInterval.Yearly.AddInterval(from);

        Assert.Equal(new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void AddInterval_Yearly_LeapDayClampsToFeb28()
    {
        var from = new DateTime(2028, 2, 29, 0, 0, 0, DateTimeKind.Utc);

        var result = SubscriptionInterval.Yearly.AddInterval(from);

        Assert.Equal(new DateTime(2029, 2, 28, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void PriceFor_Monthly_ReturnsMonthlyPrice()
    {
        var limits = PlanLimits.Get(SubscriptionPlan.Professional);

        var price = SubscriptionInterval.Monthly.PriceFor(limits);

        Assert.Equal(limits.MonthlyPrice, price);
    }

    [Fact]
    public void PriceFor_Yearly_ReturnsAnnualPrice()
    {
        var limits = PlanLimits.Get(SubscriptionPlan.Professional);

        var price = SubscriptionInterval.Yearly.PriceFor(limits);

        Assert.Equal(limits.AnnualPrice, price);
    }

    [Fact]
    public void ToMollieInterval_Monthly_Returns1Month()
    {
        Assert.Equal("1 month", SubscriptionInterval.Monthly.ToMollieInterval());
    }

    [Fact]
    public void ToMollieInterval_Yearly_Returns12Months()
    {
        Assert.Equal("12 months", SubscriptionInterval.Yearly.ToMollieInterval());
    }

    [Fact]
    public void PriceFor_Subscription_NoNegotiatedPrice_FallsBackToPlanLimits()
    {
        var sub = new Subscription { Plan = SubscriptionPlan.Agency, Interval = SubscriptionInterval.Monthly };
        var limits = PlanLimits.Get(SubscriptionPlan.Agency);

        var price = SubscriptionInterval.Monthly.PriceFor(sub, limits);

        Assert.Equal(limits.MonthlyPrice, price);
    }

    [Fact]
    public void PriceFor_Subscription_NegotiatedMonthlyPrice_OverridesPlanLimits()
    {
        var sub = new Subscription
        {
            Plan = SubscriptionPlan.Agency,
            Interval = SubscriptionInterval.Monthly,
            NegotiatedMonthlyPrice = 249m,
        };
        var limits = PlanLimits.Get(SubscriptionPlan.Agency);

        var price = SubscriptionInterval.Monthly.PriceFor(sub, limits);

        Assert.Equal(249m, price);
        Assert.NotEqual(limits.MonthlyPrice, price);
    }

    [Fact]
    public void PriceFor_Subscription_NegotiatedAnnualPrice_OverridesPlanLimits_AndDoesNotLeakIntoMonthly()
    {
        var sub = new Subscription
        {
            Plan = SubscriptionPlan.Agency,
            Interval = SubscriptionInterval.Yearly,
            NegotiatedAnnualPrice = 2400m,
        };
        var limits = PlanLimits.Get(SubscriptionPlan.Agency);

        Assert.Equal(2400m, SubscriptionInterval.Yearly.PriceFor(sub, limits));
        Assert.Equal(limits.MonthlyPrice, SubscriptionInterval.Monthly.PriceFor(sub, limits));
    }
}
