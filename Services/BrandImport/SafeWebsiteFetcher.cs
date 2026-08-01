// Services/BrandImport/SafeWebsiteFetcher.cs
// Safely fetches HTML/CSS resources from an admin-supplied external website. All requests go
// through IUrlSecurityValidator both before the request is built AND again — via ConnectCallback
// — at the exact moment a socket is opened, which is what actually closes the DNS-rebinding
// window (see spec section 6). Redirects are never followed automatically by HttpClient; each
// hop is re-validated by this class exactly like the original URL.
using System.Net;
using System.Net.Sockets;

namespace GentleBook.Api.Services.BrandImport;

public sealed record FetchResult(
    bool Success,
    string? Body,
    Uri? FinalUri,
    string? ContentType,
    string? ErrorCode,
    string? ErrorMessageSafe);

public interface ISafeWebsiteFetcher
{
    /// <summary>
    /// Fetches <paramref name="url"/> (following same-origin-scheme-safe redirects, each
    /// re-validated) with the given content-type allow-list, byte and redirect limits.
    /// </summary>
    Task<FetchResult> FetchAsync(
        Uri url,
        IReadOnlyCollection<string> allowedContentTypePrefixes,
        long maxBytes,
        int maxRedirects,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class SafeWebsiteFetcher : ISafeWebsiteFetcher
{
    private readonly IUrlSecurityValidator _validator;
    private readonly ILogger<SafeWebsiteFetcher> _logger;

    public SafeWebsiteFetcher(IUrlSecurityValidator validator, ILogger<SafeWebsiteFetcher> logger)
    {
        _validator = validator;
        _logger = logger;
    }

    public async Task<FetchResult> FetchAsync(
        Uri url,
        IReadOnlyCollection<string> allowedContentTypePrefixes,
        long maxBytes,
        int maxRedirects,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var currentUri = url;

        for (var redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            var validation = await _validator.ValidateAsync(currentUri, cancellationToken);
            if (!validation.IsAllowed)
                return new FetchResult(false, null, null, null, validation.ErrorCode, validation.ErrorMessageSafe);

            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                ConnectTimeout = timeout,
                // Re-validates + connects directly to a checked IP — closes the TOCTOU/DNS-rebinding
                // gap between the ValidateAsync call above and the actual TCP connect.
                ConnectCallback = async (context, ct) => await ConnectValidatedAsync(context, timeout, ct),
            };

            using var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GentleBookBrandBot/1.0 (+https://gentlebook.app/brand-import)");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,text/css,*/*;q=0.1");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new FetchResult(false, null, null, null, "timeout", "Die Website hat nicht rechtzeitig geantwortet.");
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Brand import fetch failed for a tenant-supplied URL: {ErrorType}", ex.GetType().Name);
                return new FetchResult(false, null, null, null, "fetch_failed", "Die Website konnte nicht erreicht werden.");
            }

            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
                {
                    var location = response.Headers.Location;
                    var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);

                    if (!string.Equals(nextUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(nextUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                        return new FetchResult(false, null, null, null, "redirect_scheme_blocked", "Die Weiterleitung verwendet ein nicht erlaubtes Protokoll.");

                    currentUri = nextUri;
                    continue; // loop re-validates currentUri from the top
                }

                if (!response.IsSuccessStatusCode)
                    return new FetchResult(false, null, null, null, "http_error", "Die Website hat einen Fehler zurückgegeben.");

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (contentType == null || !allowedContentTypePrefixes.Any(p => contentType.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    return new FetchResult(false, null, null, contentType, "content_type_not_allowed", "Der Inhaltstyp der Seite wird nicht unterstützt.");

                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength.HasValue && declaredLength.Value > maxBytes)
                    return new FetchResult(false, null, null, contentType, "response_too_large", "Die Antwort der Website ist zu groß.");

                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                using var memoryStream = new MemoryStream();
                var buffer = new byte[16 * 1024];
                long totalRead = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
                {
                    totalRead += read;
                    if (totalRead > maxBytes)
                        return new FetchResult(false, null, null, contentType, "response_too_large", "Die Antwort der Website ist zu groß.");
                    await memoryStream.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                }

                var encoding = GetEncoding(response.Content.Headers.ContentType?.CharSet);
                var body = encoding.GetString(memoryStream.ToArray());
                return new FetchResult(true, body, currentUri, contentType, null, null);
            }
        }

        return new FetchResult(false, null, null, null, "too_many_redirects", "Die Website leitet zu oft weiter.");
    }

    private static System.Text.Encoding GetEncoding(string? charSet)
    {
        if (string.IsNullOrWhiteSpace(charSet)) return System.Text.Encoding.UTF8;
        try
        {
            return System.Text.Encoding.GetEncoding(charSet.Trim('"'));
        }
        catch
        {
            return System.Text.Encoding.UTF8;
        }
    }

    private async ValueTask<Stream> ConnectValidatedAsync(SocketsHttpConnectionContext context, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }

        var safeAddress = addresses.FirstOrDefault(a => !UrlSecurityValidator.IsBlockedAddress(a));
        if (safeAddress == null)
            throw new InvalidOperationException("SSRF protection: no safe address available for connection.");

        var socket = new Socket(safeAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(timeout);
            await socket.ConnectAsync(new IPEndPoint(safeAddress, port), connectCts.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
