using System.Security.Claims;
using GentleBook.Api.Configuration;
using Xunit;

namespace Gentle.Book.API.Tests;

public class LocationScopeAuthorizationTests
{
    private static ClaimsPrincipal PrincipalWith(string role, Guid? locationId = null)
    {
        var claims = new List<Claim> { new("role", role) };
        if (locationId.HasValue) claims.Add(new Claim("locationId", locationId.Value.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("SuperAdmin")]
    [InlineData("Employee")]
    public void GetAccessScope_NonLocationAdminRoles_HaveFullTenantAccess(string role)
    {
        var scope = LocationScopeAuthorization.GetAccessScope(PrincipalWith(role));

        Assert.True(scope.IsFullTenantAccess);
        Assert.Null(scope.LocationId);
    }

    [Fact]
    public void GetAccessScope_LocationAdminWithAssignedLocation_IsScopedToThatLocation()
    {
        var locationId = Guid.NewGuid();
        var scope = LocationScopeAuthorization.GetAccessScope(PrincipalWith("LocationAdmin", locationId));

        Assert.False(scope.IsFullTenantAccess);
        Assert.Equal(locationId, scope.LocationId);
    }

    [Fact]
    public void GetAccessScope_LocationAdminWithoutAssignedLocation_IsScopedButLocationIdIsNull()
    {
        var scope = LocationScopeAuthorization.GetAccessScope(PrincipalWith("LocationAdmin"));

        Assert.False(scope.IsFullTenantAccess);
        Assert.Null(scope.LocationId);
    }
}
