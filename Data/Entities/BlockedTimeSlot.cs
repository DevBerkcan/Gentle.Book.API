namespace GentleBook.Api.Data.Entities;

public class BlockedTimeSlot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DateOnly BlockDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
}
