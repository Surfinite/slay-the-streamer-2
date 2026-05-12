using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal sealed class YouTubeHttp : IYouTubeHttp {
    private const string ChromeUA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    // YouTube serves a consent-wall page on first contact if no CONSENT cookie is present.
    // We bypass by sending CONSENT=YES+cb as a direct request header on every call.
    // CookieContainer was attempted previously but silently dropped the cookie due to a
    // known .NET quirk with Add(Uri, Cookie) when Domain contains a leading dot
    // (verified 2026-05-12 — body landed at consent.youtube.com despite container setup).
    // Direct header is the robust pattern used by pytchat / youtube-chat / chat-downloader.
    private const string ConsentCookieHeader = "CONSENT=YES+cb";

    private readonly HttpClient _client;

    public YouTubeHttp() : this(BuildDefaultHandler(), ownsHandler: true) { }

    // Test-friendly constructor — accepts any HttpMessageHandler.
    internal YouTubeHttp(HttpMessageHandler handler, bool ownsHandler = false) {
        _client = new HttpClient(handler, disposeHandler: ownsHandler) {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeUA);
        _client.DefaultRequestHeaders.Add("Cookie", ConsentCookieHeader);
    }

    private static HttpClientHandler BuildDefaultHandler() => new() {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        UseCookies = false,   // disable container; the Cookie header above is the source of truth
    };

    public async Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, CancellationToken ct) {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        EnsureSuccessOrThrow(resp, url);
        return resp;
    }

    public async Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, CancellationToken ct) {
        var req = new HttpRequestMessage(HttpMethod.Post, url) {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        EnsureSuccessOrThrow(resp, url);
        return resp;
    }

    private static void EnsureSuccessOrThrow(HttpResponseMessage resp, Uri url) {
        if (resp.IsSuccessStatusCode) return;
        var retryAfter = resp.Headers.RetryAfter?.Delta;
        throw new YouTubeHttpStatusException(
            resp.StatusCode, retryAfter,
            $"HTTP {(int)resp.StatusCode} from {url}");
    }

    public void Dispose() => _client.Dispose();
}
