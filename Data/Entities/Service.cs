// Data/Entities/Service.cs
namespace GentleBook.Api.Data.Entities;

public class Service
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "EUR";
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ServiceCategory Category { get; set; } = null!;
    public ICollection<ServiceEmployee> ServiceEmployees { get; set; } = new List<ServiceEmployee>();
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
