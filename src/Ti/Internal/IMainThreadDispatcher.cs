using System;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>
/// Marshals callbacks onto a target thread (typically the Godot main thread
/// in production; the calling thread for tests). Decouples Ti/* from Godot.
/// </summary>
public interface IMainThreadDispatcher {
    /// <summary>Queue <paramref name="action"/> for execution on the dispatcher's target thread.</summary>
    void Post(Action action);

    /// <summary>Awaits processing of all currently-queued actions. For ImmediateDispatcher this is a no-op.</summary>
    Task DrainAsync();
}
