using System;
using System.Net;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

/// <summary>
/// Thrown by IYouTubeHttp on non-success status codes. Carries the status code
/// and any Retry-After delta so the reconnect cadence can honor it (per D7's
/// 429 carve-out).
/// </summary>
internal sealed class YouTubeHttpStatusException : Exception {
    public HttpStatusCode StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    public YouTubeHttpStatusException(HttpStatusCode statusCode, TimeSpan? retryAfter, string message)
        : base(message) {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }
}
