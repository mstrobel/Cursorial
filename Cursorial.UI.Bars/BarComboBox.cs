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
/// value picker: a commit gesture (Enter / Space / item click) writes the chosen value into
/// <see cref="IValueCommandParameter.Value"/> and executes the command — one real operation — while a dismissal
/// (Escape / light-dismiss / focus-out / toggle-close) restores the pre-open selection, so neither the model nor the
/// face keeps an unchosen value. This value channel works with <em>any</em> <see cref="System.Windows.Input.ICommand"/>.
/// </para>
/// <para>
/// When the command is additionally an <see cref="IPreviewableCommand"/> (a <see cref="BarCommand"/> with preview
/// delegates), the session dry-runs the effect live: moving the highlight (selection-follows-highlight) writes the
/// highlighted value into <see cref="IValueCommandParameter.PreviewValue"/> and calls
/// <see cref="IPreviewableCommand.Preview"/> — a dry-run <c>Execute</c>: the consumer produces the outcome,
/// <em>committing nothing</em>; the close then first calls <see cref="IPreviewableCommand.CancelPreview"/> when a
/// dry-run ran (unwind, byte-exact), and only then does a commit gesture let <c>Execute</c> do the thing for real on
/// clean state — <c>Execute</c> needs zero preview awareness. The dry-run is <b>atomic</b> — it either becomes the
/// real execution or is entirely unwound (the <see cref="IPreviewableCommand"/> contract): <c>Preview</c> and
/// <c>Execute</c> (and the parameter mutations that feed them) are gated by <c>CanExecute</c>, and the combo greys
/// while <c>CanExecute</c> is false (the CD25 coupling every command source has), but <c>CancelPreview</c> is a
/// command-side cleanup verb structurally outside the <c>Execute</c> gate — always delivered — so a command gated
/// mid-session has its dry-run unwound, its execution refused, and the pre-open selection restored to the face. A
/// command that is NOT previewable never receives a preview verb: nothing tentative is ever shown, and a dry-run can
/// never be mis-executed as the real thing.
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
public class BarComboBox : ComboBox, ICommandSource
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

    /// <inheritdoc/>
    void ICommandSource.CanExecuteChanged(object sender, EventArgs e) => OnCanExecuteChanged(sender, e);

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
    // newly highlighted value. Closed-state selection changes (programmatic, type-ahead on the face) never preview,
    // and a command that is not an IPreviewableCommand never receives a preview verb (nothing tentative is shown —
    // the value channel still commits on close through Execute).
    private void OnSelectionChangedPreview(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsDropDownOpen || !_valueSession || SelectedIndex < 0 || ValueParameter is not { } parameter)
            return;

        // The probe is the STRUCTURAL gate (CanPreview — "do you support dry-runs at all"): a wrapper command
        // (BarCommand) forwards it honestly from its inner command, so a session never starts on advertised verbs
        // the inner cannot honor.
        if (Command is not IPreviewableCommand { CanPreview: true } previewable)
            return;

        // The dispatch — payload write included — is gated: a gated command acquires no NEW tentative state and
        // never observes its parameter mutated (the atomicity contract; Preview also self-gates, e.g. BarCommand's).
        // A refused Preview owes no CancelPreview at close (the |= keeps any earlier successful preview's debt).
        var raw = CommandParameter;
        if (!previewable.CanExecute(raw))
            return;

        parameter.PreviewValue = ValueOf(SelectedItem);
        previewable.Preview(raw);
        _previewActive = true;
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

        // An active dry-run is ALWAYS unwound before anything else, commit gesture or not: cancel restores the
        // exact pre-preview state, so Execute later runs on clean state (zero preview awareness) and a dismissal
        // leaves no residue. CancelPreview is a command-side cleanup verb structurally outside the Execute gate
        // (the atomicity contract) — a command that gated itself mid-session still receives the unwind.
        if (hadPreview && parameter is not null && Command is IPreviewableCommand { CanPreview: true } previewable)
            previewable.CancelPreview(CommandParameter);

        // The commit stays gated (payload write included): a gated command refuses the commit outright. Anything
        // short of a delivered commit — dismissal, refused commit, torn-down parameter — is a full rollback.
        var commitDelivered = committed && parameter is not null && commitIndex >= 0
                              && TryExecuteCommit(parameter, commitValue);

        if (!commitDelivered && !IsDropDownOpen) // an execute above may have REOPENED the drop-down — never yank a live session
        {
            // Rolled back (dismissal or refused commit): the highlight was tentative — give the face back the
            // pre-open selection (−1 clears), even when the parameter is already gone, so the face never diverges
            // from the unchanged model. The drop-down is closed, so this cannot re-enter the preview path.
            SelectedIndex = selectionAtOpen;
        }
    }

    // A commit gesture's delivery: Value lands on the parameter, then Execute — the command's ordinary execution,
    // with any active dry-run already unwound (a preview rides the IPreviewableCommand verbs, never Execute). Gated
    // by CanExecute like every command source (ButtonBase.OnClick parity), payload write included: a gated command
    // must never observe its parameter mutated (no half-applied Value on a refused commit). Returns whether the
    // execution was delivered.
    private bool TryExecuteCommit(IValueCommandParameter parameter, object? value)
    {
        if (Command is not { } command)
            return false;

        var raw = CommandParameter;
        if (!command.CanExecute(raw))
            return false;

        parameter.Value = value;
        command.Execute(raw);
        return true;
    }

    // The value an item contributes: a ComboBoxItem container is unwrapped to its content (the item IS the value
    // otherwise — e.g. an ItemsSource of enum values or strings). The parameter's typed surface casts to T.
    private static object? ValueOf(object? item) => item is ComboBoxItem container ? container.Content : item;
}
