namespace GentleBook.Api.Data.Entities;

/// <summary>
/// A course of N sessions of one service assigned (internally, no online sale) to one customer,
/// e.g. "Laser Achseln – 6 Sitzungen".
///
/// The remaining balance is deliberately NOT stored as a counter. Used sessions are always
/// derived: <see cref="PreviouslyUsedSessions"/> + the number of this package's bookings with
/// Status == Completed (see Services/TreatmentPackageRules.cs). That makes a double debit
/// impossible by construction — re-saving, a repeated PATCH, the auto-complete job and a manual
/// completion racing each other all converge on the same single Completed booking row — and any
/// future "un-complete" flow would correct the balance automatically. Booking, rescheduling and
/// cancelling never touch the balance because only Completed counts.
/// </summary>
public class TreatmentPackage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid ServiceId { get; set; }

    public string Name { get; set; } = string.Empty;
    public int TotalSessions { get; set; }

    /// <summary>Sessions already done before this package was tracked in GentleBook (migration of existing customers). 0 for new packages.</summary>
    public int PreviouslyUsedSessions { get; set; }

    public TreatmentPackageStatus Status { get; set; } = TreatmentPackageStatus.Active;

    /// <summary>
    /// Secret for the customer's personal booking link (/booking/{slug}/book?serviceId=…&amp;packageToken=…).
    /// Random 256-bit, URL-safe; the server resolves the package from it — never from client-supplied ids.
    /// </summary>
    public string BookingToken { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
    public Service Service { get; set; } = null!;
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}

public enum TreatmentPackageStatus
{
    Active,
    /// <summary>Ended early by the studio (refund, customer stopped, mistake). Fully used packages stay Active and are simply "verbraucht" (derived).</summary>
    Cancelled,
}
