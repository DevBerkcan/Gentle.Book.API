using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Configuration;

// Curated starter field-sets an admin can apply with one click, then freely edit/delete
// afterward — same "static reference data, no DB table" approach as PlanLimits.cs, since these
// change rarely and don't need a per-tenant editor.
public static class IntakeFormTemplates
{
    public record TemplateField(string Label, IntakeFormFieldType Type, bool Required, string[]? Options = null);
    public record Template(string Key, string Label, IndustryType Industry, TemplateField[] Fields);

    private static readonly Template[] _templates =
    {
        new("beauty_general", "Beauty – Allgemein", IndustryType.Beauty, new[]
        {
            new TemplateField("Bekannte Allergien", IntakeFormFieldType.Text, false),
            new TemplateField("Hauttyp", IntakeFormFieldType.MultipleChoice, false, new[] { "Trocken", "Normal", "Fettig", "Mischhaut" }),
            new TemplateField("Medikamente, die für die Behandlung relevant sein könnten", IntakeFormFieldType.Textarea, false),
            new TemplateField("Bist du schwanger?", IntakeFormFieldType.YesNo, false),
            new TemplateField("Gewünschtes Ergebnis", IntakeFormFieldType.Textarea, false),
        }),
        new("lashes", "Wimpern", IndustryType.Beauty, new[]
        {
            new TemplateField("Trägst du Kontaktlinsen?", IntakeFormFieldType.YesNo, true),
            new TemplateField("Bestehen bekannte Allergien gegen Klebstoffe?", IntakeFormFieldType.YesNo, true),
            new TemplateField("Hattest du bereits Reaktionen bei Lash-Behandlungen?", IntakeFormFieldType.Textarea, false),
            new TemplateField("Wann war deine letzte Wimpernverlängerung?", IntakeFormFieldType.Date, false),
        }),
        new("facial", "Gesichtsbehandlung", IndustryType.Beauty, new[]
        {
            new TemplateField("Wie würdest du deinen Hauttyp beschreiben?", IntakeFormFieldType.MultipleChoice, true, new[] { "Trocken", "Normal", "Fettig", "Mischhaut", "Empfindlich" }),
            new TemplateField("Bestehen bekannte Hauterkrankungen?", IntakeFormFieldType.Textarea, false),
            new TemplateField("Verwendest du Retinol oder ähnliche Wirkstoffe?", IntakeFormFieldType.YesNo, false),
            new TemplateField("Bestehen bekannte Allergien?", IntakeFormFieldType.Text, false),
        }),
        new("eyebrows", "Augenbrauen", IndustryType.Beauty, new[]
        {
            new TemplateField("Wurden deine Augenbrauen bereits gefärbt?", IntakeFormFieldType.YesNo, false),
            new TemplateField("Bestehen bekannte Allergien gegen Haarfarben?", IntakeFormFieldType.YesNo, true),
            new TemplateField("Gab es bereits Hautreaktionen?", IntakeFormFieldType.Textarea, false),
        }),
        new("general_consultation", "Allgemeine Anamnese", IndustryType.Other, new[]
        {
            new TemplateField("Bekannte Allergien", IntakeFormFieldType.Text, false),
            new TemplateField("Medikamente, die für die Behandlung relevant sein könnten", IntakeFormFieldType.Textarea, false),
            new TemplateField("Bestehende Vorerkrankungen", IntakeFormFieldType.Textarea, false),
            new TemplateField("Bist du schwanger?", IntakeFormFieldType.YesNo, false),
        }),
    };

    /// <summary>Templates for a given industry, plus the industry-agnostic "general_consultation" fallback.</summary>
    public static IReadOnlyList<Template> ForIndustry(IndustryType industry) =>
        _templates.Where(t => t.Industry == industry || t.Key == "general_consultation").ToList();

    public static Template? Find(string key) => _templates.FirstOrDefault(t => t.Key == key);
}
