// tests/Game/Content/SealedNeowLocTests.cs
using SlayTheStreamer2.Game.Content;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.Content;

public class SealedNeowLocTests {
    [Fact]
    public void Provides_DoomedKeys() {
        Assert.True(SealedNeowLoc.TryProvide("enchantments", "STREAMER_DOOMED.title", out var title));
        Assert.Equal("Doomed", title);
        Assert.True(SealedNeowLoc.TryProvide("enchantments", "STREAMER_DOOMED.description", out var desc));
        Assert.Equal("Apply [blue]{Amount}[/blue] [gold]Doom[/gold] to you when played.", desc);
        Assert.True(SealedNeowLoc.TryProvide("enchantments", "STREAMER_DOOMED.extraCardText", out var extra));
        Assert.Equal("Apply {Amount} [gold]Doom[/gold] to you.", extra);
    }

    [Fact]
    public void DoesNotProvide_OtherKeys() {
        Assert.False(SealedNeowLoc.TryProvide("enchantments", "INKY.title", out _));
        Assert.False(SealedNeowLoc.TryProvide("relics", "STREAMER_DOOMED.title", out _));
    }

    [Fact]
    public void Replaces_TalismanKeys_WithConstants() {
        Assert.True(SealedNeowLoc.TryReplace("relics", "NEOWS_TALISMAN.description", out var d));
        Assert.Equal("Upon pickup, [gold]Upgrade[/gold] [blue]2[/blue] random cards. They become [red]Doomed[/red]: apply [red]3[/red] [gold]Doom[/gold] to you when played.", d);
        Assert.True(SealedNeowLoc.TryReplace("relics", "NEOWS_TALISMAN.eventDescription", out var e));
        Assert.Equal("[gold]Upgrade[/gold] [blue]2[/blue] random cards. They become [red]Doomed[/red]: apply [red]3[/red] [gold]Doom[/gold] to you when played.", e);
        Assert.False(SealedNeowLoc.TryReplace("relics", "NEOWS_TALISMAN.flavor", out _));
        Assert.False(SealedNeowLoc.TryReplace("relics", "POMANDER.description", out _));
    }

    [Fact]
    public void NoEmDashes() {
        foreach (var (t, k) in new[] { ("enchantments", "STREAMER_DOOMED.title"), ("enchantments", "STREAMER_DOOMED.description"), ("enchantments", "STREAMER_DOOMED.extraCardText") }) {
            SealedNeowLoc.TryProvide(t, k, out var s); Assert.DoesNotContain("—", s);
        }
        SealedNeowLoc.TryReplace("relics", "NEOWS_TALISMAN.description", out var d); Assert.DoesNotContain("—", d);
    }
}
