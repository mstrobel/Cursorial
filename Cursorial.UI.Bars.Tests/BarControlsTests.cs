using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Bars;

// Phase 1 spec for the shared bar controls: BarButton/BarToggleButton over ButtonBase/ToggleButton, the BarCommand
// define-once auto-fill, the ICheckableCommandParameter command-owned checked sync, and the Toolbar row.
public sealed class BarControlsTests
{
    private static (UIHeadlessHost Host, T Control) Show<T>(Func<T> create) where T : UIElement
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(40, 4),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });
        var control = create();
        host.ShowRoot(control);
        host.RunUntilIdle();
        return (host, control);
    }

    private static T TopLeft<T>(T control) where T : UIElement
    {
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Top;
        return control;
    }

    [Fact] // a BarButton renders its label and activating it runs its command (keyboard, ButtonBase path)
    public void BarButton_ActivateExecutesCommand()
    {
        var ran = 0;
        var (host, button) = Show(() => TopLeft(new BarButton { Content = "Paste", Command = new BarCommand(() => ran++) }));
        using var _h = host;

        Assert.Contains("Paste", host.GetRowText(0));
        button.Focus();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(1, ran);
    }

    [Fact] // a BarButton auto-fills its label from a BarCommand when Content is unset (define-once)
    public void BarButton_AutoFillsLabelFromCommand()
    {
        var (host, button) = Show(() => TopLeft(new BarButton { Command = new BarCommand(() => { }) { Text = "Copy" } }));
        using var _h = host;

        Assert.Equal("Copy", button.Content);
        Assert.Contains("Copy", host.GetRowText(0));
    }

    [Fact] // an explicit Content wins over the command's Text
    public void BarButton_ExplicitContentWinsOverCommand()
    {
        var (host, button) = Show(() => TopLeft(new BarButton { Content = "Mine", Command = new BarCommand(() => { }) { Text = "Copy" } }));
        using var _h = host;

        Assert.Equal("Mine", button.Content);
        Assert.Contains("Mine", host.GetRowText(0));
        Assert.DoesNotContain("Copy", host.GetRowText(0));
    }

    [Fact] // a disabled command disables the button (CanExecute coupling, inherited from ButtonBase)
    public void BarButton_DisabledWhenCommandCannotExecute()
    {
        var (host, button) = Show(() => TopLeft(new BarButton { Content = "X", Command = new BarCommand(() => { }, () => false) }));
        using var _h = host;

        Assert.False(button.IsEffectivelyEnabled);
    }

    [Fact] // a BarToggleButton reflects a command-owned ICheckableCommandParameter; clicking toggles it both ways
    public void BarToggleButton_ChecksFromCommandParameter()
    {
        var param = new CheckableCommandParameter(isChecked: false);
        var (host, toggle) = Show(() => TopLeft(new BarToggleButton { Content = "B", Command = new BarCommand(p => ((CheckableCommandParameter)p!).Toggle()), CommandParameter = param }));
        using var _h = host;

        Assert.NotEqual(true, toggle.IsChecked); // initially unchecked

        toggle.Focus();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.True(param.IsChecked);          // the command toggled the parameter
        Assert.Equal(true, toggle.IsChecked);  // the button re-synced (Execute's auto re-query → OnCommandStateChanged)

        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.False(param.IsChecked);
        Assert.NotEqual(true, toggle.IsChecked);
    }

    [Fact] // the initial checked state is reflected on attach (no click needed)
    public void BarToggleButton_InitialCheckedFromParameter()
    {
        var param = new CheckableCommandParameter(isChecked: true);
        var (host, toggle) = Show(() => TopLeft(new BarToggleButton { Content = "B", Command = new BarCommand(p => ((CheckableCommandParameter)p!).Toggle()), CommandParameter = param }));
        using var _h = host;

        Assert.Equal(true, toggle.IsChecked);
    }

    [Fact] // a Toolbar hosts bar controls in a single row, separator included
    public void Toolbar_HostsItemsInARow()
    {
        var (host, _) = Show(() =>
        {
            var toolbar = TopLeft(new Toolbar());
            toolbar.Items.Add(new BarButton { Content = "Cut" });
            toolbar.Items.Add(new BarSeparator());
            toolbar.Items.Add(new BarButton { Content = "Copy" });
            return toolbar;
        });
        using var _h = host;

        var row = host.GetRowText(0);
        Assert.Contains("Cut", row);
        Assert.Contains("Copy", row);
        Assert.Contains("│", row); // the separator cell divides the clusters
    }

    [Fact] // a BarLabel renders its caption text and is non-interactive (not focusable)
    public void BarLabel_RendersCaption()
    {
        var (host, label) = Show(() => TopLeft(new BarLabel { Content = "Font:" }));
        using var _h = host;

        Assert.Contains("Font:", host.GetRowText(0));
        Assert.False(label.Focusable);
    }

    // ── BarCommand auto-fill on a runtime Command swap (Phase-1 audit) ──

    [Fact] // swapping Command to a different BarCommand REFRESHES the auto-filled label (no stale text)
    public void BarButton_AutoFilledLabelRefreshesOnCommandSwap()
    {
        var (host, button) = Show(() => TopLeft(new BarButton { Command = new BarCommand(() => { }) { Text = "Copy" } }));
        using var _h = host;
        Assert.Equal("Copy", button.Content);

        button.Command = new BarCommand(() => { }) { Text = "Paste" };
        host.RunUntilIdle();

        Assert.Equal("Paste", button.Content);            // refreshed — NOT the stale "Copy" (the fixed bug)
        Assert.Contains("Paste", host.GetRowText(0));
        Assert.DoesNotContain("Copy", host.GetRowText(0));
    }

    [Fact] // clearing Command clears the previously auto-filled label (nothing stranded)
    public void BarButton_AutoFilledLabelClearsWhenCommandRemoved()
    {
        var (host, button) = Show(() => TopLeft(new BarButton { Command = new BarCommand(() => { }) { Text = "Copy" } }));
        using var _h = host;
        Assert.Equal("Copy", button.Content);

        button.Command = null;
        host.RunUntilIdle();

        Assert.Null(button.Content);                      // the stale auto-fill was cleared
        Assert.DoesNotContain("Copy", host.GetRowText(0));
    }

    [Fact] // an explicit Content still wins after a Command swap (the helper never clobbers an author value)
    public void BarButton_ExplicitContentSurvivesCommandSwap()
    {
        var (host, button) = Show(() => TopLeft(new BarButton { Content = "Mine", Command = new BarCommand(() => { }) { Text = "Copy" } }));
        using var _h = host;
        Assert.Equal("Mine", button.Content);

        button.Command = new BarCommand(() => { }) { Text = "Paste" };
        host.RunUntilIdle();

        Assert.Equal("Mine", button.Content);             // the explicit author value is untouched by the swap
    }

    // ── caps-nocolor interactive cue for the bar button family (Phase-1 audit) ──

    [Fact] // under the NoColor tier a focused BarButton reverse-videos (the palette fill collapses to Default) — the
           // bar controls fall outside the shared exact-type .caps-nocolor layer, so their theme carries the cue
    public void BarButton_FocusInverseCue_UnderNoColor()
    {
        var (host, button) = Show(() => TopLeft(new BarButton { Content = "Cut" }));
        using var _h = host;

        host.Application.RequestedColorTier = ColorDepth.NoColor;
        host.RunUntilIdle();
        Assert.Equal(ColorDepth.NoColor, host.Application.ActualThemeVariant.Tier);

        button.Focus();
        host.RunUntilIdle();

        // Re-pinned 2026-07-13 (per-axis decomposition): the cue rides the Inverse AXIS now.
        Assert.True(TextElement.GetInverse(button)); // the monochrome focus cue
        Assert.Equal(TextAttributes.Inverse, TextElement.ComposeAttributes(button).Flags & TextAttributes.Inverse);
    }

    [Fact] // under a color tier the SAME focus does NOT set Inverse (the palette fill is the cue — no double-invert)
    public void BarButton_FocusNoInverseCue_UnderColor()
    {
        var (host, button) = Show(() => TopLeft(new BarButton { Content = "Cut" }));
        using var _h = host;

        host.Application.RequestedColorTier = ColorDepth.Truecolor;
        host.RunUntilIdle();
        Assert.Equal(ColorDepth.Truecolor, host.Application.ActualThemeVariant.Tier);

        button.Focus();
        host.RunUntilIdle();

        // Re-pinned to the axis (audit fix): the old GetTextAttributes read the aggregate (always None
        // now), which can never fail — a broken CD8 descent leaking InteractiveCueInverse=true into a
        // color tier would double-invert while the old assert stayed green. Assert the live axis.
        Assert.False(TextElement.GetInverse(button)); // color fill IS the cue — no attribute inversion
    }
}
