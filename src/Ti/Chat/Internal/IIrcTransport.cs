using System;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.Internal;

/// <summary>
/// IRC connection abstraction so TwitchIrcChatService can be unit-tested
/// without a real socket. Production impl is SslIrcTransport.
/// </summary>
internal interface IIrcTransport : IDisposable {
    Task ConnectAsync(string host, int port, CancellationToken ct);
    Task<string?> ReadLineAsync(CancellationToken ct);   // null = remote closed
    Task WriteLineAsync(string line, CancellationToken ct);
}
