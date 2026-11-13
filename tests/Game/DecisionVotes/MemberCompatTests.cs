using System;
using System.Linq;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

// The shapes below mirror real game drift between the default branch and the
// Beta (v0.107.1 vs v0.113.0): a handler renamed while a dead method keeps the
// new name on the old build, a private field renamed, an optional parameter
// appended to a constructor, a return type changed.
public class MemberCompatTests {
#pragma warning disable CS0169, CS0414, IDE0051, IDE0052, IDE0060, CA1822
    private sealed class HasPressedAndReleased {
        private void Pressed(int x) { }
        private void Released(int x) { }
    }
    private sealed class HasReleasedOnly {
        private void Released(int x) { }
    }
    private sealed class HasPressedWithOtherArgs {
        private void Pressed(string x) { }
        private void Released(int x) { }
    }
    private sealed class PlayReturnsVoid { public void Play(string name) { } }
    private sealed class PlayReturnsInt { public int Play(string name) => 1; }
    private class BaseWithPrivates {
        private void Hidden(int x) { }
        private int _inherited = 3;
    }
    private sealed class Derived : BaseWithPrivates { }

    private sealed class HasBothFields { private int _runState = 1; private int _playerCollection = 2; }
    private sealed class HasOldFieldOnly { private int _playerCollection = 2; }

    private sealed class Owner { }
    private sealed class ThreeCtors {
        static ThreeCtors() { }
        public ThreeCtors(int count, Owner player) { }
        public ThreeCtors(string label, Owner player, object? extra = null) { }
        public ThreeCtors(int count) { }
    }
    private sealed class PlayerNamedButWrongType {
        public PlayerNamedButWrongType(int player) { }
    }

    private sealed class ExactOnly {
        public readonly int A; public readonly string B;
        public ExactOnly(int a, string b) { A = a; B = b; }
    }
    private sealed class OptionalTailOnly {
        public readonly int A; public readonly string B; public readonly object? Extra = "unset";
        public OptionalTailOnly(int a, string b, object? extra = null) { A = a; B = b; Extra = extra; }
    }
    private sealed class DeclaredDefault {
        public readonly int B;
        public DeclaredDefault(int a, int b = 7) { B = b; }
    }
    private sealed class ExactAndOptionalTail {
        public readonly string Used;
        public ExactAndOptionalTail(int a, string b) { Used = "exact"; }
        public ExactAndOptionalTail(int a, string b, object? extra = null) { Used = "tail"; }
    }
    private sealed class RequiredTail {
        public RequiredTail(int a, string b, int required) { }
    }

    private sealed class HasFlagParameter { public void Changed(object player, bool isResolution) { } }
    private sealed class LacksFlagParameter { public void Changed(object player) { } }
#pragma warning restore CS0169, CS0414, IDE0051, IDE0052, IDE0060, CA1822

    private static readonly Type[] OneInt = { typeof(int) };

    [Fact]
    public void FirstMethod_PrefersTheEarlierName_WhenBothExist() {
        var m = MemberCompat.FirstMethod(typeof(HasPressedAndReleased), OneInt, "Pressed", "Released");
        Assert.Equal("Pressed", m?.Name);
    }

    [Fact]
    public void FirstMethod_FallsBackToTheLaterName_WhenTheEarlierIsMissing() {
        var m = MemberCompat.FirstMethod(typeof(HasReleasedOnly), OneInt, "Pressed", "Released");
        Assert.Equal("Released", m?.Name);
    }

    [Fact]
    public void FirstMethod_ReturnsNull_WhenNoNameExists() {
        Assert.Null(MemberCompat.FirstMethod(typeof(HasReleasedOnly), OneInt, "Pressed", "Clicked"));
    }

    [Fact]
    public void FirstMethod_SkipsANameWhoseParametersDiffer() {
        var m = MemberCompat.FirstMethod(typeof(HasPressedWithOtherArgs), OneInt, "Pressed", "Released");
        Assert.Equal("Released", m?.Name);
    }

    [Theory]
    [InlineData(typeof(PlayReturnsVoid))]
    [InlineData(typeof(PlayReturnsInt))]
    public void FirstMethod_MatchesWhateverTheReturnType(Type type) {
        var m = MemberCompat.FirstMethod(type, new[] { typeof(string) }, "Play");
        Assert.NotNull(m);
    }

    [Fact]
    public void FirstMethod_FindsAPrivateMethodDeclaredOnABaseType() {
        var m = MemberCompat.FirstMethod(typeof(Derived), OneInt, "Hidden");
        Assert.Equal(typeof(BaseWithPrivates), m?.DeclaringType);
    }

