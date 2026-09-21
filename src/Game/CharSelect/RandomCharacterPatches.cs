using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.CharSelect;

/// <summary>The standard-mode "?" (Random) character button on the Custom Mode screen,
/// resolved the way standard mode resolves it. Ported from the Sabotage mod (rand/1).
///
/// Vanilla already owns the whole mechanism and it is game-mode agnostic:
/// <see cref="RandomCharacter"/> is a real (non-playable) CharacterModel, the standard
/// screen adds one extra NCharacterSelectButton for it (NCharacterSelectScreen.InitCharacterButtons),
/// and the LOBBY resolves it at launch, not the screen: StartRunLobby.BeginRunLocally
/// walks every lobby player and replaces a RandomCharacter with
/// rng.NextItem(ModelDb.AllCharacters) drawn from the run seed's "act_selection" stream,
/// right after the act list draw. That method runs on the HOST (BeginRunForAllPlayers) and
/// on every CLIENT (HandleLobbyBeginRunMessage), in the same player order from the same
/// seed, so both machines resolve the same character with no wire traffic; the resolved
/// roster is what StartNewMultiplayerRun reads. Custom mode lacks it for two reasons only:
/// NCustomRunScreen.InitCharacterButtons builds buttons for ModelDb.AllCharacters (the five
/// playables, RandomCharacter is not in that list) and NCustomRunScreen.PlayerChanged THROWS
/// "Random character is not currently allowed in custom!" when a resolution arrives.
/// Vanilla's own custom Randomize button already skips IsRandom buttons, so it anticipated
/// the button being there.
///
/// Three lobby-local seams, all pre-launch UI, no synchronized state:
///   1. InitCharacterButtons postfix: add the "?" button as the last child of the row and
///      fix the focus neighbours. Never mounted unless seam 2 bound, so a game rename can
///      only lose the button, never leave one whose pick would throw at launch.
///   2. PlayerChanged prefix: clear the resolution flag so vanilla's remaining code runs
///      (remote portrait refresh + button refresh; the remote marker moves from "?" to the
///      resolved portrait by itself). For the LOCAL player, mirror the standard screen's
///      reveal (NCharacterSelectScreen.OnLocalCharacterChangedForRandom): select sound,
///      weak shake, and a highlight move from "?" to the resolved portrait (the custom
///      screen has no character background to swap, the highlight is its stand-in).
///   3. BeginRun prefix: after a local resolution, hold the embark one second (standard
///      mode's _delayEmbarkForCharacterSelect beat) so the reveal is seen before the fade,
///      then re-invoke BeginRun. Messages are already buffered by then (BeginRunLocally
///      sets SetBufferMessages(true) before calling the listener), the same posture
///      standard multiplayer takes for its own beat.
///
/// Cross-branch note: the lobby-player struct is <c>LobbyPlayer</c> on the default branch
/// (v0.107.1) and <c>StartRunLobbyPlayer</c> on the Beta (v0.110+), same public fields
/// (<c>id</c>, <c>character</c>). The PlayerChanged prefix reads it through Harmony's
/// <c>__args</c> injection plus two field lookups so the tree compiles against either.
///
/// Lock semantics are vanilla's: NCharacterSelectButton.Init locks the Random button until
/// the local profile has all five characters unlocked, which is correct because the draw
/// can land on any of them. Resilient targets: a rename logs and skips.
///
/// Interaction with the act-variant vote: that vote suspends at OnEmbarkPressed (before
/// the lobby launches), so it has already resolved and set Lobby.Act1 by the time seams 2
/// and 3 run. No ordering hazard.</summary>
internal static class RandomCharacterLobby {
    /// <summary>Set by the resolution patch's TargetMethods; the button never mounts without it.</summary>
    internal static bool ResolutionSeamBound;
    /// <summary>Set at a LOCAL resolution, consumed by the next BeginRun.</summary>
    internal static bool LocalRevealPending;
    internal const float RevealSeconds = 1f;
    internal const string ButtonContainerPath = "LeftContainer/CharSelectButtons/ButtonContainer";
    internal const string ButtonScenePath = "res://scenes/screens/char_select/char_select_button.tscn";
    internal const string LogPrefix = "[SlayTheStreamer2][custom-random]";

    internal static IEnumerable<NCharacterSelectButton> Buttons(Control container) =>
        container.GetChildren().OfType<NCharacterSelectButton>();
}

