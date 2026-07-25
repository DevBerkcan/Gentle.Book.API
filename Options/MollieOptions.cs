namespace GentleBook.Api.Options;

public class MollieOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.mollie.com/v2/";
    public string WebhookUrl { get; set; } = string.Empty;
    public string RedirectUrlBase { get; set; } = string.Empty;
}
