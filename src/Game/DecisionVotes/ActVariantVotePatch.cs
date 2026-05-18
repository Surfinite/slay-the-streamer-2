using System;
using System.Collections.Generic;
using System.Diagnostics;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

// Full Harmony attribute and Prefix/PrefixContinue/HandleVoteAsync/Resume
// land in Tasks 8-9. This file currently hosts only the pre-warm helper.
internal static partial class ActVariantVotePatch {

    /// <summary>
    /// Synchronously pre-warms each candidate variant's full layered combat
    /// backdrop scene. For each candidate:
    /// 1. Constructs BackgroundAssets(option.Key, rng), which walks
    ///    res://scenes/backgrounds/&lt;key&gt;/layers/ via DirAccess and picks
    ///    one random bg layer per layer group + an optional fg layer.
    /// 2. Pre-loads the root BackgroundScenePath via
    ///    PreloadManager.Cache.GetScene(...) — verified sync per spike #1.
    /// 3. Pre-loads each layer's .tscn via Cache.GetScene.
    ///
    /// Returns (a) an ActVariantPrewarmResult with the L1/L3 mode +
    /// per-candidate Succeeded/Total counts + ElapsedMs for telemetry, and
    /// (b) a parallel BackgroundAssets?[] (null entry = that variant's
    /// preload failed). The BackgroundAssets array is patch-side only
    /// because BackgroundAssets is a MegaCrit type.
    ///
    /// L3 fallback policy (all-or-nothing per v3 spec): forceL3 OR any load
    /// failure → mode=L3Fallback. The popup's L3 path renders a hex
    /// ColorRect + title text, no scene instantiation.
    /// </summary>
    internal static ActVariantPrewarmResult PreWarmAssets(
            IReadOnlyList<ActVariantOption> candidates,
            bool forceL3,
            Rng rng,
            out BackgroundAssets?[] backgroundAssetsByCandidate) {
        var sw = Stopwatch.StartNew();
        backgroundAssetsByCandidate = new BackgroundAssets?[candidates.Count];

        if (forceL3) {
            sw.Stop();
            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] pre-warm: 0/0 assets in {sw.ElapsedMilliseconds}ms (mode=L3, reason=ForceL3PopupFallback)");
            return new ActVariantPrewarmResult(ActVariantPopupMode.L3Fallback, 0, 0, sw.ElapsedMilliseconds);
        }

        int succeeded = 0;
        int total = 0;
        bool allOk = true;

        for (int i = 0; i < candidates.Count; i++) {
            var option = candidates[i];
            BackgroundAssets? bg = null;
            try {
                bg = new BackgroundAssets(option.Key, rng);
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] BackgroundAssets construction failed for {option.Key}: {ex.Message}");
                allOk = false;
                continue;
            }

            // Root scene
            total++;
            try {
                _ = PreloadManager.Cache.GetScene(bg.BackgroundScenePath);
                succeeded++;
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] preload failed for {option.Key} root ({bg.BackgroundScenePath}): {ex.Message}");
                allOk = false;
                continue;
            }

            // BG layers
            bool layersOk = true;
            foreach (var layerPath in bg.BgLayers) {
                total++;
                try {
                    _ = PreloadManager.Cache.GetScene(layerPath);
                    succeeded++;
                } catch (Exception ex) {
                    TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] preload failed for {option.Key} layer ({layerPath}): {ex.Message}");
                    layersOk = false;
                }
            }

            // Optional FG layer
            if (!string.IsNullOrEmpty(bg.FgLayer)) {
                total++;
                try {
                    _ = PreloadManager.Cache.GetScene(bg.FgLayer);
                    succeeded++;
                } catch (Exception ex) {
                    TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] preload failed for {option.Key} fg ({bg.FgLayer}): {ex.Message}");
                    layersOk = false;
                }
            }

            if (layersOk) {
                backgroundAssetsByCandidate[i] = bg;
            } else {
                allOk = false;
            }
        }

        sw.Stop();
        var mode = allOk && succeeded == total && total > 0
            ? ActVariantPopupMode.L1Textures
            : ActVariantPopupMode.L3Fallback;
        var reason = mode == ActVariantPopupMode.L1Textures
            ? "all assets loaded"
            : total == 0 ? "no paths resolved"
            : $"{succeeded}/{total} assets loaded";

        // On L3 fallback, null out the parallel array so the popup doesn't
        // attempt to instantiate any factories. (Per all-or-nothing policy.)
        if (mode == ActVariantPopupMode.L3Fallback) {
            for (int i = 0; i < backgroundAssetsByCandidate.Length; i++) {
                backgroundAssetsByCandidate[i] = null;
            }
        }

        TiLog.Info($"[SlayTheStreamer2][act-variant-vote] pre-warm: {succeeded}/{total} assets in {sw.ElapsedMilliseconds}ms (mode={mode}, reason={reason})");
        return new ActVariantPrewarmResult(mode, succeeded, total, sw.ElapsedMilliseconds);
    }
}
