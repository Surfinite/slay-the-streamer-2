using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using SlayTheStreamer2.Game.DecisionVotes;

namespace SlayTheStreamer2.Game.DevCommands;

/// <summary>
/// Zero out the per-act card skip counter without disturbing run/act memory.
/// Useful during testing to repeat a skip-budget scenario without having to
/// `act 2` or abandon-and-restart a run. Does NOT fire a "card skips reset"
/// chat receipt — that's reserved for actual run/act transitions.
///
/// Auto-discovered at DevConsole construction time. DebugOnly defaults to true
/// (modded → unlocked).
/// </summary>
public class ResetSkipsConsoleCmd : AbstractConsoleCmd {
    public override string CmdName => "resetskips";
    public override string Args => "";
    public override string Description => "Reset the per-act card skip counter to 0 (testing aid; no chat receipt fired).";
    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args) {
        int previousUsed = CardRewardSkipGatePatch.ResetBudgetForDevConsole();
        return new CmdResult(success: true, $"Card skip counter reset (was {previousUsed} used).");
    }
}
