// Data/Entities/LinkPageDesignVersion.cs
// One snapshot of a tenant's booking-page design (/admin/links "Design" tab), captured right
// before each change so it can be restored later. See TenantController.UpdateSettings, which
// writes a row here whenever PrimaryColor/LinktreeStyle/LinktreeConfig actually changes.
namespace GentleBook.Api.Data.Entities;

public class LinkPageDesignVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public string PrimaryColor { get; set; } = string.Empty;
    public string? LinktreeStyle { get; set; }
    public string? LinktreeConfig { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
