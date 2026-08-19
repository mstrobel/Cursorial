using Cursorial.CLI;
using Cursorial.CLI.Wire;

namespace Cursorial.Tests.CLI;

/// <summary>
/// The leading-globals parser (<see cref="Runner.TakeGlobalOptions"/>): --retain in both forms with
/// strict values, order-independence among globals (the historic missing-`continue` crash/leak), bare-flag
/// diagnostics, the CURIO_RETAIN environment twin, and the non-interactive lines emit matching the
/// interactive final-step shape.
/// </summary>
public class GlobalArgsTests
{
    private static Runner.GlobalArgs Parse(params string[] argv) { Runner.TakeGlobalOptions(argv, out var g); return g; }

    [Theory] // expected as string: the enum is internal, and xunit signatures must stay public
    [InlineData("--retain=all", "All")]
    [InlineData("--retain=a", "All")]
    [InlineData("--retain=final", "Final")]
    [InlineData("--retain=f", "Final")]
    [InlineData("--retain=none", "None")]
    [InlineData("--retain=n", "None")]
    public void Retain_EqualsForm_Parses(string flag, string expected)
        => Assert.Equal(expected, Parse(flag, "choose", "x").Retain.ToString());

    [Fact]
    public void Retain_SpaceForm_Parses()
        => Assert.Equal(Runner.RetainMode.Final, Parse("--retain", "final", "choose", "x").Retain);

    [Fact]
    public void Retain_InvalidValue_IsUsageError_NotSilentNone()
    {
        var ex = Assert.Throws<UsageException>(() => Parse("--retain=finall", "choose", "x"));
        Assert.Contains("none, all, or final", ex.Message);
    }

    [Fact]
    public void Retain_BareFlagAtEnd_IsUsageError_NotALeakedStepArg()
    {
        var ex = Assert.Throws<UsageException>(() => Parse("--retain"));
        Assert.Contains("requires a value", ex.Message);
    }

    [Fact]
    public void Emit_BareFlagAtEnd_IsUsageError()
        => Assert.Throws<UsageException>(() => Parse("--emit"));

    [Fact]
    public void Retain_FollowedByAnotherGlobal_BothParse()
    {
        // The missing-`continue` regression: --no-caps-cache used to leak into the step argv.
        var rest = Runner.TakeGlobalOptions(["--retain=all", "--no-caps-cache", "choose", "x"], out var g);
        Assert.Equal(Runner.RetainMode.All, g.Retain);
        Assert.True(g.NoCapsCache);
        Assert.Equal(["choose", "x"], rest);
    }

    [Fact]
    public void Retain_AsTheLastToken_DoesNotCrash()
    {
        // The missing-`continue` crash: the fall-through read past the spliced array (exit 134).
        var rest = Runner.TakeGlobalOptions(["--emit=json", "--retain=all"], out var g);
        Assert.Equal(EmitFormat.Json, g.Format);
        Assert.Equal(Runner.RetainMode.All, g.Retain);
        Assert.Empty(rest);
    }

    [Fact]
    public void Retain_EnvTwin_SeedsDefault_AndFlagWins()
    {
        try
        {
            Environment.SetEnvironmentVariable("CURIO_RETAIN", "final");
            Assert.Equal(Runner.RetainMode.Final, Parse("choose", "x").Retain);
            Assert.Equal(Runner.RetainMode.All, Parse("--retain=all", "choose", "x").Retain); // the flag wins
            Environment.SetEnvironmentVariable("CURIO_RETAIN", "bogus");
            Assert.Equal(Runner.RetainMode.None, Parse("choose", "x").Retain); // env is LENIENT, unlike the flag
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURIO_RETAIN", null);
        }
    }

    [Fact]
    public void NonInteractive_Lines_EmitsTheFinalStepOnly_MatchingInteractive()
    {
        var buffer = new StringWriter();
        var stdout = Console.Out;
        try
        {
            Console.SetOut(buffer);
            var code = Runner.RunNonInteractive(
                [["input", "--var", "a", "--default", "first"], ["input", "--var", "b", "--default", "second"]],
                stdinItems: null, EmitFormat.Lines, reason: "test");
            Assert.Equal(ExitCodes.Accepted, code);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        Assert.Equal("second", buffer.ToString().Trim()); // ONE line — the same shape a tty run emits
    }
}
