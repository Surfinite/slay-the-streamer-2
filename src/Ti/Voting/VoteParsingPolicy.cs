namespace SlayTheStreamer2.Ti.Voting;

/// <summary>Toggles for the vote-command parser. Default accepts both `#N` and `!N`.</summary>
public sealed record VoteParsingPolicy(
    bool AcceptHashCommands = true,
    bool AcceptBangCommands = true) {
    public static VoteParsingPolicy Default => new();
    public static VoteParsingPolicy HashOnly => new(true, false);
}
