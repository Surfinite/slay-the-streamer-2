namespace SlayTheStreamer2.Ti.Chat;

/// <summary>Priority for the outgoing send queue. Close > Open > Periodic. Plan A defines the enum;
/// the queue implementation that uses it lives in Ti/Chat/Internal/OutgoingMessageQueue.cs.</summary>
public enum OutgoingMessagePriority {
    Low,        // periodic tally
    Normal,     // open receipt
    High,       // close receipt
}
