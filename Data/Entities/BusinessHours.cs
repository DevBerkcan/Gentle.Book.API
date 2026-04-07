namespace GentleBook.Api.Data.Entities;

public class BusinessHours
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }

    public TimeOnly OpenTime { get; set; }
    public TimeOnly CloseTime { get; set; }
    public bool IsOpen { get; set; } = true;
    public TimeOnly? BreakStartTime { get; set; }
    public TimeOnly? BreakEndTime { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;

    // Computed property
    public string DayName => DayOfWeek switch
    {
        System.DayOfWeek.Monday => "Montag",
        System.DayOfWeek.Tuesday => "Dienstag",
        System.DayOfWeek.Wednesday => "Mittwoch",
        System.DayOfWeek.Thursday => "Donnerstag",
        System.DayOfWeek.Friday => "Freitag",
        System.DayOfWeek.Saturday => "Samstag",
        System.DayOfWeek.Sunday => "Sonntag",
        _ => DayOfWeek.ToString()
    };
}
