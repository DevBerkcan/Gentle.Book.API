// Data/Entities/Review.cs
// A customer's post-appointment rating/comment, requested automatically once a booking is
// Completed (see Services/ReviewRequestService.cs). Never shown publicly until an admin
// explicitly publishes it (IsPublished) — same "draft until confirmed" spirit as Brand Import.
namespace GentleBook.Api.Data.Entities;

public class Review
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid BookingId { get; set; }
    public Guid CustomerId { get; set; }

    public int Rating { get; set; } // 1-5
    public string? Comment { get; set; }

    public bool IsPublished { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Booking Booking { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
}
