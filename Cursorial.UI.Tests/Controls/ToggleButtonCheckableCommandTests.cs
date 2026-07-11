using System.Windows.Input;

using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

// FB-27 — command-owned checked state via coercion on ToggleButton. The framework-level spec (the Bars layer is
// covered separately): the IsChecked coercion reads the effective ICheckableCommandParameter, an unhandled parameter
// is a pure pass-through (backward-compat), and a Handled parameter forces the effective checked state to its override
// at either polarity — re-coerced on the command-state hook, with the base preference restored (no bookkeeping) when
// Handled clears.
public sealed class ToggleButtonCheckableCommandTests
{
    private static UIHeadlessHost Show(UIElement root)
    {
        var host = UIHeadlessHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    private static T TopLeft<T>(T element) where T : UIElement
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.VerticalAlignment = VerticalAlignment.Top;
        return element;
    }

    // A scriptable ICommand whose CanExecute is a settable flag (the app gates it false to grey a locked toggle) and
    // that can re-raise CanExecuteChanged — the single signal that re-queries enabled AND re-coerces checked.
    private sealed class RelayCommand : ICommand
    {
        public bool CanExecuteResult { get; set; } = true;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => CanExecuteResult;
        public void Execute(object? parameter) { }
        public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    [Fact] // an unhandled ICheckableCommandParameter never touches IsChecked — the toggle self-cycles exactly as before
    public void UnhandledParameter_IsPureBackwardCompatPassThrough()
    {
        var param = new CheckableCommandParameter(isChecked: false); // Handled = false
        var command = new RelayCommand();
        var toggle = TopLeft(new ToggleButton { Command = command, CommandParameter = param });
        using var host = Show(toggle);

        Assert.Equal(false, toggle.IsChecked);

        toggle.IsChecked = true;                 // the control's own preference (base value)
        Assert.Equal(true, toggle.IsChecked);    // unchanged by the (unhandled) parameter

        command.Raise();                         // a re-query must not disturb an unhandled parameter's toggle
        host.RunFrame();
        Assert.Equal(true, toggle.IsChecked);
        Assert.True(toggle.IsEffectivelyEnabled);
    }

    [Fact] // Handled + forced false → coerced UNCHECKED and CanExecute=false greys it (greyed+unchecked)
    public void Handled_ForcedFalse_GreysAndUnchecks()
    {
        var param = new CheckableCommandParameter();
        var command = new RelayCommand();
        var toggle = TopLeft(new ToggleButton { Command = command, CommandParameter = param });
        using var host = Show(toggle);

        toggle.IsChecked = true;                 // the preference: checked
        Assert.Equal(true, toggle.IsChecked);

        param.Override(isChecked: false);        // the command takes over: force unchecked
        command.CanExecuteResult = false;        // and grey it
        command.Raise();                         // one signal re-queries enabled AND re-coerces checked
        host.RunFrame();

        Assert.Equal(false, toggle.IsChecked);   // coerced to the forced value, not the preference
        Assert.False(toggle.IsEffectivelyEnabled); // greyed
    }

    [Fact] // Handled + forced true → greyed+CHECKED ("on but locked"), even though the preference is unchecked
    public void Handled_ForcedTrue_GreysAndChecks()
    {
        var param = new CheckableCommandParameter();
        var command = new RelayCommand();
        var toggle = TopLeft(new ToggleButton { Command = command, CommandParameter = param });
        using var host = Show(toggle);

        Assert.Equal(false, toggle.IsChecked);   // preference: unchecked (a set base value below)
        toggle.IsChecked = false;                // establish the base preference explicitly

        param.Override(isChecked: true);         // force CHECKED
        command.CanExecuteResult = false;        // and lock it
        command.Raise();
        host.RunFrame();

        Assert.Equal(true, toggle.IsChecked);    // greyed + checked
        Assert.False(toggle.IsEffectivelyEnabled);
    }

    [Fact] // clearing Handled restores the base value (the preference) with no restore bookkeeping — re-coerced on the hook
    public void ClearingHandled_RestoresBasePreference()
    {
        var param = new CheckableCommandParameter();
        var command = new RelayCommand();
        var toggle = TopLeft(new ToggleButton { Command = command, CommandParameter = param });
        using var host = Show(toggle);

        toggle.IsChecked = true;                 // preference: checked
        param.Override(isChecked: false);        // take over → force unchecked
        command.CanExecuteResult = false;
        command.Raise();
        host.RunFrame();
        Assert.Equal(false, toggle.IsChecked);   // overridden

        param.Release();                         // give the control back its own state
        command.CanExecuteResult = true;
        command.Raise();                         // re-coercion fires on the command-state hook (no IsChecked write)
        host.RunFrame();

        Assert.Equal(true, toggle.IsChecked);    // the preference reappears purely via the coercion system
        Assert.True(toggle.IsEffectivelyEnabled);
    }

    [Fact] // the override snaps on the command-state hook alone — no manual IsChecked raise, and it flips live
    public void Override_ReCoercesOnCommandStateHook()
    {
        var param = new CheckableCommandParameter();
        var command = new RelayCommand();
        var toggle = TopLeft(new ToggleButton { Command = command, CommandParameter = param });
        using var host = Show(toggle);
        toggle.IsChecked = true;                 // preference: checked

        param.Override(isChecked: false);
        command.Raise();
        host.RunFrame();
        Assert.Equal(false, toggle.IsChecked);   // forced unchecked

        param.IsCheckedOverride = true;          // flip the forced value (still Handled)
        command.Raise();
        host.RunFrame();
        Assert.Equal(true, toggle.IsChecked);    // snapped to the new forced value, hook-driven

        param.Release();
        command.Raise();
        host.RunFrame();
        Assert.Equal(true, toggle.IsChecked);    // preference (true) restored
    }

    [Fact] // a Handled override forces its value even onto a toggle whose IsChecked was NEVER written (still the Default lane)
    public void Handled_ForcesOntoUntouchedDefaultChecked()
    {
        var param = new CheckableCommandParameter();
        param.Override(isChecked: true);         // Handled BEFORE the toggle ever carries a checked value
        var command = new RelayCommand { CanExecuteResult = false };
        // IsChecked is never set here — it sits at its metadata default (the Default value-source lane).
        var toggle = TopLeft(new ToggleButton { Command = command, CommandParameter = param });
        using var host = Show(toggle);

        Assert.Equal(true, toggle.IsChecked);    // forced CHECKED despite never having a base preference (graft path)
        Assert.False(toggle.IsEffectivelyEnabled);

        param.Release();                         // released → the control's own (default: unchecked) value reappears
        command.CanExecuteResult = true;
        command.Raise();
        host.RunFrame();
        Assert.NotEqual(true, toggle.IsChecked);
        Assert.True(toggle.IsEffectivelyEnabled);
    }

    [Fact] // a plain (non-checkable) command leaves a ToggleButton's toggle behavior untouched — no coercion interference
    public void NonCheckableCommand_ToggleUnaffected()
    {
        var command = new RelayCommand();
        var toggle = TopLeft(new ToggleButton { Command = command, CommandParameter = "plain" }); // not ICheckableCommandParameter
        using var host = Show(toggle);

        toggle.Focus();
        toggle.IsChecked = true;
        Assert.Equal(true, toggle.IsChecked);

        command.Raise(); // a re-query never coerces away a non-checkable toggle's state
        host.RunFrame();
        Assert.Equal(true, toggle.IsChecked);
    }
}
