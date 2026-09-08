// src/Game/DecisionVotes/RelicTextRegistry.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 6.3: which relic ids get explanation text. Built-in ids come
/// from AuthorityLoc; other mods' relics are learned the first time they produce a
/// governed card reward and persisted as a JSON array of {id, mode}. BCL only.</summary>
public sealed class RelicTextRegistry {
    private sealed record Entry(string Id, string Mode);

    private readonly string _path;
    private readonly Dictionary<string, AuthorityMode> _learned = new(StringComparer.Ordinal);

    public static RelicTextRegistry? Instance { get; set; }

    public RelicTextRegistry(string learnedFilePath) {
        _path = learnedFilePath;
        try {
            if (File.Exists(_path)) {
                var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path)) ?? new();
                foreach (var e in entries)
                    if (Enum.TryParse<AuthorityMode>(e.Mode, out var m) && m is AuthorityMode.RemoveOne or AuthorityMode.Unskippable) _learned[e.Id] = m;
            }
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][card-scope] learned-relics file unreadable; starting empty: {ex.Message}"); }
    }

    public bool IsKnown(string relicId) => AuthorityLoc.BuiltInRelicIds.Contains(relicId) || _learned.ContainsKey(relicId);

    public bool TryGetLearnedMode(string relicId, out AuthorityMode mode) => _learned.TryGetValue(relicId, out mode);

    public void Learn(string relicId, AuthorityMode mode) {
        if (mode is not (AuthorityMode.RemoveOne or AuthorityMode.Unskippable)) return;
        if (IsKnown(relicId)) return;
        _learned[relicId] = mode;
        try {
            var list = new List<Entry>();
            foreach (var kv in _learned) list.Add(new Entry(kv.Key, kv.Value.ToString()));
            File.WriteAllText(_path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            TiLog.Info($"[SlayTheStreamer2][card-scope] learned relic {relicId} as {mode}; text will show from now on");
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][card-scope] could not persist learned relic {relicId}: {ex.Message}"); }
    }
}
