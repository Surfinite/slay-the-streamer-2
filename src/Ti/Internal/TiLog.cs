using System;
using System.Text.RegularExpressions;

namespace SlayTheStreamer2.Ti.Internal;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Logging shim for the Ti/* layer. Default Sink forwards to the StS2
/// game logger; tests override Sink to capture lines.
/// </summary>
public static class TiLog {
    private static readonly Regex OauthPattern = new(@"oauth:[A-Za-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Receives every log call. Default forwards to MegaCrit.Sts2.Core.Logging.Log;
    /// the default is wired up in ModEntry (Plan B) so the Plan A test environment
    /// gets a no-op default and can override per-test.
    /// </summary>
    public static Action<LogLevel, string, Exception?> Sink { get; set; } = (_, _, _) => { };

    public static void Debug(string msg) => Sink(LogLevel.Debug, Scrub(msg), null);
    public static void Info(string msg) => Sink(LogLevel.Info, Scrub(msg), null);
    public static void Warn(string msg) => Sink(LogLevel.Warn, Scrub(msg), null);
    public static void Error(string msg, Exception? ex = null) => Sink(LogLevel.Error, Scrub(msg), ex);

    private static string Scrub(string msg) => OauthPattern.Replace(msg, "oauth:<REDACTED>");
}
