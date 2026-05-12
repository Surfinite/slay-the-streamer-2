using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

/// <summary>
/// Parsed result of YouTubeLiveChatScraper.ParseInitialPageAsync.
/// </summary>
internal sealed record InitialPageParseResult(
    string InnertubeApiKey,
    string ClientVersion,
    string InitialContinuation);

/// <summary>
/// Parsed result of YouTubeLiveChatScraper.PollAsync.
/// </summary>
internal sealed record PollResult(
    IReadOnlyList<ParsedChatMessage> Messages,
    string? NextContinuation,
    int NextTimeoutMs);

/// <summary>
/// One parsed message from a liveChatTextMessageRenderer or paid-message renderer.
/// Author display name (not channel ID) used for ChatMessage.Login per Decision 9.
/// </summary>
internal sealed record ParsedChatMessage(
    string AuthorChannelId,
    string AuthorDisplayName,
    string Text,
    bool IsChatMember,
    bool IsChatModerator);
