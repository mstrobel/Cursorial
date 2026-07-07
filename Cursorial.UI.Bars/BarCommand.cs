using System.Windows.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// A command with the display metadata the bars guide's "define once, bind everywhere" model needs (the cell-grid
/// stand-in for WPF's <c>RoutedUICommand</c>, which Cursorial does not have): an <see cref="ICommand"/> that also
/// carries <see cref="Text"/>, an <see cref="Icon"/>, an <see cref="InputGestureText"/>, and an
/// <see cref="IsCheckable"/> flag. A single <see cref="BarCommand"/> drives a toolbar button, a (future) ribbon
/// toggle, and a menu item — each auto-filling its label / icon / gesture text from the command when those aren't
/// set explicitly on the control, so the command's text, gesture, and enabled/checked state are shared with no
/// per-surface duplication. A raw BCL <see cref="ICommand"/> still works on any bar control; the control then needs
/// its own display properties set.
/// <para>
/// <b>A bindable wrapper.</b> It is a <see cref="UIObject"/> whose behavior lives in the BINDABLE inner
/// <see cref="Command"/> (<c>&lt;BarCommand Command="{Binding SaveCommand}"/&gt;</c> — the view-model owns the
/// <see cref="ICommand"/>, the <see cref="BarCommand"/> owns the Bars display metadata). Every
/// <see cref="ICommand"/> member forwards to the inner command; while <see cref="Command"/> is <see langword="null"/>
/// nothing can run, so <see cref="CanExecute"/> answers <see langword="false"/> (bound surfaces grey until a command
/// binds). The inner's <c>CanExecuteChanged</c> is surfaced through the wrapper's, a runtime <see cref="Command"/>
/// swap rewires the subscription and raises it (every bound control re-queries immediately), and
/// <see cref="Execute"/> follows a successful run with a COURTESY re-raise — so even a raw inner
/// <see cref="ICommand"/> that never raises still re-syncs every bound control's enabled/checked state (the FB-27
/// convenience). The delegate constructors are sugar: they wrap the delegates in a <see cref="DelegateCommand"/>
/// assigned to <see cref="Command"/>.
/// </para>
/// <para>
/// It is also an <see cref="IPreviewableCommand"/> <em>by delegation</em> (implemented explicitly — the verbs are
/// for the previewing machinery, not the app-facing wrapper surface):
/// <see cref="IPreviewableCommand.CanPreview"/> answers whether the INNER command is itself preview-capable, so a
/// probing control never starts a dry-run session the inner cannot honor, and the verbs forward only then (Preview
/// self-gated on <see cref="CanExecute"/>; CancelPreview NEVER gated — the atomicity contract on
/// <see cref="IPreviewableCommand"/>). Supply the optional <c>previewExecute</c>/<c>cancelPreviewExecute</c>
/// constructor delegates and the sugared inner <see cref="DelegateCommand"/> is preview-capable; without them it
/// simply is not (nothing tentative is ever shown), while <see cref="Execute"/> still does the thing for real as
/// usual.
/// </para>
/// </summary>
public class BarCommand : UIObject, IPreviewableCommand
{
    /// <summary>The inner command the wrapper forwards to — the behavior, typically a view-model
    /// <see cref="ICommand"/> (bindable; the point of the wrapper). Swapping it rewires the
    /// <see cref="CanExecuteChanged"/> subscription and raises it so every bound control re-queries immediately.
    /// While <see langword="null"/>, <see cref="CanExecute"/> is <see langword="false"/> (nothing can run).</summary>
    public static readonly StyledProperty<ICommand?> CommandProperty =
        UIProperty.Register<BarCommand, ICommand?>(nameof(Command), changed: OnCommandChanged);

    /// <summary>The display label (a bar control auto-fills its content from this when its own is unset).</summary>
    public static readonly StyledProperty<string?> TextProperty =
        UIProperty.Register<BarCommand, string?>(nameof(Text));

    /// <summary>The icon — an <see cref="Controls.Icon"/>/icon source or a glyph string (control-interpreted).</summary>
    public static readonly StyledProperty<object?> IconProperty =
        UIProperty.Register<BarCommand, object?>(nameof(Icon));

    /// <summary>The accelerator hint shown beside the command (display-only; register the real
    /// <c>KeyBinding</c> separately). Mirrors <see cref="Controls.MenuItem.InputGestureText"/>.</summary>
    public static readonly StyledProperty<string?> InputGestureTextProperty =
        UIProperty.Register<BarCommand, string?>(nameof(InputGestureText));

    /// <summary>Whether the command is a toggle (a checkable bar control reflects its checked state).</summary>
    public static readonly StyledProperty<bool> IsCheckableProperty =
        UIProperty.Register<BarCommand, bool>(nameof(IsCheckable));

    /// <summary>Rich hover-help body (the guide's SuperTip description). When set, a bound bar control auto-provisions
    /// a <see cref="SuperTip"/> (title = <see cref="Text"/>, shortcut = <see cref="InputGestureText"/>, body = this) as
    /// its tooltip — identical wherever the command appears. Null ⇒ no SuperTip (a plain one-line tip, if any, stands).</summary>
    public static readonly StyledProperty<object?> DescriptionProperty =
        UIProperty.Register<BarCommand, object?>(nameof(Description));

    /// <summary>Creates an empty command shell (XAML-friendly): set/bind <see cref="Command"/> for the behavior and
    /// the metadata properties for the display.</summary>
    public BarCommand()
    {
    }

