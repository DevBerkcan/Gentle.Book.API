using System.Security.Claims;
using GentleBook.Api.Services;

namespace GentleBook.Api.Configuration;

// Agency-exclusive: a LocationAdmin (Data/Entities/PlatformUser.cs, PlatformRole.LocationAdmin)
// manages a tenant like a TenantAdmin but scoped to a single BusinessLocation. Deliberately
// introduced in only the three controllers that actually need it (Bookings, AdminServices,
// Employees-with-services) rather than the ~13 controllers that do role checks — see the plan
// file's Context section for why a full rollout was out of scope.
public static class LocationScopeAuthorization
{
    public record AccessScope(bool IsFullTenantAccess, Guid? LocationId);

    public static AccessScope GetAccessScope(ClaimsPrincipal user)
    {
        var role = JwtService.GetRole(user);
        if (role == "LocationAdmin")
            return new AccessScope(false, JwtService.GetLocationId(user));

        // TenantAdmin, SuperAdmin, Employee, ApiKey (see ApiKeyAuthenticationHandler) — all see
        // the full tenant. Employees keep their existing separate self-scoping (GetEmployeeScope
        // in BookingsController etc.), unaffected by this helper.
        return new AccessScope(true, null);
    }
}
