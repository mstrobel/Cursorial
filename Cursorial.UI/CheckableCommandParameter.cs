using System.ComponentModel;

namespace Cursorial.UI;

/// <summary>
/// A command parameter that carries a <b>checked</b> state a checkable control (a
/// <see cref="Controls.ToggleButton"/> and its family, a checkable bar surface) reflects — so the command (or its
/// view-model) owns the checked state and every bound surface stays in sync <em>for free</em>, with no separate
/// <c>IsChecked</c> binding. The command raises <c>CanExecuteChanged</c> whenever any of these change (the same signal
/// it already raises to update enabled state); each bound control re-queries and re-coerces on that one signal.
/// <para>
/// Two cooperating channels, both consulted by the toggle's <see cref="Controls.ToggleButton.IsChecked"/> coercion:
/// <list type="bullet">
/// <item><see cref="IsChecked"/> — the ordinary reflected state: the command's <c>Execute</c> mutates it and the
/// control reflects it as its <c>IsChecked</c> <em>base</em> value. The backward-compatible carrier.</item>
/// <item><see cref="Handled"/> + <see cref="IsCheckedOverride"/> — a <b>context gate</b>: while <see cref="Handled"/>
/// the control's effective checked state is FORCED to <see cref="IsCheckedOverride"/> (an override at either
/// polarity — greyed+unchecked, or "on but locked"), and the control's own base preference reappears automatically
/// the moment <see cref="Handled"/> clears. Pair with a <see langword="false"/> <c>CanExecute</c> to grey+lock.</item>
/// </list>
/// </para>
/// Get-only by design: the control READS the state; the command's <c>Execute</c> (or the view-model) MUTATES it.
/// Use <see cref="CheckableCommandParameter"/> for the common mutable carrier.
/// </summary>
public interface ICheckableCommandParameter
{
    /// <summary>The current reflected checked state the bound checkable control mirrors as its <c>IsChecked</c> base value.</summary>
    bool? IsChecked { get; }

    /// <summary>
    /// Whether the command has <b>taken over</b> the checked state: while <see langword="true"/> the bound control's
    /// effective <c>IsChecked</c> is coerced to <see cref="IsCheckedOverride"/>, regardless of the control's own base
    /// preference — which reappears automatically when this clears. Default <see langword="false"/> (the control's own
    /// state stands — the backward-compatible path, so existing checkable commands are unaffected).
    /// </summary>
    bool Handled => false;

    /// <summary>
    /// The value the effective checked state is FORCED to while <see cref="Handled"/> — parameter-specified, so both
    /// greyed+unchecked (<see langword="false"/>) and greyed+checked / "on but locked" (<see langword="true"/>) are
    /// expressible. Ignored while <see cref="Handled"/> is <see langword="false"/>. Default <see langword="false"/>.
    /// </summary>
    bool? IsCheckedOverride => false;
}

/// <summary>
/// The default mutable <see cref="ICheckableCommandParameter"/> — the simplest checked-state carrier to hand a command
/// as its parameter. It also raises <see cref="INotifyPropertyChanged"/> so it can be data-bound directly. Toggle it
/// (or set <see cref="IsChecked"/>) from the command's <c>Execute</c> and every bound control re-syncs; take it over
/// with <see cref="Override"/> to grey/lock a bound control (and <see cref="Release"/> to give the control back its
/// own preference) — after either, raise the command's <c>CanExecuteChanged</c> so bound controls re-coerce.
/// </summary>
public class CheckableCommandParameter(bool? isChecked = false) : ICheckableCommandParameter, INotifyPropertyChanged
{
    /// <inheritdoc cref="ICheckableCommandParameter.IsChecked"/>
    public bool? IsChecked { get; set => Set(ref field, value, IsCheckedChangedArgs); } = isChecked;

    /// <inheritdoc cref="ICheckableCommandParameter.Handled"/>
    public bool Handled { get; set => Set(ref field, value, HandledChangedArgs); }

    /// <inheritdoc cref="ICheckableCommandParameter.IsCheckedOverride"/>
    public bool? IsCheckedOverride { get; set => Set(ref field, value, IsCheckedOverrideChangedArgs); }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Flips <see cref="IsChecked"/> (the common <c>Execute</c> body for a toggle command).</summary>
    public void Toggle() => IsChecked = !IsChecked;

    /// <summary>
    /// Takes over the checked state (context-gate the control): forces the bound control's effective checked state to
    /// <paramref name="isChecked"/> by setting <see cref="IsCheckedOverride"/> then <see cref="Handled"/>. Raise the
    /// command's <c>CanExecuteChanged</c> afterward (and gate its <c>CanExecute</c> to <see langword="false"/> to
    /// grey+lock) so bound controls re-coerce.
    /// </summary>
    public void Override(bool? isChecked)
    {
        IsCheckedOverride = isChecked;
        Handled = true;
    }

    /// <summary>Releases the override (clears <see cref="Handled"/>) so the control's own base preference reappears.</summary>
    public void Release() => Handled = false;

    private void Set<T>(ref T field, T value, PropertyChangedEventArgs args)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, args);
    }

    private static readonly PropertyChangedEventArgs IsCheckedChangedArgs = new(nameof(IsChecked));
    private static readonly PropertyChangedEventArgs HandledChangedArgs = new(nameof(Handled));
    private static readonly PropertyChangedEventArgs IsCheckedOverrideChangedArgs = new(nameof(IsCheckedOverride));
}
