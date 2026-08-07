namespace GentleBook.Api.Data.Entities;

public class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastVisit { get; set; }
    public int TotalBookings { get; set; } = 0;
    public int NoShowCount { get; set; } = 0;
    public string? Notes { get; set; }
    public bool IsEmailVerified { get; set; } = false;
    public string? EmailVerificationToken { get; set; }
    public DateTime? EmailVerificationTokenExpiry { get; set; }
    public DateTime? ConsentGivenAt { get; set; }
    public int LoyaltyPoints { get; set; } = 0;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public string FullName => $"{FirstName} {LastName}";
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
}
