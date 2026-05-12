namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

/// <summary>
/// Reason the YouTube chat service is in its current state. Surfaced to ModEntry
/// for reason-specific D8 receipt wording. InvalidOrUnavailableChannel was
/// removed in v4 — D7 explicitly does not disambiguate permanent 404s from
/// transient ones.
/// </summary>
public enum YouTubeChatStatusReason {
    None,
    NoLiveBroadcastFound,
    LiveBroadcastEnded,
    NetworkError,
    RateLimited,
    ScraperParseFailed,
    UnknownError,
}
