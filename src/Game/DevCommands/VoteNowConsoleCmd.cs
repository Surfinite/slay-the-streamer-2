using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DevCommands;

/// <summary>
/// Force-end the currently-open chat vote and apply chat's pick immediately. Skips
/// the 30s countdown — useful during operator-validation testing so the streamer
/// doesn't have to wait for the timer on every test cycle. Vote resume runs the
/// normal path (dispatcher.Post → ResumeOnMainThread → SelectCard); the only
/// shortcut is bypassing the timer.
///
/// Auto-discovered at DevConsole construction time via
/// `ReflectionHelper.GetSubtypesInMods&lt;AbstractConsoleCmd&gt;()` —
/// see DevConsole.cs:33. DebugOnly defaults to true, which is satisfied
/// automatically when the game is running modded (Decision 14 of v4 spec).
/// </summary>
public class VoteNowConsoleCmd : AbstractConsoleCmd {
    public override string CmdName => "votenow";
    public override string Args => "";
    public override string Description => "Force-end the current chat vote immediately (skip the 30s timer).";
    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args) {
        var coordinator = Voter.Default;
        if (coordinator is null) {
            return new CmdResult(success: false, "Mod chat coordinator not initialised (Voter.Default == null).");
        }
        var session = coordinator.CurrentSession;
        if (session is null) {
            return new CmdResult(success: false, "No active vote to close.");
        }
        if (session.State != VoteSessionState.Open) {
            return new CmdResult(success: false, $"Vote is in state {session.State}, not Open — cannot close.");
        }
        int winner = session.CloseNow();
        return new CmdResult(success: true, $"Vote closed; winner index = #{winner}.");
    }
}
