using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Configuration;

// Pure, DB-free billing-interval math shared by MollieService, InvoiceService and the
// SuperAdmin/tenant subscription controllers — keeps the monthly-vs-yearly branch in one
// testable place instead of duplicated at every CurrentPeriodEnd/price call site.
public static class SubscriptionIntervalExtensions
{
    public static DateTime AddInterval(this SubscriptionInterval interval, DateTime from) =>
        interval == SubscriptionInterval.Yearly ? from.AddYears(1) : from.AddMonths(1);

    public static decimal PriceFor(this SubscriptionInterval interval, PlanLimits.Limits limits) =>
        interval == SubscriptionInterval.Yearly ? limits.AnnualPrice : limits.MonthlyPrice;

    // Mollie's `interval` string for CreateSubscriptionAsync — the only two values this product uses.
    public static string ToMollieInterval(this SubscriptionInterval interval) =>
        interval == SubscriptionInterval.Yearly ? "12 months" : "1 month";
}
