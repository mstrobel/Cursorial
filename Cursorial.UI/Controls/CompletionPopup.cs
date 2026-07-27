using System.Buffers;
using System.Globalization;

using Cursorial.Input;
using Cursorial.UI.Input;
using Cursorial.UI.Matching;

namespace Cursorial.UI.Controls;

/// <summary>
/// A completion / "IntelliSense" overlay for a <see cref="TextBox"/>: a non-focusable popup list of
/// ranked candidates that opens as the user types, bolds the matched characters of each row, and
/// splices the accepted candidate back into the field through a host-supplied commit hook.
/// <para>
/// <b>What it is not.</b> This is <em>not</em> an input control — there is no WPF
/// <c>ComboBox.IsEditable</c> or Avalonia <c>AutoCompleteBox</c> here, and deliberately so. Those own
/// their text box, own its text, and own the meaning of Enter; a completion overlay must do none of
/// those things, because the field it decorates is a real editor with its own semantics (a criteria
/// expression, a file path) that outlive the completion session. The nearest true analogue is an
/// editor's completion window — VS Code's suggest widget, IntelliJ's lookup — and the architecture
/// follows those: the popup is a hint, focus never leaves the field, and the field's tunnelled
/// <see cref="UIElement.PreviewKeyDown"/> owns the navigation keys only while the popup is up.
/// </para>
/// <para>
/// <b>The three seams.</b> An <see cref="ICompletionProvider"/> turns
/// <c>(text, caret)</c> into a <see cref="CompletionContext"/> — a replaced SPAN, a filter PATTERN,
/// and the candidates. <see cref="FuzzyMatcher"/> (never a hand-rolled <c>StartsWith</c>) filters,
/// ranks and highlights them. A <see cref="CommitHandler"/> turns an accepted candidate plus the
/// gesture that accepted it into the field's new text, caret and session lifetime. Everything the two
/// reference consumers disagree about lives in those three seams; the popup itself is policy-free.
/// </para>
/// <para>
/// <b>Placement.</b> The window manager clamps a popup into the viewport but never <em>flips</em> it,
/// and it only places on the closed→open edge — so this control picks <see cref="PlacementMode.Bottom"/>
/// or <see cref="PlacementMode.Top"/> itself before every open, and a live list that outgrows its
/// side is closed and re-opened rather than nudged (see <see cref="OpenOrRelocate"/>).
/// </para>
/// <para>
/// <b>Hosting.</b> Put one anywhere in the tree that lives as long as the field — typically the same
/// <see cref="Grid"/> cell. It contributes nothing to layout (its template is a bare
/// <see cref="Popup"/>, which measures 0×0) and it is not hit-testable, so it never covers the field
/// it decorates.
/// </para>
/// <para>
/// <b>Picker mode.</b> Set <see cref="Anchor"/> instead of <see cref="Target"/> and the same control
/// becomes a target-LESS chooser: a scrollable, fuzzy-filtered list hanging off any element at all (a
/// breadcrumb chip, a toolbar button). <see cref="Target"/> plays three roles at once — the placement
/// anchor, the key source, and the text being edited — and a picker wants the first two without the
/// third, which is precisely the split <see cref="Anchor"/> makes. Everything else is the same
/// architecture: the popup stays non-focusable, focus stays on the anchor, and the anchor's
/// <see cref="UIElement.PreviewKeyDown"/> drives it. The one mechanical departure is light dismiss — a
/// picker takes it, a text-mode hint does not; see <c>UpdateDismissPolicy</c> for why the field is the
/// whole of the difference.
/// </para>
/// <para>
/// The one thing the popup has to grow for it is a <see cref="Pattern"/> of its own. In text mode the
/// pattern comes back from the provider (it is a slice of the field's text, and the field is what echoes
/// it); a picker has no field, so printable keys typed while it is open append to <see cref="Pattern"/>,
/// <c>Backspace</c> deletes from it, and it is <see cref="Pattern"/> that drives the same
/// <see cref="FuzzyMatcher"/> filtering, ranking and highlighting. It is also shown in the header
/// (<c>doc · 3 matches</c>) — a filter the user cannot see is a filter the user cannot correct.
/// </para>
/// <para>
/// Two picker-mode divergences are deliberate, and both exist so the user can always get back out of a
/// filter. Filtering down to <b>nothing</b> keeps the popup open on <c>no matches</c> rather than closing
/// it — text mode can close because the field still holds the text, but here closing would throw the
/// typed pattern away and the next keystroke would have nowhere to land. And <c>Escape</c> peels the
/// <b>pattern</b> off first, closing only once it is empty. <c>Tab</c> is not an accept: outside a text
/// field it is the focus gesture, and focus leaving the anchor closes the picker anyway.
/// </para>
/// </summary>
[TemplatePart(PartPopup, typeof(Popup), IsRequired = true)]
[TemplatePart(PartList, typeof(CompletionList), IsRequired = true)]
[TemplatePart(PartHeader, typeof(TextBlock))]
[TemplatePart(PartFooter, typeof(TextBlock))]
public class CompletionPopup : Control
{
    private const string PartPopup = "PART_Popup";
    private const string PartList = "PART_List";
    private const string PartHeader = "PART_Header";
    private const string PartFooter = "PART_Footer";

    /// <summary>The ListBox part the completion list's own template registers its scroll host under.</summary>
    private const string ListScrollViewerPart = "PART_ScrollViewer";

    /// <summary>Rows the popup chrome costs on top of the visible rows — the content border's two rules.
    /// Only feeds the Bottom-vs-Top estimate, so a theme with different chrome merely biases that choice.</summary>
    private const int ChromeRows = 2;

    /// <summary>Above this many <see cref="MatchSpan"/>s the highlight scratch is pooled instead of stack-allocated.
    /// A completion pattern is a word; a pasted paragraph is not, and must not blow the stack.</summary>
    private const int StackSpanBudget = 64;

    private static readonly List<CompletionEntry> NoEntries = [];

    /// <summary>The <see cref="TextBox"/> being completed — the placement anchor, the key source, and the text this popup edits.</summary>
    public static readonly StyledProperty<TextBox?> TargetProperty =
        UIProperty.Register<CompletionPopup, TextBox?>(nameof(Target), changed: OnTargetChanged);

