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
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.CharSelect;

/// <summary>The standard-mode "?" (Random) character button on the Custom Mode screen,
/// resolved the way standard mode resolves it. Ported from the Sabotage mod (rand/1).
///
/// Vanilla already owns the whole mechanism and it is game-mode agnostic:
/// <see cref="RandomCharacter"/> is a real (non-playable) CharacterModel, the standard
/// screen adds one extra NCharacterSelectButton for it, and the LOBBY resolves it at
/// launch, not the screen: StartRunLobby.BeginRunLocally walks every lobby player and
/// replaces a RandomCharacter with rng.NextItem(ModelDb.AllCharacters) drawn from the run
/// seed's "act_selection" stream, right after the act list draw. That method runs on the
/// HOST and on every CLIENT, in the same player order from the same seed, so both
/// machines resolve the same character with no wire traffic. Custom mode simply never
/// builds the button (NCustomRunScreen.InitCharacterButtons iterates
/// ModelDb.AllCharacters, which excludes RandomCharacter). Vanilla's own custom
/// Randomize button already skips IsRandom buttons, so it anticipated one being there.
///
/// The game changed under this feature between the two live branches, so each seam
/// binds by shape:
///
///   1. InitCharacterButtons postfix (all versions): add the "?" button as the last
///      child of the row and extend the focus chain. From game v0.113.0 the Random
///      setup lives in NCharacterSelectButton.InitAsRandom and Init THROWS for the
///      Random character; before that, Init handles it. Found by name at runtime. Never
///      mounted unless a Random pick is safe at launch (see seam 2).
///   2. PlayerChanged prefix (game up to v0.111.0 only): there, PlayerChanged takes a
///      bool isRandomCharacterResolution and THROWS "Random character is not currently
///      allowed in custom!" when it is true; the prefix clears it by ref. v0.113.0
///      removed the parameter and the throw, so vanilla resolves Random in Custom Mode
///      itself and this patch does not register. Harmony injects original parameters by
///      NAME and throws during patching when the name is gone, hence the Prepare gate.
///   3. BeginRun prefix (all versions): when the local player's pick was Random, play
///      the reveal (select sound, weak shake, highlight moves from "?" to the resolved
///      portrait; the custom screen has no character background to swap) and hold the
///      embark one second, then re-invoke BeginRun. Keyed off "the Random button is
///      selected", which is how the standard screen decides from v0.113.0 and is true
///      on every version: by BeginRun time the lobby has already resolved the pick but
///      nothing has moved the local highlight.
///   4. Dread rule (game v0.113.0+): Custom Mode gained a game-mode dropdown, and the
///      lobby throws "Random or rotating character selected in Dread!" at launch, after
///      the screen has disabled its own buttons. So while the lobby's mode is Dread the
///      "?" button is hidden and a Random selection is moved to a concrete character.
///      Applied when the mode changes, when the screen opens (the dropdown's value
///      persists on the cached screen node), and once more on Embark as the backstop.
///
/// Cross-branch notes: the lobby-player struct is <c>LobbyPlayer</c> on the default
/// branch (v0.107.1) and <c>StartRunLobbyPlayer</c> on the Beta, same public fields
/// (<c>id</c>, <c>character</c>), so it is only ever handled boxed. The PlayerChanged
/// prefix declares the parameter as <c>object</c> (Harmony boxes a value-type argument
/// into it). NOT <c>__args</c>: Harmony builds that array once at method entry and
/// copies it back into every argument after a prefix that declares it, which would
/// re-set the resolution flag right after the prefix cleared it (custom-random/2).
/// Game modes are compared by name (<see cref="GameModeNames"/>).
///
/// Lock semantics are vanilla's: the Random button is locked until the local profile has
/// every character unlocked, which is correct because the draw can land on any of them.
///
/// Interaction with the act-variant vote: that vote suspends at OnEmbarkPressed (before
/// the lobby launches), so it has resolved and set Lobby.Act1 by the time seams 2 and 3
/// run. No ordering hazard.</summary>
internal static class RandomCharacterLobby {
    /// <summary>True when a Random pick is safe at launch on this game version: either
    /// vanilla resolves it without throwing (game v0.113.0+), or the resolution prefix
    /// registered. The button never mounts without it.</summary>
    internal static bool ResolutionSeamBound;
    /// <summary>Set while the reveal beat is running; the BeginRun prefix's own
    /// re-invoke sees it, clears it and lets vanilla through.</summary>
    internal static bool RevealInFlight;
    internal const float RevealSeconds = 1f;
    internal const string ButtonContainerPath = "LeftContainer/CharSelectButtons/ButtonContainer";
    internal const string ButtonScenePath = "res://scenes/screens/char_select/char_select_button.tscn";
    internal const string ResolutionFlagName = "isRandomCharacterResolution";
    internal const string LogPrefix = "[SlayTheStreamer2][custom-random]";

