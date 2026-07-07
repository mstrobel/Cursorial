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
/// It is also the delegate-based <see cref="IPreviewableCommand"/>: supply the optional
/// <c>previewExecute</c>/<c>cancelPreviewExecute</c> delegates and a previewing control (a
/// <c>BarComboBox</c>/<c>BarGallery</c> drop-down) dry-runs the command's effect live while the user decides — see
/// the atomicity contract on <see cref="IPreviewableCommand"/>. Without them the command simply is not
/// preview-capable: the verbs no-op (nothing tentative is ever shown), while <see cref="Execute"/> still does the
/// thing for real as usual.
/// </para>
/// </summary>
public class BarCommand : IPreviewableCommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<object?>? _previewExecute;
    private readonly Action<object?>? _cancelPreviewExecute;

    /// <summary>Creates a parameterless command.</summary>
    public BarCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    /// <summary>Creates a command whose delegates receive the control's <c>CommandParameter</c>. The optional
    /// <paramref name="previewExecute"/>/<paramref name="cancelPreviewExecute"/> pair makes it preview-capable
    /// (see <see cref="IPreviewableCommand"/>): <paramref name="previewExecute"/> is the dry-run — produce the
    /// effect, commit nothing, keep enough state to restore exactly — and <paramref name="cancelPreviewExecute"/>
    /// unwinds it. <paramref name="execute"/> stays the ordinary execution, written with zero preview awareness
    /// (any active dry-run is always cancelled before it runs).</summary>
    public BarCommand(Action<object?> execute, Func<object?, bool>? canExecute = null,
                      Action<object?>? previewExecute = null, Action<object?>? cancelPreviewExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _previewExecute = previewExecute;
        _cancelPreviewExecute = cancelPreviewExecute;
    }

    /// <summary>The display label (a bar control auto-fills its content from this when its own is unset).</summary>
    public string? Text { get; init; }

    /// <summary>The icon — an <see cref="Controls.Icon"/>/icon source or a glyph string (control-interpreted).</summary>
    public object? Icon { get; init; }

    /// <summary>The accelerator hint shown beside the command (display-only; register the real
    /// <c>KeyBinding</c> separately). Mirrors <see cref="Controls.MenuItem.InputGestureText"/>.</summary>
    public string? InputGestureText { get; init; }

    /// <summary>Whether the command is a toggle (a checkable bar control reflects its checked state).</summary>
    public bool IsCheckable { get; init; }

    /// <summary>Rich hover-help body (the guide's SuperTip description). When set, a bound bar control auto-provisions
    /// a <see cref="SuperTip"/> (title = <see cref="Text"/>, shortcut = <see cref="InputGestureText"/>, body = this) as
    /// its tooltip — identical wherever the command appears. Null ⇒ no SuperTip (a plain one-line tip, if any, stands).</summary>
    public object? Description { get; init; }

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <summary>Re-queries <see cref="CanExecute"/> on every bound control (raise when the gating state changes).</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc/>
    /// <remarks>Does the thing for real — the ordinary execution, needing zero preview awareness: a previewing
    /// control cancels any active dry-run BEFORE executing (see <see cref="IPreviewableCommand"/>), so nothing is
    /// ever pending here and a dry-run can never be mis-executed as the real thing. Self-gated by
    /// <see cref="CanExecute"/>.</remarks>
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        _execute(parameter);

        // Re-query after running (WPF CommandManager-like), so a checkable parameter's new state — toggled inside
        // Execute — re-syncs every bound control's checked/enabled visual without the author raising it by hand.
        RaiseCanExecuteChanged();
    }

    /// <inheritdoc/>
    /// <remarks>The dry-run: self-gated by <see cref="CanExecute"/> — a gated command acquires no NEW tentative
    /// state (the <see cref="IPreviewableCommand"/> atomicity contract). A no-op without a <c>previewExecute</c>
    /// delegate. Deliberately does not auto-raise <c>CanExecuteChanged</c>: a dry-run is transient — the definitive
    /// re-query rides the real <see cref="Execute"/>, and a cancel restores the state bound controls already
    /// reflect.</remarks>
    public void Preview(object? parameter)
    {
        if (_previewExecute is null || !CanExecute(parameter))
            return;

        _previewExecute(parameter);
    }

    /// <inheritdoc/>
    /// <remarks>NEVER gated (the <see cref="IPreviewableCommand"/> atomicity contract: unwinding an applied dry-run
    /// is a cleanup obligation that must stay deliverable even after the command gates itself mid-session) — and
    /// structurally separate from <see cref="Execute"/>, so the self-gate cannot swallow it. A no-op without a
    /// <c>cancelPreviewExecute</c> delegate.</remarks>
    public void CancelPreview(object? parameter) => _cancelPreviewExecute?.Invoke(parameter);
}
