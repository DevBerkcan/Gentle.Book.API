using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GentleBook.Api.Options;
using Microsoft.Extensions.Options;

namespace GentleBook.Api.Services;

// Thin typed wrapper around the subset of the Mollie REST v2 API GentleBook needs.
// No third-party SDK — System.Net.Http.Json (shared framework) is enough for this scope.
public class MollieClient
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<MollieOptions> _options;

    public MollieClient(HttpClient http, IOptionsMonitor<MollieOptions> options)
    {
        _http = http;
        _options = options;
    }

    private void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.CurrentValue.ApiKey);

    public async Task<MollieCustomer> CreateCustomerAsync(string name, string email, string locale, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "customers")
        {
            Content = JsonContent.Create(new { name, email, locale })
        };
        Authorize(req);
        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<MollieCustomer>(cancellationToken: ct))!;
    }

    public async Task<MolliePayment> CreateFirstPaymentAsync(
        string customerId, decimal amount, string currency, string description,
        string redirectUrl, string webhookUrl, IDictionary<string, string>? metadata, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "payments")
        {
            // No `method` here on purpose — Mollie rejects method=directdebit combined with
            // sequenceType=first (SEPA mandates aren't collected via direct API method
            // selection). Omitting it sends the customer to Mollie's hosted checkout to pick
            // a method themselves (SEPA Direct Debit included), which is how Mollie expects
            // first-time mandate collection to work.
            Content = JsonContent.Create(new
            {
                amount = new { currency, value = amount.ToString("F2") },
                customerId,
                sequenceType = "first",
                description,
                redirectUrl,
                webhookUrl,
                metadata
            })
        };
        Authorize(req);
        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<MolliePayment>(cancellationToken: ct))!;
    }

    public async Task<MolliePayment> GetPaymentAsync(string paymentId, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"payments/{paymentId}");
        Authorize(req);
        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<MolliePayment>(cancellationToken: ct))!;
    }

    public async Task<MollieSubscription> CreateSubscriptionAsync(
        string customerId, string mandateId, decimal amount, string currency, string interval,
        string description, string webhookUrl, IDictionary<string, string>? metadata, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"customers/{customerId}/subscriptions")
        {
            Content = JsonContent.Create(new
            {
                amount = new { currency, value = amount.ToString("F2") },
                mandateId,
                interval,
                description,
                webhookUrl,
                metadata
            })
        };
        Authorize(req);
        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<MollieSubscription>(cancellationToken: ct))!;
    }

    public async Task<MollieSubscription> GetSubscriptionAsync(string customerId, string subscriptionId, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"customers/{customerId}/subscriptions/{subscriptionId}");
        Authorize(req);
        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<MollieSubscription>(cancellationToken: ct))!;
    }

    public async Task<List<MolliePayment>> GetSubscriptionPaymentsAsync(string customerId, string subscriptionId, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"customers/{customerId}/subscriptions/{subscriptionId}/payments");
        Authorize(req);
        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var wrapper = await res.Content.ReadFromJsonAsync<MollieEmbeddedPayments>(cancellationToken: ct);
        return wrapper?.Embedded?.Payments ?? new List<MolliePayment>();
    }

    public async Task<MollieMandate?> GetMandateAsync(string customerId, string mandateId, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"customers/{customerId}/mandates/{mandateId}");
        Authorize(req);
        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<MollieMandate>(cancellationToken: ct);
    }

    public async Task CancelSubscriptionAsync(string customerId, string subscriptionId, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"customers/{customerId}/subscriptions/{subscriptionId}");
        Authorize(req);
        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
}

// ── Minimal DTOs — only the fields GentleBook actually consumes ─────────────

public class MollieAmount
{
    public string Currency { get; set; } = "EUR";
    public string Value { get; set; } = "0.00";
    public decimal AsDecimal => decimal.TryParse(Value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
}

public class MollieCustomer
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Email { get; set; }
}

public class MolliePayment
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = ""; // open, pending, paid, failed, canceled, expired
    public string? SequenceType { get; set; }
    public string? CustomerId { get; set; }
    public string? MandateId { get; set; }
    public string? SubscriptionId { get; set; }
    public MollieAmount? Amount { get; set; }
    public MollieAmount? AmountChargedBack { get; set; }
    public MollieAmount? AmountRefunded { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }

    [JsonPropertyName("_links")]
    public MolliePaymentLinks? Links { get; set; }

    public bool IsPaid => Status == "paid";
    public bool IsFailedOrExpired => Status is "failed" or "canceled" or "expired";
    public bool HasChargeback => AmountChargedBack?.AsDecimal > 0;
    public bool HasRefund => AmountRefunded?.AsDecimal > 0;
    public string? CheckoutUrl() => Links?.Checkout?.Href;
}

public class MolliePaymentLinks
{
    public MollieLink? Checkout { get; set; }
}

public class MollieLink
{
    public string? Href { get; set; }
}

public class MollieSubscription
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = ""; // pending, active, canceled, suspended, completed
    public string? MandateId { get; set; }
    public string? CustomerId { get; set; }
    public MollieAmount? Amount { get; set; }
}

public class MollieMandate
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = ""; // valid, pending, invalid
}

public class MollieEmbeddedPayments
{
    [JsonPropertyName("_embedded")]
    public MollieEmbeddedPaymentsList? Embedded { get; set; }
}

public class MollieEmbeddedPaymentsList
{
    public List<MolliePayment> Payments { get; set; } = new();
}
