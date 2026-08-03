// Data/Entities/Employee.cs
namespace GentleBook.Api.Data.Entities;

public class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? Specialty { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Location { get; set; }
    public string? Username { get; set; }
    public string? PasswordHash { get; set; }
    public string? PhotoUrl { get; set; }
    public string? Tagline { get; set; }
    public Guid? LocationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    // Named distinctly from the free-text `Location` string field above (kept for backwards
    // compatibility / display) — this is the structured FK used for location-scoped filtering.
    public BusinessLocation? AssignedLocation { get; set; }
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public ICollection<BlockedTimeSlot> BlockedTimeSlots { get; set; } = new List<BlockedTimeSlot>();
    public ICollection<ServiceEmployee> ServiceEmployees { get; set; } = new List<ServiceEmployee>();
    public ICollection<EmployeeSchedule> Schedules { get; set; } = new List<EmployeeSchedule>();
}
