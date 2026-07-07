using System.Windows.Input;

using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// A bar-surface combobox (the bars guide's <c>BarComboBox</c> — <c>[value ▾]</c>): the existing
/// <see cref="ComboBox"/> with a flat, compact bar face (no chrome border — the bar field tint IS the affordance). It
/// inherits every ComboBox behavior (single selection, the drop-down list, type-ahead, the editable text mode). Its
/// drop-down rows are ordinary <c>ComboBoxItem</c>s (the built-in item theme). On a <see cref="Toolbar"/> it packs and
/// overflows like any bar item.
/// <para>
/// <b>Value command + live preview.</b> Give it a <see cref="Command"/> whose <see cref="CommandParameter"/> is an
/// <see cref="IValueCommandParameter"/> (a <see cref="ValueCommandParameter{T}"/>) and the drop-down becomes a
/// previewing value picker: moving the highlight (selection-follows-highlight) writes the highlighted value into
/// <see cref="IValueCommandParameter.PreviewValue"/> and executes the command with
/// <see cref="ValueCommandParameterAction.Preview"/> — the consumer shows the outcome live <em>without committing</em>;
/// a commit (Enter / Space / item click) first executes <see cref="ValueCommandParameterAction.CancelPreview"/> when a
/// preview ran (restore exactly), then sets <see cref="IValueCommandParameter.Value"/> and executes
/// <see cref="ValueCommandParameterAction.Commit"/> — one real operation; a dismissal (Escape / light-dismiss /
/// focus-out / toggle-close) executes <c>CancelPreview</c> (again, only when a preview ran) and restores the pre-open
/// selection, so neither the model nor the face keeps an unchosen value. <see cref="IValueCommandParameter.Action"/>
/// is always reset to <see cref="ValueCommandParameterAction.Commit"/> after each of these executes, so other
/// surfaces sharing the parameter are never misrouted; every execute (and the parameter mutation that feeds it) is
/// gated by <c>CanExecute</c>, and the combo greys while <c>CanExecute</c> is false (the CD25 coupling every command
/// source has) — keep <c>CanExecute</c> stable across an open preview session, since a gate that closes mid-session
/// also gates the session's own <c>CancelPreview</c>.
/// </para>
/// <para>
/// Unlike a checkable toggle (FB-27's lazy per-control default), NO default parameter is auto-provisioned — the value
/// type <c>T</c> is not inferrable by the control, so the app supplies the <see cref="ValueCommandParameter{T}"/>
/// explicitly; without one (or without a command) the combo behaves exactly as before. Only drop-down sessions route
/// through the command: a closed-state selection change (a programmatic model sync, or face type-ahead) and editable
/// free-text commits do not execute it — the control cannot tell a user gesture from the app reflecting the model
/// back into the face, and auto-committing the latter would echo.
/// </para>
/// </summary>
public class BarComboBox : ComboBox
{
    /// <summary>The command the drop-down routes value actions to. Its <c>CanExecute</c> also gates the combo's
    /// effective enabled state (CD25 — as on <see cref="ButtonBase.CommandProperty"/>), re-queried on
    /// <c>CanExecuteChanged</c>. The value routing itself additionally requires <see cref="CommandParameter"/> to be
    /// an <see cref="IValueCommandParameter"/>.</summary>
    public static readonly StyledProperty<ICommand?> CommandProperty =
        UIProperty.Register<BarComboBox, ICommand?>(nameof(Command), changed: OnCommandChanged);

    /// <summary>The parameter passed to <see cref="Command"/> — an <see cref="IValueCommandParameter"/> enables the
    /// value/preview routing (see the class remarks); anything else leaves the combo's behavior untouched.</summary>
    public static readonly StyledProperty<object?> CommandParameterProperty =
        UIProperty.Register<BarComboBox, object?>(nameof(CommandParameter), changed: OnCommandParameterChanged);

    private bool _valueSession;        // the current drop-down session opened with a live value parameter
    private int _selectionAtOpen = -1; // the pre-open selection a dismissal restores (−1 = none)
    private bool _previewActive;       // a Preview actually executed this session (so the close owes a CancelPreview)

    /// <summary>Creates a bar combo box.</summary>
    public BarComboBox() => SelectionChanged += OnSelectionChangedPreview;

    /// <inheritdoc cref="CommandProperty"/>
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <inheritdoc cref="CommandParameterProperty"/>
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

    // The value parameter the drop-down routes through — non-null only when BOTH a command and an
    // IValueCommandParameter parameter are present (no default is ever provisioned: T is the app's to choose).
    private IValueCommandParameter? ValueParameter
        => Command is not null ? CommandParameter as IValueCommandParameter : null;

    // ───────────────────────────── command enabled coupling (CD25, ButtonBase parity) ─────────────────────────────

