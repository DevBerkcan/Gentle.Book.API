namespace GentleBook.Api.Services.AI;

// AI-assisted design suggestions for the "Meine Links" booking-page editor (Agency-exclusive).
// Deliberately separate from the Service-Finder contracts (ServiceFinderContracts.cs) — a
// different domain (page design, not service recommendations) — but it follows the identical
// safety pattern: the model picks only from the fixed, known set of keys the frontend already
// defines in components/admin/links/constants.ts (PAGE_TEMPLATES, COLOR_PALETTES); anything
// outside that set is dropped rather than applied, exactly like AiOrchestrator sanitizes
// suggested service IDs against the deterministic engine's own result.
public sealed record DesignSuggestion(
    string? PageTemplate,
    string? ColorPaletteKey,
    string? CtaText,
    string? CtaBadge);

public sealed record DesignAssistantProviderMessage(
    string Message,
    DesignSuggestion? Suggestion,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCost,
    string Model);

public sealed record DesignAssistantTurnResponse(
    string Reply,
    DesignSuggestion? Suggestion,
    bool UsedAiFallback);