    internal static IEnumerable<NCharacterSelectButton> Buttons(Control container) =>
        container.GetChildren().OfType<NCharacterSelectButton>();

    /// <summary>The lobby's local player as a boxed struct (its type name differs per
    /// game branch), or null.</summary>
    internal static object? LocalPlayer(object? lobby) =>
        lobby is null ? null : AccessTools.Property(lobby.GetType(), "LocalPlayer")?.GetValue(lobby);

    internal static ulong? PlayerId(object? player) =>
        player is null ? null : AccessTools.Field(player.GetType(), "id")?.GetValue(player) as ulong?;

    internal static CharacterModel? PlayerCharacter(object? player) =>
        player is null ? null : AccessTools.Field(player.GetType(), "character")?.GetValue(player) as CharacterModel;
}

[HarmonyPatch]
internal static class RandomCharacterButtonPatch {
    private static readonly Lazy<MethodInfo?> Target = new(() =>
        AccessTools.Method(typeof(NCustomRunScreen), "InitCharacterButtons"));
    /// <summary>Game v0.113.0+ only; null before, where Init handles the Random character.</summary>
    private static readonly Lazy<MethodInfo?> InitAsRandomMethod = new(() =>
        AccessTools.Method(typeof(NCharacterSelectButton), "InitAsRandom"));

    private static bool Prepare(MethodBase? original) {
        if (original is not null || Target.Value is not null) return true;
        TiLog.Error($"{RandomCharacterLobby.LogPrefix} NCustomRunScreen.InitCharacterButtons not found via reflection (game update?); the Random character button will not be added to Custom Mode");
        return false;
    }

    private static IEnumerable<MethodBase> TargetMethods() => new[] { Target.Value! };

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
            try {
                var initAsRandom = InitAsRandomMethod.Value;
                if (initAsRandom is not null) initAsRandom.Invoke(button, new object[] { __instance });
                else button.Init(model, __instance);
            } catch (Exception e) {
                // A half-initialised button must not stay in the row: vanilla's Randomize
                // could select it and its pick would not behave as Random.
                container.RemoveChild(button);
                button.QueueFree();
                TiLog.Error($"{RandomCharacterLobby.LogPrefix} Random button init failed; button removed, Custom Mode is vanilla", e);
                return;
            }
            // Focus neighbours: vanilla wired the playable characters in a left-right
            // chain with the seed input above; extend the chain by one.
            var prev = buttons[^1];
            prev.FocusNeighborRight = button.GetPath();
            button.FocusNeighborLeft = prev.GetPath();
            button.FocusNeighborRight = button.GetPath();
            button.FocusNeighborTop = prev.FocusNeighborTop;
            button.FocusNeighborBottom = button.GetPath();
            TiLog.Info($"{RandomCharacterLobby.LogPrefix} Random character button added to Custom Mode ({(button.IsLocked ? "locked: not every character is unlocked on this profile" : "unlocked")})");
            RandomCharacterDreadRule.Apply(__instance, "InitCharacterButtons");
        } catch (Exception e) { TiLog.Error($"{RandomCharacterLobby.LogPrefix} Random button mount failed", e); }
    }
}

[HarmonyPatch]
internal static class RandomCharacterResolvePatch {
    private static readonly Lazy<MethodInfo?> Target = new(() =>
        AccessTools.Method(typeof(NCustomRunScreen), nameof(NCustomRunScreen.PlayerChanged)));

