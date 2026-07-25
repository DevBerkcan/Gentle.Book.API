using System.Net.Http.Headers;
using System.Net.Http.Json;
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Options;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GentleBook.Api.Services;

// Pushes a confirmed GentleBook subscription payment to the Gentle.Suite CRM as a real invoice.
// Invoked only via Hangfire (BackgroundJob.Enqueue from MollieService), never inline in the
// webhook — a CRM outage must never block or revert GentleBook's own subscription state.
// Throws deliberately on failure so Hangfire's AutomaticRetry handles retries.
[AutomaticRetry(Attempts = 10, DelaysInSeconds = new[] { 60, 300, 900, 3600, 3600, 3600, 3600, 3600, 3600, 3600 })]
public class CrmPushService
{
    private readonly GentleBookDbContext _db;
    private readonly HttpClient _http;
    private readonly IOptions<CrmOptions> _options;
    private readonly ILogger<CrmPushService> _logger;

    public CrmPushService(GentleBookDbContext db, HttpClient http, IOptions<CrmOptions> options, ILogger<CrmPushService> logger)
    {
        _db = db;
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task PushSubscriptionPaymentAsync(Guid subscriptionId, string molliePaymentId, CancellationToken ct)
    {
        var sub = await _db.Subscriptions.Include(s => s.Tenant).ThenInclude(t => t.Settings)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
            ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found for CRM push.");

        var settings = sub.Tenant.Settings
            ?? throw new InvalidOperationException($"TenantSettings missing for tenant {sub.TenantId}, cannot push to CRM.");

        var limits = PlanLimits.Get(sub.Plan);
        var periodStart = sub.CurrentPeriodStart ?? DateTime.UtcNow;
        var periodEnd = sub.CurrentPeriodEnd ?? periodStart.AddMonths(1);

        var payload = new
        {
            gentleBookTenantId = sub.TenantId,
            tenantLegalName = settings.LegalCompanyName ?? settings.CompanyName,
            tenantVatId = settings.VatId,
            billingStreet = settings.BillingStreet ?? "",
            billingZip = settings.BillingZipCode ?? "",
            billingCity = settings.BillingCity ?? "",
            billingCountry = settings.BillingCountry ?? "DE",
            contactEmail = settings.Email ?? "",
            planName = limits.DisplayName,
            amount = limits.MonthlyPrice,
            billingPeriodStart = periodStart,
            billingPeriodEnd = periodEnd,
            paymentDate = DateTime.UtcNow,
            molliePaymentId
        };

        var req = new HttpRequestMessage(HttpMethod.Post, "api/integrations/gentlebook/subscription-payments")
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Add("X-GentleBook-Api-Key", _options.Value.ApiKey);

        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"CRM push failed ({(int)res.StatusCode}): {body}");
        }

        var result = await res.Content.ReadFromJsonAsync<CrmPushResponse>(cancellationToken: ct);
        if (result?.CrmCustomerId != null && string.IsNullOrEmpty(sub.CrmCustomerId))
        {
            sub.CrmCustomerId = result.CrmCustomerId;
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("CRM push succeeded for Subscription {SubscriptionId}: invoice {InvoiceNumber}", subscriptionId, result?.InvoiceNumber);
    }

    private record CrmPushResponse(string CrmCustomerId, Guid InvoiceId, string InvoiceNumber);
}
