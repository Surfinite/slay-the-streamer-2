using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

/// <summary>
/// Thin HTTP abstraction for the YouTube scraper. Throws YouTubeHttpStatusException
/// on non-success responses (status code + Retry-After preserved).
/// </summary>
internal interface IYouTubeHttp : IDisposable {
    Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, CancellationToken ct);
    Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, CancellationToken ct);
}
