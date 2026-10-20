namespace GentleBook.Api.Data.Entities;

public enum DataSubjectRequestType
{
    Export,
    Deletion,
}

public enum DataSubjectRequestStatus
{
    Pending,
    Completed,
    Rejected,
}

// GDPR Art. 15 (export) / Art. 17 (erasure) on-demand self-service requests
// (LEGAL_AUDIT_2026-09-06.md HOCH: "Ein Supportlink ist kein umgesetzter Datenexport").
// Export resolves synchronously (the row is written Completed for the audit trail, the data
// itself is never stored here). Deletion is logged Pending because it is irreversible and must
// be reviewed by a SuperAdmin before SubscriptionService's purge runs early — instead of a
// TenantAdmin being able to trigger the same destructive operation the system otherwise only
// runs automatically after a 30-day retention window.
public class DataSubjectRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public DataSubjectRequestType Type { get; set; }
    public DataSubjectRequestStatus Status { get; set; } = DataSubjectRequestStatus.Pending;
    public Guid RequestedByUserId { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public Guid? ProcessedByUserId { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? Notes { get; set; }
}