    [Fact]
    public void FirstField_PrefersTheEarlierName_WhenBothExist() {
        var f = MemberCompat.FirstField(typeof(HasBothFields), "_runState", "_playerCollection");
        Assert.Equal("_runState", f?.Name);
    }

    [Fact]
    public void FirstField_FallsBackToTheLaterName_WhenTheEarlierIsMissing() {
        var f = MemberCompat.FirstField(typeof(HasOldFieldOnly), "_runState", "_playerCollection");
        Assert.Equal("_playerCollection", f?.Name);
    }

    [Fact]
    public void FirstField_ReturnsNull_WhenNoNameExists() {
        Assert.Null(MemberCompat.FirstField(typeof(HasOldFieldOnly), "_runState", "_players"));
    }

    [Fact]
    public void FirstField_FindsAPrivateFieldDeclaredOnABaseType() {
        var f = MemberCompat.FirstField(typeof(Derived), "_inherited");
        Assert.Equal(typeof(BaseWithPrivates), f?.DeclaringType);
    }

    [Fact]
    public void ConstructorsWithParameter_ReturnsEveryInstanceConstructorTakingIt() {
        var ctors = MemberCompat.ConstructorsWithParameter(typeof(ThreeCtors), "player", typeof(Owner));
        Assert.Equal(new[] { 2, 3 }, ctors.Select(c => c.GetParameters().Length).OrderBy(n => n).ToArray());
        Assert.All(ctors, c => Assert.False(c.IsStatic));
    }

    [Fact]
    public void ConstructorsWithParameter_IgnoresAParameterOfAnotherType() {
        Assert.Empty(MemberCompat.ConstructorsWithParameter(typeof(PlayerNamedButWrongType), "player", typeof(Owner)));
    }

    [Fact]
    public void Construct_UsesTheExactConstructor() {
        var made = (ExactOnly)MemberCompat.Construct(typeof(ExactOnly), new[] { typeof(int), typeof(string) }, new object?[] { 4, "four" });
        Assert.Equal((4, "four"), (made.A, made.B));
    }

    [Fact]
    public void Construct_PassesNullForAnAppendedOptionalReferenceParameter() {
        var made = (OptionalTailOnly)MemberCompat.Construct(typeof(OptionalTailOnly), new[] { typeof(int), typeof(string) }, new object?[] { 4, "four" });
        Assert.Equal((4, "four"), (made.A, made.B));
        Assert.Null(made.Extra);
    }

    [Fact]
    public void Construct_PassesTheDeclaredDefaultForAnAppendedOptionalParameter() {
        var made = (DeclaredDefault)MemberCompat.Construct(typeof(DeclaredDefault), new[] { typeof(int) }, new object?[] { 1 });
        Assert.Equal(7, made.B);
    }

    [Fact]
    public void Construct_PrefersTheExactConstructorOverOneWithAnOptionalTail() {
        var made = (ExactAndOptionalTail)MemberCompat.Construct(typeof(ExactAndOptionalTail), new[] { typeof(int), typeof(string) }, new object?[] { 1, "x" });
        Assert.Equal("exact", made.Used);
    }

    [Fact]
    public void Construct_Throws_WhenTheOnlyCandidateNeedsARequiredExtraArgument() {
        Assert.Throws<MissingMethodException>(() =>
            MemberCompat.Construct(typeof(RequiredTail), new[] { typeof(int), typeof(string) }, new object?[] { 1, "x" }));
    }

    [Fact]
    public void Construct_Throws_WhenTheLeadingParameterTypesDiffer() {
        Assert.Throws<MissingMethodException>(() =>
            MemberCompat.Construct(typeof(ExactOnly), new[] { typeof(string), typeof(int) }, new object?[] { "x", 1 }));
    }

    [Fact]
    public void HasParameter_IsTrue_WhenNameAndTypeMatch() {
        var m = typeof(HasFlagParameter).GetMethod("Changed");
        Assert.True(MemberCompat.HasParameter(m, "isResolution", typeof(bool)));
    }

    [Fact]
    public void HasParameter_IsFalse_WhenTheParameterWasRemoved() {
        var m = typeof(LacksFlagParameter).GetMethod("Changed");
        Assert.False(MemberCompat.HasParameter(m, "isResolution", typeof(bool)));
    }

    [Fact]
    public void HasParameter_IsFalse_ForAMissingMethod() {
        Assert.False(MemberCompat.HasParameter(null, "isResolution", typeof(bool)));
    }
}
