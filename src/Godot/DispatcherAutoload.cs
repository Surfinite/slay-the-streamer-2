using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace SlayTheStreamer2.Godot;

/// <summary>
/// Runtime-attached Node that hops arbitrary Actions onto the Godot main thread
/// via CallDeferred. Despite the name, this is NOT a Godot project autoload
/// configured in ProjectSettings; the class name preserves the connection to
/// notes/06 item #6 (validate autoload registration from a mod assembly).
/// </summary>
public partial class DispatcherAutoload : Node {
    public void Post(Action action) {
        ArgumentNullException.ThrowIfNull(action);
        // String literal "Run" instead of MethodName.Run for portability across
        // Godot source-generator versions.
        // Explicit (Action) cast avoids overload-resolution ambiguity in Callable.From.
        CallDeferred("Run", Callable.From((Action)action));
    }

    private void Run(Callable callable) {
        try {
            callable.Call();
        } catch (Exception e) {
            Log.Error($"[slay_the_streamer_2] DispatcherAutoload.Run threw: {e}");
        }
    }
}