    /// <summary>
    /// The element a target-less <b>picker</b> hangs from: the placement anchor <em>and</em> the key source,
    /// with none of <see cref="Target"/>'s third role (there is no text to edit).
    /// <para>
    /// <see cref="Target"/> wins outright. Its three roles are indivisible — the field it completes is the
    /// thing it must be placed under, must read keys from, and must splice into — so with a
    /// <see cref="Target"/> set this property is dormant and the control behaves exactly as it did before
    /// picker mode existed.
    /// </para>
    /// </summary>
    public static readonly StyledProperty<UIElement?> AnchorProperty =
        UIProperty.Register<CompletionPopup, UIElement?>(nameof(Anchor), changed: OnAnchorChanged);

    /// <summary>The completion seam. <see langword="null"/> ⇒ the popup never opens.</summary>
    public static readonly StyledProperty<ICompletionProvider?> ProviderProperty =
        UIProperty.Register<CompletionPopup, ICompletionProvider?>(nameof(Provider), changed: OnProviderChanged);

    /// <summary>
    /// How many candidate rows are visible at once before the list scrolls; the WPF
    /// <c>ComboBox.MaxDropDownHeight</c> role, spelled in ROWS rather than in device-independent
    /// pixels because a row here is exactly one terminal cell. Zero or negative ⇒ no cap. It also sets
    /// the PageUp/PageDown step, so paging always moves by exactly one screenful.
    /// </summary>
    public static readonly StyledProperty<int> MaxVisibleItemsProperty =
        UIProperty.Register<CompletionPopup, int>(nameof(MaxVisibleItems), defaultValue: 8, changed: OnMaxVisibleItemsChanged);

    /// <summary>Whether the <c>"3 matches"</c> header row is shown.</summary>
    public static readonly StyledProperty<bool> ShowHeaderProperty =
        UIProperty.Register<CompletionPopup, bool>(nameof(ShowHeader), defaultValue: true, changed: OnChromeVisibilityChanged);

    /// <summary>Whether the key-hint footer row is shown.</summary>
    public static readonly StyledProperty<bool> ShowFooterProperty =
        UIProperty.Register<CompletionPopup, bool>(nameof(ShowFooter), defaultValue: true, changed: OnChromeVisibilityChanged);

    /// <summary>The key-hint footer's text.</summary>
    public static readonly StyledProperty<string?> FooterTextProperty =
        UIProperty.Register<CompletionPopup, string?>(nameof(FooterText), defaultValue: DefaultFooterText, changed: OnFooterTextChanged);

    /// <summary>The suffix appended to a row's kind label when it only matched fuzzily; <see langword="null"/> suppresses it.</summary>
    public static readonly StyledProperty<string?> FuzzyLabelProperty =
        UIProperty.Register<CompletionPopup, string?>(nameof(FuzzyLabel), defaultValue: "fuzzy");

    /// <summary>Whether the popup is currently showing candidates (<c>:open</c>).</summary>
    public static readonly DirectProperty<CompletionPopup, bool> IsOpenProperty =
        UIProperty.RegisterDirect<CompletionPopup, bool>(nameof(IsOpen), static c => c._isOpen);

    /// <summary>The number of candidates that survived filtering (0 while closed).</summary>
    public static readonly DirectProperty<CompletionPopup, int> MatchCountProperty =
        UIProperty.RegisterDirect<CompletionPopup, int>(nameof(MatchCount), static c => c._matchCount);

    /// <summary>The header's text (<c>"1 match"</c> / <c>"7 matches"</c>, prefixed by the picker's
    /// <see cref="Pattern"/>) — what the default template's <c>PART_Header</c> shows.</summary>
    public static readonly DirectProperty<CompletionPopup, string?> HeaderTextProperty =
        UIProperty.RegisterDirect<CompletionPopup, string?>(nameof(HeaderText), static c => c._headerText);

    /// <summary>
    /// The picker's own filter pattern — what the user has typed into a popup that has no field to type
    /// into. Empty in text mode, where the pattern belongs to the provider's
    /// <see cref="CompletionContext.Pattern"/> instead. Writing it re-filters an open picker.
    /// </summary>
    public static readonly DirectProperty<CompletionPopup, string> PatternProperty =
        UIProperty.RegisterDirect<CompletionPopup, string>(
            nameof(Pattern), static c => c._pattern, static (c, v) => c.Pattern = v, unsetValue: "");

    private readonly List<CompletionEntry> _entries = [];

    private Popup? _popup;
    private CompletionList? _list;
    private TextBlock? _header;
    private TextBlock? _footer;
    private UIElement? _hookedAnchor; // the Anchor whose keys/focus we are currently listening to (picker mode only)

    private bool _isOpen;
    private int _matchCount;
    private string? _headerText;
    private string _pattern = "";
    private int _replaceStart;
    private int _replaceEnd;

    // ── the three re-entrancy guards ────────────────────────────────────────────────────────────────
    //
    // _accepting is the one the model REQUIRES: committing writes Target.Text, which raises
    // TextChanged, which would re-query the provider against the just-committed text and re-open the
    // session the accept was in the middle of ending — a mutual recursion that ends in either a stack
    // overflow or a popup that refuses to close. Every write to the target's text/caret is fenced by
    // it and every inbound target event checks it.
    //
    // _closing and _relocating are narrower: they mark the two places where WE close the Popup part on
    // purpose, so its Closed event (which also fires for the paths we do NOT control — a target
    // detach, a content swap) can tell "the popup closed under us, tear the session down" from "we are
    // closing it ourselves and already know".
    private bool _accepting;
    private bool _closing;
    private bool _relocating;

    /// <summary>ASCII-only by design: <c>↑↓</c> are ambiguous-width and would mis-measure on the terminals the
    /// renderer's width defense exists for, exactly like the ComboBox caret's <c>v</c>.</summary>
    private const string DefaultFooterText = "^v move   Tab/Enter accept   Esc dismiss";

    static CompletionPopup()
    {
        // A hint overlay is never a focus target and never a hit target: its host-tree element is a
        // 0x0 placeholder for the Popup part, and the real, clickable content lives on the popup's own
        // surface. Overriding the DEFAULTS (rather than writing the fields in the constructor) leaves
        // both settable by a style for the rare host that wants otherwise.
        FocusableProperty.OverrideDefaultValue<CompletionPopup>(false);
        IsTabStopProperty.OverrideDefaultValue<CompletionPopup>(false);
        IsHitTestVisibleProperty.OverrideDefaultValue<CompletionPopup>(false);
    }

    /// <summary>Creates a completion popup. Set <see cref="Target"/> and <see cref="Provider"/> to arm it.</summary>
    public CompletionPopup() {}

    /// <inheritdoc cref="TargetProperty"/>
    public TextBox? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    /// <inheritdoc cref="AnchorProperty"/>
    public UIElement? Anchor { get => GetValue(AnchorProperty); set => SetValue(AnchorProperty, value); }