    /// <summary>Registers only where PlayerChanged still has the resolution flag (game up
    /// to v0.111.0). Returning false here is the clean skip; an empty TargetMethods would
    /// fall through to attribute resolution and throw.</summary>
    private static bool Prepare(MethodBase? original) {
        if (original is not null) return true;
        var target = Target.Value;
        if (target is null) {
            TiLog.Error($"{RandomCharacterLobby.LogPrefix} NCustomRunScreen.PlayerChanged not found via reflection (game update?); the Random character button will not be added to Custom Mode");
            return false;
        }
        if (!MemberCompat.HasParameter(target, RandomCharacterLobby.ResolutionFlagName, typeof(bool))) {
            RandomCharacterLobby.ResolutionSeamBound = true;
            TiLog.Info($"{RandomCharacterLobby.LogPrefix} NCustomRunScreen.PlayerChanged has no {RandomCharacterLobby.ResolutionFlagName} flag on this game version: vanilla resolves Random in Custom Mode itself; resolution patch not needed");
            return false;
        }
        RandomCharacterLobby.ResolutionSeamBound = true;
        return true;
    }

    private static IEnumerable<MethodBase> TargetMethods() => new[] { Target.Value! };

    /// <summary>Runs BEFORE vanilla's body; clearing the flag by ref is what turns the
    /// throw into vanilla's ordinary player-changed handling (remote portrait refresh +
    /// button refresh). The reveal itself happens at BeginRun, on every game version.
    /// The player struct arrives boxed as <paramref name="player"/> because its type
    /// name differs per game branch; see the class remarks.</summary>
    private static void Prefix(NCustomRunScreen __instance, object player, ref bool isRandomCharacterResolution) {
        if (!isRandomCharacterResolution) return;
        isRandomCharacterResolution = false;
        try {
            var playerId = RandomCharacterLobby.PlayerId(player);
            var localId = RandomCharacterLobby.PlayerId(RandomCharacterLobby.LocalPlayer(__instance.Lobby));
            bool local = playerId is not null && playerId == localId;
            var entry = RandomCharacterLobby.PlayerCharacter(player)?.Id.Entry ?? "?";
            TiLog.Info($"{RandomCharacterLobby.LogPrefix} {(local ? "local" : "remote")} player {playerId?.ToString() ?? "?"} resolved Random to {entry}");
        } catch (Exception e) { TiLog.Warn($"{RandomCharacterLobby.LogPrefix} resolution log failed (launch continues): {e.Message}"); }
    }
}

[HarmonyPatch]
internal static class RandomCharacterRevealBeatPatch {
    private static readonly Lazy<MethodInfo?> Target = new(() =>
        AccessTools.Method(typeof(NCustomRunScreen), nameof(NCustomRunScreen.BeginRun)));

    private static bool Prepare(MethodBase? original) {
        if (original is not null || Target.Value is not null) return true;
        TiLog.Error($"{RandomCharacterLobby.LogPrefix} NCustomRunScreen.BeginRun not found via reflection (game update?); a Random pick embarks without the reveal");
        return false;
    }

    private static IEnumerable<MethodBase> TargetMethods() => new[] { Target.Value! };

    private static readonly FieldInfo? ConfirmField = AccessTools.Field(typeof(NCustomRunScreen), "_confirmButton");
    private static readonly FieldInfo? UnreadyField = AccessTools.Field(typeof(NCustomRunScreen), "_unreadyButton");
    private static readonly FieldInfo? SelectedField = AccessTools.Field(typeof(NCharacterSelectButton), "_isSelected");
    private static readonly MethodInfo? RefreshStateMethod = AccessTools.Method(typeof(NCharacterSelectButton), "RefreshState");

    private static bool Prefix(NCustomRunScreen __instance, string seed, List<ActModel> acts, IReadOnlyList<ModifierModel> modifiers, MethodBase __originalMethod) {
        if (RandomCharacterLobby.RevealInFlight) {
            // Our own re-invoke after the beat: let vanilla's body run.
            RandomCharacterLobby.RevealInFlight = false;
            return true;
        }
        try {
            var container = __instance.GetNodeOrNull<Control>(RandomCharacterLobby.ButtonContainerPath);
            if (container is null) return true;
            var randomButton = RandomCharacterLobby.Buttons(container).FirstOrDefault(b => b.IsRandom);
            if (randomButton is null || !randomButton.IsSelected) return true;   // the local pick was not Random

            // The lobby resolved every Random pick in BeginRunLocally, before calling us.
            var resolved = RandomCharacterLobby.PlayerCharacter(RandomCharacterLobby.LocalPlayer(__instance.Lobby));
            if (resolved is null || resolved is RandomCharacter) {
                TiLog.Warn($"{RandomCharacterLobby.LogPrefix} local Random pick is not resolved at BeginRun; embarking without the reveal");
                return true;
            }
            TiLog.Info($"{RandomCharacterLobby.LogPrefix} local Random pick resolved to {resolved.Id.Entry}; reveal beat {RandomCharacterLobby.RevealSeconds}s");
            SfxCmd.Play(resolved.CharacterSelectSfx);
            NGame.Instance?.ScreenShake(ShakeStrength.Weak, ShakeDuration.Short, 90f);
            MoveLocalHighlight(container, resolved);
            // Vanilla's BeginRun disables these first; do it before the beat so no click
            // lands in the window, then let the re-invoked original do the rest.
            (ConfirmField?.GetValue(__instance) as NButton)?.Disable();
            (UnreadyField?.GetValue(__instance) as NButton)?.Disable();
            RandomCharacterLobby.RevealInFlight = true;
            TaskHelper.RunSafely(AfterBeat(__instance, seed, acts, modifiers, __originalMethod));
            return false;
        } catch (Exception e) {
            RandomCharacterLobby.RevealInFlight = false;
            TiLog.Warn($"{RandomCharacterLobby.LogPrefix} reveal failed; embarking immediately: {e.Message}");
            return true;
        }
    }

