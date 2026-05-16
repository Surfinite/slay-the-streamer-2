using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using SlayTheStreamer2.Game.DecisionVotes;

namespace SlayTheStreamer2.Game.DevCommands;

/// <summary>
/// Dev-only: re-roll the currently-open boss vote with a different sample from
/// the same (run, act). Used during B.3.1 operator validation to test multiple
/// candidate sets without abandoning the run + starting fresh.
///
/// The stale HandleVoteAsync from the cancelled old session bails cleanly via
/// the generation guard in BossVotePatch.ResumeOnMainThread — no swap, no
/// synthetic Proceed click. The new vote is started with _rerollSalt bumped,
/// producing a different sample from the same (run, act).
///
/// Auto-discovered at DevConsole construction time via
/// `ReflectionHelper.GetSubtypesInMods&lt;AbstractConsoleCmd&gt;()`. DebugOnly
/// defaults to true (only surfaces when the game is running modded).
/// </summary>
public class RerollVoteConsoleCmd : AbstractConsoleCmd {
    public override string CmdName => "rerollvote";
    public override string Args => "";
    public override string Description => "Re-roll the current boss vote with a different candidate set (B.3.1 dev affordance).";
    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args) {
        var (ok, message) = BossVotePatch.RerollCurrent();
        return new CmdResult(success: ok, message);
    }
}
