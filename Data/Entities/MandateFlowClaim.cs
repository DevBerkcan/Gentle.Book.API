namespace GentleBook.Api.Data.Entities;

// A short-lived lock row, keyed by the primary key on SubscriptionId itself: whichever
// StartMandateFlowAsync call inserts this row first "owns" starting a Mollie mandate/first
// payment for that subscription. A second near-simultaneous call (double-click, two browser
// tabs) hits the same primary key and gets a DbUpdateException instead of also calling Mollie.
// Removed again once the owning call resolves (success or failure) so a later, legitimate retry
// isn't blocked forever.
public class MandateFlowClaim
{
    public Guid SubscriptionId { get; set; }
    public DateTime ClaimedAt { get; set; } = DateTime.UtcNow;
}
