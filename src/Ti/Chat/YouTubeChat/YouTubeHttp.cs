using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal sealed class YouTubeHttp : IYouTubeHttp {
    private const string ChromeUA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private readonly HttpClient _client;

    public YouTubeHttp() : this(BuildDefaultHandler(), ownsHandler: true) { }

    // Test-friendly constructor — accepts any HttpMessageHandler.
    internal YouTubeHttp(HttpMessageHandler handler, bool ownsHandler = false) {
        if (handler is HttpClientHandler clientHandler) {
            // Production path: clientHandler already has CookieContainer with CONSENT cookie.
            _client = new HttpClient(handler, disposeHandler: ownsHandler) {
                Timeout = TimeSpan.FromSeconds(15),
            };
        } else {
            // Test path: arbitrary handler — inject CONSENT cookie via default header.
            _client = new HttpClient(handler, disposeHandler: ownsHandler) {
                Timeout = TimeSpan.FromSeconds(15),
            };
            _client.DefaultRequestHeaders.Add("Cookie", "CONSENT=YES+cb");
        }
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeUA);
    }

    private static HttpClientHandler BuildDefaultHandler() {
        var cookies = new CookieContainer();
        cookies.Add(new Uri("https://www.youtube.com/"),
                    new Cookie("CONSENT", "YES+cb", "/", ".youtube.com"));
        return new HttpClientHandler {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            CookieContainer = cookies,
            UseCookies = true,
        };
    }

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
