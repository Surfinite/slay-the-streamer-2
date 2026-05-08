using System;

namespace SlayTheStreamer2.Ti.Chat;

public sealed record ChatConnectionChangedEventArgs(
    ChatConnectionState OldState,
    ChatConnectionState NewState,
    string? Reason);
