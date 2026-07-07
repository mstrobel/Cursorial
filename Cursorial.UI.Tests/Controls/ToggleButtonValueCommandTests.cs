using System.Windows.Input;

using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Controls;

// ValueCommandParameter<T> radio sets at the framework level: several ToggleButtons share ONE command, each with its
// OWN parameter carrying a distinct Value; on every re-query the command's CanExecute receives each control's
// parameter and reflects "does this control's Value match the model" into it. A plain ToggleButton (outside the Bars
// layer) consumes only the FB-27 coercion channel — an unhandled parameter is a pure pass-through there — so the
// framework-level radio pattern writes the match through Override (Handled + IsCheckedOverride); the Bars-layer
// reflected-IsChecked variant is covered in Cursorial.UI.Bars.Tests. The key invariant throughout: one
// CanExecuteChanged re-queries EVERY bound control with ITS parameter — no cross-talk between the set's members.
public sealed class ToggleButtonValueCommandTests
{
    private static UITestHost Show(UIElement root)
    {
        var host = UITestHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    // The radio command: CanExecute reflects the per-parameter match (through the coercion channel a plain
    // ToggleButton consumes); Execute applies the clicked control's Value and re-raises (BarCommand-like) so the
    // whole set re-syncs off the one signal.
    private sealed class AlignCommand : ICommand
    {
        public string Model = "Left";
        public bool Gated; // context gate: the whole set greys (CanExecute false) while set
        public int ExecuteCount;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            // Pattern-match, not cast: the wiring re-query can arrive with a null parameter (Command assigned before
            // CommandParameter in an object initializer) — the FB-27 canExecute idiom.
            if (parameter is ValueCommandParameter<string> p && !Gated)
                p.Override(p.Value == Model); // the command owns the radio state: force each control to its match
            return !Gated;
        }

        public void Execute(object? parameter)
        {
            ExecuteCount++;
            Model = ((ValueCommandParameter<string>)parameter!).Value;
            Raise(); // WPF-CommandManager-like: applying the value re-queries every bound control
        }

        public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private static (UITestHost Host, AlignCommand Command, ToggleButton[] Buttons, ValueCommandParameter<string>[] Parameters) ShowRadioSet()
    {
        var command = new AlignCommand();
        var parameters = new[]
        {
            new ValueCommandParameter<string>("Left"),
            new ValueCommandParameter<string>("Center"),
            new ValueCommandParameter<string>("Right"),
        };
        var buttons = new ToggleButton[3];
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        for (var i = 0; i < 3; i++)
        {
            buttons[i] = new ToggleButton { Content = parameters[i].Value, Command = command, CommandParameter = parameters[i] };
            panel.Children.Add(buttons[i]);
        }

        var host = Show(panel);
        command.Raise(); // initial sync (the app raises once after wiring the set)
        host.RunFrame();
        return (host, command, buttons, parameters);
    }

    private static void AssertChecked(ToggleButton[] buttons, params bool[] expected)
    {
        for (var i = 0; i < buttons.Length; i++)
            Assert.Equal(expected[i], buttons[i].IsChecked);
    }

    [Fact] // exactly the button whose Value matches the model is checked after a re-query — each control got ITS parameter
    public void RadioSet_ExactlyMatchingButtonIsChecked()
    {
        var (host, command, buttons, parameters) = ShowRadioSet();
        using var _ = host;

        AssertChecked(buttons, true, false, false); // Model = "Left"
        Assert.Equal(true, parameters[0].IsCheckedOverride);
        Assert.Equal(false, parameters[1].IsCheckedOverride);
        Assert.Equal(false, parameters[2].IsCheckedOverride);

        command.Model = "Right"; // the model moves (caret enters a right-aligned column) …
        command.Raise();         // … one signal re-queries EVERY bound control with its own parameter
        host.RunFrame();

        AssertChecked(buttons, false, false, true);
    }

    [Fact] // Execute applies the CLICKED control's Value (its parameter travels with the click) and re-syncs the set
    public void RadioSet_ExecuteAppliesClickedValue()
    {
        var (host, command, buttons, _) = ShowRadioSet();
        using var _h = host;

        buttons[1].Focus(); // "Center"
        host.SendKey(Cursorial.Input.Key.Enter);
        host.RunFrame();

        Assert.Equal("Center", command.Model);   // Execute read the clicked button's parameter.Value
        Assert.Equal(1, command.ExecuteCount);   // one commit, no echo executes from the re-query
        AssertChecked(buttons, false, true, false);
    }

    [Fact] // the FB-27 context gate composes on a ValueCommandParameter: the whole set greys+forces, then restores
    public void RadioSet_ContextGate_GreysWholeSet()
    {
        var (host, command, buttons, parameters) = ShowRadioSet();
        using var _ = host;

        foreach (var parameter in parameters)
            parameter.Override(isChecked: false); // context makes alignment inapplicable: force the set unchecked …
        command.Gated = true;                     // … and grey it
        command.Raise();
        host.RunFrame();

        AssertChecked(buttons, false, false, false);
        Assert.All(buttons, b => Assert.False(b.IsEffectivelyEnabled));

        foreach (var parameter in parameters)
            parameter.Release();
        command.Gated = false;
        command.Raise(); // release: the radio state (from CanExecute's match) reappears on the same one signal
        host.RunFrame();

        AssertChecked(buttons, true, false, false); // Model is still "Left"
        Assert.All(buttons, b => Assert.True(b.IsEffectivelyEnabled));
    }
}
