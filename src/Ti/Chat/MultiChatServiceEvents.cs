namespace SlayTheStreamer2.Ti.Chat;

public sealed record ChildConnectionStateChangedEventArgs(
    string ChildName,
    ChatConnectionChangedEventArgs Inner);