[HarmonyPatch]
internal static class RandomCharacterButtonPatch {
    private static IEnumerable<MethodBase> TargetMethods() {
        var m = AccessTools.Method(typeof(NCustomRunScreen), "InitCharacterButtons");
        if (m is not null) return new[] { m };
        TiLog.Error($"{RandomCharacterLobby.LogPrefix} NCustomRunScreen.InitCharacterButtons not found via reflection (game update?); the Random character button will not be added to Custom Mode");
        return Array.Empty<MethodBase>();
    }

    private static void Postfix(NCustomRunScreen __instance) {
        try {
            if (!RandomCharacterLobby.ResolutionSeamBound) {
                TiLog.Warn($"{RandomCharacterLobby.LogPrefix} resolution seam unbound; Random button NOT added (a pick would throw at launch)");
                return;
            }
            var container = __instance.GetNodeOrNull<Control>(RandomCharacterLobby.ButtonContainerPath);
            if (container is null) { TiLog.Warn($"{RandomCharacterLobby.LogPrefix} {RandomCharacterLobby.ButtonContainerPath} not found; Random button not added"); return; }
            var buttons = RandomCharacterLobby.Buttons(container).ToList();
            if (buttons.Count == 0) { TiLog.Warn($"{RandomCharacterLobby.LogPrefix} no character buttons in the container; Random button not added"); return; }
            if (buttons.Any(b => b.IsRandom)) return;   // the screen is one cached node; idempotent
            var model = ModelDb.Character<RandomCharacter>();
            var button = PreloadManager.Cache.GetScene(RandomCharacterLobby.ButtonScenePath).Instantiate<NCharacterSelectButton>();
            button.Name = model.Id.Entry + "_button";
            container.AddChildSafely(button);
            button.Init(model, __instance);
            // Focus neighbours: vanilla wired the five in a left-right chain with the seed
            // input above; extend the chain by one.
            var prev = buttons[^1];
            prev.FocusNeighborRight = button.GetPath();
            button.FocusNeighborLeft = prev.GetPath();
            button.FocusNeighborRight = button.GetPath();
            button.FocusNeighborTop = prev.FocusNeighborTop;
            button.FocusNeighborBottom = button.GetPath();
            TiLog.Info($"{RandomCharacterLobby.LogPrefix} Random character button added to Custom Mode ({(button.IsLocked ? "locked: not every character is unlocked on this profile" : "unlocked")})");
        } catch (Exception e) { TiLog.Error($"{RandomCharacterLobby.LogPrefix} Random button mount failed", e); }
    }
}

[HarmonyPatch]
internal static class RandomCharacterResolvePatch {
    private static IEnumerable<MethodBase> TargetMethods() {
        var m = AccessTools.Method(typeof(NCustomRunScreen), nameof(NCustomRunScreen.PlayerChanged));
        if (m is not null) { RandomCharacterLobby.ResolutionSeamBound = true; return new[] { m }; }
        TiLog.Error($"{RandomCharacterLobby.LogPrefix} NCustomRunScreen.PlayerChanged not found via reflection (game update?); the Random character button will not be added to Custom Mode");
        return Array.Empty<MethodBase>();
    }

    private static readonly FieldInfo? SelectedField = AccessTools.Field(typeof(NCharacterSelectButton), "_isSelected");
    private static readonly MethodInfo? RefreshStateMethod = AccessTools.Method(typeof(NCharacterSelectButton), "RefreshState");