    /// <inheritdoc cref="ButtonBase.IsEnabledCore"/>
    protected override bool IsEnabledCore
        => Command is not { } command || command.CanExecute(CommandParameter);

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        if (Command is { } command)
            command.CanExecuteChanged += OnCanExecuteChanged;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (Command is { } command)
            command.CanExecuteChanged -= OnCanExecuteChanged;
        base.OnDetachedFromTree(in e);
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e) => InvalidateIsEnabledCore();

    private static void OnCommandChanged(UIObject sender, ICommand? oldValue, ICommand? newValue)
    {
        if (sender is not BarComboBox combo)
            return;

        // Unsubscribe the old command (on detach AND on Command change, CD25), subscribe the new — ButtonBase parity.
        if (oldValue is { } old && combo.IsAttachedToTree)
            old.CanExecuteChanged -= combo.OnCanExecuteChanged;

        if (newValue is { } @new && combo.IsAttachedToTree)
            @new.CanExecuteChanged += combo.OnCanExecuteChanged;

        combo.InvalidateIsEnabledCore();
    }

    private static void OnCommandParameterChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is BarComboBox combo)
            combo.InvalidateIsEnabledCore();
    }

    // ───────────────────────────── the value/preview session ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnDropDownOpened()
    {
        base.OnDropDownOpened();
        _valueSession = ValueParameter is not null;
        _previewActive = false;
        _selectionAtOpen = SelectedIndex;
    }

    // Selection-follows-highlight makes a selection change while open the "highlight moved" signal: preview the
    // newly highlighted value. Closed-state selection changes (programmatic, type-ahead on the face) never preview.
    private void OnSelectionChangedPreview(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsDropDownOpen || !_valueSession || SelectedIndex < 0 || ValueParameter is not { } parameter)
            return;

        // A gated Preview does not execute — and then no CancelPreview is owed at close (the |= keeps any earlier
        // successful preview's debt).
        _previewActive |= ExecuteValueAction(parameter, ValueCommandParameterAction.Preview, ValueOf(SelectedItem));
    }

    /// <inheritdoc/>
    protected override void OnDropDownClosed(bool committed)
    {
        base.OnDropDownClosed(committed);

        if (!_valueSession)
            return;

        // Consume the session state — and snapshot the commit verdict/value — FIRST: the executes below run app code
        // (BarCommand also auto-raises CanExecuteChanged inside Execute) that may move the selection (e.g. a
        // two-way-bound SelectedItem following the model the CancelPreview handler restores) or reopen the drop-down.
        _valueSession = false;
        var selectionAtOpen = _selectionAtOpen;
        var hadPreview = _previewActive;
        var commitIndex = SelectedIndex;
        var commitValue = ValueOf(SelectedItem);
        _selectionAtOpen = -1;
        _previewActive = false;

        var parameter = ValueParameter; // may be null when the command/parameter was torn down mid-session

        // An active preview is ALWAYS withdrawn before anything else, commit or not: cancel restores the exact
        // pre-preview state, so a commit is one real operation on clean state and a dismissal leaves no residue.
        if (hadPreview && parameter is not null)
            ExecuteValueAction(parameter, ValueCommandParameterAction.CancelPreview);

        if (committed)
        {
            if (parameter is not null && commitIndex >= 0)
                ExecuteValueAction(parameter, ValueCommandParameterAction.Commit, commitValue);
        }
        else if (!IsDropDownOpen) // an execute above may have REOPENED the drop-down — never yank a live session
        {
            // Dismissal: the highlight was tentative — give the face back the pre-open selection (−1 clears), even
            // when the parameter is already gone. The drop-down is closed, so this cannot re-enter the preview path.
            SelectedIndex = selectionAtOpen;
        }
    }

    // One Execute with the parameter's Action set for its duration, then reset to Commit — a shared parameter must
    // never carry a lingering Preview/CancelPreview into another surface's Execute (a toggle click routing the same
    // command). Gated by CanExecute like every command source (ButtonBase.OnClick parity) — including the payload
    // write: a gated command must never observe its parameter mutated (no half-applied Value on a refused commit).
    // Returns whether the command actually executed.
    private bool ExecuteValueAction(IValueCommandParameter parameter, ValueCommandParameterAction action, object? payload = null)
    {
        if (Command is not { } command)
            return false;

        var raw = CommandParameter;
        parameter.Action = action;
        try
        {
            if (!command.CanExecute(raw))
                return false;

            switch (action)
            {
                case ValueCommandParameterAction.Preview:
                    parameter.PreviewValue = payload;
                    break;
                case ValueCommandParameterAction.Commit:
                    parameter.Value = payload;
                    break;
            }

            command.Execute(raw);
            return true;
        }
        finally
        {
            parameter.Action = ValueCommandParameterAction.Commit;
        }
    }

    // The value an item contributes: a ComboBoxItem container is unwrapped to its content (the item IS the value
    // otherwise — e.g. an ItemsSource of enum values or strings). The parameter's typed surface casts to T.
    private static object? ValueOf(object? item) => item is ComboBoxItem container ? container.Content : item;
}
