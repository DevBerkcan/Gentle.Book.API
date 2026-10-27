using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// LEGAL_AUDIT_2026-09-06.md: "Tracking-IP ungekürzt gespeichert" — PageView.IpAddress was
// persisted exactly as received from HttpContext.Connection.RemoteIpAddress, with no redaction.
public class TrackingServiceIpAnonymizationTests
{
    [Theory]
    [InlineData("203.0.113.42", "203.0.113.0")]
    [InlineData("2001:db8:85a3:8d3:1319:8a2e:370:7348", "2001:db8:85a3::")]
    public async Task TrackPageViewAsync_ZeroesLastIpSegmentBeforePersisting(string rawIp, string expectedAnonymized)
    {
        using var db = Gentle.Book.API.Tests.TestSupport.TestDbContextFactory.Create();
        var service = new TrackingService(db, NullLogger<TrackingService>.Instance, new GentleBook.Api.Services.TenantContext());

        var (success, error) = await service.TrackPageViewAsync(new TrackPageViewDto { PageUrl = "/booking/demo" }, "test-agent", rawIp);

        Assert.True(success, error);
        var stored = await db.PageViews.AsNoTracking().SingleAsync();
        Assert.Equal(expectedAnonymized, stored.IpAddress);
        Assert.NotEqual(rawIp, stored.IpAddress);
    }

    [Fact]
    public async Task TrackPageViewAsync_NullIp_StoresNull()
    {
        using var db = Gentle.Book.API.Tests.TestSupport.TestDbContextFactory.Create();
        var service = new TrackingService(db, NullLogger<TrackingService>.Instance, new GentleBook.Api.Services.TenantContext());

        var (success, error) = await service.TrackPageViewAsync(new TrackPageViewDto { PageUrl = "/booking/demo" }, "test-agent", null);

        Assert.True(success, error);
        var stored = await db.PageViews.AsNoTracking().SingleAsync();
        Assert.Null(stored.IpAddress);
    }
}
