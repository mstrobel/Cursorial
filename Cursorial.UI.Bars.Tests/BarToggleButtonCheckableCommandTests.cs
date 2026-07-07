using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// FB-27 at the Bars layer — command-owned, context-gated checked state on BarToggleButton. Covers the preserved
// backward-compatible path (an unhandled parameter toggled from Execute drives the button), the Handled context-gate
// (a command greys+locks its bound toggles at either polarity), the restore of the base preference when Handled
// clears, and multi-surface sync with a bind-time snap (two bar toggles bound to one command / one shared parameter).
public sealed class BarToggleButtonCheckableCommandTests
{
    private static (UITestHost Host, T Control) Show<T>(Func<T> create) where T : UIElement
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(40, 4),
            Capabilities = TestCapabilities.KittyTruecolor,
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

    [Theory] // BACKWARD-COMPAT: an existing-style checkable command (parameter toggled from Execute, Handled=false) drives the button
    [InlineData(true)]
    [InlineData(false)]
    public void UnhandledParameter_DrivesCheckedFromExecute(bool registerParameterFirst)
    {
        var param = new CheckableCommandParameter(isChecked: false);
        var command = new BarCommand(p => ((CheckableCommandParameter)p!).Toggle());

        var (host, toggle) = registerParameterFirst
                                 ? Show(() => TopLeft(new BarToggleButton { Content = "B", CommandParameter = param, Command = command }))
                                 : Show(() => TopLeft(new BarToggleButton { Content = "B", Command = command, CommandParameter = param }));

        using var _h = host;

        Assert.NotEqual(true, toggle.IsChecked);

        toggle.Focus();
        host.SendKey(Key.Enter);                 // Execute toggles the parameter, auto re-queries → the button re-syncs
        host.RunUntilIdle();
        Assert.True(param.IsChecked);
        Assert.Equal(true, toggle.IsChecked);
        Assert.True(toggle.IsEffectivelyEnabled); // never greyed by an unhandled parameter

        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.False(param.IsChecked);
        Assert.NotEqual(true, toggle.IsChecked);
    }

    [Theory] // Handled + forced false → the bound toggle is greyed+unchecked (context-gated OFF), even with a checked preference
    [InlineData(true)]
    [InlineData(false)]
    public void Handled_ForcedFalse_GreysAndUnchecks(bool registerParameterFirst)
    {
        var param = new CheckableCommandParameter(isChecked: true); // preference: checked
        var command = new BarCommand(_ => { }, canExecute: p => p is not ICheckableCommandParameter cp || !cp.Handled);

        var (host, toggle) = registerParameterFirst
                                 ? Show(() => TopLeft(new BarToggleButton { Content = "B", CommandParameter = param, Command = command }))
                                 : Show(() => TopLeft(new BarToggleButton { Content = "B", Command = command, CommandParameter = param }));

        using var _h = host;

        Assert.Equal(true, toggle.IsChecked);     // reflects the checked preference on bind
        Assert.True(toggle.IsEffectivelyEnabled);

        param.Override(isChecked: false);         // command takes over: force unchecked + (via CanExecute) grey it
        command.RaiseCanExecuteChanged();
        host.RunUntilIdle();

        Assert.Equal(false, toggle.IsChecked);    // greyed + unchecked
        Assert.False(toggle.IsEffectivelyEnabled);
    }

