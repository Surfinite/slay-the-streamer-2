using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// Aggregator that wraps N child <see cref="IChatConsumer"/> services behind a single
/// <see cref="IChatConsumer"/> surface. ConnectAsync is intentionally not exposed —
/// children are pre-connected by the wiring code (ModEntry) before construction.
/// </summary>
public sealed class MultiChatService : IChatConsumer {
    private readonly Dictionary<string, IChatConsumer> _children;
    private readonly List<(string Name, EventHandler<ChatConnectionChangedEventArgs> Handler)> _stateHandlers = new();

    public IReadOnlyList<string> ConfiguredPlatforms { get; }

    public MultiChatService(params (string Name, IChatConsumer Service)[] children) {
        if (children == null || children.Length == 0)
            throw new ArgumentException("MultiChatService requires >=1 child", nameof(children));
        foreach (var (name, service) in children) {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("child Name must be non-empty", nameof(children));
            if (service == null)
                throw new ArgumentException($"child '{name}' Service must not be null", nameof(children));
        }
        var names = children.Select(c => c.Name).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new ArgumentException(
                $"MultiChatService child names must be unique: {string.Join(", ", names)}",
                nameof(children));

        _children = children.ToDictionary(c => c.Name, c => c.Service, StringComparer.Ordinal);
        ConfiguredPlatforms = names;
        _lastAggregateState = AggregateState();

        foreach (var (name, child) in children) {
            child.MessageReceived += OnChildMessageReceived;
            var capturedName = name;
            EventHandler<ChatConnectionChangedEventArgs> handler =
                (s, e) => OnChildConnectionStateChangedInternal(capturedName, s, e);
            child.ConnectionStateChanged += handler;
            _stateHandlers.Add((name, handler));
        }
    }

    public ChatConnectionState State => AggregateState();
    public bool IsConnected => _children.Values.Any(c => c.IsConnected);
    public bool CanSend => _children.Values.Any(c => c.CanSend);
    public DateTimeOffset? LastMessageReceivedAt =>
        _children.Values.Select(c => c.LastMessageReceivedAt).Where(x => x.HasValue).Max();
    public Exception? LastError => null;   // per Round-2 #14: aggregation loses info; consumers query per-child.

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChildConnectionStateChangedEventArgs>? ChildConnectionStateChanged;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;
    private ChatConnectionState _lastAggregateState;

    public ChatConnectionState GetChildState(string name) {
        if (_children.TryGetValue(name, out var child)) return child.State;
        TiLog.Warn($"[MultiChatService] GetChildState: unknown child name '{name}'; returning Disposed");
        return ChatConnectionState.Disposed;
    }

    public IChatConsumer? GetChild(string name) =>
        _children.TryGetValue(name, out var child) ? child : null;

    private ChatConnectionState AggregateState() {
        if (_children.Values.Any(c => c.State == ChatConnectionState.ConnectedReadWrite))
            return ChatConnectionState.ConnectedReadWrite;
        if (_children.Values.Any(c => c.State == ChatConnectionState.ConnectedReadOnly))
            return ChatConnectionState.ConnectedReadOnly;
        if (_children.Values.Any(c => c.State == ChatConnectionState.Reconnecting))
            return ChatConnectionState.Reconnecting;
        if (_children.Values.Any(c => c.State == ChatConnectionState.Connecting))
            return ChatConnectionState.Connecting;
        // All children terminal. Mixed-terminal: AuthFailed > JoinFailed > Disposed > Disconnected.
        var allTerminal = _children.Values.All(c =>
            c.State is ChatConnectionState.AuthenticationFailed
                    or ChatConnectionState.JoinFailed
                    or ChatConnectionState.Disposed
                    or ChatConnectionState.Disconnected);
        if (!allTerminal) return ChatConnectionState.Disconnected;
        if (_children.Values.Any(c => c.State == ChatConnectionState.AuthenticationFailed))
            return ChatConnectionState.AuthenticationFailed;
        if (_children.Values.Any(c => c.State == ChatConnectionState.JoinFailed))
            return ChatConnectionState.JoinFailed;
        if (_children.Values.All(c => c.State == ChatConnectionState.Disposed))
            return ChatConnectionState.Disposed;
        return ChatConnectionState.Disconnected;
    }

    private void OnChildMessageReceived(object? sender, ChatMessage msg) =>
        MessageReceived?.Invoke(this, msg);

    private void OnChildConnectionStateChangedInternal(string name, object? sender, ChatConnectionChangedEventArgs e) {
        var newAggregate = AggregateState();
        var oldAggregate = _lastAggregateState;
        _lastAggregateState = newAggregate;

        var childArgs = new ChildConnectionStateChangedEventArgs(name, e);
        try { ChildConnectionStateChanged?.Invoke(this, childArgs); }
        catch (Exception ex) { TiLog.Error("[MultiChatService] ChildConnectionStateChanged handler threw", ex); }

        if (newAggregate != oldAggregate) {
            var aggArgs = new ChatConnectionChangedEventArgs(oldAggregate, newAggregate,
                $"child '{name}' transition triggered aggregate change");
            try { ConnectionStateChanged?.Invoke(this, aggArgs); }
            catch (Exception ex) { TiLog.Error("[MultiChatService] ConnectionStateChanged handler threw", ex); }
        }
    }

    public void Disconnect() {
        foreach (var c in _children.Values) {
            try { c.Disconnect(); } catch (Exception ex) {
                TiLog.Warn($"[MultiChatService] child Disconnect threw: {ex.Message}");
            }
        }
    }

    public async Task SendMessageAsync(
        string text,
        OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
        CancellationToken ct = default) {
        var sendable = _children.Values.Where(c => c.CanSend).ToList();
        if (sendable.Count == 0) {
            TiLog.Debug("[MultiChatService] SendMessageAsync: no CanSend children; receipt skipped");
            return;
        }
        int successCount = 0;
        foreach (var c in sendable) {
            try { await c.SendMessageAsync(text, priority, ct); successCount++; }
            catch (Exception ex) {
                TiLog.Warn($"[MultiChatService] child SendMessageAsync threw: {ex.Message}");
            }
        }
        if (successCount == 0) {
            TiLog.Warn($"[MultiChatService] SendMessageAsync: all {sendable.Count} sendable children failed; receipt dropped");
        }
    }

    public void Dispose() {
        for (int i = 0; i < _stateHandlers.Count; i++) {
            var (name, handler) = _stateHandlers[i];
            if (_children.TryGetValue(name, out var child)) {
                try { child.ConnectionStateChanged -= handler; }
                catch { /* swallow; child may already be disposed */ }
            }
        }
        foreach (var c in _children.Values) {
            try { c.MessageReceived -= OnChildMessageReceived; } catch { }
            try { c.Dispose(); } catch (Exception ex) {
                TiLog.Warn($"[MultiChatService] child Dispose threw: {ex.Message}");
            }
        }
    }
}
