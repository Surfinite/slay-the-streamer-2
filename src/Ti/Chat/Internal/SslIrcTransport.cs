using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.Internal;

internal sealed class SslIrcTransport : IIrcTransport {
    private TcpClient? _tcp;
    private SslStream? _ssl;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    public async Task ConnectAsync(string host, int port, CancellationToken ct) {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct);
        _ssl = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false);
        await _ssl.AuthenticateAsClientAsync(host);
        _reader = new StreamReader(_ssl, Encoding.UTF8);
        _writer = new StreamWriter(_ssl, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct) {
        if (_reader is null) return null;
        return await _reader.ReadLineAsync(ct);
    }

    public Task WriteLineAsync(string line, CancellationToken ct) {
        if (_writer is null) return Task.FromException(new InvalidOperationException("not connected"));
        return _writer.WriteLineAsync(line);   // CT support omitted; close-on-dispose is enough
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _ssl?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }
}
