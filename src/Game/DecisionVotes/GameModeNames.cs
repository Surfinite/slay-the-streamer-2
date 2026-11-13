using System;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Game-mode checks by enum member NAME, never by compiled constant.
///
/// The game's <c>GameMode</c> enum was <c>None, Standard, Daily, Custom</c> up to
/// v0.111.0 and is <c>None, Standard, Daily, Dread, Blitz</c> from v0.113.0. Value 3
/// therefore means Custom on the default branch and Dread on the Beta. A compiled
/// <c>GameMode.Dread</c> is just the number 3, so it would read every Custom Mode run
/// on the default branch as Dread (and the member does not exist there at all, so the
/// tree would not even compile against that branch). <c>ToString()</c> on the boxed
/// value yields the name from whichever game DLL is loaded.
///
/// Callers pass the value straight from <c>IRunState.GameMode</c> or
/// <c>StartRunLobby.GameMode</c>; both getters have the same shape on every version.
/// </summary>
internal static class GameModeNames {
    internal const string Dread = "Dread";
    internal const string Blitz = "Blitz";

    internal static bool IsDread(object? gameMode) => Is(gameMode, Dread);
    internal static bool IsBlitz(object? gameMode) => Is(gameMode, Blitz);

    private static bool Is(object? gameMode, string name) =>
        gameMode is not null && string.Equals(gameMode.ToString(), name, StringComparison.Ordinal);
}
