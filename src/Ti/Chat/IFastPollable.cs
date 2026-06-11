namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// Optional capability for chat services whose message delivery is poll-based
/// (YouTube scraper). While fast polling is enabled the service polls at its
/// minimum safe interval instead of the server-recommended cadence, so messages
/// land with minimal delay. The voting layer enables this for the duration of
/// an open vote; event-push services (Twitch IRC) are real-time already and
/// don't implement it. Implementations must be safe to call from any thread.
/// </summary>
public interface IFastPollable {
    void SetFastPolling(bool enabled);
}
