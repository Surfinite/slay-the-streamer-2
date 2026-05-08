using System;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>
/// Synchronous pass-through dispatcher. Executes actions on the calling thread.
/// Used by tests and by non-Godot consumers (the IRC fixture-generator tool, future
/// headless integration tests). Public on purpose — see Optional Enhancement #10.
/// </summary>
public sealed class ImmediateDispatcher : IMainThreadDispatcher {
    public void Post(Action action) => action();
    public Task DrainAsync() => Task.CompletedTask;
}
