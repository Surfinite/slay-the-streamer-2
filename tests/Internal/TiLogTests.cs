using System;
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Internal;

[Collection("TiLog.Sink")]
public class TiLogTests : IDisposable {
    private readonly Action<LogLevel, string, Exception?> _originalSink;
    private readonly List<(LogLevel Level, string Msg, Exception? Ex)> _captured = new();

    public TiLogTests() {
        _originalSink = TiLog.Sink;
        TiLog.Sink = (lvl, msg, ex) => _captured.Add((lvl, msg, ex));
    }

    public void Dispose() => TiLog.Sink = _originalSink;

    [Fact]
    public void InfoForwardsToSinkAtInfoLevel() {
        TiLog.Info("hello");
        Assert.Single(_captured);
        Assert.Equal(LogLevel.Info, _captured[0].Level);
        Assert.Equal("hello", _captured[0].Msg);
        Assert.Null(_captured[0].Ex);
    }

    [Fact]
    public void DebugWarnErrorForwardWithCorrectLevels() {
        TiLog.Debug("d");
        TiLog.Warn("w");
        var ex = new InvalidOperationException("boom");
        TiLog.Error("e", ex);

        Assert.Equal(3, _captured.Count);
        Assert.Equal(LogLevel.Debug, _captured[0].Level);
        Assert.Equal(LogLevel.Warn, _captured[1].Level);
        Assert.Equal(LogLevel.Error, _captured[2].Level);
        Assert.Same(ex, _captured[2].Ex);
    }

    [Fact]
    public void OauthTokensAreScrubbedFromMessages() {
        TiLog.Info("connecting with oauth:abc123def456 to channel #foo");
        Assert.Single(_captured);
        Assert.DoesNotContain("abc123def456", _captured[0].Msg);
        Assert.Contains("oauth:<REDACTED>", _captured[0].Msg);
        Assert.Contains("#foo", _captured[0].Msg);
    }

    [Fact]
    public void OauthTokensAreScrubbedAcrossLevels() {
        TiLog.Warn("oauth:wxyz0987");
        TiLog.Error("token=oauth:wxyz0987 fail", new Exception());
        Assert.DoesNotContain("wxyz0987", _captured[0].Msg);
        Assert.DoesNotContain("wxyz0987", _captured[1].Msg);
    }
}
