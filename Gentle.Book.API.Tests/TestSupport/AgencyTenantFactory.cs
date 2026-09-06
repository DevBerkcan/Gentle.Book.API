using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;

namespace Gentle.Book.API.Tests.TestSupport;

/// <summary>
/// Seeds a tenant + subscription on a given plan — shared by the Agency-feature-gate test suite
/// (one gate check + one happy-path per Agency-exclusive feature) so each test file doesn't
/// re-declare its own seeding boilerplate.
/// </summary>
public static class AgencyTenantFactory
{
    public static (Tenant tenant, Subscription subscription) Seed(
        GentleBookDbContext db, SubscriptionPlan plan, IndustryType industry = IndustryType.Hairdresser, string name = "Salon Agency-Test")
    {
        var tenant = new Tenant { Name = name, Slug = name.ToLowerInvariant().Replace(" ", "-") + "-" + Guid.NewGuid(), IsActive = true, IndustryType = industry };
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = plan, Status = SubscriptionStatus.Active };
        tenant.Subscription = subscription;

        db.Tenants.Add(tenant);
        db.Subscriptions.Add(subscription);
        db.SaveChanges();

        return (tenant, subscription);
    }
}