    /// <summary>Runs BEFORE vanilla's body; clearing the flag by ref is what turns the
    /// throw into vanilla's ordinary player-changed handling. The player struct arrives
    /// boxed in <paramref name="__args"/> (index 0) because its type name differs per
    /// game branch; see the class remarks.</summary>
    private static void Prefix(NCustomRunScreen __instance, object[] __args, ref bool isRandomCharacterResolution) {
        if (!isRandomCharacterResolution) return;
        isRandomCharacterResolution = false;
        try {
            var player = __args[0];
            var playerType = player.GetType();
            var playerId = AccessTools.Field(playerType, "id")?.GetValue(player) as ulong?;
            var character = AccessTools.Field(playerType, "character")?.GetValue(player) as CharacterModel;
            var lobby = __instance.Lobby;
            object? localPlayer = lobby is null ? null : AccessTools.Property(lobby.GetType(), "LocalPlayer")?.GetValue(lobby);
            var localId = localPlayer is null ? null : AccessTools.Field(localPlayer.GetType(), "id")?.GetValue(localPlayer) as ulong?;
            bool local = playerId is not null && playerId == localId;
            var entry = character?.Id.Entry ?? "?";
            TiLog.Info($"{RandomCharacterLobby.LogPrefix} {(local ? "local" : "remote")} player {playerId?.ToString() ?? "?"} resolved Random to {entry}");
            if (!local || character is null) return;
            SfxCmd.Play(character.CharacterSelectSfx);
            NGame.Instance?.ScreenShake(ShakeStrength.Weak, ShakeDuration.Short, 90f);
            MoveLocalHighlight(__instance, character);
            RandomCharacterLobby.LocalRevealPending = true;
        } catch (Exception e) { TiLog.Warn($"{RandomCharacterLobby.LogPrefix} reveal failed (launch continues): {e.Message}"); }
    }

    /// <summary>Highlight only: never NCharacterSelectButton.Select(), which calls back into
    /// SelectCharacter -> StartRunLobby.SetLocalCharacter and sends a character-changed
    /// message mid-launch. The private selected flag plus RefreshState is exactly what
    /// Select() does visually; warn-on-miss, cosmetic.</summary>
    private static void MoveLocalHighlight(NCustomRunScreen screen, CharacterModel resolved) {
        var container = screen.GetNodeOrNull<Control>(RandomCharacterLobby.ButtonContainerPath);
        if (container is null) return;
        if (SelectedField is null || RefreshStateMethod is null) { TiLog.Warn($"{RandomCharacterLobby.LogPrefix} NCharacterSelectButton._isSelected/RefreshState not found; highlight stays on the Random button"); return; }
        foreach (var b in RandomCharacterLobby.Buttons(container)) {
            if (b.IsRandom) b.Deselect();
            else if (b.Character == resolved) { SelectedField.SetValue(b, true); RefreshStateMethod.Invoke(b, null); }
        }
    }
}

[HarmonyPatch]
internal static class RandomCharacterRevealBeatPatch {
    private static IEnumerable<MethodBase> TargetMethods() {
        var m = AccessTools.Method(typeof(NCustomRunScreen), nameof(NCustomRunScreen.BeginRun));
        if (m is not null) return new[] { m };
        TiLog.Error($"{RandomCharacterLobby.LogPrefix} NCustomRunScreen.BeginRun not found via reflection (game update?); a Random pick embarks without the one-second reveal beat");
        return Array.Empty<MethodBase>();
    }

    private static readonly FieldInfo? ConfirmField = AccessTools.Field(typeof(NCustomRunScreen), "_confirmButton");
    private static readonly FieldInfo? UnreadyField = AccessTools.Field(typeof(NCustomRunScreen), "_unreadyButton");

    private static bool Prefix(NCustomRunScreen __instance, string seed, List<ActModel> acts, IReadOnlyList<ModifierModel> modifiers, MethodBase __originalMethod) {
        if (!RandomCharacterLobby.LocalRevealPending) return true;
        RandomCharacterLobby.LocalRevealPending = false;
        try {
            // Vanilla's BeginRun disables these first; do it before the beat so no click
            // lands in the window, then let the re-invoked original do the rest.
            (ConfirmField?.GetValue(__instance) as NButton)?.Disable();
            (UnreadyField?.GetValue(__instance) as NButton)?.Disable();
            TaskHelper.RunSafely(AfterBeat(__instance, seed, acts, modifiers, __originalMethod));
            return false;
        } catch (Exception e) {
            TiLog.Warn($"{RandomCharacterLobby.LogPrefix} reveal beat failed; embarking immediately: {e.Message}");
            return true;
        }
    }

    private static async Task AfterBeat(NCustomRunScreen screen, string seed, List<ActModel> acts, IReadOnlyList<ModifierModel> modifiers, MethodBase original) {
        await Cmd.Wait(RandomCharacterLobby.RevealSeconds, ignoreCombatEnd: true);
        if (!screen.IsValid()) { TiLog.Warn($"{RandomCharacterLobby.LogPrefix} custom screen gone during the reveal beat; BeginRun not re-invoked"); return; }
        // Re-enters this prefix with the latch already cleared, so vanilla's body runs.
        original.Invoke(screen, new object[] { seed, acts, modifiers });
    }
}
