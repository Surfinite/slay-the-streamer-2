using System.Collections.Generic;

namespace SlayTheStreamer2.Ti.Chat.Internal;

/// <summary>Discriminated union of parsed Twitch IRC events.</summary>
public abstract record IrcEvent;

public sealed record PingEvent(string Token) : IrcEvent;
public sealed record ReconnectEvent : IrcEvent;
public sealed record PrivmsgEvent(ChatMessage Message) : IrcEvent;
public sealed record NoticeEvent(string? Channel, string Text, string? MsgId) : IrcEvent;
public sealed record CapAckEvent(IReadOnlyList<string> Capabilities) : IrcEvent;
public sealed record CapNakEvent(IReadOnlyList<string> Capabilities) : IrcEvent;
public sealed record UserStateEvent(string Channel, string? DisplayName) : IrcEvent;
public sealed record RoomStateEvent(string Channel, IReadOnlyDictionary<string, string> Tags) : IrcEvent;
public sealed record UnknownIrcEvent(string Raw) : IrcEvent;
