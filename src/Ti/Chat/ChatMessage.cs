using System;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// One incoming chat message after parsing. UserId is null when the IRC client
/// is connected without `twitch.tv/tags` capability or for messages from
/// untagged sources. VoterKey is the stable identifier VoteSession tallies on.
/// </summary>
public sealed record ChatMessage(
    string? UserId,
    string Login,
    string DisplayName,
    string Text,
    DateTimeOffset ReceivedAt,
    bool IsSubscriber,
    bool IsModerator,
    bool IsVip) {
    public string VoterKey => UserId ?? $"login:{Login}";
}
