using System.ComponentModel;

namespace Cursorial.UI.Bars;

/// <summary>
/// A command parameter that carries a <b>checked</b> state. A checkable bar control (a
/// <c>BarToggleButton</c>, a checkable split/menu entry) re-reads <see cref="IsChecked"/> every time it
/// re-queries the command's <c>CanExecute</c> — so the command (or its view-model) owns the checked state and the
/// UI stays in sync <em>for free</em>, with no separate <c>IsChecked</c> binding. The command raises
/// <c>CanExecuteChanged</c> when the state changes (the same signal it already raises to update enabled state),
/// and every bound control reflects both at once.
/// <para>
/// Get-only by design: the control READS the state; the command's <c>Execute</c> (or the view-model) MUTATES it.
/// Use <see cref="CheckableCommandParameter"/> for the common mutable carrier.
/// </para>
/// </summary>
public interface ICheckableCommandParameter
{
    /// <summary>The current checked state the bound checkable control reflects.</summary>
    bool IsChecked { get; }
}

/// <summary>
/// The default mutable <see cref="ICheckableCommandParameter"/> — the simplest checked-state carrier to hand a
/// <see cref="BarCommand"/> as its parameter. It also raises <see cref="INotifyPropertyChanged"/> so it can be
/// data-bound directly. Toggle it from the command's <c>Execute</c> (then <see cref="BarCommand.RaiseCanExecuteChanged"/>)
/// and every bound control re-syncs.
/// </summary>
public class CheckableCommandParameter(bool isChecked = false) : ICheckableCommandParameter, INotifyPropertyChanged
{
    private bool _isChecked = isChecked;

    /// <inheritdoc cref="ICheckableCommandParameter.IsChecked"/>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, IsCheckedChangedArgs);
        }
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Flips <see cref="IsChecked"/> (the common Execute body for a toggle command).</summary>
    public void Toggle() => IsChecked = !IsChecked;

    private static readonly PropertyChangedEventArgs IsCheckedChangedArgs = new(nameof(IsChecked));
}
