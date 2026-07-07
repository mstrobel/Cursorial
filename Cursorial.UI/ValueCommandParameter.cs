using System.ComponentModel;

namespace Cursorial.UI;

/// <summary>
/// The action a value-carrying command parameter is asking the command to take (the Actipro
/// <c>ValueCommandParameterAction</c> shape): the ordinary <see cref="Commit"/>, or the live-preview pair
/// <see cref="Preview"/> / <see cref="CancelPreview"/>. A control that supports live preview (a
/// <c>BarComboBox</c>/<c>BarGallery</c> drop-down) sets this on the shared <see cref="IValueCommandParameter"/>
/// before each <c>Execute</c> and resets it to <see cref="Commit"/> afterward, so a surface that never previews
/// (a plain button click routing the same command) always executes as a commit.
/// </summary>
public enum ValueCommandParameterAction
{
    /// <summary>Apply <see cref="IValueCommandParameter.Value"/> for real — one real operation (one undo unit at the
    /// consumer). The default: <c>default(ValueCommandParameterAction)</c> is a commit.</summary>
    Commit,

    /// <summary>Apply <see cref="IValueCommandParameter.PreviewValue"/> <b>without committing</b> — show the outcome
    /// live while the user is still deciding. The command must keep enough state to restore exactly on
    /// <see cref="CancelPreview"/>; a later <see cref="Preview"/> re-previews (replaces the previous preview, not
    /// stacks). Preview must never dirty the model permanently.</summary>
    Preview,

    /// <summary>Withdraw the active preview and restore the exact pre-preview state. A previewing control always
    /// sends this before a <see cref="Commit"/> too, so the commit handler never has to unwind preview state itself —
    /// cancel restores, then commit applies once.</summary>
    CancelPreview,
}

/// <summary>
/// The non-generic surface of a <see cref="ValueCommandParameter{T}"/> — the contract a framework control uses to
/// contribute a value to (and drive a live preview through) a shared command <em>without knowing the value type</em>:
/// a <c>BarComboBox</c>/<c>BarGallery</c> writes the highlighted item into <see cref="PreviewValue"/> (and the
/// committed item into <see cref="Value"/>) as plain <see cref="object"/>s, sets <see cref="Action"/>, and executes
/// the command. Implemented explicitly by <see cref="ValueCommandParameter{T}"/>, which converts to/from <c>T</c>.
/// </summary>
public interface IValueCommandParameter
{
    /// <summary>The current <b>committed</b> value. The command's <c>CanExecute</c> compares this to the model's
    /// current value on each re-query (the radio pattern); its <c>Execute</c> applies it on
    /// <see cref="ValueCommandParameterAction.Commit"/>. Setting via this non-generic surface casts to the
    /// underlying <c>T</c> — a wrong-typed value throws <see cref="InvalidCastException"/>, and <see langword="null"/>
    /// throws <see cref="NullReferenceException"/> when <c>T</c> is a non-nullable value type (a committed value is
    /// never silently defaulted; use <see cref="PreviewValue"/> for a clearable candidate).</summary>
    object? Value { get; set; }

    /// <summary>The <b>candidate</b> value under the user's highlight — what
    /// <see cref="ValueCommandParameterAction.Preview"/> applies. Never consulted for the checked/radio state;
    /// <see cref="Value"/> stays the committed value throughout a preview session.</summary>
    object? PreviewValue { get; set; }

    /// <summary>What the next <c>Execute</c> should do with the values (default
    /// <see cref="ValueCommandParameterAction.Commit"/>). A previewing control sets it for the duration of a single
    /// <c>Execute</c> and resets it to <c>Commit</c> — never leave a non-commit action on a shared parameter.</summary>
    ValueCommandParameterAction Action { get; set; }
}

