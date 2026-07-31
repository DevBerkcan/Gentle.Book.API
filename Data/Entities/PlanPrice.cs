// Data/Entities/PlanPrice.cs
namespace GentleBook.Api.Data.Entities;

// Persisted override for a plan's monthly price. Loaded into Configuration.PlanLimits'
// in-memory cache at startup and on every SuperAdmin edit — see PlanLimits.SetPrice.
public class PlanPrice
{
    public SubscriptionPlan Plan { get; set; }
    public decimal MonthlyPrice { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
