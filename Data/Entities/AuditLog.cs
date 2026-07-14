namespace GentleBook.Api.Data.Entities;

/// <summary>
/// Persistent audit trail for security-relevant and administrative actions.
/// TenantId is null for platform-level (SuperAdmin) actions.
/// </summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }

    /// <summary>SuperAdmin | TenantAdmin | Employee | System</summary>
    public string ActorType { get; set; } = "System";
    public Guid? ActorId { get; set; }
    public string? ActorName { get; set; }

    /// <summary>Machine-readable action key, e.g. "tenant.created", "booking.status_changed"</summary>
    public string Action { get; set; } = string.Empty;

    public string? EntityType { get; set; }
    public string? EntityId { get; set; }

    /// <summary>Human-readable details (old/new values, context)</summary>
    public string? Details { get; set; }

    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
