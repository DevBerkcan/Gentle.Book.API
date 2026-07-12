// Data/Entities/WaitlistEntry.cs
namespace GentleBook.Api.Data.Entities;

public class WaitlistEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ServiceId { get; set; }
    public Guid? EmployeeId { get; set; }
    public DateOnly? PreferredDate { get; set; }

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Notes { get; set; }

    public WaitlistStatus Status { get; set; } = WaitlistStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? NotifiedAt { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Service? Service { get; set; }
    public Employee? Employee { get; set; }
}

public enum WaitlistStatus
{
    Pending,
    Notified,
    Booked,
    Expired
}
