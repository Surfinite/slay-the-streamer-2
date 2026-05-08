namespace SlayTheStreamer2.Ti.Chat;

public enum ChatConnectionState {
    Disconnected,
    Connecting,
    ConnectedReadOnly,           // anonymous justinfan
    ConnectedReadWrite,          // authenticated
    Reconnecting,
    AuthenticationFailed,        // terminal — no retry
    JoinFailed,                  // banned / channel doesn't exist — no retry
    Disposed,
}