    [Theory] // Handled + forced indeterminate → the bound toggle is indeterminate, even when marked IsThreeState=false
    [InlineData(true)]
    [InlineData(false)]
    public void Handled_ForcedIndeterminate_OverridesIsThreeState(bool registerParameterFirst)
    {
        var param = new CheckableCommandParameter(isChecked: true); // preference: checked
        var command = new BarCommand(_ => {});

        var (host, toggle) = registerParameterFirst
                                 ? Show(() => TopLeft(new BarToggleButton { Content = "B", CommandParameter = param, Command = command }))
                                 : Show(() => TopLeft(new BarToggleButton { Content = "B", Command = command, CommandParameter = param }));

        using var _h = host;
        
        Assert.False(toggle.IsThreeState);
        Assert.Equal(true, toggle.IsChecked);     // reflects the checked preference on bind
        Assert.True(toggle.IsEffectivelyEnabled);

        param.Override(isChecked: null);          // command takes over: force unchecked + (via CanExecute) grey it
        command.RaiseCanExecuteChanged();
        host.RunUntilIdle();

        Assert.False(toggle.IsThreeState);        // still not explicitly three-state
        Assert.Null(toggle.IsChecked);            // indeterminate
        Assert.True(toggle.IsEffectivelyEnabled);
    }

    [Theory] // Handled + forced true → the bound toggle is greyed+CHECKED ("on but locked")
    [InlineData(true)]
    [InlineData(false)]
    public void Handled_ForcedTrue_GreysAndChecks(bool registerParameterFirst)
    {
        var param = new CheckableCommandParameter(isChecked: false); // preference: unchecked
        var command = new BarCommand(_ => { }, canExecute: p => p is not ICheckableCommandParameter cp || !cp.Handled);

        var (host, toggle) = registerParameterFirst
                                 ? Show(() => TopLeft(new BarToggleButton { Content = "B", CommandParameter = param, Command = command }))
                                 : Show(() => TopLeft(new BarToggleButton { Content = "B", Command = command, CommandParameter = param }));

        using var _h = host;

        Assert.NotEqual(true, toggle.IsChecked);

        param.Override(isChecked: true);          // force CHECKED
        command.RaiseCanExecuteChanged();
        host.RunUntilIdle();

        Assert.Equal(true, toggle.IsChecked);     // greyed + checked
        Assert.False(toggle.IsEffectivelyEnabled);

        param.Release();                          // release → the unchecked preference reappears, enabled again
        command.RaiseCanExecuteChanged();
        host.RunUntilIdle();
        Assert.NotEqual(true, toggle.IsChecked);
        Assert.True(toggle.IsEffectivelyEnabled);
    }

    [Fact] // MULTI-SURFACE: two bar toggles bound to one command / one shared parameter both reflect it; bind snaps immediately
    public void MultiSurface_TwoTogglesShareOneParameter()
    {
        var param = new CheckableCommandParameter(isChecked: true); // shared; already checked at bind time
        var command = new BarCommand(p => ((CheckableCommandParameter)p!).Toggle(),
                                     canExecute: p => p is not ICheckableCommandParameter cp || !cp.Handled);

        BarToggleButton? a = null, b = null;
        var (host, _) = Show(() =>
        {
            var toolbar = TopLeft(new Toolbar());
            a = new BarToggleButton { Content = "B", Command = command, CommandParameter = param };
            b = new BarToggleButton { Content = "B", CommandParameter = param, Command = command };
            toolbar.Items.Add(a);
            toolbar.Items.Add(b);
            return toolbar;
        });
        using var _h = host;

        Assert.Equal(true, a!.IsChecked); // bind-time snap on BOTH surfaces (no click, no first re-query lag)
        Assert.Equal(true, b!.IsChecked);

        a.Focus();
        host.SendKey(Key.Enter);          // toggling via one surface re-syncs the OTHER through the shared parameter
        host.RunUntilIdle();
        Assert.False(param.IsChecked);
        Assert.NotEqual(true, a.IsChecked);
        Assert.NotEqual(true, b.IsChecked);

        param.Override(isChecked: true);  // the command context-gates BOTH surfaces at once
        command.RaiseCanExecuteChanged();
        host.RunUntilIdle();
        Assert.Equal(true, a.IsChecked);
        Assert.Equal(true, b.IsChecked);
        Assert.False(a.IsEffectivelyEnabled);
        Assert.False(b.IsEffectivelyEnabled);
    }
}
