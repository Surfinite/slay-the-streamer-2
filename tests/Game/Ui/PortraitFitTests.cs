using System.Numerics;
using SlayTheStreamer2.Game.Ui;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.Ui;

public class PortraitFitTests {
    [Theory]
    [InlineData(100f, 100f, 256f, 256f, 1f)]     // smaller than slot → no upscale
    [InlineData(256f, 256f, 256f, 256f, 1f)]     // exact match → 1.0
    [InlineData(512f, 256f, 256f, 256f, 0.5f)]   // wider than slot → X-limited
    [InlineData(256f, 512f, 256f, 256f, 0.5f)]   // taller than slot → Y-limited
    [InlineData(0f,   0f,   256f, 256f, 1f)]     // zero bounds → fit=1.0 (MathF.Max floor)
    [InlineData(-1f,  -1f,  256f, 256f, 1f)]     // negative bounds → fit=1.0 (MathF.Max floor)
    public void ComputeFitScale_ReturnsExpected(
        float boundsX, float boundsY,
        float slotX, float slotY,
        float expected) {
        var fit = PortraitFit.ComputeFitScale(new Vector2(boundsX, boundsY), new Vector2(slotX, slotY));
        Assert.Equal(expected, fit, precision: 4);
    }
}
