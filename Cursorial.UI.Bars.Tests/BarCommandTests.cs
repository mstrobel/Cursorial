using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Bars;

// BarCommand as a BINDABLE WRAPPER (a UIObject with a Command StyledProperty holding the behavior): every ICommand
// member forwards to the inner command, the inner's CanExecuteChanged surfaces through the wrapper's, a runtime
// inner SWAP rewires the subscription and raises so every bound control re-queries immediately, Execute adds the
// COURTESY re-raise (so a raw inner ICommand that never raises still re-syncs bound controls — the FB-27
// convenience), and the delegate constructors are sugar over an inner DelegateCommand (regression-pinned).
// IPreviewableCommand is implemented explicitly by delegation — CanPreview answers from the INNER command, so the
// dry-run session probe never lies.
public sealed class BarCommandTests
{
    private static UIHeadlessHost NewHost(int w = 30, int h = 12) =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(w, h), Capabilities = HeadlessCapabilities.KittyTruecolor });

    // A raw BCL ICommand that CANNOT raise CanExecuteChanged (the event is a black hole) — the worst-case inner.
    private sealed class RawCommand : ICommand
    {
        public bool CanExecuteResult = true;
        public int Executes;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => CanExecuteResult;
        public void Execute(object? parameter) => Executes++;
    }

    [Fact] // the wrapper forwards CanExecute/Execute to the inner; a NULL inner means nothing can run (CanExecute false)
    public void Forwards_ToInner_NullInnerCannotRun()
    {
        var shell = new BarCommand(); // XAML-friendly empty shell
        Assert.False(shell.CanExecute(null));
        shell.Execute(null); // no-throw no-op

        var raw = new RawCommand();
        shell.Command = raw;
        Assert.True(shell.CanExecute(null));
        shell.Execute(null);
        Assert.Equal(1, raw.Executes);
    }

    [Fact] // Execute adds the COURTESY re-raise, and the inner's own raises surface through the wrapper's event
    public void CourtesyRaise_AndInnerRaise_SurfaceThroughWrapper()
    {
        var raw = new RawCommand(); // cannot raise anything itself
        var command = new BarCommand { Command = raw };
        var raised = 0;
        command.CanExecuteChanged += (_, _) => raised++;

        command.Execute(null); // a successful run is followed by the courtesy raise — even over a mute inner
        Assert.Equal(1, raised);

        raw.CanExecuteResult = false;
        command.Execute(null); // a refused run raises nothing (nothing ran, nothing changed)
        Assert.Equal(1, raised);

        var inner = new DelegateCommand(() => { });
        command.Command = inner; // swap raises (see the swap test) …
        raised = 0;
        inner.RaiseCanExecuteChanged(); // … and the new inner's own raises forward through the wrapper
        Assert.Equal(1, raised);
    }

    [Fact] // an inner SWAP rewires the subscription and re-raises: bound controls re-query with no manual raise
    public void InnerSwap_Raises_AndBoundControlsRequery()
    {
        using var host = NewHost();
        var command = new BarCommand { Command = new DelegateCommand(() => { }, canExecute: () => false) };
        var button = new BarButton
        {
            Content = "B",
            Command = command,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        host.RunUntilIdle();
        Assert.False(button.IsEffectivelyEnabled); // gated inner ⇒ greyed

        var old = (DelegateCommand)command.Command!;
        command.Command = new DelegateCommand(() => { }); // swap to an ungated inner — NO manual raise anywhere
        host.RunUntilIdle();
        Assert.True(button.IsEffectivelyEnabled); // the swap's own raise re-queried the bound control

        var raised = 0;
        command.CanExecuteChanged += (_, _) => raised++;
        old.RaiseCanExecuteChanged(); // the OLD inner is unhooked — its raises no longer reach the wrapper
        Assert.Equal(0, raised);

        command.Command = null; // swap to nothing: raises again, and the control greys (nothing can run)
        host.RunUntilIdle();
        Assert.Equal(1, raised);
        Assert.False(button.IsEffectivelyEnabled);
    }

    [Fact] // PROBE HONESTY: a wrapped NON-previewable inner answers CanPreview=false and the dry-run session never starts
    public void WrappedNonPreviewableInner_NoDryRunSession()
    {
        using var host = NewHost();
        var log = new List<string>();
        var parameter = new ValueCommandParameter<string>("Sm");
        var command = new BarCommand
        {
            Command = new DelegateCommand(p => log.Add($"execute:{((IValueCommandParameter)p!).Value}")),
        };
        Assert.False(((IPreviewableCommand)command).CanPreview); // honest: the inner cannot dry-run

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

        host.SendKey(Key.RightArrow); // highlight "Md" — no session verbs may flow
        host.RunUntilIdle();
        Assert.Empty(log);
        Assert.Null(parameter.PreviewValue);

        host.SendKey(Key.Enter); // the value commit still lands through Execute
        host.RunUntilIdle();
        Assert.Equal(new[] { "execute:Md" }, log);
        Assert.Equal("Md", parameter.Value);
    }

    [Fact] // a wrapped PREVIEWABLE inner runs the full dry-run session THROUGH the wrapper (verbs forwarded)
    public void WrappedPreviewableInner_FullDryRunSession()
    {
        using var host = NewHost();
        var applied = "Sm";
        string? snapshot = null;
        var parameter = new ValueCommandParameter<string>("Sm");
        var command = new BarCommand
        {
            Command = new DelegateCommand(
                p => applied = (string)((IValueCommandParameter)p!).Value!,
                canExecute: null,
                previewExecute: p =>
                {
                    snapshot ??= applied;
                    applied = (string)((IValueCommandParameter)p!).PreviewValue!;
                },
                cancelPreviewExecute: _ =>
                {
                    if (snapshot is not null)
                    {
                        applied = snapshot;
                        snapshot = null;
                    }
                }),
        };
        Assert.True(((IPreviewableCommand)command).CanPreview); // forwarded from the capable inner

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

        host.SendKey(Key.RightArrow); // dry-run "Md" through the wrapper
        host.RunUntilIdle();
        Assert.Equal("Md", applied);
        Assert.Equal("Sm", snapshot);

        host.SendKey(Key.Escape);     // unwind byte-exact + face restore
        host.RunUntilIdle();
        Assert.Equal("Sm", applied);
        Assert.Null(snapshot);
        Assert.Equal(0, gallery.SelectedIndex);

        gallery.IsDropDownOpen = true; // … and a commit gesture executes for real after the unwind
        host.RunUntilIdle();
        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal("Md", applied);
        Assert.Null(snapshot);
        Assert.Equal("Md", parameter.Value);
    }

    [Fact] // REGRESSION-PIN: the delegate constructors are sugar over an inner DelegateCommand and behave as before
    public void DelegateCtorSugar_BehavesAsBefore()
    {
        var executed = 0;
        var gated = false;
        var command = new BarCommand(p => executed++, canExecute: _ => !gated) { Text = "_Bold", IsCheckable = true };

        Assert.IsType<DelegateCommand>(command.Command); // the sugar wraps an inner DelegateCommand
        Assert.Equal("_Bold", command.Text);             // metadata object-initializers still work (now bindable)
        Assert.True(command.IsCheckable);
        Assert.False(((IPreviewableCommand)command).CanPreview); // no preview delegates ⇒ not preview-capable

        var raised = 0;
        command.CanExecuteChanged += (_, _) => raised++;
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        Assert.Equal(1, executed);
        Assert.Equal(1, raised); // the post-execute courtesy raise (the pre-wrapper BarCommand behavior)

        gated = true;
        Assert.False(command.CanExecute(null));
        command.Execute(null); // self-gated, exactly as before
        Assert.Equal(1, executed);

        var previewable = new BarCommand(_ => { }, null, previewExecute: _ => { }, cancelPreviewExecute: _ => { });
        Assert.True(((IPreviewableCommand)previewable).CanPreview); // preview delegates ⇒ capable, as before
    }
}