    /// <summary>Creates a command over a parameterless delegate pair (sugar: wraps them in a
    /// <see cref="DelegateCommand"/> assigned to <see cref="Command"/>).</summary>
    public BarCommand(Action execute, Func<bool>? canExecute = null)
        => Command = new DelegateCommand(execute, canExecute);

    /// <summary>Creates a command whose delegates receive the control's <c>CommandParameter</c> (sugar: wraps them in
    /// a <see cref="DelegateCommand"/> assigned to <see cref="Command"/>). The optional
    /// <paramref name="previewExecute"/>/<paramref name="cancelPreviewExecute"/> pair makes the inner command
    /// preview-capable (see <see cref="IPreviewableCommand"/>): <paramref name="previewExecute"/> is the dry-run —
    /// produce the effect, commit nothing, keep enough state to restore exactly — and
    /// <paramref name="cancelPreviewExecute"/> unwinds it. <paramref name="execute"/> stays the ordinary execution,
    /// written with zero preview awareness (any active dry-run is always cancelled before it runs).</summary>
    public BarCommand(Action<object?> execute, Func<object?, bool>? canExecute = null,
                      Action<object?>? previewExecute = null, Action<object?>? cancelPreviewExecute = null)
        => Command = new DelegateCommand(execute, canExecute, previewExecute, cancelPreviewExecute);

    /// <inheritdoc cref="CommandProperty"/>
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <inheritdoc cref="TextProperty"/>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <inheritdoc cref="IconProperty"/>
    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <inheritdoc cref="InputGestureTextProperty"/>
    public string? InputGestureText { get => GetValue(InputGestureTextProperty); set => SetValue(InputGestureTextProperty, value); }

    /// <inheritdoc cref="IsCheckableProperty"/>
    public bool IsCheckable { get => GetValue(IsCheckableProperty); set => SetValue(IsCheckableProperty, value); }

    /// <inheritdoc cref="DescriptionProperty"/>
    public object? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <summary>Re-queries <see cref="CanExecute"/> on every bound control (raise when the gating state changes).
    /// The inner command's own raises are forwarded here automatically.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc/>
    /// <remarks>Forwards to the inner <see cref="Command"/>; <see langword="false"/> while it is
    /// <see langword="null"/> (nothing can run, so bound surfaces grey until a command binds).</remarks>
    public bool CanExecute(object? parameter) => Command is { } inner && inner.CanExecute(parameter);

    /// <inheritdoc/>
    /// <remarks>Forwards to the inner <see cref="Command"/> — doing the thing for real, with zero preview awareness
    /// (a previewing control cancels any active dry-run BEFORE executing; see <see cref="IPreviewableCommand"/>).
    /// Self-gated by <see cref="CanExecute"/>, then followed by a COURTESY <see cref="RaiseCanExecuteChanged"/>
    /// (WPF CommandManager-like), so a checkable/value parameter mutated inside the inner's execute re-syncs every
    /// bound control even when the inner never raises its own event.</remarks>
    public void Execute(object? parameter)
    {
        if (Command is not { } inner || !inner.CanExecute(parameter))
            return;

        inner.Execute(parameter);
        RaiseCanExecuteChanged();
    }

    // ───────────────────────────── IPreviewableCommand (explicit — delegation only) ─────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Honest forwarding: <see langword="true"/> only when the INNER command is itself preview-capable —
    /// a wrapper must never advertise a capability its inner cannot honor (the probe never lies).</remarks>
    bool IPreviewableCommand.CanPreview => Command is IPreviewableCommand { CanPreview: true };

    /// <inheritdoc/>
    /// <remarks>Forwards the dry-run to a preview-capable inner, self-gated by <see cref="CanExecute"/> (a gated
    /// command acquires no NEW tentative state — the <see cref="IPreviewableCommand"/> atomicity contract).</remarks>
    void IPreviewableCommand.Preview(object? parameter)
    {
        if (Command is not IPreviewableCommand { CanPreview: true } inner || !inner.CanExecute(parameter))
            return;

        inner.Preview(parameter);
    }

    /// <inheritdoc/>
    /// <remarks>Forwards the unwind to a preview-capable inner. NEVER gated (the <see cref="IPreviewableCommand"/>
    /// atomicity contract: the cleanup obligation must stay deliverable even after the command gates itself
    /// mid-session) — and structurally separate from <see cref="Execute"/>, so no gate can swallow it.</remarks>
    void IPreviewableCommand.CancelPreview(object? parameter)
    {
        if (Command is IPreviewableCommand { CanPreview: true } inner)
            inner.CancelPreview(parameter);
    }

    // ───────────────────────────── inner-command plumbing ─────────────────────────────

    // Surface the inner's CanExecuteChanged through the wrapper's (bound controls subscribe to the WRAPPER — it is
    // their ICommand). On a swap (Command is bindable): unhook the old inner, hook the new, and raise — the swap
    // itself changes effective executability, so every bound control must re-query immediately.
    private static void OnCommandChanged(UIObject sender, ICommand? oldValue, ICommand? newValue)
    {
        if (sender is not BarCommand command)
            return;

        if (oldValue is { } old)
            old.CanExecuteChanged -= command.OnInnerCanExecuteChanged;

        if (newValue is { } @new)
            @new.CanExecuteChanged += command.OnInnerCanExecuteChanged;

        command.RaiseCanExecuteChanged();
    }

    private void OnInnerCanExecuteChanged(object? sender, EventArgs e) => RaiseCanExecuteChanged();
}
