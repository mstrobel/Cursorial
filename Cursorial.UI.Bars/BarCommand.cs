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
/// </summary>
public class BarCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>Creates a parameterless command.</summary>
    public BarCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    /// <summary>Creates a command whose delegates receive the control's <c>CommandParameter</c>.</summary>
    public BarCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
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
    /// <remarks>Self-gated by <see cref="CanExecute"/> — except for a parameter carrying
    /// <see cref="ValueCommandParameterAction.CancelPreview"/>, which always executes. That is the preview-atomicity
    /// contract (see <see cref="IValueCommandParameter"/>): a live preview is either committed or entirely rolled
    /// back, so the rollback must stay deliverable even after the command gates itself mid-session — while a gated
    /// command still refuses NEW tentative state (<see cref="ValueCommandParameterAction.Preview"/>) and refuses
    /// commits. A custom <see cref="ICommand"/> with its own internal gate should honor the same exemption.</remarks>
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter) &&
            parameter is not IValueCommandParameter { Action: ValueCommandParameterAction.CancelPreview })
            return;

        _execute(parameter);

        // Re-query after running (WPF CommandManager-like), so a checkable parameter's new state — toggled inside
        // Execute — re-syncs every bound control's checked/enabled visual without the author raising it by hand.
        RaiseCanExecuteChanged();
    }
}