    /// <summary>Highlight only: never NCharacterSelectButton.Select(), which calls back into
    /// SelectCharacter -> StartRunLobby.SetLocalCharacter and sends a character-changed
    /// message mid-launch. The private selected flag plus RefreshState is exactly what
    /// Select() does visually; warn-on-miss, cosmetic.</summary>
    private static void MoveLocalHighlight(Control container, CharacterModel resolved) {
        if (SelectedField is null || RefreshStateMethod is null) { TiLog.Warn($"{RandomCharacterLobby.LogPrefix} NCharacterSelectButton._isSelected/RefreshState not found; highlight stays on the Random button"); return; }
        foreach (var b in RandomCharacterLobby.Buttons(container)) {
            if (b.IsRandom) b.Deselect();
            else if (b.Character == resolved) { SelectedField.SetValue(b, true); RefreshStateMethod.Invoke(b, null); }
        }
    }

    private static async Task AfterBeat(NCustomRunScreen screen, string seed, List<ActModel> acts, IReadOnlyList<ModifierModel> modifiers, MethodBase original) {
        try {
            await Cmd.Wait(RandomCharacterLobby.RevealSeconds, ignoreCombatEnd: true);
        } catch (Exception e) {
            // The launch must still happen: the screen's buttons are already disabled.
            TiLog.Warn($"{RandomCharacterLobby.LogPrefix} reveal beat wait failed; embarking now: {e.Message}");
        }
        if (!screen.IsValid()) {
            RandomCharacterLobby.RevealInFlight = false;
            TiLog.Warn($"{RandomCharacterLobby.LogPrefix} custom screen gone during the reveal beat; BeginRun not re-invoked");
            return;
        }
        // Re-enters the prefix with RevealInFlight set, so vanilla's body runs.
        original.Invoke(screen, new object[] { seed, acts, modifiers });
    }
}

/// <summary>Seam 4: no Random pick while the Custom Mode lobby is set to Dread.</summary>
internal static class RandomCharacterDreadRule {
    /// <summary>Idempotent. Hides the "?" button while the lobby's game mode is Dread
    /// (showing it again otherwise) and, if it is the local selection, moves the
    /// selection to the first unlocked concrete character. On game versions without a
    /// Dread mode the name never matches and this only ever shows the button.</summary>
    internal static void Apply(NCustomRunScreen screen, string trigger) {
        try {
            var container = screen.GetNodeOrNull<Control>(RandomCharacterLobby.ButtonContainerPath);
            if (container is null) return;
            var buttons = RandomCharacterLobby.Buttons(container).ToList();
            var random = buttons.FirstOrDefault(b => b.IsRandom);
            if (random is null) return;
            var lobby = screen.Lobby;
            bool dread = lobby is not null && GameModeNames.IsDread(lobby.GameMode);
            var concrete = buttons.Where(b => !b.IsRandom).ToList();

            if (random.Visible == dread) {
                random.Visible = !dread;
                if (concrete.Count > 0) {
                    var last = concrete[^1];
                    last.FocusNeighborRight = dread ? last.GetPath() : random.GetPath();
                }
                TiLog.Info($"{RandomCharacterLobby.LogPrefix} Random button {(dread ? "hidden: the game forbids a Random pick in Dread" : "shown")} ({trigger})");
            }
            if (!dread || !random.IsSelected) return;

            var fallback = concrete.FirstOrDefault(b => !b.IsLocked);
            if (fallback is null) {
                TiLog.Warn($"{RandomCharacterLobby.LogPrefix} Random is selected in Dread and no unlocked character is available to move the selection to ({trigger})");
                return;
            }
            // A real Select(): the lobby must hold a concrete character before launch.
            fallback.Select();
            TiLog.Info($"{RandomCharacterLobby.LogPrefix} Random was selected in Dread; selection moved to {fallback.Character?.Id.Entry ?? "?"} ({trigger})");
        } catch (Exception e) { TiLog.Warn($"{RandomCharacterLobby.LogPrefix} Dread rule failed ({trigger}): {e.Message}"); }
    }

