// Data/Entities/ApiKey.cs
namespace GentleBook.Api.Data.Entities;

// Agency-exclusive: lets a tenant call the public v1 API from external systems.
// Only the hash is stored — the raw key is shown to the admin exactly once at creation,
// same principle as PasswordResetToken.TokenHash.
public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
