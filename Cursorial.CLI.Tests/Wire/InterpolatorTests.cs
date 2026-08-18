using Cursorial.CLI.Wire;

using Xunit;

namespace Cursorial.Tests.CLI.Wire;

public class InterpolatorTests
{
    private static VariableBag Bag()
    {
        var bag = new VariableBag();
        bag.BindText("name", "release-v2");
        bag.BindSelection("picks", new[] { "alpha", "gamma" }, new[] { 0, 2 });
        return bag;
    }

    [Fact]
    public void SubstitutesAccessor()
    {
        var result = Interpolator.Apply(new[] { "confirm", "Tag {name}?" }, Bag());

        Assert.Equal(new[] { "confirm", "Tag release-v2?" }, result);
    }

    [Fact]
    public void UnboundAccessor_BecomesEmpty()
    {
        var result = Interpolator.Apply(new[] { "echo", "[{ghost}]" }, Bag());

        Assert.Equal("[]", result[1]);
    }

    [Fact]
    public void BraceEscapes()
    {
        var result = Interpolator.Apply(new[] { "echo", "{{name}}", "a{{b", "c}}d" }, Bag());

        Assert.Equal("{name}", result[1]);
        Assert.Equal("a{b", result[2]);
        Assert.Equal("c}d", result[3]);
    }

    [Fact]
    public void SubstitutedValue_IsNotReinterpolated()
    {
        var bag = Bag();
        bag.BindText("outer", "{name}"); // a value that looks like an accessor stays literal

        var result = Interpolator.Apply(new[] { "echo", "{outer}" }, bag);

        Assert.Equal("{name}", result[1]);
    }

    [Fact]
    public void MultipleAccessorsInOneToken()
    {
        var result = Interpolator.Apply(new[] { "echo", "{name}:{picks}" }, Bag());

        Assert.Equal("release-v2:alpha gamma", result[1]);
    }

    [Fact]
    public void IndexAccessor()
    {
        var result = Interpolator.Apply(new[] { "echo", "{picks.index}" }, Bag());

        Assert.Equal("0 2", result[1]);
    }

    [Fact]
    public void MultiSelection_SpaceJoins()
    {
        var result = Interpolator.Apply(new[] { "echo", "{picks}" }, Bag());

        Assert.Equal("alpha gamma", result[1]);
    }

    [Fact]
    public void ReturnsNewArray_NeverMutatesInput()
    {
        var input = new[] { "echo", "{name}" };
        var result = Interpolator.Apply(input, Bag());

        Assert.NotSame(input, result);
        Assert.Equal("{name}", input[1]);
    }

    [Fact]
    public void UnmatchedBraces_PassThroughLiterally()
    {
        var result = Interpolator.Apply(new[] { "echo", "a{b", "c}d", "{" }, Bag());

        Assert.Equal("a{b", result[1]);
        Assert.Equal("c}d", result[2]);
        Assert.Equal("{", result[3]);
    }

    [Fact]
    public void EmptyAccessor_BecomesEmpty()
    {
        var result = Interpolator.Apply(new[] { "echo", "a{}b" }, Bag());

        Assert.Equal("ab", result[1]);
    }

    [Fact]
    public void TokensWithoutBraces_PassThrough()
    {
        var result = Interpolator.Apply(new[] { "confirm", "plain", "--optional" }, Bag());

        Assert.Equal(new[] { "confirm", "plain", "--optional" }, result);
    }
}