    /// <inheritdoc cref="ProviderProperty"/>
    public ICompletionProvider? Provider { get => GetValue(ProviderProperty); set => SetValue(ProviderProperty, value); }

    /// <inheritdoc cref="MaxVisibleItemsProperty"/>
    public int MaxVisibleItems { get => GetValue(MaxVisibleItemsProperty); set => SetValue(MaxVisibleItemsProperty, value); }

    /// <inheritdoc cref="ShowHeaderProperty"/>
    public bool ShowHeader { get => GetValue(ShowHeaderProperty); set => SetValue(ShowHeaderProperty, value); }

    /// <inheritdoc cref="ShowFooterProperty"/>
    public bool ShowFooter { get => GetValue(ShowFooterProperty); set => SetValue(ShowFooterProperty, value); }

    /// <inheritdoc cref="FooterTextProperty"/>
    public string? FooterText { get => GetValue(FooterTextProperty); set => SetValue(FooterTextProperty, value); }

    /// <inheritdoc cref="FuzzyLabelProperty"/>
    public string? FuzzyLabel { get => GetValue(FuzzyLabelProperty); set => SetValue(FuzzyLabelProperty, value); }

    /// <inheritdoc cref="IsOpenProperty"/>
    public bool IsOpen => _isOpen;

    /// <inheritdoc cref="MatchCountProperty"/>
    public int MatchCount => _matchCount;

    /// <inheritdoc cref="HeaderTextProperty"/>
    public string? HeaderText => _headerText;

    /// <inheritdoc cref="PatternProperty"/>
    public string Pattern
    {
        get => _pattern;
        set
        {
            if (SetPattern(value))
                Update(CompletionTrigger.TextChanged, allowOpen: false); // a filter edit refines a session; it never starts one
        }
    }

    /// <summary>
    /// Whether this popup is a target-less <b>picker</b> (an <see cref="Anchor"/> and no
    /// <see cref="Target"/>) rather than a text-field completion overlay. A plain CLR property on purpose:
    /// it is derived from the two properties that are already observable, and it can only change when one of
    /// them is written.
    /// </summary>
    public bool IsPicker => Target is null && Anchor is not null;

    /// <summary>
    /// The commit hook — the whole point of the control's generality. It receives the accepted
    /// candidate, the field's text, the span the session owns and the gesture that accepted, and
    /// returns the field's new text, the new caret, and whether the session survives.
    /// <para>
    /// <see langword="null"/> ⇒ <see cref="CompletionCommit.Splice"/>: replace the span with the
    /// candidate's insert text, park the caret after it, and keep the session alive iff the candidate
    /// says <see cref="CompletionItem.ContinuesSession"/>. That default is already right for an
    /// expression editor. A path bar overrides it to make Enter on a FOLDER insert a trailing
    /// separator and keep completing, while Enter on a FILE commits and closes — which is precisely
    /// the divergence the <see cref="CompletionAcceptReason"/> parameter exists to express.
    /// </para>
    /// <para>
    /// <b>Never consulted in picker mode.</b> Every member of <see cref="CompletionAccept"/> and
    /// <see cref="CompletionCommit"/> is about text, and a picker has none; handing a hook a <c>Text</c> of
    /// <c>""</c> over a span of <c>[0,0)</c> just to keep the shape would be a lie the host would have to
    /// pattern-match its way around. A picker reports through <see cref="Picked"/> instead, whose args carry
    /// the same "does this end the session?" decision as
    /// <see cref="CompletionPickedEventArgs.KeepOpen"/>.
    /// </para>
    /// </summary>
    public Func<CompletionAccept, CompletionCommit>? CommitHandler { get; set; }

    /// <summary>The ranked, filtered rows currently on offer (empty while closed). Entry 0 is the best match.</summary>
    public IReadOnlyList<CompletionEntry> Entries => _entries;

    /// <summary>The highlighted row's index, or −1. Setting it moves the highlight and scrolls it into view.</summary>
    public int SelectedIndex
    {
        get => _list?.SelectedIndex ?? -1;
        set => SelectIndex(value);
    }

    /// <summary>The highlighted row, or <see langword="null"/>.</summary>
    public CompletionEntry? SelectedEntry
    {
        get
        {
            var index = SelectedIndex;
            return (uint) index < (uint) _entries.Count ? _entries[index] : null;
        }
    }

    /// <summary>Inclusive start of the text span an accept replaces — the provider's <see cref="CompletionContext.ReplaceStart"/>.</summary>
    public int ReplaceStart => _replaceStart;

    /// <summary>Exclusive end of the text span an accept replaces. May sit AFTER the caret (a mid-token caret).</summary>
    public int ReplaceEnd => _replaceEnd;

    /// <summary>Raised after the popup opens.</summary>
    public event EventHandler? Opened;

    /// <summary>Raised after the popup closes, for any reason (accept, Escape, no candidates, detach).</summary>
    public event EventHandler? Closed;

    /// <summary>Raised after a candidate has been accepted and the commit written to the target.
    /// Text mode only — a picker writes to no target, and reports through <see cref="Picked"/> instead.</summary>
    public event EventHandler<CompletionCommittedEventArgs>? Committed;

    /// <summary>
    /// Raised when a candidate is chosen out of a <b>picker</b> (an <see cref="Anchor"/> and no
    /// <see cref="Target"/>) — the whole answer, since nothing was spliced anywhere. A handler may set
    /// <see cref="CompletionPickedEventArgs.KeepOpen"/> to keep the picker up on a cleared pattern.
    /// </summary>
    public event EventHandler<CompletionPickedEventArgs>? Picked;

    // Test/inspection seams (template-private parts).
    internal Popup? PopupPart => _popup;
    internal CompletionList? ListPart => _list;
    internal TextBlock? HeaderPart => _header;
    internal TextBlock? FooterPart => _footer;

    /// <summary>
    /// Re-queries the provider against the target's current text and caret, opening, updating or
    /// closing the popup accordingly. This is the hook a host with an asynchronous candidate source
    /// calls from its UI-thread continuation — the re-query reads the CURRENT text, so a late answer
    /// can never be applied to a stale one.
    /// </summary>
    public void Refresh() => Update(CompletionTrigger.Explicit, allowOpen: true);

