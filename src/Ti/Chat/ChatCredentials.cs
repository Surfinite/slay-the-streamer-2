using System;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// Twitch chat login credentials. Stores token without the "oauth:" prefix;
/// TwitchIrcChatService prepends it on PASS. ToString redacts the token.
/// </summary>
public sealed class ChatCredentials {
    public string Username { get; }
    public string OauthToken { get; }

    public ChatCredentials(string username, string oauthToken) {
        if (username is null) throw new ArgumentNullException(nameof(username));
        if (oauthToken is null) throw new ArgumentNullException(nameof(oauthToken));

        Username = username.ToLowerInvariant();
        OauthToken = oauthToken.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)
            ? oauthToken.Substring("oauth:".Length)
            : oauthToken;
    }

    public override string ToString() => $"ChatCredentials[{Username}, oauth:<REDACTED>]";
}
