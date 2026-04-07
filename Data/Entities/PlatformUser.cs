// Data/Entities/PlatformUser.cs
// Platform-level users: SuperAdmin and TenantAdmin.
// Separate from Employee — employees still use the Employee entity for scheduling.
namespace GentleBook.Api.Data.Entities;

public class PlatformUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Null for SuperAdmin, set for TenantAdmin</summary>
    public Guid? TenantId { get; set; }

    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;

    public PlatformRole Role { get; set; } = PlatformRole.TenantAdmin;

    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant? Tenant { get; set; }
}

public enum PlatformRole
{
    SuperAdmin,
    TenantAdmin
}
