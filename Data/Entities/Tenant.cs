// Data/Entities/Tenant.cs
namespace GentleBook.Api.Data.Entities;

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-readable name, e.g. "Barber Wagner"</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>URL-safe slug, unique across platform, e.g. "barber-wagner"</summary>
    public string Slug { get; set; } = string.Empty;

    public IndustryType IndustryType { get; set; } = IndustryType.Other;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public TenantSettings? Settings { get; set; }
    public Subscription? Subscription { get; set; }
    public ICollection<PlatformUser> Users { get; set; } = new List<PlatformUser>();
    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
    public ICollection<Service> Services { get; set; } = new List<Service>();
    public ICollection<ServiceCategory> ServiceCategories { get; set; } = new List<ServiceCategory>();
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public ICollection<Customer> Customers { get; set; } = new List<Customer>();
    public ICollection<BusinessHours> BusinessHours { get; set; } = new List<BusinessHours>();
}

public enum IndustryType
{
    Hairdresser,
    Beauty,
    Massage,
    Nail,
    Barbershop,
    Tattoo,
    Physio,
    Other
}