/// <summary>
/// A <see cref="CheckableCommandParameter"/> that also carries a <b>value</b> the bound control contributes when the
/// shared command executes (the Actipro <c>ValueCommandParameter&lt;T&gt;</c> pattern): several controls bind to
/// <em>one</em> command, each with its <em>own</em> parameter instance holding a distinct <see cref="Value"/>, and
/// the command reads the parameter to know which control is talking to it.
/// <para>
/// <b>The radio set.</b> Because it inherits the FB-27 checkable machinery, a set of toggles sharing one command
/// becomes a radio group with no extra wiring: on every re-query the command's <c>CanExecute</c> receives each
/// control's parameter in turn, compares <see cref="Value"/> to the model's current value, and writes the result
/// into <see cref="CheckableCommandParameter.IsChecked"/> — the control machinery then reflects it. <c>Execute</c>
/// reads the clicked control's <see cref="Value"/> and applies it; the auto re-query re-syncs the whole set.
/// <code>
/// // Left / Center / Right — three BarToggleButtons, ONE command, one parameter each:
/// var left   = new ValueCommandParameter&lt;TextAlignment&gt;(TextAlignment.Left);
/// var center = new ValueCommandParameter&lt;TextAlignment&gt;(TextAlignment.Center);
/// var right  = new ValueCommandParameter&lt;TextAlignment&gt;(TextAlignment.Right);
/// var align = new BarCommand(
///     execute: p =&gt; model.Alignment = ((ValueCommandParameter&lt;TextAlignment&gt;)p!).Value,
///     canExecute: p =&gt;
///     {
///         if (p is ValueCommandParameter&lt;TextAlignment&gt; vp)          // pattern-match: the wiring re-query
///             vp.IsChecked = vp.Value == model.Alignment;              //   can arrive before the parameter is set
///         return true;                                                 // exactly the matching button shows checked
///     });
/// // new BarToggleButton { Command = align, CommandParameter = left } … etc.; when the caret moves,
/// // call align.RaiseCanExecuteChanged() and every button re-syncs.
/// </code>
/// (A plain <see cref="Controls.ToggleButton"/> outside the Bars layer reflects only the
/// <see cref="CheckableCommandParameter.Handled"/>/<see cref="CheckableCommandParameter.IsCheckedOverride"/> coercion
/// channel — an unhandled parameter is a backward-compatible pass-through there — so a framework-level radio set
/// writes <c>vp.Override(matches)</c> instead of <c>vp.IsChecked</c> in the same place.)
/// </para>
/// <para>
/// <b>Live preview.</b> The same parameter drives preview-before-commit (see
/// <see cref="ValueCommandParameterAction"/>): a previewing control (<c>BarComboBox</c>/<c>BarGallery</c>) writes the
/// highlighted value into <see cref="PreviewValue"/> and executes with <see cref="Action"/> =
/// <see cref="ValueCommandParameterAction.Preview"/>; a dismissal executes
/// <see cref="ValueCommandParameterAction.CancelPreview"/> when a preview ran (restore exactly); a commit executes
/// <c>CancelPreview</c> (again, only when a preview ran) then <see cref="ValueCommandParameterAction.Commit"/> with
/// <see cref="Value"/> set — so the consumer's commit is always one real operation on unpreviewed state.
/// </para>
/// Raises <see cref="INotifyPropertyChanged"/> for <see cref="Value"/>/<see cref="PreviewValue"/>/<see cref="Action"/>
/// (as the base does for its members) so the parameter can be data-bound directly. The FB-27 context-gate composes
/// unchanged: <see cref="CheckableCommandParameter.Override"/> a whole set (each parameter) to grey+force it while a
/// context makes the value inapplicable, and <see cref="CheckableCommandParameter.Release"/> to give the radio state
/// back.
/// </summary>
/// <typeparam name="T">The value type the control contributes (an enum such as a heading level or alignment, a
/// brush/color choice, a size…).</typeparam>
/// <param name="value">The value this control contributes to the shared command.</param>
/// <param name="isChecked">The initial reflected checked state (see
/// <see cref="CheckableCommandParameter.IsChecked"/>); the first re-query normally overwrites it.</param>
public class ValueCommandParameter<T>(T value, bool isChecked = false)
    : CheckableCommandParameter(isChecked), IValueCommandParameter
{
    /// <inheritdoc cref="IValueCommandParameter.Value"/>
    public T Value { get; set => Set(ref field, value, ValueChangedArgs); } = value;

    /// <inheritdoc cref="IValueCommandParameter.PreviewValue"/>
    public T? PreviewValue { get; set => Set(ref field, value, PreviewValueChangedArgs); }

    /// <inheritdoc cref="IValueCommandParameter.Action"/>
    public ValueCommandParameterAction Action { get; set => Set(ref field, value, ActionChangedArgs); }

    // The non-generic surface (framework controls write object?s without knowing T; the typed properties stay the
    // app-facing API). The Value cast is unforgiving by design — a wrong-typed item in a value-bound picker is a
    // programming error, not a state.
    object? IValueCommandParameter.Value { get => Value; set => Value = (T)value!; }

    /// <inheritdoc cref="IValueCommandParameter.PreviewValue"/>
    object? IValueCommandParameter.PreviewValue
    {
        get => PreviewValue;
        // null clears the candidate: default(T) — a straight (T?) cast would throw unboxing null for a value-type T.
        set => PreviewValue = value is null ? default : (T?)value;
    }

    private static readonly PropertyChangedEventArgs ValueChangedArgs = new(nameof(Value));
    private static readonly PropertyChangedEventArgs PreviewValueChangedArgs = new(nameof(PreviewValue));
    private static readonly PropertyChangedEventArgs ActionChangedArgs = new(nameof(Action));
}