    /// <summary>True when launching now would hit the lobby's Dread throw: see
    /// <see cref="RandomCharacterRules.MustRefuseEmbark"/>. The lobby's player list is
    /// read reflectively because its element type differs per game branch. Any failure
    /// to read it answers false: never block Embark on a broken check.</summary>
    internal static bool MustRefuseEmbark(NCustomRunScreen screen) {
        try {
            var lobby = screen.Lobby;
            if (lobby is null) return false;
            if (AccessTools.Property(lobby.GetType(), "Players")?.GetValue(lobby) is not System.Collections.IEnumerable players) return false;
            var onRandom = new List<bool>();
            foreach (var player in players) onRandom.Add(RandomCharacterLobby.PlayerCharacter(player) is RandomCharacter);
            if (!RandomCharacterRules.MustRefuseEmbark(lobby.GameMode, onRandom)) return false;
            TiLog.Warn($"{RandomCharacterLobby.LogPrefix} Embark refused: the lobby is in Dread and {onRandom.Count(r => r)} player(s) are still on Random, which the game cannot launch; press Embark again once their selection has moved");
            return true;
        } catch (Exception e) {
            TiLog.Warn($"{RandomCharacterLobby.LogPrefix} Dread embark check failed; not blocking Embark: {e.Message}");
            return false;
        }
    }
}

/// <summary>Re-applies the Dread rule when the Custom Mode screen opens (every game
/// version; the game-mode dropdown's value persists on the cached screen node) and when
/// the lobby's game mode changes (GameModeChanged exists from game v0.113.0).</summary>
[HarmonyPatch]
internal static class RandomCharacterDreadRefreshPatch {
    private static readonly Lazy<IReadOnlyList<MethodBase>> Targets = new(() => {
        var found = new List<MethodBase>();
        foreach (var name in new[] { "OnSubmenuOpened", "GameModeChanged" }) {
            var m = MemberCompat.FirstMethod(typeof(NCustomRunScreen), Type.EmptyTypes, name);
            if (m is not null && m.DeclaringType == typeof(NCustomRunScreen)) found.Add(m);
        }
        return found;
    });

    private static bool Prepare(MethodBase? original) => original is not null || Targets.Value.Count > 0;

    private static IEnumerable<MethodBase> TargetMethods() => Targets.Value;

    private static void Postfix(NCustomRunScreen __instance, MethodBase __originalMethod) =>
        RandomCharacterDreadRule.Apply(__instance, __originalMethod.Name);
}

/// <summary>Backstop for the Dread rule: runs first on Embark, before vanilla disables
/// the screen's buttons and marks the lobby ready. Without it, any path that left Random
/// selected in Dread would end in the lobby's launch-time throw with no way back.</summary>
[HarmonyPatch]
internal static class RandomCharacterDreadEmbarkGuardPatch {
    private static readonly Lazy<MethodInfo?> Target = new(() =>
        MemberCompat.FirstMethod(typeof(NCustomRunScreen), new[] { typeof(NButton) }, "OnEmbarkPressed"));

    private static bool Prepare(MethodBase? original) => original is not null || Target.Value is not null;

    private static IEnumerable<MethodBase> TargetMethods() => new[] { Target.Value! };

    /// <summary>Returns false (Embark does nothing, the screen stays usable) while the
    /// mode is Dread and any lobby player is still on Random after the local selection
    /// has been moved. That is a remote player whose own client has not moved it yet;
    /// pressing Embark again a moment later goes through.</summary>
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NCustomRunScreen __instance) {
        RandomCharacterDreadRule.Apply(__instance, "OnEmbarkPressed");
        return !RandomCharacterDreadRule.MustRefuseEmbark(__instance);
    }
}
