namespace GentleBook.Api.Services.AI;

public interface IAiToolRegistry
{
    IReadOnlyList<string> GetPublicTools();
    IReadOnlyList<string> GetStaffTools();
}

public sealed class AiToolRegistry : IAiToolRegistry
{
    private static readonly string[] PublicTools =
    {
        "GetActiveIndustryProfile",
        "GetFinderQuestions",
        "EvaluateFinderAnswers",
        "SearchApprovedKnowledge",
        "GetServiceDetails",
        "GetEmployeesForService",
        "GetAvailableSlots",
        "CreateBookingDraft",
        "GetApprovedPreparationGuidance",
        "GetApprovedAftercareGuidance",
        "GetApprovedRebookingGuidance"
    };

    private static readonly string[] StaffTools =
    {
        "ConfirmBooking"
    };

    public IReadOnlyList<string> GetPublicTools() => PublicTools;

    public IReadOnlyList<string> GetStaffTools() => StaffTools;
}
