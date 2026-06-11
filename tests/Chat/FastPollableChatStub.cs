using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat;

namespace SlayTheStreamer2.Tests.Chat;

/// <summary>
/// Minimal IChatConsumer that records SetFastPolling calls. Used by
/// VoteCoordinator and MultiChatService fast-polling tests; FakeChatService
/// deliberately does NOT implement IFastPollable so it doubles as the
/// "non-pollable child" in forwarding tests.
/// </summary>
internal sealed class FastPollableChatStub : IChatConsumer, IFastPollable {
    public List<bool> FastPollCalls { get; } = new();
    public bool ThrowOnSetFastPolling { get; set; }

    public ChatConnectionState State => ChatConnectionState.ConnectedReadWrite;
    public bool IsConnected => true;
    public bool CanSend => true;
    public DateTimeOffset? LastMessageReceivedAt => null;
    public Exception? LastError => null;

#pragma warning disable 67   // events satisfy the interface; never raised by this stub
    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;
#pragma warning restore 67

    public void SetFastPolling(bool enabled) {
        if (ThrowOnSetFastPolling) throw new InvalidOperationException("SimulateSetFastPollingThrow");
        FastPollCalls.Add(enabled);
    }

    public void Disconnect() { }
    public Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal, CancellationToken ct = default) =>
        Task.CompletedTask;
    public void Dispose() { }
}