    /// <summary>
    /// Opens a <b>picker</b> on a cleared <see cref="Pattern"/>, returning whether it came up (a provider
    /// with nothing to offer leaves it closed). Picker mode only: a text-mode session is started by typing
    /// in the field or by <see cref="Refresh"/>, never by a host command, because "open a completion list
    /// the user did not ask for" is not a gesture a text field has.
    /// <para>
    /// The pattern reset is the point of having this at all rather than making the host write
    /// <c>Pattern = ""</c> then <see cref="Refresh"/>: a picker that reopened still filtered by whatever
    /// was typed into it last time would show a mystery subset of a list the user just asked to see whole.
    /// </para>
    /// </summary>
    public bool Open()
    {
        if (!IsPicker)
            return false;

        SetPattern("");
        Update(CompletionTrigger.Explicit, allowOpen: true);
        return _isOpen;
    }

    /// <summary>
    /// Closes the popup, keeping whatever the user has typed. This is what Escape does — dismissing a
    /// completion must never revert the field, only stop offering.
    /// </summary>
    public void Close() => CloseSession();

    /// <summary>
    /// Accepts the highlighted row. Returns <see langword="false"/> (and does nothing) when the popup
    /// is closed or nothing is highlighted, so a caller can let the key fall through to the field.
    /// </summary>
    /// <param name="reason">The gesture to report to the commit hook.</param>
    public bool AcceptSelected(CompletionAcceptReason reason = CompletionAcceptReason.Programmatic)
        => Accept(SelectedEntry, reason);

    /// <summary>
    /// Accepts <paramref name="entry"/>, running it through <see cref="CommitHandler"/> and writing the
    /// result to the target — or, in picker mode, reporting it through <see cref="Picked"/> and splicing
    /// nothing. Returns <see langword="false"/> when the popup is closed, the entry is
    /// <see langword="null"/>, or there is neither a target nor an anchor.
    /// </summary>
    /// <param name="entry">The row to accept — normally one of <see cref="Entries"/>.</param>
    /// <param name="reason">The gesture to report to the commit hook / the pick event.</param>
    public bool Accept(CompletionEntry? entry, CompletionAcceptReason reason = CompletionAcceptReason.Programmatic)
    {
        if (!_isOpen || entry is null || _accepting)
            return false;

        if (Target is not { } target)
            return IsPicker && AcceptPicked(entry, reason);

        var text = target.Text;
        var start = Math.Clamp(_replaceStart, 0, text.Length);
        var end = Math.Clamp(_replaceEnd, start, text.Length);
        var accept = new CompletionAccept(entry.Item, text, start, end, reason);
        var commit = CommitHandler is { } handler ? handler(accept) : CompletionCommit.Splice(accept);
        var newText = commit.NewText ?? text;

        // THE re-entrancy fence. Writing Text raises TextChanged and writing CaretIndex raises
        // SelectionChanged; both are wired to Update, which would re-query the provider mid-accept and
        // resurrect the session we are closing (and, for a KeepOpen commit, race the Continued query
        // below). Both target writes live inside the guard, and every inbound handler checks it.
        _accepting = true;

        try
        {
            target.SetCurrentValue(TextBox.TextProperty, newText);
            target.CaretIndex = Math.Clamp(commit.NewCaretIndex, 0, newText.Length);
        }
        finally
        {
            _accepting = false;
        }

        Committed?.Invoke(this, new CompletionCommittedEventArgs(entry.Item, reason, commit));

        if (commit.KeepOpen)
            Update(CompletionTrigger.Continued, allowOpen: true); // the drill-in: re-query against the POST-commit text
        else
            CloseSession();

        return true;
    }

    /// <summary>
    /// The picker's accept: report the pick and get out of the way. There is no text to write, so the
    /// whole of the text path's clamping / splicing / caret arithmetic is not merely skipped but absent.
    /// </summary>
    private bool AcceptPicked(CompletionEntry entry, CompletionAcceptReason reason)
    {
        var args = new CompletionPickedEventArgs(entry.Item, reason);

        // The same fence as the text path, for a related reason. A host answers a pick by doing something
        // structural — navigating, rebuilding the tree the ANCHOR lives in — and detaching the anchor fires
        // its LostFocus, which closes this session out from under the accept. Closing mid-accept is correct
        // (the surface must never outlive what it hangs from); re-QUERYING mid-accept is not, and the fence
        // is what stops the KeepOpen branch below from racing a session the handler already tore down.
        _accepting = true;

        try
        {
            Picked?.Invoke(this, args);
        }
        finally
        {
            _accepting = false;
        }

        if (args.KeepOpen)
        {
            SetPattern(""); // the drill-in starts over: the pattern that found the pick cannot filter its children
            Update(CompletionTrigger.Continued, allowOpen: true);
        }
        else
        {
            CloseSession();
        }

        return true;
    }

    // ───────────────────────────── template ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;

        if (_list is not null)
        {
            _list.Owner = null;
            _list.SelectionChanged -= OnListSelectionChanged;
        }

        _popup = GetTemplatePart<Popup>(PartPopup);
        _list = GetTemplatePart<CompletionList>(PartList);
        _header = GetTemplatePart<TextBlock>(PartHeader);
        _footer = GetTemplatePart<TextBlock>(PartFooter);

        if (_popup is not null)
        {
            UpdateDismissPolicy();
            _popup.PlacementTarget = AnchorElement;
            _popup.Closed += OnPopupClosed;
        }

        if (_list is not null)
        {
            _list.Owner = this;
            _list.SelectionChanged += OnListSelectionChanged;
        }

        // Push the live state into freshly-realized parts (a template swap while a session is open).
        PushEntriesToList();
        UpdateChrome();

        if (_isOpen)
            OpenOrRelocate();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;

        if (_list is not null)
        {
            _list.Owner = null;
            _list.SelectionChanged -= OnListSelectionChanged;
        }

