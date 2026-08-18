using Cursorial.CLI.Wire;

using Xunit;

namespace Cursorial.Tests.CLI.Wire;

public class StepArgsTests
{
    [Fact]
    public void ParsesCommandletName()
    {
        var step = StepArgs.Parse(new[] { "choose" });

        Assert.Equal("choose", step.CommandletName);
        Assert.Empty(step.Positionals);
    }

    [Fact]
    public void MissingCommandletName_Throws()
        => Assert.Throws<UsageException>(() => StepArgs.Parse(System.Array.Empty<string>()));

    [Fact]
    public void OptionAsFirstToken_Throws()
        => Assert.Throws<UsageException>(() => StepArgs.Parse(new[] { "--var", "x" }));

    [Fact]
    public void OptionWithSpaceSeparatedValue()
    {
        var step = StepArgs.Parse(new[] { "input", "--var", "name" });

        Assert.Equal("name", step.GetOption("var"));
        Assert.True(step.HasFlag("var")); // presence reported for valued options too
        Assert.Empty(step.Positionals);
    }

    [Fact]
    public void OptionWithEqualsValue()
    {
        var step = StepArgs.Parse(new[] { "input", "--placeholder=type here" });

        Assert.Equal("type here", step.GetOption("placeholder"));
    }

    [Fact]
    public void EqualsWithEmptyValue()
    {
        var step = StepArgs.Parse(new[] { "input", "--default=" });

        Assert.Equal("", step.GetOption("default"));
        Assert.True(step.HasFlag("default"));
    }

    [Fact]
    public void EqualsValueMayStartWithDash()
    {
        var step = StepArgs.Parse(new[] { "input", "--default=-5" });

        Assert.Equal("-5", step.GetOption("default"));
    }

    [Fact]
    public void BareFlag()
    {
        var step = StepArgs.Parse(new[] { "confirm", "--optional" });

        Assert.True(step.HasFlag("optional"));
        Assert.Null(step.GetOption("optional"));
    }

    [Fact]
    public void FlagFollowedByOption_StaysAFlag()
    {
        var step = StepArgs.Parse(new[] { "choose", "--multi", "--var", "picks" });

        Assert.True(step.HasFlag("multi"));
        Assert.Null(step.GetOption("multi"));
        Assert.Equal("picks", step.GetOption("var"));
    }

    // Schema-less parsing is greedy: a flag directly followed by a positional takes it as a value.
    // HasFlag still reports presence, so flag semantics survive; positionals go first or after `--`.
    [Fact]
    public void FlagFollowedByPositional_GreedilyTakesValue()
    {
        var step = StepArgs.Parse(new[] { "confirm", "--optional", "Proceed?" });

        Assert.True(step.HasFlag("optional"));
        Assert.True(step.Optional);
        Assert.Equal("Proceed?", step.GetOption("optional"));
        Assert.Empty(step.Positionals);
    }

    [Fact]
    public void DoubleDash_EndsOptionParsing()
    {
        var step = StepArgs.Parse(new[] { "choose", "--var", "pick", "--", "--not-an-option", "-x", "b" });

        Assert.Equal("pick", step.GetOption("var"));
        Assert.Equal(new[] { "--not-an-option", "-x", "b" }, step.Positionals);
        Assert.False(step.HasFlag("not-an-option"));
    }

    [Fact]
    public void PositionalsMixedWithOptions()
    {
        var step = StepArgs.Parse(new[] { "choose", "apple", "banana", "--var", "fruit", "cherry" });

        Assert.Equal(new[] { "apple", "banana", "cherry" }, step.Positionals);
        Assert.Equal("fruit", step.GetOption("var"));
    }

    [Fact]
    public void RepeatableOption_GetAll_PreservesOrder()
    {
        var step = StepArgs.Parse(new[] { "style", "--stamp", "app-error", "--stamp=app-bold", "--stamp", "app-dim" });

        Assert.Equal(new[] { "app-error", "app-bold", "app-dim" }, step.GetAll("stamp"));
        Assert.Equal("app-dim", step.GetOption("stamp")); // last value wins
    }

    [Fact]
    public void GetAll_AbsentOption_Empty()
        => Assert.Empty(StepArgs.Parse(new[] { "confirm" }).GetAll("stamp"));

    [Fact]
    public void LoneShortOption_Throws()
        => Assert.Throws<UsageException>(() => StepArgs.Parse(new[] { "choose", "-x" }));

    [Fact]
    public void SingleDash_IsPositional()
    {
        var step = StepArgs.Parse(new[] { "style", "-" });

        Assert.Equal(new[] { "-" }, step.Positionals);
    }

    [Fact]
    public void SingleDash_CanBeAnOptionValue()
    {
        var step = StepArgs.Parse(new[] { "style", "--file", "-" });

        Assert.Equal("-", step.GetOption("file"));
    }

    [Fact]
    public void EmptyOptionName_Throws()
        => Assert.Throws<UsageException>(() => StepArgs.Parse(new[] { "choose", "--=value" }));

    [Fact]
    public void ConvenienceAccessors()
    {
        var step = StepArgs.Parse(new[] { "confirm", "--var", "sure", "--optional", "--default", "yes" });

        Assert.Equal("sure", step.Var);
        Assert.True(step.Optional);
        Assert.Equal("yes", step.Default);
    }

    [Fact]
    public void ConvenienceAccessors_Absent()
    {
        var step = StepArgs.Parse(new[] { "confirm" });

        Assert.Null(step.Var);
        Assert.False(step.Optional);
        Assert.Null(step.Default);
    }

    [Fact]
    public void UnknownOptionsAreCollected_NotRejected()
    {
        // Unknown-option validation is per-commandlet, later; StepArgs just collects.
        var step = StepArgs.Parse(new[] { "choose", "--definitely-not-real", "v" });

        Assert.Equal("v", step.GetOption("definitely-not-real"));
    }
}
