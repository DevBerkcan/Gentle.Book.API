namespace GentleBook.Api.Options;

// The seam for a real AI provider (see IAiProviderAdapter/NullAiProviderAdapter in Services/AI).
// Bound from the "Ai" config section but currently unread by any concrete provider — no real
// implementation exists yet. Left in place so wiring one up later is a config + DI change, not a
// code change here.
public class AiProviderOptions
{
    public string Provider { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}
