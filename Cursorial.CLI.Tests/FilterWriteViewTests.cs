using Cursorial.CLI.Commandlets;
using Cursorial.CLI.Views;
using Cursorial.CLI.Wire;
using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.CLI;

/// <summary>
/// The M1 `filter` view through the real input path — the framework's <c>CompletionPopup</c> riding
/// the query box: it opens over the FULL list before any typing (the list is the prompt), typing
/// narrows and re-ranks it, Up/Down move the popup highlight while typing keeps flowing, Enter
/// accepts the highlight with the ORIGINAL item index, and Esc is left to the host
/// (CloseOnEscape=False): the popup neither closes nor handles it, so the runner's abort binding
/// gets the key.
/// </summary>
public class FilterViewTests
{
    private static UIHeadlessHost Host() => UIHeadlessHost.Create(new UIHeadlessHostOptions
    {
        InitialSize = new Size(60, 16),
        Capabilities = HeadlessCapabilities.KittyTruecolor,
    });

    private static (FilterViewModel Vm, FilterView View) Show(UIHeadlessHost host, params string[] items)
    {
        var vm = new FilterViewModel(host.Application, "Pick:", items);
        var view = new FilterView { DataContext = vm };
        host.ShowRoot(view);
        host.RunUntilIdle();
        return (vm, view);
    }

    [Fact]
    public void Filter_OpensOverTheFullList_BeforeAnyTyping()
    {
        using var host = Host();
        var (_, view) = Show(host, "alpha", "beta", "gamma");

        Assert.True(view.Popup.IsOpen); // the auto-open: the list IS the prompt
        Assert.Equal(3, view.Popup.MatchCount);
        Assert.Equal(["alpha", "beta", "gamma"], view.Popup.Entries.Select(e => e.Item.Display)); // provider order
        Assert.Equal(0, view.Popup.SelectedIndex); // the top row is pre-highlighted: Enter needs no Down first
        Assert.Equal(8, view.MinHeight); // the reserve is SIZED: 3 matches + field/header/footer/chrome — no 13-row hole
    }

    [Fact]
    public void Filter_TypedQuery_NarrowsAndReopens()
    {
        using var host = Host();
        var (vm, view) = Show(host, "alpha", "beta", "gamma");

        host.SendText("ga");
        host.RunUntilIdle();

        Assert.Equal("ga", vm.Query); // focus landed in the query box without a click
        Assert.Equal("gamma", Assert.Single(view.Popup.Entries).Item.Display);

        host.SendText("zz"); // "gazz" — nothing survives, and a text session closes on zero matches...
        host.RunUntilIdle();
        Assert.False(view.Popup.IsOpen);
        Assert.Equal(0, view.MinHeight); // the reserve went with it — no phantom band while the list is gone

        host.SendKey(Key.Backspace);
        host.SendKey(Key.Backspace);
        host.RunUntilIdle();

        Assert.True(view.Popup.IsOpen); // ...but the next matching edit reopens the session
        Assert.Equal("gamma", Assert.Single(view.Popup.Entries).Item.Display);
    }

    [Fact]
    public void Filter_DownArrow_MovesHighlight_WhileTypingContinues()
    {
        using var host = Host();
        var (vm, view) = Show(host, "alpha", "beta", "gamma");

        host.SendText("a");
        host.RunUntilIdle();
        Assert.Equal(3, view.Popup.MatchCount); // every item contains an 'a'
        Assert.Equal("alpha", view.Popup.Entries[0].Item.Display); // ranked: the prefix match outranks the scattered ones

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(1, view.Popup.SelectedIndex); // the popup borrowed the plain arrow...

        host.SendText("m");
        host.RunUntilIdle();
        Assert.Equal("am", vm.Query); // ...while typing kept flowing into the query box
        Assert.Equal("gamma", Assert.Single(view.Popup.Entries).Item.Display);
        Assert.Equal(0, view.Popup.SelectedIndex); // a re-filter re-highlights the best match
    }

    [Fact]
    public void Filter_Enter_AcceptsHighlight_WithOriginalIndex()
    {
        using var host = Host();
        var (vm, view) = Show(host, "alpha", "beta", "gamma");

        host.SendText("et"); // beta alone survives
        host.RunUntilIdle();

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(ExitCodes.Accepted, vm.CompletedCode);
        Assert.Equal("beta", vm.Query); // the accept splice: the retained receipt shows the chosen value
        Assert.Equal(0, view.MinHeight); // the reserve dropped with the dismissal — the receipt retains ONE row, not twelve

        var result = vm.BuildResult("pick");
        Assert.Equal(VariableKind.Selection, result.Kind);
        Assert.Equal("beta", Assert.Single(result.Values));
        Assert.Equal(1, Assert.Single(result.Indices)); // ORIGINAL index into Items — in the narrowed list it sat at 0
    }

    [Fact]
    public void Filter_Escape_IsLeftToTheHost_PopupStaysOpen()
    {
        using var host = Host();
        var (vm, view) = Show(host, "alpha", "beta");

        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.True(view.Popup.IsOpen); // CloseOnEscape=False: the popup neither closed...
        Assert.Null(vm.CompletedCode); // ...nor completed anything — the runner's pre-process abort owns Esc
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
