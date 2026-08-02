using System.Windows.Input;

namespace Cursorial.UI;

public interface IRequeryCommand : IPreviewableCommand
{
    void RaiseCanExecuteChanged();
}

/// <summary>
/// The plain delegate-backed <see cref="ICommand"/> (and, when the preview delegates are supplied, the delegate-backed
/// <see cref="IPreviewableCommand"/>): <c>execute</c>/<c>canExecute</c> for the ordinary command surface, plus the
/// optional <c>previewExecute</c>/<c>cancelPreviewExecute</c> dry-run pair — <see cref="CanPreview"/> reports whether
/// the pair was supplied, so a probing control (or a wrapping <c>BarCommand</c>) knows the capability
/// honestly. This is the building block <c>BarCommand</c>'s delegate constructors wrap; use it directly
/// anywhere a view-model command is wanted without the Bars display metadata.
/// <para>
/// Deliberately does <b>not</b> auto-raise <see cref="CanExecuteChanged"/> after <see cref="Execute"/> — it is a
/// plain building block, and the courtesy re-raise that re-syncs bound bar controls is the
/// <c>BarCommand</c> wrapper's job (raise <see cref="RaiseCanExecuteChanged"/> yourself when using it
/// standalone and the gating state changes).
/// </para>
/// </summary>
public class DelegateCommand : IRequeryCommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<object?>? _previewExecute;
    private readonly Action<object?>? _cancelPreviewExecute;

    /// <summary>Creates a parameterless command.</summary>
    public DelegateCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    /// <summary>Creates a command whose delegates receive the command parameter. The optional
    /// <paramref name="previewExecute"/>/<paramref name="cancelPreviewExecute"/> pair makes it preview-capable
    /// (<see cref="CanPreview"/>; see <see cref="IPreviewableCommand"/>): <paramref name="previewExecute"/> is the
    /// dry-run — produce the effect, commit nothing, keep enough state to restore exactly — and
    /// <paramref name="cancelPreviewExecute"/> unwinds it. <paramref name="execute"/> stays the ordinary execution,
    /// written with zero preview awareness (a previewing control always cancels the dry-run before it runs).</summary>
    public DelegateCommand(Action<object?> execute, Func<object?, bool>? canExecute = null,
                           Action<object?>? previewExecute = null, Action<object?>? cancelPreviewExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _previewExecute = previewExecute;
        _cancelPreviewExecute = cancelPreviewExecute;
    }

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <summary>Re-queries <see cref="CanExecute"/> on every bound control (raise when the gating state changes).</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc/>
    /// <remarks>Does the thing for real — see the class remarks for why there is no post-execute auto-raise here.
    /// Self-gated by <see cref="CanExecute"/>.</remarks>
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        _execute(parameter);
    }

    /// <inheritdoc/>
    /// <remarks>The structural gate: <see langword="true"/> exactly when the preview delegate pair was supplied.</remarks>
    public bool CanPreview => _previewExecute is not null;

    /// <inheritdoc/>
    /// <remarks>The dry-run: self-gated by <see cref="CanExecute"/> — a gated command acquires no NEW tentative
    /// state (the <see cref="IPreviewableCommand"/> atomicity contract). A no-op without a <c>previewExecute</c>
    /// delegate (<see cref="CanPreview"/> is <see langword="false"/>).</remarks>
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
