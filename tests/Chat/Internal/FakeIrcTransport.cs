using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat.Internal;

namespace SlayTheStreamer2.Tests.Chat.Internal;

/// <summary>Records writes; releases reads when InjectIncoming is called.</summary>
internal sealed class FakeIrcTransport : IIrcTransport {
    private readonly BlockingCollection<string> _incoming = new();
    public List<string> Writes { get; } = new();
    public bool ConnectCalled { get; private set; }
    public bool Disposed { get; private set; }
    public string? ConnectHost { get; private set; }
    public int ConnectPort { get; private set; }

    public Task ConnectAsync(string host, int port, CancellationToken ct) {
        ConnectCalled = true;
        ConnectHost = host;
        ConnectPort = port;
        return Task.CompletedTask;
    }

    public Task<string?> ReadLineAsync(CancellationToken ct) {
        return Task.Run<string?>(() => {
            try { return _incoming.Take(ct); }
            catch (OperationCanceledException) { return null; }
            catch (InvalidOperationException) { return null; }   // CompleteAdding called
        }, ct);
    }

    public Task WriteLineAsync(string line, CancellationToken ct) {
        lock (Writes) Writes.Add(line);
        return Task.CompletedTask;
    }

    /// <summary>Test API: deliver a line as if read from the remote.</summary>
    public void InjectIncoming(string line) => _incoming.Add(line);

    /// <summary>Test API: simulate remote closing the connection.</summary>
    public void Close() => _incoming.CompleteAdding();

    public void Dispose() {
        Disposed = true;
        _incoming.CompleteAdding();
    }
}
