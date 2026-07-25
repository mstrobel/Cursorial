using System.Windows.Input;

using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Controls;

// MenuItem's ICheckableCommandParameter support (ported from ToggleButton), gated on IsCheckable. The FIRST priority is
// backward-compat: an item NOT using an ICheckableCommandParameter — no command, a plain command, or a plain parameter
// — behaves exactly as before (no coercion, no auto-installed carrier). Then the feature itself, and the IsCheckable
// [un]wiring the design hinges on.
public sealed class MenuItemCheckableCommandTests
{
    private static UIHeadlessHost Show(UIElement root)
    {
        var host = UIHeadlessHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    // A scriptable ICommand whose CanExecute is a settable flag; it records the last parameter it was handed and can
    // re-raise CanExecuteChanged (the single signal that re-queries enabled AND re-coerces checked).
    private sealed class RelayCommand : ICommand
    {
        public bool CanExecuteResult { get; set; } = true;
        public object? LastParameter { get; private set; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) { LastParameter = parameter; return CanExecuteResult; }
        public void Execute(object? parameter) { LastParameter = parameter; }
        public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── backward-compat: the default behavior is IDENTICAL when not using an ICheckableCommandParameter ──

    [Fact] // a checkable item with NO command toggles IsChecked exactly as before; no parameter is ever synthesized
    public void Checkable_NoCommand_TogglesNormally()
    {
        var item = new MenuItem { IsCheckable = true };
        using var host = Show(item);

        Assert.False(item.IsChecked);
        Assert.Null(item.CommandParameter);

        item.IsChecked = true;
        Assert.True(item.IsChecked);
        item.IsChecked = false;
        Assert.False(item.IsChecked);
        Assert.Null(item.CommandParameter); // still no auto-installed carrier
    }

    [Fact] // ACCEPTED DEVIATION: a checkable, COMMANDED item with no CommandParameter is auto-issued a default carrier
    public void Checkable_CommandNoParameter_AutoDefaultInstalled()
    {
        var command = new RelayCommand();
        var item = new MenuItem { IsCheckable = true, Command = command };
        using var host = Show(item);

        var carrier = Assert.IsType<CheckableCommandParameter>(item.CommandParameter); // ToggleButton parity

        item.IsChecked = true;                        // the item's toggle reflects into the carrier
        Assert.Equal(true, carrier.IsChecked);
        command.Raise();
        Assert.Same(carrier, command.LastParameter);  // the command receives the carrier (the accepted deviation)
    }

    [Fact] // un-wiring (IsCheckable → false) removes the auto-issued default; CommandParameter reverts to null
    public void UnwireRemovesAutoDefault()
    {
        var item = new MenuItem { IsCheckable = true, Command = new RelayCommand() };
        using var host = Show(item);
        Assert.NotNull(item.CommandParameter); // the auto default

        item.IsCheckable = false;
        Assert.Null(item.CommandParameter);    // reverted — a user-provided parameter would instead be preserved
    }

    [Fact] // a user's plain (non-checkable) CommandParameter is preserved and never coerces IsChecked
    public void Checkable_PlainParameter_Preserved()
    {
        var item = new MenuItem { IsCheckable = true, Command = new RelayCommand(), CommandParameter = "hello" };
        using var host = Show(item);

        Assert.Equal("hello", item.CommandParameter);

        item.IsChecked = true; // coercion is inert (the parameter isn't an ICheckableCommandParameter)
        Assert.True(item.IsChecked);
        Assert.Equal("hello", item.CommandParameter); // unchanged
    }

    [Fact] // a NON-checkable item ignores even a fully-configured checkable parameter (the machinery is off)
    public void NonCheckable_IgnoresCheckableParameter()
    {
        var param = new CheckableCommandParameter { Handled = true, IsCheckedOverride = true };
        var command = new RelayCommand();
        var item = new MenuItem { IsCheckable = false, Command = command, CommandParameter = param };
        using var host = Show(item);

        command.Raise();
        Assert.False(item.IsChecked); // not coerced — IsCheckable is false
    }

    // ── the feature: an explicitly-provided ICheckableCommandParameter drives the checked state ──

    [Fact] // an unhandled parameter is a pure pass-through — IsChecked follows the item's own preference
    public void UnhandledParameter_IsPassThrough()
    {
        var param = new CheckableCommandParameter(isChecked: false);
        var item = new MenuItem { IsCheckable = true, Command = new RelayCommand(), CommandParameter = param };
        using var host = Show(item);

        Assert.False(item.IsChecked);
        item.IsChecked = true;
        Assert.True(item.IsChecked); // unchanged by the (unhandled) parameter
    }

    [Fact] // Handled + forced false → coerced UNCHECKED regardless of the preference
    public void Handled_ForcedFalse_Unchecks()
    {
        var param = new CheckableCommandParameter();
        var command = new RelayCommand();
        var item = new MenuItem { IsCheckable = true, Command = command, CommandParameter = param };
        using var host = Show(item);

        item.IsChecked = true; // preference: checked
        Assert.True(item.IsChecked);

        param.IsCheckedOverride = false;
        param.Handled = true;
        command.Raise();
        Assert.False(item.IsChecked); // forced value, not the preference
    }

    [Fact] // Handled + forced true → coerced CHECKED even though the preference is unchecked ("on but locked")
    public void Handled_ForcedTrue_Checks()
    {
        var param = new CheckableCommandParameter();
        var command = new RelayCommand();
        var item = new MenuItem { IsCheckable = true, Command = command, CommandParameter = param };
        using var host = Show(item);

        item.IsChecked = false;
        Assert.False(item.IsChecked);

        param.IsCheckedOverride = true;
        param.Handled = true;
        command.Raise();
        Assert.True(item.IsChecked);
    }

    [Fact] // clearing Handled restores the base preference purely via re-coercion on the command-state hook
    public void ClearingHandled_RestoresBasePreference()
    {
        var param = new CheckableCommandParameter();
        var command = new RelayCommand();
        var item = new MenuItem { IsCheckable = true, Command = command, CommandParameter = param };
        using var host = Show(item);

        item.IsChecked = true; // preference: checked
        param.IsCheckedOverride = false;
        param.Handled = true;
        command.Raise();
        Assert.False(item.IsChecked); // overridden

        param.Handled = false;
        command.Raise();
        Assert.True(item.IsChecked); // preference reappears via the coercion system
    }

    // ── the IsCheckable [un]wiring the design hinges on ──

    [Fact] // toggling IsCheckable WIRES (the param drives checked) then UN-WIRES (checked no longer follows it)
    public void IsCheckableToggle_WiresAndUnwires()
    {
        var param = new CheckableCommandParameter { Handled = true, IsCheckedOverride = true };
        var command = new RelayCommand();
        var item = new MenuItem { IsCheckable = false, Command = command, CommandParameter = param };
        using var host = Show(item);

        Assert.False(item.IsChecked); // not wired yet (not checkable)

        item.IsCheckable = true;      // wire: the forced-true override now drives IsChecked
        Assert.True(item.IsChecked);

        item.IsCheckable = false;     // un-wire: IsChecked no longer follows the parameter
        Assert.False(item.IsChecked);
    }

    [Fact] // the checkable item reserves the icon-tray column (the checkmark's home); it drops when IsCheckable clears
    public void IconTrayFollowsCheckable()
    {
        var item = new MenuItem { IsCheckable = true, Command = new RelayCommand() };
        using var host = Show(item);

        Assert.True(item.IsIconTrayVisible);  // checkable ⇒ the check column shows
        item.IsCheckable = false;
        Assert.False(item.IsIconTrayVisible); // no longer checkable and no icon ⇒ no tray
    }
}
