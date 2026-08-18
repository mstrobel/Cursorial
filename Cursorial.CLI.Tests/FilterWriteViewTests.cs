using Cursorial.CLI.Commandlets;
using Cursorial.CLI.Views;
using Cursorial.CLI.Wire;
using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.CLI;

/// <summary>
/// The M1 `filter` view through the real input path: focus lands in the query box, typing narrows
/// the match list with selection following, Up/Down move the selection from the ROOT key bindings
/// (the query box is single-line, so arrows bubble) while typing keeps flowing, and Enter accepts
/// with the ORIGINAL item index.
/// </summary>
public class FilterViewTests
{
    private static UIHeadlessHost Host() => UIHeadlessHost.Create(new UIHeadlessHostOptions
    {
        InitialSize = new Size(60, 12),
        Capabilities = HeadlessCapabilities.KittyTruecolor,
    });

    [Fact]
    public void Filter_TypedQuery_NarrowsMatches_SelectionFollows()
    {
        using var host = Host();
        var vm = new FilterViewModel(host.Application, "Pick:", ["alpha", "beta", "gamma"]);
        host.ShowRoot(new FilterView { DataContext = vm });
        host.RunUntilIdle();

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, vm.Matches); // empty query: everything
        Assert.Equal("alpha", vm.Selected);

        host.SendText("ga");
        host.RunUntilIdle();

        Assert.Equal("ga", vm.Query); // focus landed in the query box without a click
        Assert.Equal("gamma", Assert.Single(vm.Matches));
        Assert.Equal("gamma", vm.Selected); // selection follows the narrowing
    }

    [Fact]
    public void Filter_DownArrow_MovesSelection_WhileTypingContinues()
    {
        using var host = Host();
        var vm = new FilterViewModel(host.Application, "Pick:", ["alpha", "beta", "gamma"]);
        host.ShowRoot(new FilterView { DataContext = vm });
        host.RunUntilIdle();

        host.SendText("a");
        host.RunUntilIdle();
        Assert.Equal(new[] { "alpha", "gamma", "beta" }, vm.Matches); // ranked: first-match 0 < 1 < 3
        Assert.Equal("alpha", vm.Selected);

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal("gamma", vm.Selected); // the root binding moved the list selection...

        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();
        Assert.Equal("alpha", vm.Selected); // ...and back

        host.SendKey(Key.DownArrow);
        host.SendText("m");
        host.RunUntilIdle();
        Assert.Equal("am", vm.Query); // ...while typing kept flowing into the query box
        Assert.Equal("gamma", Assert.Single(vm.Matches)); // a-m in order: only gamma
        Assert.Equal("gamma", vm.Selected);
    }

    [Fact]
    public void Filter_Enter_AcceptsWithOriginalIndex()
    {
        using var host = Host();
        var vm = new FilterViewModel(host.Application, "Pick:", ["alpha", "beta", "gamma"]);
        host.ShowRoot(new FilterView { DataContext = vm });
        host.RunUntilIdle();

        host.SendText("et"); // beta alone survives
        host.RunUntilIdle();
        Assert.Equal("beta", vm.Selected);

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(ExitCodes.Accepted, vm.CompletedCode);
        var result = vm.BuildResult("pick");
        Assert.Equal(VariableKind.Selection, result.Kind);
        Assert.Equal("beta", Assert.Single(result.Values));
        Assert.Equal(1, Assert.Single(result.Indices)); // ORIGINAL index into Items — in Matches it sat at 0
    }
}

/// <summary>
/// The M1 `write` view: Enter inserts newlines (AcceptsReturn), Ctrl+Enter is pinned as swallowed
/// by the editor (TextBox's AcceptsReturn arm matches Key.Enter regardless of Ctrl — the gesture
/// never reaches a root binding), and Ctrl+D is the accept chord that bubbles to the root binding.
/// </summary>
public class WriteViewTests
{
    private static UIHeadlessHost Host() => UIHeadlessHost.Create(new UIHeadlessHostOptions
    {
        InitialSize = new Size(60, 12),
        Capabilities = HeadlessCapabilities.KittyTruecolor,
    });

    [Fact]
    public void Write_Enter_InsertsNewline_DoesNotAccept()
    {
        using var host = Host();
        var vm = new WriteViewModel(host.Application, "msg>", "");
        host.ShowRoot(new WriteView { DataContext = vm });
        host.RunUntilIdle();

        host.SendText("line one");
        host.SendKey(Key.Enter);
        host.SendText("line two");
        host.RunUntilIdle();

        Assert.Equal("line one\nline two", vm.Text); // Enter stayed in the editor as a newline
        Assert.Null(vm.CompletedCode);
    }

    [Fact]
    public void Write_CtrlEnter_IsSwallowedByTheEditor_AsNewline()
    {
        // Pins the framework fact that forced the Ctrl+D chord: TextBox.OnKeyDown's
        // `case Key.Enter when AcceptsReturn` has no ctrl guard, so Ctrl+Enter inserts a newline
        // and is handled before any root KeyBinding sweep (bindings sweep on bubble, post-handlers).
        using var host = Host();
        var vm = new WriteViewModel(host.Application, "msg>", "");
        host.ShowRoot(new WriteView { DataContext = vm });
        host.RunUntilIdle();

        host.SendText("draft");
        host.SendKey(Key.Enter, KeyModifiers.Control);
        host.RunUntilIdle();

        Assert.Equal("draft\n", vm.Text);
        Assert.Null(vm.CompletedCode); // NOT an accept — if this fails, re-pin the view's gesture
    }

    [Fact]
    public void Write_CtrlD_AcceptsMultilineValue()
    {
        using var host = Host();
        var vm = new WriteViewModel(host.Application, "msg>", "");
        host.ShowRoot(new WriteView { DataContext = vm });
        host.RunUntilIdle();

        host.SendText("one");
        host.SendKey(Key.Enter);
        host.SendText("two");
        host.RunUntilIdle();

        host.SendKey(Key.Character, KeyModifiers.Control, text: "d");
        host.RunUntilIdle();

        Assert.Equal(ExitCodes.Accepted, vm.CompletedCode);
        var result = vm.BuildResult("msg");
        Assert.Equal(VariableKind.Text, result.Kind);
        Assert.Equal("one\ntwo", Assert.Single(result.Values)); // the multiline value is ONE Text variable
    }
}
