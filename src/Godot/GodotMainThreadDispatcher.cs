using System;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Godot;

/// <summary>
/// IMainThreadDispatcher implementation backed by a registered DispatcherAutoload
/// Node. Post forwards to the autoload's CallDeferred-based queue.
/// </summary>
public sealed class GodotMainThreadDispatcher : IMainThreadDispatcher {
    private DispatcherAutoload? _autoload;

    public void SetAutoload(DispatcherAutoload a) => _autoload = a;

    public void Post(Action action) =>
        (_autoload ?? throw new InvalidOperationException(
            "GodotMainThreadDispatcher.Post called before SetAutoload."))
            .Post(action);

    /// <summary>
    /// Barrier-Post: completes after previously-posted actions have run.
    /// Does NOT recursively drain actions posted during the drain. Plan A
    /// production code does not call DrainAsync (verified by grep of src/);
    /// only test code uses it.
    /// </summary>
    public Task DrainAsync() {
        var tcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => tcs.TrySetResult());
        return tcs.Task;
    }
}
