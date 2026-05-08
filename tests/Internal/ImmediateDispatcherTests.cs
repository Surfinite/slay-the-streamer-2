using System;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Internal;

public class ImmediateDispatcherTests {
    [Fact]
    public void PostInvokesActionOnCallingThread() {
        var dispatcher = new ImmediateDispatcher();
        var threadSeen = -1;
        dispatcher.Post(() => threadSeen = Thread.CurrentThread.ManagedThreadId);
        Assert.Equal(Thread.CurrentThread.ManagedThreadId, threadSeen);
    }

    [Fact]
    public void PostExecutesSynchronously() {
        var dispatcher = new ImmediateDispatcher();
        var ran = false;
        dispatcher.Post(() => ran = true);
        Assert.True(ran, "action ran synchronously inside Post");
    }

    [Fact]
    public async Task DrainAsyncCompletesImmediately() {
        var dispatcher = new ImmediateDispatcher();
        var task = dispatcher.DrainAsync();
        Assert.True(task.IsCompletedSuccessfully, "DrainAsync should complete synchronously");
        await task;   // sanity: never throws, never blocks
    }

    [Fact]
    public void Post_NullAction_ThrowsArgumentNullException() {
        var d = new ImmediateDispatcher();
        Assert.Throws<ArgumentNullException>(() => d.Post(null!));
    }
}
