namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// One option in a vote. Index is 0-based and equals the option's position in
/// the Options list. Constructor is internal — only VoteCoordinator builds these,
/// keeping Index and position in sync.
/// </summary>
public sealed record VoteOption {
    public int Index { get; }
    public string Label { get; }
    internal VoteOption(int index, string label) { Index = index; Label = label; }
}
