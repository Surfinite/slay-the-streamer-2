using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

[Collection("TiLog.Sink")]
public class BossVoteSeedTests {
    [Fact]
    public void Stable_SameInput_ReturnsSameValue() {
        int a = BossVoteSeed.Stable("seed1", 0);
        int b = BossVoteSeed.Stable("seed1", 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Stable_DifferentActIndex_ReturnsDifferentValue() {
        int a = BossVoteSeed.Stable("seed1", 0);
        int b = BossVoteSeed.Stable("seed1", 1);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Stable_DifferentSeed_ReturnsDifferentValue() {
        int a = BossVoteSeed.Stable("seed1", 0);
        int b = BossVoteSeed.Stable("seed2", 0);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Stable_NullSeed_DoesNotThrow_AndIsDeterministic() {
        int a = BossVoteSeed.Stable(null, 0);
        int b = BossVoteSeed.Stable(null, 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Stable_EmptySeed_DoesNotThrow_AndIsDeterministic() {
        int a = BossVoteSeed.Stable("", 0);
        int b = BossVoteSeed.Stable("", 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Stable_KnownValue_FnvOneACompliance() {
        // FNV-1a 32-bit of "abc" + actIndex 0, computed independently.
        // fnvOffset = unchecked((int)2166136261); fnvPrime = 16777619.
        // hash starts at fnvOffset.
        //   hash ^= 'a' (97); hash *= 16777619; (carries through int unchecked)
        //   hash ^= 'b' (98); hash *= 16777619;
        //   hash ^= 'c' (99); hash *= 16777619;
        //   hash ^= 0 (actIndex); hash *= 16777619;
        // Expected value verified offline using:
        //   python3 -c "h = 0x811c9dc5
        //                for c in 'abc': h ^= ord(c); h = (h * 0x01000193) & 0xFFFFFFFF
        //                h ^= 0; h = (h * 0x01000193) & 0xFFFFFFFF
        //                print(h if h < 0x80000000 else h - 0x100000000)"
        // Result: 1781783633 (= 0x6a33dc51)
        // Note: the plan spec cited -1424385571 but that is incorrect — both the
        // Python reference and a step-by-step trace confirm 1781783633. The intermediate
        // FNV-1a-32 of "abc" (0x1a47e90b = 440920331) matches the well-known reference
        // value, so the algorithm is correct.
        int v = BossVoteSeed.Stable("abc", 0);
        Assert.Equal(1781783633, v);
    }
}
