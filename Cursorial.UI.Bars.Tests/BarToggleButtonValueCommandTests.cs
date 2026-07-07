using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// ValueCommandParameter<T> radio sets at the Bars layer: three BarToggleButtons share ONE BarCommand, each with its
// OWN parameter carrying a distinct Value. This is the reflected-IsChecked Actipro pattern: on every re-query the
// command's CanExecute receives each control's parameter, writes "does this control's Value match the model" into
// IsChecked, and BarToggleButton's SyncCheckedFromCommand reflects it — exactly one button checked, no IsChecked
// bindings, no per-surface wiring. Execute applies the clicked button's Value; BarCommand's auto re-raise re-syncs
// the whole set. The invariant under test: one CanExecuteChanged re-queries EVERY bound control with ITS parameter —
// no cross-talk — and the FB-27 Handled/Override context gate composes on the same parameters unchanged.
public sealed class BarToggleButtonValueCommandTests
{
    private static UITestHost NewHost() =>
        UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(40, 4),
            Capabilities = TestCapabilities.KittyTruecolor,
        });

    // The shared radio state: Model is "the caret column's current alignment"; Gated is the FB-27 context gate
    // ("alignment is inapplicable here"), greying the set while each parameter's Override forces its checked face.
    private sealed class AlignModel
    {
        public string Model = "Left";
        public bool Gated;
        public int ExecuteCount;

        public BarCommand CreateCommand() => new(
            execute: p =>
            {
                ExecuteCount++;
                Model = ((ValueCommandParameter<string>)p!).Value; // the clicked button's own Value travels with it
            },
            canExecute: p =>
            {
                // Pattern-match (FB-27 idiom): the wiring re-query can arrive before CommandParameter is assigned.
                // While the context gate holds, the Override channel owns the checked face — don't fight it here.
                if (p is ValueCommandParameter<string> vp && !Gated)
                    vp.IsChecked = vp.Value == Model; // the radio rule: checked ⇔ this control's Value == the model's
                return !Gated;
            });
    }

    private static (UITestHost Host, AlignModel Model, BarCommand Command, BarToggleButton[] Buttons, ValueCommandParameter<string>[] Parameters) ShowRadioSet()
    {
        var host = NewHost();
        var model = new AlignModel();
        var command = model.CreateCommand();
        var parameters = new[]
        {
            new ValueCommandParameter<string>("Left"),
            new ValueCommandParameter<string>("Center"),
            new ValueCommandParameter<string>("Right"),
        };

        var toolbar = new Toolbar
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var buttons = new BarToggleButton[3];
        for (var i = 0; i < 3; i++)
        {
            buttons[i] = new BarToggleButton { Content = parameters[i].Value[..1], Command = command, CommandParameter = parameters[i] };
            toolbar.Items.Add(buttons[i]);
        }

        host.ShowRoot(toolbar);
        host.RunUntilIdle();
        command.RaiseCanExecuteChanged(); // initial sync — the app raises once after wiring the set
        host.RunUntilIdle();
        return (host, model, command, buttons, parameters);
    }

    private static void AssertChecked(BarToggleButton[] buttons, params bool[] expected)
    {
        for (var i = 0; i < buttons.Length; i++)
            Assert.Equal(expected[i], buttons[i].IsChecked);
    }

    [Fact] // one re-query, three controls, three parameters: exactly the Value-matching button is checked — no cross-talk
    public void RadioSet_ExactlyMatchingButtonIsChecked()
    {
        var (host, model, command, buttons, parameters) = ShowRadioSet();
        using var _ = host;

        AssertChecked(buttons, true, false, false); // Model = "Left"

        model.Model = "Center"; // the model moves (caret enters a centered column) …
        command.RaiseCanExecuteChanged();
        host.RunUntilIdle();

        AssertChecked(buttons, false, true, false); // … and the ONE signal re-synced every surface off ITS parameter
        Assert.Equal(true, parameters[1].IsChecked);
        Assert.False(parameters[0].IsChecked);
        Assert.False(parameters[2].IsChecked);
    }

    [Fact] // clicking a member applies ITS Value (Execute reads the clicked parameter) and the auto re-raise re-syncs the set
    public void RadioSet_ClickAppliesClickedValue_AndReSyncs()
    {
        var (host, model, _, buttons, _) = ShowRadioSet();
        using var _h = host;

        buttons[2].Focus(); // "Right"
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal("Right", model.Model);
        Assert.Equal(1, model.ExecuteCount);         // one commit — the re-query echo never re-executes
        AssertChecked(buttons, false, false, true);  // the whole set re-synced from BarCommand's auto re-raise

        buttons[0].Focus(); // "Left" — a second pick moves the radio again
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal("Left", model.Model);
        AssertChecked(buttons, true, false, false);
    }

    [Fact] // FB-27 composition: the context gate greys+forces the WHOLE set on the inherited Override channel, then restores
    public void RadioSet_ContextGate_GreysAndForcesWholeSet()
    {
        var (host, model, command, buttons, parameters) = ShowRadioSet();
        using var _ = host;

        foreach (var parameter in parameters)
            parameter.Override(isChecked: false); // context makes the value inapplicable: force every face unchecked …
        model.Gated = true;                       // … and grey the set
        command.RaiseCanExecuteChanged();
        host.RunUntilIdle();

        AssertChecked(buttons, false, false, false); // even the model-matching member is forced off
        Assert.All(buttons, b => Assert.False(b.IsEffectivelyEnabled));

        foreach (var parameter in parameters)
            parameter.Release();
        model.Gated = false;
        command.RaiseCanExecuteChanged(); // one signal: enabled returns AND the radio state reappears per-parameter
        host.RunUntilIdle();

        AssertChecked(buttons, true, false, false); // Model is still "Left"
        Assert.All(buttons, b => Assert.True(b.IsEffectivelyEnabled));
    }
}
