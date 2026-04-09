// Data/Entities/TenantLink.cs
namespace GentleBook.Api.Data.Entities;

public class TenantLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public LinkIconType IconType { get; set; } = LinkIconType.Custom;
    public int DisplayOrder { get; set; } = 0;
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}

public enum LinkIconType
{
    Booking,
    Instagram,
    WhatsApp,
    GoogleMaps,
    Facebook,
    TikTok,
    YouTube,
    Website,
    Phone,
    Email,
    Custom
}
