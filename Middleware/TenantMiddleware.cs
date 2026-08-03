// Middleware/TenantMiddleware.cs
// Reads TenantId and role from JWT claims and populates ITenantContext.
// Also enforces subscription access (returns 402 when trial is expired).
using System.IdentityModel.Tokens.Jwt;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Middleware;

public class TenantMiddleware
{
    private readonly RequestDelegate _next;

    public TenantMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext, GentleBookDbContext db)
    {
        // Skip public endpoints and auth endpoints — no tenant required
        var path = context.Request.Path.Value ?? "";
        if (IsExempt(path))
        {
            await _next(context);
            return;
        }

        // Read JWT claims
        var user = context.User;
        if (!user.Identity?.IsAuthenticated ?? true)
        {
            await _next(context);
            return;
        }

        var roleClaimValue = user.FindFirst("role")?.Value ?? user.FindFirst("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")?.Value;

        // SuperAdmin: no tenant restriction
        if (roleClaimValue == "SuperAdmin")
        {
            tenantContext.Set(null, null, isSuperAdmin: true, role: roleClaimValue);
            await _next(context);
            return;
        }

        // Tenant-scoped role: extract tenantId from claims
        var tenantIdClaim = user.FindFirst("tenantId")?.Value;
        var tenantSlugClaim = user.FindFirst("tenantSlug")?.Value;

        if (!Guid.TryParse(tenantIdClaim, out var tenantId))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { message = "Invalid or missing tenant claim." });
            return;
        }

        tenantContext.Set(tenantId, tenantSlugClaim, role: roleClaimValue);

        // Validate tenant is active and subscription allows access
        var subscription = await db.Subscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);

        var expiredBillingAccess = subscription?.Status == SubscriptionStatus.Expired && IsBillingPath(path);
        if (subscription == null || (!subscription.IsAccessAllowed && !expiredBillingAccess))
        {
            context.Response.StatusCode = 402; // Payment Required
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Your trial has expired or your subscription is inactive.",
                status = subscription?.Status.ToString() ?? "NotFound",
                trialEndsAt = subscription?.TrialEndsAt
            });
            return;
        }

        await _next(context);
    }

    private static bool IsExempt(string path)
    {
        return path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/public", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/hangfire", StringComparison.OrdinalIgnoreCase)
            || path == "/health";
    }

    private static bool IsBillingPath(string path) =>
        path.StartsWith("/api/tenant/subscription", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/tenant/plan-pricing", StringComparison.OrdinalIgnoreCase);
}
