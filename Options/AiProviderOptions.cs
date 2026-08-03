namespace GentleBook.Api.Options;

// OpenAI provider config (see Services/AI/OpenAiClient.cs, OpenAiProviderAdapter.cs), bound from
// the "Ai" config section. One platform-wide key — not per-tenant — used only for Agency-plan
// tenants (AiOrchestrator gates this). Program.cs falls back to NullAiProviderAdapter when
// ApiKey is empty, so an unconfigured/missing key never breaks the app, it just keeps the
// existing deterministic Service Finder behavior for every tenant.
public class AiProviderOptions
{
    public string Provider { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}
