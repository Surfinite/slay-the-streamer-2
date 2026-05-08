using System;

namespace SlayTheStreamer2.Ti.Chat.Internal;

/// <summary>
/// Pure-function parser for Twitch IRC lines. Input lines are pre-trimmed
/// (no CRLF). Output is a typed IrcEvent; UnknownIrcEvent for commands we
/// don't handle in v0.1.
/// </summary>
public static class TwitchIrcParser {
    public static IrcEvent? Parse(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // PING is the only command that doesn't start with a `:` prefix.
        if (line.StartsWith("PING ", StringComparison.Ordinal)) {
            var tokenStart = "PING ".Length;
            var token = line.Substring(tokenStart).TrimStart(':');
            return new PingEvent(token);
        }

        // Everything else has a leading `:prefix`.
        // Format: :prefix COMMAND [params] [:trailing]
        // (We're ignoring the @tags prefix here; PRIVMSG handling adds it in 7.2.)
        var rest = line;
        if (rest.StartsWith(":", StringComparison.Ordinal)) {
            var space = rest.IndexOf(' ');
            if (space < 0) return new UnknownIrcEvent(line);
            rest = rest.Substring(space + 1);
        }
        // Now rest starts with COMMAND.
        if (rest.StartsWith("RECONNECT", StringComparison.Ordinal)) {
            return new ReconnectEvent();
        }

        return new UnknownIrcEvent(line);
    }
}
