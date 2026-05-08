using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Ti.Chat.Internal;

/// <summary>Pure-function Twitch IRC parser. Input: one CRLF-stripped line.</summary>
public static class TwitchIrcParser {
    public static IrcEvent? Parse(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;

        if (line.StartsWith("PING ", StringComparison.Ordinal)) {
            var token = line.Substring("PING ".Length).TrimStart(':');
            return new PingEvent(token);
        }

        // Optional @tags prefix
        IReadOnlyDictionary<string, string> tags = EmptyTags;
        var rest = line;
        if (rest.StartsWith("@", StringComparison.Ordinal)) {
            var space = rest.IndexOf(' ');
            if (space < 0) return new UnknownIrcEvent(line);
            tags = ParseTags(rest.Substring(1, space - 1));
            rest = rest.Substring(space + 1);
        }

        // :prefix
        string? prefix = null;
        if (rest.StartsWith(":", StringComparison.Ordinal)) {
            var space = rest.IndexOf(' ');
            if (space < 0) return new UnknownIrcEvent(line);
            prefix = rest.Substring(1, space - 1);
            rest = rest.Substring(space + 1);
        }

        // COMMAND [params] [:trailing]
        var (command, paramsAndTrailing) = SplitCommandAndArgs(rest);

        switch (command) {
            case "RECONNECT": return new ReconnectEvent();
            case "PRIVMSG": return ParsePrivmsg(prefix, paramsAndTrailing, tags);
            default: return new UnknownIrcEvent(line);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyTags = new Dictionary<string, string>();

    private static (string Command, string Args) SplitCommandAndArgs(string s) {
        var space = s.IndexOf(' ');
        if (space < 0) return (s, string.Empty);
        return (s.Substring(0, space), s.Substring(space + 1));
    }

    private static IrcEvent? ParsePrivmsg(string? prefix, string paramsAndTrailing, IReadOnlyDictionary<string, string> tags) {
        if (prefix is null) return null;
        // prefix format: "alice!alice@alice.tmi.twitch.tv"
        var bang = prefix.IndexOf('!');
        var login = bang > 0 ? prefix.Substring(0, bang) : prefix;

        // params + trailing format: "#foo :the message body"
        var colon = paramsAndTrailing.IndexOf(" :", StringComparison.Ordinal);
        var text = colon >= 0 ? paramsAndTrailing.Substring(colon + 2) : paramsAndTrailing;

        tags.TryGetValue("user-id", out var userId);
        tags.TryGetValue("display-name", out var displayNameRaw);
        var displayName = string.IsNullOrEmpty(displayNameRaw) ? login : displayNameRaw;

        var (sub, mod, vip) = ParseBadges(tags.GetValueOrDefault("badges", ""));

        // Parser stays a pure function; if `tmi-sent-ts` is absent, leave
        // ReceivedAt = DateTimeOffset.MinValue and let TwitchIrcChatService
        // (Plan B) stamp it from its injected IClock. The parser doesn't need
        // a clock dependency.
        DateTimeOffset receivedAt = DateTimeOffset.MinValue;
        if (tags.TryGetValue("tmi-sent-ts", out var ts) && long.TryParse(ts, out var ms)) {
            receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        return new PrivmsgEvent(new ChatMessage(
            UserId: string.IsNullOrEmpty(userId) ? null : userId,
            Login: login,
            DisplayName: displayName,
            Text: text,
            ReceivedAt: receivedAt,
            IsSubscriber: sub, IsModerator: mod, IsVip: vip));
    }

    private static IReadOnlyDictionary<string, string> ParseTags(string tagSegment) {
        var dict = new Dictionary<string, string>();
        var entries = tagSegment.Split(';');
        foreach (var entry in entries) {
            var eq = entry.IndexOf('=');
            if (eq < 0) { dict[entry] = string.Empty; continue; }
            var key = entry.Substring(0, eq);
            var rawValue = entry.Substring(eq + 1);
            dict[key] = UnescapeTagValue(rawValue);
        }
        return dict;
    }

    private static string UnescapeTagValue(string raw) {
        if (raw.Length == 0 || raw.IndexOf('\\') < 0) return raw;
        var sb = new System.Text.StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++) {
            var c = raw[i];
            if (c != '\\' || i + 1 >= raw.Length) { sb.Append(c); continue; }
            var n = raw[++i];
            sb.Append(n switch {
                ':' => ';',
                's' => ' ',
                '\\' => '\\',
                'r' => '\r',
                'n' => '\n',
                _ => n,                       // unknown escape — keep literal
            });
        }
        return sb.ToString();
    }

    private static (bool Sub, bool Mod, bool Vip) ParseBadges(string raw) {
        if (string.IsNullOrEmpty(raw)) return (false, false, false);
        bool sub = false, mod = false, vip = false;
        foreach (var entry in raw.Split(',')) {
            var slash = entry.IndexOf('/');
            var name = slash >= 0 ? entry.Substring(0, slash) : entry;
            switch (name) {
                case "subscriber": case "founder": sub = true; break;
                case "moderator": mod = true; break;
                case "vip": vip = true; break;
            }
        }
        return (sub, mod, vip);
    }
}
