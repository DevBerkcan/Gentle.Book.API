using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the GDPR Art. 15/17 self-service gap flagged by LEGAL_AUDIT_2026-09-06.md
// ("Ein Supportlink ist kein umgesetzter Datenexport"): a TenantAdmin must be able to get a
// real export of their own data, and a deletion request must (a) require an already-cancelled
// subscription, (b) stay Pending until a SuperAdmin reviews it, and (c) actually purge the data
// — using the exact same irreversible purge the automated 30-day retention job runs — once
// approved, never on a TenantAdmin's say alone.
public class DataSubjectRequestServiceTests
{
    private static (Tenant tenant, Subscription subscription) SeedTenant(
        GentleBook.Api.Data.GentleBookDbContext db, SubscriptionStatus status)
    {
        var tenant = new Tenant { Name = "Salon Auskunft", Slug = "salon-auskunft-" + Guid.NewGuid(), IsActive = true };
        var settings = new TenantSettings { TenantId = tenant.Id, CompanyName = "Salon Auskunft", Email = "info@salon-auskunft.de" };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Plan = SubscriptionPlan.Starter,
            Interval = SubscriptionInterval.Monthly,
            Status = status,
        };
        tenant.Subscription = subscription;
        tenant.Settings = settings;
        var customer = new Customer { TenantId = tenant.Id, FirstName = "Lena", LastName = "Kunde", Email = "lena@example.test" };
        db.AddRange(tenant, settings, subscription, customer);
        db.SaveChanges();
        return (tenant, subscription);
    }

    [Fact]
    public async Task BuildTenantDataExportAsync_ReturnsSettingsAndCustomers()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionStatus.Active);
        var service = TestServiceFactory.CreateDataSubjectRequestService(db);

        var export = await service.BuildTenantDataExportAsync(tenant.Id);

        var json = System.Text.Json.JsonSerializer.Serialize(export);
        Assert.Contains("Salon Auskunft", json);
        Assert.Contains("Lena", json);
    }

    [Fact]
    public async Task RequestDeletionAsync_SubscriptionStillActive_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionStatus.Active);
        var service = TestServiceFactory.CreateDataSubjectRequestService(db);

        var (success, error, request) = await service.RequestDeletionAsync(tenant.Id, Guid.NewGuid());

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Null(request);
        Assert.Empty(db.DataSubjectRequests);
    }

    [Fact]
    public async Task RequestDeletionAsync_AlreadyCancelled_CreatesPendingRequest()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionStatus.Cancelled);
        var service = TestServiceFactory.CreateDataSubjectRequestService(db);
        var actorId = Guid.NewGuid();

        var (success, error, request) = await service.RequestDeletionAsync(tenant.Id, actorId);

        Assert.True(success, error);
        Assert.NotNull(request);
        Assert.Equal(DataSubjectRequestStatus.Pending, request!.Status);
        Assert.Equal(actorId, request.RequestedByUserId);
    }

    [Fact]
    public async Task RequestDeletionAsync_AlreadyPending_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionStatus.Cancelled);
        var service = TestServiceFactory.CreateDataSubjectRequestService(db);

        var first = await service.RequestDeletionAsync(tenant.Id, Guid.NewGuid());
        Assert.True(first.Success);

        var second = await service.RequestDeletionAsync(tenant.Id, Guid.NewGuid());

        Assert.False(second.Success);
        Assert.Single(db.DataSubjectRequests);
    }

    [Fact]
    public async Task ApproveDeletionRequestAsync_PurgesDataAndMarksCompleted()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionStatus.Cancelled);
        var service = TestServiceFactory.CreateDataSubjectRequestService(db);
        var (_, _, request) = await service.RequestDeletionAsync(tenant.Id, Guid.NewGuid());

        var (success, error) = await service.ApproveDeletionRequestAsync(request!.Id, Guid.NewGuid());

        Assert.True(success, error);
        var reloadedRequest = await db.DataSubjectRequests.AsNoTracking().FirstAsync(r => r.Id == request.Id);
        Assert.Equal(DataSubjectRequestStatus.Completed, reloadedRequest.Status);
        Assert.NotNull(reloadedRequest.ProcessedAt);

        var reloadedSub = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.TenantId == tenant.Id);
        Assert.NotNull(reloadedSub.OperationalDataDeletedAt);
        Assert.False(await db.Customers.IgnoreQueryFilters().AsNoTracking().AnyAsync(c => c.TenantId == tenant.Id));
    }

    [Fact]
    public async Task RejectDeletionRequestAsync_SetsRejectedWithReason_DataUntouched()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenant(db, SubscriptionStatus.Cancelled);
        var service = TestServiceFactory.CreateDataSubjectRequestService(db);
        var (_, _, request) = await service.RequestDeletionAsync(tenant.Id, Guid.NewGuid());

        var (success, error) = await service.RejectDeletionRequestAsync(request!.Id, Guid.NewGuid(), "Laufender Rechtsstreit – Aufbewahrungspflicht.");

        Assert.True(success, error);
        var reloaded = await db.DataSubjectRequests.AsNoTracking().FirstAsync(r => r.Id == request.Id);
        Assert.Equal(DataSubjectRequestStatus.Rejected, reloaded.Status);
        Assert.Equal("Laufender Rechtsstreit – Aufbewahrungspflicht.", reloaded.Notes);
        Assert.True(await db.Customers.IgnoreQueryFilters().AsNoTracking().AnyAsync(c => c.TenantId == tenant.Id));
    }
}
