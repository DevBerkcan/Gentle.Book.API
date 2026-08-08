using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Configuration;

// Digital intake forms only make sense for industries that actually do a consultation/health
// check before treatment — a Wellness/Physio/Other tenant doesn't need the feature. Same
// null-means-allowed convention as AgencyFeatureGate.cs; this gate is checked in addition to
// (not instead of) the Agency plan gate.
public static class IntakeFormIndustryGate
{
    private static readonly IndustryType[] AllowedIndustries =
    {
        IndustryType.Beauty,
        IndustryType.Hairdresser,
        IndustryType.Nail,
        IndustryType.Barbershop,
        IndustryType.Tattoo,
        IndustryType.Massage,
    };

    public static string? ValidateForIndustry(IndustryType industry) =>
        AllowedIndustries.Contains(industry)
            ? null
            : "Formulare sind für diese Branche nicht verfügbar.";

    public static bool IsAllowed(IndustryType industry) => AllowedIndustries.Contains(industry);
}
