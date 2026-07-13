// Services/TenantContext.cs
// Holds the current tenant's identity for the duration of an HTTP request.
// Injected as Scoped so it is safe to set/read within a single request.
namespace GentleBook.Api.Services;

public interface ITenantContext
{
    Guid? TenantId { get; }
    string? TenantSlug { get; }
    string? Role { get; }
    bool IsSuperAdmin { get; }
    void Set(Guid? tenantId, string? tenantSlug, bool isSuperAdmin = false, string? role = null);
}

public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public string? TenantSlug { get; private set; }
    public string? Role { get; private set; }
    public bool IsSuperAdmin { get; private set; }

    public void Set(Guid? tenantId, string? tenantSlug, bool isSuperAdmin = false, string? role = null)
    {
        TenantId = tenantId;
        TenantSlug = tenantSlug;
        IsSuperAdmin = isSuperAdmin;
        Role = role;
    }
}
