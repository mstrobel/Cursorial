using Cursorial.Input;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §12 — CheckBox + RadioButton (C1; rows C207–C218). Default-theme glyph rendering (ASCII triple),
/// toggle behavior, RadioButton group mutual exclusion via <c>SetCurrentValue</c>, arrow-key group
/// navigation, the can't-uncheck-self rule, and the 3-state stance. One test per row.
/// </summary>
public sealed class Section12_CheckRadio
{
    // ───────────────────────────── helpers ─────────────────────────────

    private static UIHeadlessHost Show(UIElement root, TerminalCapabilities? caps = null)
    {
        var host = caps is { } c
            ? UIHeadlessHost.Create(new UIHeadlessHostOptions { Capabilities = c })
            : UIHeadlessHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    // ───────────────────────────── CheckBox (C207–C210) ─────────────────────────────

    [Theory] // C207 (re-pinned, caps-mechanism review) — glyph cell + space + content, UNIFORM at any
             // subject position: the caps-unicode opt-up is a Requires(Unicode) rule now, so a CheckBox
             // shown AS the root renders the same glyphs as a nested one (the old ASCII expectation pinned
             // the top-level-element gap — a root-level control could never match the .caps-* descendant
             // rule). Unicode is unconditional today (SD14 recorded deferral); the ASCII base returns the
             // moment a negotiated glyph capability exists.
    [InlineData(null, "[ ]")]     // unchecked (shared by both glyph sets)
    [InlineData(true, "[✓]")]     // checked — the caps-unicode mark
    [InlineData("ind", "[▪]")]    // indeterminate — the caps-unicode mark
    public void C207_CheckBoxGlyphRender(object? state, string glyph)
    {
        var cb = new CheckBox { Content = "Opt", IsThreeState = true };
        cb.IsChecked = state switch { true => true, "ind" => null, _ => false };

        using var host = Show(cb, HeadlessCapabilities.KittyTruecolor);
        var row = host.GetRowText(0);

        Assert.StartsWith(glyph, row);   // the themed glyph — position-independent
        Assert.Contains("Opt", row);     // glyph + space + content presenter
    }

    [Fact] // C208 (re-pinned, caps-mechanism review) — glyphs render IDENTICALLY under an Ansi16/legacy
           // preset and at root subject position: caps-unicode is unconditional (SD14 deferral — no
           // negotiated glyph source), so the opt-up applies on every tier; what this pins is tier- and
           // position-INDEPENDENCE of the glyph set, and it starts pinning real ASCII fallback the moment
           // the deferral lifts and the legacy preset stops satisfying Requires(Unicode).
    public void C208_CheckBoxGlyphsUniformAcrossTiers()
    {
        var cb = new CheckBox { Content = "X" };
        using var host = Show(cb, HeadlessCapabilities.Ansi16Legacy);
        Assert.StartsWith("[ ]", host.GetRowText(0)); // unchecked is shared by both glyph sets

        cb.IsChecked = true;
        host.RunFrame();
        Assert.StartsWith("[✓]", host.GetRowText(0)); // same glyph set as truecolor — no tier drift
    }

    [Fact] // C209 — Space/click toggles; :checked flips
    public void C209_CheckBoxToggles()
    {
        var cb = new CheckBox();
        using var host = Show(cb);
        cb.Focus();

        host.SendKey(Key.Space);
        host.RunFrame();
        Assert.Equal(true, cb.IsChecked);
        Assert.True(cb.HasCustomPseudoClass(":checked"));

        host.SendKey(Key.Space);
        host.RunFrame();
        Assert.Equal(false, cb.IsChecked);
        Assert.False(cb.HasCustomPseudoClass(":checked"));
    }

    [Fact] // C210 — folds the mnemonic; access key toggles
    public void C210_CheckBoxAccessKeyToggles()
    {
        var cb = new CheckBox { Content = "_Accept" };
        using var host = Show(cb);

        // The folded mnemonic 'A' (the ContentPresenter recognizes the access key in the default theme).
        ((IAccessKeyTarget)cb).OnAccessKey(new AccessKeyEventArgs('A', isMultiMatch: false, cb));
        host.RunFrame();
        Assert.Equal(true, cb.IsChecked);
    }

    // ───────────────────────────── RadioButton (C211–C218) ─────────────────────────────

    [Fact] // C211 — properties + ASCII glyphs
    public void C211_RadioButtonProperties()
    {
        var rb = new RadioButton { Content = "A" };
        Assert.Null(rb.GroupName);

        using var host = Show(rb);
        Assert.StartsWith("( )", host.GetRowText(0)); // unchecked is shared by both glyph sets
        rb.IsChecked = true;
        host.RunFrame();
        // Re-pinned (caps-mechanism review): the caps-unicode mark, uniform at root subject position —
        // the old "(*)" pinned the top-level-element gap (a root-level RadioButton could never match the
        // .caps-* descendant opt-up that every nested RadioButton got).
        Assert.StartsWith("(●)", host.GetRowText(0));
        Assert.True(rb.HasCustomPseudoClass(":checked"));
    }

    [Fact] // C212 — same logical parent, GroupName null: checking one unchecks peers
    public void C212_LogicalParentGrouping()
    {
        var panel = new StackPanel();
        var r1 = new RadioButton();
        var r2 = new RadioButton();
        var r3 = new RadioButton();
        panel.Children.Add(r1);
        panel.Children.Add(r2);
        panel.Children.Add(r3);
        using var host = Show(panel);

        r1.IsChecked = true;
        host.RunFrame();
        r2.IsChecked = true; // checking r2 unchecks r1 (and r3 was never checked)
        host.RunFrame();

        Assert.Equal(false, r1.IsChecked);
        Assert.Equal(true, r2.IsChecked);
        Assert.Equal(false, r3.IsChecked);
    }

    [Fact] // C213 — same GroupName across different logical parents groups them
    public void C213_NamedGroupSpansSurfaceRoot()
    {
        var left = new StackPanel();
        var right = new StackPanel();
        var rootPanel = new StackPanel { Orientation = Orientation.Horizontal };
        rootPanel.Children.Add(left);
        rootPanel.Children.Add(right);

        var r1 = new RadioButton { GroupName = "G" };
        var r2 = new RadioButton { GroupName = "G" };
        var r3 = new RadioButton { GroupName = "G" }; // different logical parent, same name
        left.Children.Add(r1);
        left.Children.Add(r2);
        right.Children.Add(r3);

        using var host = Show(rootPanel);

        r3.IsChecked = true;
        host.RunFrame();
        r1.IsChecked = true; // checking r1 unchecks r3 (named group spans the root)
        host.RunFrame();

        Assert.Equal(true, r1.IsChecked);
        Assert.Equal(false, r3.IsChecked);
    }

    [Fact] // C214 — a radio can't uncheck itself by clicking
    public void C214_CheckedRadioCannotUncheckSelf()
    {
        var rb = new RadioButton();
        using var host = Show(rb);
        rb.Focus();

        host.SendKey(Key.Space);
        host.RunFrame();
        Assert.Equal(true, rb.IsChecked);

        host.SendKey(Key.Space); // re-click a checked radio: stays checked (WPF)
        host.RunFrame();
        Assert.Equal(true, rb.IsChecked);
    }

    [Fact] // C215 — arrow keys move focus + check the next radio in the group
    public void C215_ArrowKeysMoveAndCheckWithinGroup()
    {
        var panel = new StackPanel();
        var r1 = new RadioButton();
        var r2 = new RadioButton();
        var r3 = new RadioButton();
        panel.Children.Add(r1);
        panel.Children.Add(r2);
        panel.Children.Add(r3);
        using var host = Show(panel);

        r1.IsChecked = true;
        r1.Focus();
        host.RunFrame();

        host.SendKey(Key.DownArrow); // → r2 focused + checked
        host.RunFrame();
        Assert.True(r2.IsFocused);
        Assert.Equal(true, r2.IsChecked);
        Assert.Equal(false, r1.IsChecked);
    }

    [Fact] // C216 — peer uncheck via SetCurrentValue preserves the peer's binding
    public void C216_PeerUncheckPreservesValueSource()
    {
        var panel = new StackPanel();
        var r1 = new RadioButton();
        var r2 = new RadioButton();
        panel.Children.Add(r1);
        panel.Children.Add(r2);
        using var host = Show(panel);

        r1.IsChecked = true;
        host.RunFrame();
        // r1 holds a local value at this point; checking r2 unchecks r1 via SetCurrentValue (not a
        // local SetValue) — its value SOURCE stays LocalValue (the binding/animation would survive).
        r2.IsChecked = true;
        host.RunFrame();

        Assert.Equal(false, r1.IsChecked);
        Assert.Equal(BindingPriority.LocalValue, r1.GetValueSource(ToggleButton.IsCheckedProperty).Priority);
    }

    [Fact] // C217 — styling IsChecked is unsupported; selectors react to :checked, never set it
    public void C217_SetCurrentValueStanceForIsChecked()
    {
        var panel = new StackPanel();
        var r1 = new RadioButton();
        var r2 = new RadioButton();
        panel.Children.Add(r1);
        panel.Children.Add(r2);
        using var host = Show(panel);

        r1.IsChecked = true;
        host.RunFrame();
        r2.IsChecked = true;
        host.RunFrame();

        // The group uncheck is a SetCurrentValue, not a style frame — r1's value lane is LocalValue.
        Assert.Equal(false, r1.IsChecked);
        Assert.Equal(BindingPriority.LocalValue, r1.GetValueSource(ToggleButton.IsCheckedProperty).Priority);
    }

    [Fact] // C218 — radios are 2-state by click; the bool? shape exists but they don't cycle to null
    public void C218_RadioThreeStateStance()
    {
        var rb = new RadioButton { IsThreeState = true };
        using var host = Show(rb);
        rb.Focus();

        for (var i = 0; i < 4; i++)
        {
            host.SendKey(Key.Space);
            host.RunFrame();
            Assert.NotNull(rb.IsChecked); // never cycles to indeterminate by click (C218)
        }

        // An explicit set still allows null (the bool? shape from ToggleButton exists).
        rb.IsChecked = null;
        Assert.Null(rb.IsChecked);
    }
}
