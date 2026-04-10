namespace GentleBook.Api.Data.Entities;

public class EmployeeSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid EmployeeId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public bool IsWorkingDay { get; set; } = true;
    public TimeOnly StartTime { get; set; } = new TimeOnly(9, 0);
    public TimeOnly EndTime { get; set; } = new TimeOnly(18, 0);

    // Navigation
    public Employee Employee { get; set; } = null!;
    public Tenant Tenant { get; set; } = null!;

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
