using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Helpers;        // AddChildSafely
using MegaCrit.Sts2.Core.Nodes.Vfx;      // NSpeechBubbleVfx
using MegaCrit.Sts2.Core.Saves;          // SaveManager, FastModeType
using MegaCrit.Sts2.Core.Settings;       // VfxColor
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// voter-names: while a voter's name is on a living enemy, that voter's chat
/// messages replay as vanilla speech bubbles from the enemy. Matching is by
/// exact VoterKey (robust across Jr./Roman decorations and both platforms —
/// deliberately better than StS1's name-string comparison). Chat events fire
/// on background threads; everything Godot-facing is marshalled through the
/// main-thread dispatcher. Pure cosmetics; every path try/catch → Warn.
/// </summary>
internal static class VoterSpeechPatch {
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(8);
    private static readonly Dictionary<string, DateTimeOffset> _lastBubbleByVoter = new();
    private static IMainThreadDispatcher? _dispatcher;

    public static void Attach(IChatConsumer chat, IMainThreadDispatcher dispatcher) {
        _dispatcher = dispatcher;
        chat.MessageReceived += OnMessage;
    }

    private static void OnMessage(object? sender, ChatMessage msg) {
        try {
            var settings = ModSettings.Current;
            if (settings is null || !settings.NamedEnemiesSpeak || !settings.NameEnemiesAfterVoters) return;
            var dispatcher = _dispatcher;
            if (dispatcher is null) return;

            var text = BubbleText.Sanitize(msg.Text);
            if (text is null) return;
            var voterKey = msg.VoterKey;

            // Cooldown check on the chat thread (cheap, racy-tolerant: worst
            // case one extra bubble). The dictionary is only mutated here.
            lock (_lastBubbleByVoter) {
                var now = DateTimeOffset.UtcNow;
                if (_lastBubbleByVoter.TryGetValue(voterKey, out var last) && now - last < Cooldown) return;
                _lastBubbleByVoter[voterKey] = now;
            }

            dispatcher.Post(() => ShowBubbleOnMainThread(voterKey, text));
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][voter-names] bubble handler failed: {ex.Message}");
        }
    }

    private static void ShowBubbleOnMainThread(string voterKey, string text) {
        try {
            if (OverlayOcclusion.IsOccludingOverlayVisible()) return;   // popup up: bubble would be buried
            foreach (var (node, key) in VoterNamesPatch.NamedLivingCreatures()) {
                if (key != voterKey) continue;
                var creature = node.Entity;
                if (creature is null || creature.IsDead) return;

                bool fast = SaveManager.Instance.PrefsSave.FastMode == FastModeType.Fast;
                double seconds = Math.Max(0.5, BubbleText.RawCharCount(text) * (fast ? 0.10 : 0.12));
                var vfx = NSpeechBubbleVfx.Create(text, creature, seconds, VfxColor.White);
                if (vfx != null) {
                    creature.GetVfxContainer()?.AddChildSafely(vfx);
                    TiLog.Info($"[SlayTheStreamer2][voter-names] bubble from '{voterKey}' ({text.Length} chars)");
                }
                return;   // first match wins; after pool exhaustion the same key can be on several enemies — any of them speaking is acceptable
            }
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][voter-names] bubble create failed: {ex.Message}");
        }
    }
}
