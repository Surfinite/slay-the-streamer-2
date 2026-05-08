namespace SlayTheStreamer2.Ti.Voting;

public enum VoteSessionState {
    Open,         // accepting votes
    Closing,      // duration elapsed or CloseNow() called; computing winner + sending close receipt
    Closed,       // WinnerIndex set; subscribers notified
    Cancelled,    // Cancel() called; no winner; awaiters cancelled
    Disposed,
}
