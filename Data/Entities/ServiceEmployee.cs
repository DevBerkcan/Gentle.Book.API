namespace GentleBook.Api.Data.Entities;

public class ServiceEmployee
{
    public Guid ServiceId { get; set; }
    public Service Service { get; set; } = null!;

    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; } // Employee who created the assignment
}