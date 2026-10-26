using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GentleBook.Api.Data;

// SQL Server's datetime/datetime2 columns carry no timezone info — every DateTime round-trips
// through them with Kind reset to Unspecified, even though every entity in this codebase is
// written via DateTime.UtcNow. Left as Unspecified, System.Text.Json serializes it without a
// trailing "Z", and browsers then parse the result as local time instead of UTC (e.g. a note
// sent at 16:40 Europe/Berlin showing as 15:40 once DST ends). Re-marking as Utc on the way
// out of the database fixes this for every DateTime column at once — see
// GentleBookDbContext.ConfigureConventions.
public class UtcDateTimeValueConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeValueConverter() : base(
        v => v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    { }
}

public class NullableUtcDateTimeValueConverter : ValueConverter<DateTime?, DateTime?>
{
    public NullableUtcDateTimeValueConverter() : base(
        v => v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v)
    { }
}
