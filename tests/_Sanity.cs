using Xunit;

namespace SlayTheStreamer2.Tests;

public class _Sanity {
    [Fact]
    public void TestRunnerIsAlive() {
        Assert.Equal(2, 1 + 1);
    }
}