        _popup = null;
        _list = null;
        _header = null;
        _footer = null;
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        CloseSession(); // close the popup surface FIRST so it can never outlive the control that owns it
        base.OnDetachedFromTree(in e);
    }

    // ───────────────────────────── the target / anchor wiring ─────────────────────────────

    /// <summary>What the popup hangs from and reads keys from: the <see cref="Target"/> when there is one,
    /// otherwise the picker's <see cref="Anchor"/>.</summary>
    private UIElement? AnchorElement => Target ?? Anchor;

    private static void OnTargetChanged(UIObject sender, TextBox? oldValue, TextBox? newValue)
    {
        if (sender is not CompletionPopup popup)
            return;

        popup.UnhookTarget(oldValue);
        popup.CloseSession(); // the old field's session cannot mean anything on the new one
        popup.HookTarget(newValue);
        popup.UpdateAnchorHook(); // gaining/losing a Target is what arms/disarms picker mode

        if (popup._popup is not null)
            popup._popup.PlacementTarget = popup.AnchorElement;
    }

    private static void OnAnchorChanged(UIObject sender, UIElement? oldValue, UIElement? newValue)
    {
        if (sender is not CompletionPopup popup)
            return;

        popup.UpdateAnchorHook();

        // With a Target set the Anchor is dormant, and writing a dormant property must not disturb a live
        // text-mode session — that is what makes picker mode purely ADDITIVE for every existing consumer.
        if (popup.Target is not null)
            return;

        popup.CloseSession(); // the old anchor's session cannot mean anything on the new one

        if (popup._popup is not null)
            popup._popup.PlacementTarget = newValue;
    }

    private static void OnProviderChanged(UIObject sender, ICompletionProvider? oldValue, ICompletionProvider? newValue)
    {
        // A provider swap invalidates the offered set wholesale; re-derive rather than leave stale rows up.
        if (sender is CompletionPopup { _isOpen: true } popup)
            popup.Update(CompletionTrigger.Explicit, allowOpen: true);
    }

    private void HookTarget(TextBox? target)
    {
        if (target is null)
            return;

        target.TextChanged += OnTargetTextChanged;
        target.SelectionChanged += OnTargetSelectionChanged;
        target.LostFocus += OnTargetLostFocus;

        // PreviewKeyDown is a TUNNEL event, so a handler on the target runs before the TextBox's own
        // OnKeyDown class stage (which guards on e.Handled) and before the dispatcher's unhandled
        // navigation tail (which is what makes Tab reach us instead of moving focus). Bubbling KeyDown
        // would be too late for both.
        target.PreviewKeyDown += OnTargetPreviewKeyDown;
    }

    private void UnhookTarget(TextBox? target)
    {
        if (target is null)
            return;

        target.TextChanged -= OnTargetTextChanged;
        target.SelectionChanged -= OnTargetSelectionChanged;
        target.LostFocus -= OnTargetLostFocus;
        target.PreviewKeyDown -= OnTargetPreviewKeyDown;
    }

    // The picker's half of the wiring: focus-out and keys, and neither of the two TEXT channels — an anchor
    // has no text to change and no caret to move, which is the whole difference between the two modes.
    private void UpdateAnchorHook()
    {
        var wanted = Target is null ? Anchor : null;

        if (ReferenceEquals(wanted, _hookedAnchor))
            return;

        if (_hookedAnchor is not null)
        {
            _hookedAnchor.LostFocus -= OnAnchorLostFocus;
            _hookedAnchor.PreviewKeyDown -= OnAnchorPreviewKeyDown;
        }

        _hookedAnchor = wanted;

        if (_hookedAnchor is not null)
        {
            _hookedAnchor.LostFocus += OnAnchorLostFocus;
            _hookedAnchor.PreviewKeyDown += OnAnchorPreviewKeyDown;
        }
    }

    private void OnTargetTextChanged(object? sender, RoutedEventArgs e) => Update(CompletionTrigger.TextChanged, allowOpen: true);

    // A caret move REFINES an open session (the span and pattern under the caret changed) but never
    // starts one — clicking into a word should not pop a completion list at someone.
    private void OnTargetSelectionChanged(object? sender, RoutedEventArgs e) => Update(CompletionTrigger.CaretMoved, allowOpen: false);

    private void OnTargetLostFocus(object? sender, FocusChangedEventArgs e)
    {
        // The popup content is non-focusable, so focus can never be "inside" it — but the guard is
        // still the right one: it keeps a host that DOES make something in a custom footer focusable
        // from closing the session out from under itself (the ComboBox.OnLostFocus idiom).
        if (Target is { IsKeyboardFocusWithin: true })
            return;

        CloseSession();
    }

    private void OnTargetPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isOpen || e.Handled || _entries.Count == 0)
            return;

        // Modifier-free only. Shift+Tab must still walk the tab order, Ctrl+Enter must still reach the
        // field, and Shift+Down must still extend a text selection — an overlay may borrow the plain
        // keys, never the chorded ones. Home/End are never borrowed at all: in a text field they are
        // caret motion the user needs far more often than "first/last candidate".
        if (e.Modifiers != KeyModifiers.None)
            return;

        var page = VisibleRowCapacity();

        switch (e.Key)
        {
            case Key.DownArrow: MoveSelection(1); break;
            case Key.UpArrow: MoveSelection(-1); break;
            case Key.PageDown: MoveSelection(page); break;
            case Key.PageUp: MoveSelection(-page); break;
            case Key.Escape: CloseSession(); break;

            case Key.Enter:
            case Key.Tab:
                var reason = e.Key == Key.Enter ? CompletionAcceptReason.Enter : CompletionAcceptReason.Tab;

                if (!Accept(SelectedEntry, reason))
                    return; // nothing highlighted — leave the key to the field (Enter still inserts a newline)

                break;

            default: return;
        }

        e.Handled = true;
    }

    private void OnAnchorLostFocus(object? sender, FocusChangedEventArgs e)
    {
        // Focus IS the picker's lifetime: the surface is non-focusable, so the anchor holding focus is the
        // only reason keys reach it at all. Losing focus therefore means the picker can no longer be driven,
        // and a list nobody can drive must not stay on screen.
        if (Anchor is { IsKeyboardFocusWithin: true })
            return;

        CloseSession();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────
    // The picker's keymap. Same tunnel-on-the-anchor mechanism as text mode, with three departures that
    // all come from the same fact: the anchor is a chip or a button, not an editor, so there is no field
    // to "let the key fall through to".
    //
    //  • Printable keys and Backspace EDIT THE PATTERN instead of the field. Space included — a folder
    //    called "My Documents" is unreachable otherwise, and swallowing it is also what stops the
    //    ButtonBase underneath from treating the space as an activation.
    //  • Home/End are borrowed (first/last candidate). Text mode deliberately refuses to — there they are
    //    caret motion the user needs far more often — but a picker has no caret to move.
    //  • Enter is claimed even when nothing is highlighted. Letting it through would activate the anchor
    //    button, i.e. a filter that matched nothing would silently turn Enter into a navigation.
    //
    // Tab is NOT an accept here (it is in text mode, where it means "complete this far"): outside a field
    // Tab is the focus gesture, and letting it move focus closes the picker through OnAnchorLostFocus,
    // which is exactly what a user pressing Tab meant.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────
    private void OnAnchorPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isOpen || e.Handled)
            return;

        if (TryTypePattern(e))
        {
            e.Handled = true;
            return;
        }

        // Modifier-free only, for the text-mode reason: an overlay may borrow the plain keys, never the
        // chorded ones (Shift+Tab must still walk the tab order, Ctrl+Home must still reach the host).
        if (e.Modifiers != KeyModifiers.None)
            return;

        var page = VisibleRowCapacity();

        switch (e.Key)
        {
            case Key.DownArrow: MoveSelection(1); break;
            case Key.UpArrow: MoveSelection(-1); break;
            case Key.PageDown: MoveSelection(page); break;
            case Key.PageUp: MoveSelection(-page); break;
            case Key.Home: SelectIndex(0); break;
            case Key.End: SelectIndex(_entries.Count - 1); break;
            case Key.Backspace: BackspacePattern(); break;

            // Escape peels the FILTER off before it dismisses. The pattern is invisible to everything but
            // this popup's header, so an Escape that always closed would leave a user who mistyped one
            // character choosing between backspacing blind and losing the list — and the state that most
            // provokes an Escape ("no matches") is exactly the one where the pattern is the problem.
            case Key.Escape when _pattern.Length > 0: Pattern = ""; break;
            case Key.Escape: CloseSession(); break;

            case Key.Enter: Accept(SelectedEntry, CompletionAcceptReason.Enter); break;

            default: return;
        }

        e.Handled = true;
    }

    /// <summary>Whether <paramref name="e"/> is a printable keystroke, and appends it to the pattern if so.</summary>
    private bool TryTypePattern(KeyEventArgs e)
    {
        // The dispatcher's own TextInput rule (§7.5 step 7): Shift is part of typing, everything else is a
        // chord. Control characters are filtered because a terminal delivers some of them as Key.Character.
        if (e.Key != Key.Character || e.Text.IsEmpty || (e.Modifiers & ~KeyModifiers.Shift) != KeyModifiers.None)
            return false;

        var typed = e.Text.Span;

        if (char.IsControl(typed[0]))
            return false;

        Pattern = _pattern + typed.ToString();
        return true;
    }

    private void BackspacePattern()
    {
        if (_pattern.Length == 0)
            return;

        // One TEXT ELEMENT, not one char: a pattern can hold surrogate pairs and combining marks, and
        // lopping a char off the end of either leaves a broken cluster the matcher would then filter on.
        var enumerator = StringInfo.GetTextElementEnumerator(_pattern);
        var lastStart = 0;

        while (enumerator.MoveNext())
            lastStart = enumerator.ElementIndex;

        Pattern = _pattern[..lastStart];
    }

    // ───────────────────────────── the session ─────────────────────────────

    /// <summary>
    /// The single funnel: ask the provider, rank the answer, and open / update / close. Every inbound
    /// path (typing, caret motion, an explicit refresh, a KeepOpen commit) lands here.
    /// </summary>
    /// <param name="trigger">Why we are asking — forwarded to the provider verbatim.</param>
    /// <param name="allowOpen">Whether a currently-closed popup may open. False for caret motion.</param>
    private void Update(CompletionTrigger trigger, bool allowOpen)
    {
        if (_accepting)
            return; // the accept's own text/caret writes must not re-enter the query

        if (!allowOpen && !_isOpen)
            return;

        if (Provider is not { } provider)
        {
            CloseSession();
            return;
        }

        string text;
        int caret;

        if (Target is { } target)
        {
            text = target.Text;
            caret = Math.Clamp(target.CaretIndex, 0, text.Length);
        }
        else if (Anchor is { IsAttachedToTree: true })
        {
            // Picker mode queries with its OWN pattern standing in for the field's text, caret at the end.
            // Reusing the one seam rather than growing a second "just give me items" one is what keeps the
            // async story (return what you have, call Refresh from the continuation) true for both modes.
            text = _pattern;
            caret = text.Length;
        }
        else
        {
            CloseSession(); // no field to complete and no anchor to hang from
            return;
        }

        if (provider.GetCompletions(new CompletionQuery(text, caret, trigger)) is not { } context)
        {
            CloseSession();
            return;
        }

        RebuildEntries(context);

        // Filtering to nothing closes a TEXT session — the field still holds what was typed, so nothing is
        // lost and an empty hint would just be noise. A PICKER has to stay up on "no matches" instead: the
        // pattern lives only in this popup, so closing would throw it away, and there would be nowhere for
        // the Backspace that fixes the typo to land.
        if (_entries.Count == 0 && !(_isOpen && IsPicker))
        {
            CloseSession();
            return;
        }

        _replaceStart = Math.Clamp(context.ReplaceStart, 0, text.Length);
        _replaceEnd = Math.Clamp(context.ReplaceEnd, _replaceStart, text.Length);

        SetAndRaise(MatchCountProperty, ref _matchCount, _entries.Count);
        SetAndRaise(HeaderTextProperty, ref _headerText, ComposeHeaderText(_entries.Count));

        PushEntriesToList();
        UpdateChrome();
        OpenOrRelocate();
    }

    private void CloseSession()
    {
        if (_popup is { IsOpen: true })
        {
            _closing = true;

            try
            {
                _popup.SetCurrentValue(Popup.IsOpenProperty, false);
            }
            finally
            {
                _closing = false;
            }
        }

        _replaceStart = 0;
        _replaceEnd = 0;
        _entries.Clear();
        SetPattern(""); // a closed picker owns no filter — the next Open() must start from the whole list
        SetAndRaise(MatchCountProperty, ref _matchCount, 0);
        SetAndRaise(HeaderTextProperty, ref _headerText, null);

        if (_list is not null)
            _list.ItemsSource = NoEntries; // drop the containers so a stale row can never flash on re-open

        SetOpenState(false);
    }

    /// <summary>Writes <see cref="Pattern"/> without re-filtering, returning whether it actually changed.</summary>
    private bool SetPattern(string? value) => SetAndRaise(PatternProperty, ref _pattern, value ?? "");

    /// <summary>
    /// The header strip: <c>"7 matches"</c>, prefixed by the picker's filter when there is one
    /// (<c>"doc · 3 matches"</c>). Text mode never sees the prefix (its pattern is echoed by the field
    /// itself) and never sees the zero case (it closes instead), so its header is unchanged.
    /// </summary>
    private string ComposeHeaderText(int count)
    {
        var matches = count switch
        {
            0 => "no matches",
            1 => "1 match",
            _ => $"{count} matches"
        };

        return _pattern.Length > 0 && IsPicker ? $"{_pattern} · {matches}" : matches;
    }

    private void SetOpenState(bool value)
    {
        if (!SetAndRaise(IsOpenProperty, ref _isOpen, value))
            return;

        // IsOpen is a DirectProperty, so it drives no PseudoClassMapping — the class is set by hand
        // (the ComboBox.IsDropDownOpen idiom).
        PseudoClasses.Set(":open", value);

        if (value)
            Opened?.Invoke(this, EventArgs.Empty);
        else
            Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        // Our own closes announce themselves; anything else (the anchor detaching, a content swap, a
        // host reaching into the Popup part) means the surface went away under us — tear the session down.
        if (_closing || _relocating)
            return;

        CloseSession();
    }

    // ───────────────────────────── placement ─────────────────────────────

    /// <summary>
    /// Opens the popup at the side with room, or moves an already-open one there.
    /// <para>
    /// Two window-manager facts shape this. First, <c>WindowManager.PlacePopup</c> <b>clamps</b> a
    /// surface into the viewport but never flips it: a Bottom-placed popup with no room below is
    /// pushed back UP over its own anchor, hiding the very text the user is typing. Choosing the side
    /// before opening is therefore the control's job, not the manager's. Second, placement runs on the
    /// closed→open edge (and on a content-driven refit), so writing <c>Placement</c> on a live popup
    /// changes nothing — which is why a side change closes and re-opens instead, with
    /// <see cref="_relocating"/> telling <see cref="OnPopupClosed"/> that the intervening close is not
    /// a dismissal.
    /// </para>
    /// </summary>
    private void OpenOrRelocate()
    {
        if (_popup is null)
            return;

        var placement = ChoosePlacement();

        if (_popup.IsOpen)
        {
            if (_popup.Placement == placement)
            {
                SetOpenState(true);
                return;
            }

            _relocating = true;

            try
            {
                _popup.SetCurrentValue(Popup.IsOpenProperty, false);
            }
            finally
            {
                _relocating = false;
            }
        }

        _popup.Placement = placement;
        _popup.PlacementTarget = AnchorElement;
        UpdateDismissPolicy(); // the mode can have changed since the template was applied (Target/Anchor are writable)
        _popup.SetCurrentValue(Popup.IsOpenProperty, true);
        SetOpenState(true);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────
    // Light dismiss is exactly where the two modes part company, and the reason is the field.
    //
    // A TEXT-mode hint is not light-dismissable: the user is typing in the field, and an outside press is
    // how they dismiss the FIELD, not the hint — the field's own focus-out closes us, and swallowing that
    // press to close a hint the user was not looking at would cost them the click they meant to spend.
    //
    // A PICKER has no field, so there is nothing for that argument to protect. It is a chooser the user
    // opened deliberately, and every chooser in the framework (ContextMenu, ComboBox's list, MenuItem's
    // submenu) goes away on an outside press. Without this, a press on non-focusable dead space — a label,
    // a panel background, a dialog's chrome — left the list floating with the keyboard still pointed at it,
    // because only a FOCUS-taking press reaches OnAnchorLostFocus. This is what the drop-downs had when
    // they were ContextMenus, restored.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────
    private void UpdateDismissPolicy()
    {
        if (_popup is not null)
            _popup.StaysOpen = !IsPicker;
    }

    private PlacementMode ChoosePlacement()
    {
        if (UIApplication.Current?.WindowManager is not { } manager || AnchorElement is not { IsAttachedToTree: true } anchor)
            return PlacementMode.Bottom;

        // Screen space, not window space: a dialog's field is offset by its window's surface origin,
        // and the viewport the manager clamps against is the screen.
        var surfaceTop = manager.SurfaceForElement(anchor)?.Top ?? 0;
        var (_, windowRow) = anchor.TranslateToWindow(0, 0);
        var anchorTop = surfaceTop + windowRow;
        var anchorBottom = anchorTop + anchor.Bounds.Rows;

        var wanted = VisibleRowCapacity() + (ShowHeader ? 1 : 0) + (ShowFooter ? 1 : 0) + ChromeRows;
        var roomBelow = manager.ScreenSize.Rows - anchorBottom;

        // Prefer Bottom — it is where a completion list is expected — and only flip when Top genuinely
        // does better. A tie keeps Bottom so the choice never oscillates as rows come and go.
        return roomBelow >= wanted || roomBelow >= anchorTop ? PlacementMode.Bottom : PlacementMode.Top;
    }

    /// <summary>How many candidate rows are actually on screen before the list scrolls. One entry is one cell row.</summary>
    private int VisibleRowCapacity()
    {
        var cap = MaxVisibleItems;
        var shown = cap > 0 ? Math.Min(cap, _entries.Count) : _entries.Count;
        return Math.Max(1, shown);
    }

    // ───────────────────────────── filtering + ranking ─────────────────────────────

    /// <summary>
    /// Matches every candidate against the context's pattern with <see cref="FuzzyMatcher"/>, drops the
    /// non-matches, and orders the survivors.
    /// <para>
    /// The scratch buffer is the matcher's documented zero-allocation shape — one
    /// <c>stackalloc MatchSpan[MaxSpanCount(pattern)]</c> reused across the whole pass, so a filter
    /// over thousands of candidates touches the heap only for the rows that actually survive. The
    /// pattern comes from a text field, though, and a paste can make it arbitrarily long, so anything
    /// past <see cref="StackSpanBudget"/> spans is pooled instead (the matcher makes the same
    /// trade-off internally, for the same reason).
    /// </para>
    /// </summary>
    private void RebuildEntries(CompletionContext context)
    {
        _entries.Clear();

        var items = context.Items;
        var pattern = context.Pattern;
        var fuzzyLabel = FuzzyLabel;

        if (pattern.Length == 0)
        {
            // An empty pattern shows everything — and shows it in PROVIDER order. Running it through
            // CompareRank instead would be actively wrong: every candidate ties at score 0, so the
            // matcher's length-then-ordinal tiebreak (which exists to stop a *filtered* list from
            // reshuffling) would re-sort a directory listing by name length.
            var empty = FuzzyMatcher.Match(pattern, pattern); // FuzzyMatchKind.Empty, score 0, no spans

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                _entries.Add(new CompletionEntry(item, empty, CompletionEntry.EmptySpans, item.KindLabel, i));
            }

            _entries.Sort(CompareSourceOrder);
            return;
        }

        var maxSpans = FuzzyMatcher.MaxSpanCount(pattern);
        var rented = maxSpans > StackSpanBudget ? ArrayPool<MatchSpan>.Shared.Rent(maxSpans) : null;
        Span<MatchSpan> spans = rented is null ? stackalloc MatchSpan[maxSpans] : rented.AsSpan(0, maxSpans);

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var match = FuzzyMatcher.Match(pattern, item.Display, spans, context.MatchOptions);

                if (!match.IsMatch)
                    continue;

                var runs = match.SpanCount == 0 ? CompletionEntry.EmptySpans : spans[..match.SpanCount].ToArray();
                var fuzzy = match.Kind == FuzzyMatchKind.Subsequence;
                _entries.Add(new CompletionEntry(item, match, runs, ComposeKindLabel(item, fuzzy, fuzzyLabel), i));
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<MatchSpan>.Shared.Return(rented);
        }

        _entries.Sort(CompareRanked);
    }

    /// <summary>The mock's <c>folder · fuzzy</c>: the item's own label, annotated when the row only matched fuzzily.</summary>
    private static string? ComposeKindLabel(CompletionItem item, bool fuzzy, string? fuzzyLabel)
    {
        if (!fuzzy || string.IsNullOrEmpty(fuzzyLabel))
            return item.KindLabel;

        return string.IsNullOrEmpty(item.KindLabel) ? fuzzyLabel : $"{item.KindLabel} · {fuzzyLabel}";
    }

    // SortGroup first (a file can never out-score its way above a folder), then the matcher's total
    // order, then the provider's original index. That last term is what makes the sort deterministic
    // despite List.Sort being unstable — without it two candidates with the same display string could
    // swap places between keystrokes and the list would visibly twitch.
    private static int CompareRanked(CompletionEntry left, CompletionEntry right)
    {
        if (left.Item.SortGroup != right.Item.SortGroup)
            return left.Item.SortGroup.CompareTo(right.Item.SortGroup);

        var rank = FuzzyMatcher.CompareRank(left.Match, left.Item.Display, right.Match, right.Item.Display);
        return rank != 0 ? rank : left.SourceIndex.CompareTo(right.SourceIndex);
    }

    private static int CompareSourceOrder(CompletionEntry left, CompletionEntry right)
    {
        if (left.Item.SortGroup != right.Item.SortGroup)
            return left.Item.SortGroup.CompareTo(right.Item.SortGroup);

        return left.SourceIndex.CompareTo(right.SourceIndex);
    }

    // ───────────────────────────── the list + chrome ─────────────────────────────

    private void PushEntriesToList()
    {
        if (_list is null)
            return;

        // A fresh snapshot per pass. Re-assigning the SAME List instance after mutating it in place
        // would be a no-op for the items host (same reference ⇒ no change notification, and a plain
        // List raises none of its own), so the rows would silently keep the previous filter's contents.
        _list.ItemsSource = _entries.ToArray();

        // The highlight always resets to the best match. Trying to preserve it across a re-filter reads
        // as a bug from the user's side — one more typed character can drop the row that was
        // highlighted, and "keep it if it survived" makes the highlight jump around the list instead.
        SelectIndex(_entries.Count > 0 ? 0 : -1);
    }

    private void SelectIndex(int index)
    {
        if (_list is null)
            return;

        _list.SelectedIndex = (uint) index < (uint) _entries.Count ? index : -1;
        EnsureSelectedVisible();
    }

    private void MoveSelection(int delta)
    {
        var count = _entries.Count;

        if (count == 0)
            return;

        var current = SelectedIndex;
        SelectIndex(Math.Clamp(current < 0 ? 0 : current + delta, 0, count - 1));
    }

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e) => EnsureSelectedVisible();

    // The list's own ScrollViewer follows keyboard FOCUS (ScrollViewer.OnDescendantGotFocus), and a
    // completion row is deliberately never focused — so the popup scrolls the highlight into view
    // itself. One entry is one cell row, which makes the index the row offset directly.
    private void EnsureSelectedVisible()
    {
        if (_list?.GetTemplatePart<ScrollViewer>(ListScrollViewerPart) is not { } scroll)
            return; // before the list's first template expansion; the next selection move catches up

        var index = _list.SelectedIndex;
        var viewport = scroll.Viewport.Rows;

        if (index < 0 || viewport <= 0)
            return;

        if (index < scroll.VerticalOffset)
            scroll.VerticalOffset = index;
        else if (index >= scroll.VerticalOffset + viewport)
            scroll.VerticalOffset = index - viewport + 1;
    }

    private static void OnChromeVisibilityChanged(UIObject sender, bool oldValue, bool newValue)
        => (sender as CompletionPopup)?.UpdateChrome();

    private static void OnFooterTextChanged(UIObject sender, string? oldValue, string? newValue)
        => (sender as CompletionPopup)?.UpdateChrome();

    private static void OnMaxVisibleItemsChanged(UIObject sender, int oldValue, int newValue)
        => (sender as CompletionPopup)?.UpdateChrome();

    // ───────────────────────────── why the chrome is PUSHED, not bound ─────────────────────────────
    //
    // Every part of this template except the Popup itself lives inside the Popup's Child — and a
    // Popup's Child is a LOGICAL child, not a visual one. ControlTemplate.StampTemplatedParent walks
    // `VisualChildrenForTemplateStamp` only, so nothing under a Popup.Child ever receives a
    // TemplatedParent stamp, and a `{TemplateBinding}` written there silently resolves to nothing.
    // (The bug is easy to miss because the neighbouring idioms DO work across the same seam:
    // DynamicResource / SetResourceReference walk `LogicalParent ?? TemplatedParent`, and the Popup is
    // itself a stamped part, so resource lookup and inheritance cross the surface boundary fine. Only
    // TemplateBinding, which needs the stamp on the binding TARGET, does not — which is how the same
    // latent break survives unnoticed in the ComboBox drop-down's MaxDropDownHeight binding, whose
    // default is PositiveInfinity and therefore looks identical whether it resolves or not.)
    //
    // So the three values that have to reach the popup's content — the header text, the footer text,
    // and the list's height cap — are written straight onto the parts instead. Header and footer
    // COLLAPSE rather than hide when suppressed: a hidden row still occupies a cell row, and the popup
    // surface is sized to its content, so hiding would leave a blank stripe where the strip was.
    private void UpdateChrome()
    {
        if (_list is not null)
        {
            var cap = MaxVisibleItems;
            _list.SetCurrentValue(UIElement.MaxHeightProperty, cap > 0 ? cap : LayoutMath.Unbounded);
        }

        if (_header is not null)
        {
            _header.Text = _headerText;
            _header.Visibility = ShowHeader && !string.IsNullOrEmpty(_headerText) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_footer is not null)
        {
            var footer = FooterText;
            _footer.Text = footer;
            _footer.Visibility = ShowFooter && !string.IsNullOrEmpty(footer) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
