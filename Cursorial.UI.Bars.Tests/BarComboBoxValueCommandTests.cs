using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// BarComboBox/BarGallery + ValueCommandParameter<T> + IPreviewableCommand — the live-preview flow (the Actipro
// Avalonia command-side verbs over the WPF-style value parameter): while the drop-down is open, moving the highlight
// writes the highlighted value into PreviewValue and calls Preview(parameter) on the shared command (the consumer
// shows the outcome WITHOUT committing); a commit (Enter / Space / item click) calls CancelPreview (restore exactly)
// then Execute with Value set — Execute is always and only the commit, one real operation; a dismissal (Escape /
// light-dismiss) calls CancelPreview and restores the pre-open selection. Invariants pinned here: preview never
// dirties the model permanently (cancel restores byte-exact), the preview is ATOMIC — either committed or entirely
// rolled back, structurally: CancelPreview is a separate command verb outside the Execute self-gate, so it is
// delivered even through a mid-session CanExecute gate flip while the gated commit is refused un-applied — an
// UNAWARE command (not IPreviewableCommand) never receives a preview verb yet still commits through Execute, the
// combo greys while CanExecute is false (CD25), and with NO value parameter — or no command — the controls behave
// exactly as before (no default parameter is provisioned — T isn't inferrable, and the parameter is never touched).
public sealed class BarComboBoxValueCommandTests
{
    private static UITestHost NewHost(int w = 30, int h = 12) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = TestCapabilities.KittyTruecolor });

    // Locate a rendered text on screen (column, row) — the click target the popup actually shows.
    private static (int Column, int Row) FindOnScreen(UITestHost host, string text, int height = 12)
    {
        for (var row = 0; row < height; row++)
        {
            var column = host.GetRowText(row).IndexOf(text, StringComparison.Ordinal);
            if (column >= 0)
                return (column, row);
        }

        throw new InvalidOperationException($"'{text}' not found on screen.");
    }

    // The consumer of the preview contract: Applied is the document state; Snapshot is the pre-preview state an
    // active preview must restore (byte-exact); Log records every Execute routed through the parameter; Gated is the
    // command's own CanExecute gate (false by default — the stable-gate case every other test runs under).
    private sealed class SizeModel
    {
        public string Applied = "Sm";
        public string? Snapshot;
        public bool Gated;
        public readonly List<string> Log = [];

        public BarCommand CreateCommand() => new(
            canExecute: _ => !Gated,
            execute: p =>
            {
                // Execute is always and only the COMMIT (one real operation — one undo group at the consumer).
                Applied = (string)((IValueCommandParameter)p!).Value!;
                Log.Add($"commit:{Applied}");
            },
            previewExecute: p =>
            {
                Snapshot ??= Applied;                                         // first preview snapshots the real state
                Applied = (string)((IValueCommandParameter)p!).PreviewValue!; // apply WITHOUT committing (a re-preview replaces)
                Log.Add($"preview:{Applied}");
            },
            cancelPreviewExecute: _ =>
            {
                if (Snapshot is not null)
                {
                    Applied = Snapshot; // restore the exact pre-preview state
                    Snapshot = null;
                }
                Log.Add("cancel");
            });
    }

    private static (UITestHost Host, SizeModel Model, ValueCommandParameter<string> Parameter, BarGallery Gallery) ShowGallery()
    {
        var host = NewHost();
        var model = new SizeModel();
        var parameter = new ValueCommandParameter<string>("Sm");
        var gallery = new BarGallery
        {
            ItemsSource = new[] { "Sm", "Md", "Lg" },
            Command = model.CreateCommand(),
            CommandParameter = parameter,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        gallery.SelectedIndex = 0;
        host.ShowRoot(gallery);
        host.RunUntilIdle();
        gallery.Focus();
        gallery.IsDropDownOpen = true;
        host.RunUntilIdle();
        return (host, model, parameter, gallery);
    }

    [Fact] // highlight → preview; move highlight → re-preview; Esc → reverted byte-exact + the pre-open selection is back
    public void Gallery_HighlightPreviews_EscapeCancelsAndRestores()
    {
        var (host, model, parameter, gallery) = ShowGallery();
        using var _ = host;

        host.SendKey(Key.RightArrow); // highlight "Md" (selection-follows-highlight)
        host.RunUntilIdle();
        Assert.Equal("Md", model.Applied);   // previewed live …
        Assert.Equal("Sm", model.Snapshot);  // … without committing (the real state is snapshotted, not lost)
        Assert.Equal("Md", parameter.PreviewValue);
        Assert.Equal("Sm", parameter.Value); // Value stays the COMMITTED value throughout the preview session

        host.SendKey(Key.RightArrow); // highlight "Lg" — a re-preview replaces the previous preview, never stacks
        host.RunUntilIdle();
        Assert.Equal("Lg", model.Applied);
        Assert.Equal("Sm", model.Snapshot);

        host.SendKey(Key.Escape); // dismiss: cancel the preview, put everything back
        host.RunUntilIdle();
        Assert.False(gallery.IsDropDownOpen);
        Assert.Equal("Sm", model.Applied);   // byte-exact revert
        Assert.Null(model.Snapshot);
        Assert.Equal(0, gallery.SelectedIndex); // the tentative highlight did not stick to the face either
        Assert.Equal("Sm", parameter.Value);    // nothing was committed
        Assert.Equal(new[] { "preview:Md", "preview:Lg", "cancel" }, model.Log);
    }

    [Fact] // Enter commits: CancelPreview restores first, then ONE Commit applies the highlighted value for real
    public void Gallery_EnterCommits_OneRealOperation()
    {
        var (host, model, parameter, gallery) = ShowGallery();
        using var _ = host;

        host.SendKey(Key.RightArrow); // preview "Md"
        host.RunUntilIdle();
        host.SendKey(Key.Enter);      // commit it
        host.RunUntilIdle();

        Assert.False(gallery.IsDropDownOpen);
        Assert.Equal("Md", model.Applied);
        Assert.Null(model.Snapshot);              // the preview was unwound BEFORE the commit applied
        Assert.Equal("Md", parameter.Value);      // the committed value landed on the parameter
        Assert.Equal(1, gallery.SelectedIndex);   // a commit keeps the chosen selection on the face
        Assert.Equal(new[] { "preview:Md", "cancel", "commit:Md" }, model.Log);
        Assert.Equal(1, model.Log.Count(entry => entry.StartsWith("commit", StringComparison.Ordinal)));
    }

    [Fact] // light-dismiss (click away) is a dismissal like Escape: cancel + restore, nothing committed
    public void Gallery_LightDismissCancels()
    {
        var (host, model, parameter, gallery) = ShowGallery();
        using var _ = host;

        host.SendKey(Key.RightArrow); // preview "Md"
        host.RunUntilIdle();
        Assert.Equal("Md", model.Applied);

        host.SendClick(28, 11); // click empty screen far from the face and the open drop-down
        host.RunUntilIdle();

        Assert.False(gallery.IsDropDownOpen);
        Assert.Equal("Sm", model.Applied);
        Assert.Equal(0, gallery.SelectedIndex);
        Assert.Equal("Sm", parameter.Value);
        Assert.DoesNotContain(model.Log, entry => entry.StartsWith("commit", StringComparison.Ordinal));
    }

    [Fact] // ATOMICITY: the gate flips false mid-preview — a dismissal still delivers CancelPreview THROUGH the gate
    public void Gallery_GateFlipsMidPreview_DismissalStillRollsBack()
    {
        var (host, model, parameter, gallery) = ShowGallery();
        using var _ = host;

        host.SendKey(Key.RightArrow); // preview "Md" while the gate is open
        host.RunUntilIdle();
        Assert.Equal("Md", model.Applied);

        model.Gated = true;           // the command gates itself mid-session (no re-query reaches before the gesture)
        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.False(gallery.IsDropDownOpen);
        Assert.Equal("Sm", model.Applied);      // byte-exact rollback — the cancel was DELIVERED despite the gate
        Assert.Null(model.Snapshot);
        Assert.Equal(0, gallery.SelectedIndex); // the face rolled back too
        Assert.Equal("Sm", parameter.Value);
        Assert.Equal(new[] { "preview:Md", "cancel" }, model.Log);
    }

    [Fact] // ATOMICITY: the gate flips false mid-preview — a COMMIT gesture nets a FULL rollback (cancel runs, commit refused)
    public void Gallery_GateFlipsMidPreview_CommitGestureRollsBack()
    {
        var (host, model, parameter, gallery) = ShowGallery();
        using var _ = host;

        host.SendKey(Key.RightArrow); // preview "Md" while the gate is open
        host.RunUntilIdle();
        Assert.Equal("Md", model.Applied);

        model.Gated = true;
        host.SendKey(Key.Enter);      // the commit gesture — the gate refuses it; the preview must still unwind
        host.RunUntilIdle();

        Assert.False(gallery.IsDropDownOpen);
        Assert.Equal("Sm", model.Applied);      // model back to the pre-session state
        Assert.Null(model.Snapshot);
        Assert.Equal("Sm", parameter.Value);    // the refused commit never mutated the parameter
        Assert.Equal(0, gallery.SelectedIndex); // and the face never diverges from the unchanged model
        Assert.Equal(new[] { "preview:Md", "cancel" }, model.Log); // no commit entry — nothing was half-applied
    }

    [Fact] // clicking an item commits it (the click's own selection change previews first — then cancel + one commit)
    public void ComboBox_ItemClickCommits()
    {
        var host = NewHost();
        using var _ = host;
        var model = new SizeModel();
        var parameter = new ValueCommandParameter<string>("Sm");
        var combo = new BarComboBox
        {
            ItemsSource = new[] { "Sm", "Md", "Lg" },
            Command = model.CreateCommand(),
            CommandParameter = parameter,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        combo.SelectedIndex = 0;
        host.ShowRoot(combo);
        host.RunUntilIdle();
        combo.Focus();
        combo.IsDropDownOpen = true;
        host.RunUntilIdle();

        // Click "Lg" where it actually renders (screen truth — popup surface placement is not coordinate-stable
        // across themes, cf. the Section25 click test's caveat).
        var (column, row) = FindOnScreen(host, "Lg");
        host.SendClick(column, row);
        host.RunUntilIdle();

        Assert.False(combo.IsDropDownOpen);
        Assert.Equal("Lg", model.Applied);
        Assert.Equal("Lg", parameter.Value);
        Assert.Null(model.Snapshot);
        Assert.Equal(new[] { "preview:Lg", "cancel", "commit:Lg" }, model.Log); // the click's selection previews, then commits
    }

    [Fact] // Space is an ACCEPT like Enter (WPF parity): cancel the preview, then one real commit
    public void Gallery_SpaceCommits()
    {
        var (host, model, parameter, gallery) = ShowGallery();
        using var _ = host;

        host.SendKey(Key.RightArrow); // preview "Md"
        host.RunUntilIdle();
        host.SendKey(Key.Space);      // accept it
        host.RunUntilIdle();

        Assert.False(gallery.IsDropDownOpen);
        Assert.Equal("Md", model.Applied);
        Assert.Equal("Md", parameter.Value);
        Assert.Equal(1, gallery.SelectedIndex);
        Assert.Equal(new[] { "preview:Md", "cancel", "commit:Md" }, model.Log);
    }

    [Fact] // a gated command greys the combo (the CD25 coupling every command source has) and recovers on release
    public void GatedCommand_GreysTheCombo()
    {
        var host = NewHost();
        using var _ = host;
        var gated = false;
        var command = new BarCommand(_ => { }, canExecute: _ => !gated);
        var combo = new BarComboBox
        {
            ItemsSource = new[] { "Sm", "Md", "Lg" },
            Command = command,
            CommandParameter = new ValueCommandParameter<string>("Sm"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(combo);
        host.RunUntilIdle();
        Assert.True(combo.IsEffectivelyEnabled);

        gated = true;
        command.RaiseCanExecuteChanged();
        host.RunUntilIdle();
        Assert.False(combo.IsEffectivelyEnabled); // greyed off the one CanExecuteChanged signal

        gated = false;
        command.RaiseCanExecuteChanged();
        host.RunUntilIdle();
        Assert.True(combo.IsEffectivelyEnabled);
    }

    // An ICommand that knows NOTHING about previewing (not IPreviewableCommand): the session must never send it a
    // preview verb — structurally it cannot mis-execute a preview as a commit — yet the value channel still commits
    // through Execute.
    private sealed class PlainCommand : ICommand
    {
        public readonly List<string> Log = [];
        public event EventHandler? CanExecuteChanged { add { } remove { } } // never raised — satisfies ICommand
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => Log.Add($"execute:{((IValueCommandParameter)parameter!).Value}");
    }

    [Fact] // an UNAWARE command gets NO preview verbs: highlighting shows nothing tentative, the commit still lands
           // through Execute (the value channel works with any ICommand), and a dismissal still restores the face
    public void UnawareCommand_NoPreview_CommitStillLands()
    {
        var host = NewHost();
        using var _ = host;
        var command = new PlainCommand();
        var parameter = new ValueCommandParameter<string>("Sm");
        var gallery = new BarGallery
        {
            ItemsSource = new[] { "Sm", "Md", "Lg" },
            Command = command,
            CommandParameter = parameter,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        gallery.SelectedIndex = 0;
        host.ShowRoot(gallery);
        host.RunUntilIdle();
        gallery.Focus();
        gallery.IsDropDownOpen = true;
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow);        // highlight "Md" — nothing tentative may be shown
        host.RunUntilIdle();
        Assert.Empty(command.Log);           // no preview verb reached the unaware command
        Assert.Null(parameter.PreviewValue); // and its parameter carries no candidate

        host.SendKey(Key.Enter);             // the commit still lands, through plain Execute
        host.RunUntilIdle();
        Assert.Equal(new[] { "execute:Md" }, command.Log);
        Assert.Equal("Md", parameter.Value);
        Assert.Equal(1, gallery.SelectedIndex);

        gallery.IsDropDownOpen = true;       // … and a dismissal still restores the face (the value session applies)
        host.RunUntilIdle();
        host.SendKey(Key.RightArrow);        // highlight "Lg"
        host.RunUntilIdle();
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.Equal(1, gallery.SelectedIndex);           // back to the committed "Md"
        Assert.Equal(new[] { "execute:Md" }, command.Log); // no further executes
    }

    [Fact] // a value parameter WITHOUT a command is inert: no session, no restore, and the parameter is never touched
    public void ValueParameterWithoutCommand_IsInert()
    {
        var host = NewHost();
        using var _ = host;
        var parameter = new ValueCommandParameter<string>("Sm");
        var combo = new BarComboBox
        {
            ItemsSource = new[] { "Sm", "Md", "Lg" },
            CommandParameter = parameter, // no Command — the pair is required for the value routing
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        combo.SelectedIndex = 0;
        host.ShowRoot(combo);
        host.RunUntilIdle();
        combo.Focus();
        combo.IsDropDownOpen = true;
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.False(combo.IsDropDownOpen);
        Assert.Equal(1, combo.SelectedIndex);   // Escape does NOT restore — exactly the no-parameter behavior
        Assert.Equal("Sm", parameter.Value);    // untouched
        Assert.Null(parameter.PreviewValue);    // never previewed
    }

    [Fact] // NO value parameter ⇒ exactly today's behavior: no executes, and Escape keeps the navigated selection
    public void NoValueParameter_BehavesAsBefore()
    {
        var host = NewHost();
        using var _ = host;
        var executed = 0;
        var combo = new BarComboBox
        {
            ItemsSource = new[] { "Sm", "Md", "Lg" },
            Command = new BarCommand(_ => executed++),
            CommandParameter = "plain", // NOT an IValueCommandParameter — and none is ever auto-provisioned
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        combo.SelectedIndex = 0;
        host.ShowRoot(combo);
        host.RunUntilIdle();
        combo.Focus();
        combo.IsDropDownOpen = true;
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow); // selection-follows-highlight, as always
        host.RunUntilIdle();
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal(0, executed); // navigation never executes the command

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(combo.IsDropDownOpen);
        Assert.Equal(1, combo.SelectedIndex); // Escape does NOT restore — the pre-FB behavior, byte-for-byte
        Assert.Equal(0, executed);
    }
}
