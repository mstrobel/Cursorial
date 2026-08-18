using Cursorial.CLI.Wire;

using Xunit;

namespace Cursorial.Tests.CLI.Wire;

public class PipelineParserTests
{
    [Fact]
    public void NoSeparator_SingleStep()
    {
        var steps = PipelineParser.Split(new[] { "choose", "a", "b" });

        var step = Assert.Single(steps);
        Assert.Equal(new[] { "choose", "a", "b" }, step);
    }

    [Fact]
    public void SplitsOnSeparator()
    {
        var steps = PipelineParser.Split(new[] { "choose", "a", "++", "input", "--var", "x", "++", "confirm", "ok" });

        Assert.Equal(3, steps.Count);
        Assert.Equal(new[] { "choose", "a" }, steps[0]);
        Assert.Equal(new[] { "input", "--var", "x" }, steps[1]);
        Assert.Equal(new[] { "confirm", "ok" }, steps[2]);
    }

    [Fact]
    public void SeparatorInsideLargerToken_DoesNotSplit()
    {
        var steps = PipelineParser.Split(new[] { "choose", "a++b", "c++" });

        var step = Assert.Single(steps);
        Assert.Equal(new[] { "choose", "a++b", "c++" }, step);
    }

    // Decided: the separator is recognized at the top level of the argv scan regardless of a
    // step-local `--`. The shell user writes `curio choose -- a b ++ confirm ok` and expects the
    // split; `--` scoping is StepArgs' business within each step.
    [Fact]
    public void SeparatorAfterDoubleDash_StillSplits()
    {
        var steps = PipelineParser.Split(new[] { "choose", "--", "a", "b", "++", "confirm", "ok" });

        Assert.Equal(2, steps.Count);
        Assert.Equal(new[] { "choose", "--", "a", "b" }, steps[0]);
        Assert.Equal(new[] { "confirm", "ok" }, steps[1]);
    }

    [Fact]
    public void LeadingSeparator_Throws()
        => Assert.Throws<UsageException>(() => PipelineParser.Split(new[] { "++", "choose", "a" }));

    [Fact]
    public void TrailingSeparator_Throws()
        => Assert.Throws<UsageException>(() => PipelineParser.Split(new[] { "choose", "a", "++" }));

    [Fact]
    public void DoubleSeparator_Throws()
        => Assert.Throws<UsageException>(() => PipelineParser.Split(new[] { "choose", "a", "++", "++", "confirm" }));

    [Fact]
    public void SepOverride_SpaceForm()
    {
        var steps = PipelineParser.Split(new[] { "--sep", ":::", "echo", "++", ":::", "confirm", "ok" });

        Assert.Equal(2, steps.Count);
        Assert.Equal(new[] { "echo", "++" }, steps[0]); // literal ++ survives under the override
        Assert.Equal(new[] { "confirm", "ok" }, steps[1]);
    }

    [Fact]
    public void SepOverride_EqualsForm()
    {
        var steps = PipelineParser.Split(new[] { "--sep=:::", "choose", "a", ":::", "confirm" });

        Assert.Equal(2, steps.Count);
        Assert.Equal(new[] { "choose", "a" }, steps[0]);
        Assert.Equal(new[] { "confirm" }, steps[1]);
    }

    [Fact]
    public void SepOverride_MissingToken_Throws()
        => Assert.Throws<UsageException>(() => PipelineParser.Split(new[] { "--sep" }));

    [Theory]
    [InlineData("--sep=")]
    [InlineData("--sep", "")]
    public void SepOverride_EmptyToken_Throws(params string[] argv)
        => Assert.Throws<UsageException>(() => PipelineParser.Split(argv));

    [Fact]
    public void SepAfterFirstStepName_IsNotGlobal()
    {
        // `--sep` past the front of argv is an ordinary step option; the default separator still splits.
        var steps = PipelineParser.Split(new[] { "choose", "--sep", ":::", "++", "confirm" });

        Assert.Equal(2, steps.Count);
        Assert.Equal(new[] { "choose", "--sep", ":::" }, steps[0]);
        Assert.Equal(new[] { "confirm" }, steps[1]);
    }

    [Fact]
    public void CustomSeparatorArgument()
    {
        var steps = PipelineParser.Split(new[] { "choose", "a", "@@", "confirm" }, "@@");

        Assert.Equal(2, steps.Count);
        Assert.Equal(new[] { "choose", "a" }, steps[0]);
        Assert.Equal(new[] { "confirm" }, steps[1]);
    }

    [Fact]
    public void EmptyArgv_NoSteps()
        => Assert.Empty(PipelineParser.Split(System.Array.Empty<string>()));
}
