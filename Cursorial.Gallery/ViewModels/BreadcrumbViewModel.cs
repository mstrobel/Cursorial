using System.Collections.ObjectModel;
using System.Windows.Input;

using Cursorial.Gallery.Infrastructure;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery.ViewModels;

/// <summary>
/// The Breadcrumb page: a <see cref="BreadcrumbBar"/> over a trail that is deliberately <b>not</b> a file path —
/// the segments are <see cref="TrailNode"/> view-models describing a cosmic-address hierarchy (Universe ▸ Laniakea ▸
/// Milky Way ▸ … ▸ Delft). That is the point of the control: it is an <see cref="ItemsControl"/> over ANY hierarchy
/// and knows nothing about paths, separators or file systems. Everything path-shaped on this page — the trail's
/// text rendering, the parse on commit, the sibling lists behind each <c>▸</c> — belongs to the page.
/// <para>
/// <b>Overflow.</b> <see cref="DeeperCommand"/>/<see cref="ShallowerCommand"/> change the trail's depth and
/// <see cref="NarrowerCommand"/>/<see cref="WiderCommand"/> change <see cref="BarWidth"/>, so the leading fold can
/// be provoked without resizing the terminal: when the chips no longer fit, the ANCESTORS collapse behind a leading
/// <c>…</c> chip (never the current segment — a trail that folded its tail would hide where you are), and pressing
/// the ellipsis drops the elided list.
/// </para>
/// <para>
/// <b>Navigation is the host's.</b> The bar raises <see cref="BreadcrumbBar.ItemActivated"/> and does <em>not</em>
/// mutate itself; this page answers by truncating <see cref="Trail"/> at the activated depth. That single fact is
/// what makes the same control work for a directory trail and for an object graph.
/// </para>
/// <para>
/// <b>Edit mode is a hand-off.</b> The bar owns only the chips ↔ text-box state machine. On
/// <see cref="BreadcrumbBar.EditingStarted"/> this page supplies the text (its own <c>" / "</c>-joined rendering);
/// on <see cref="BreadcrumbBar.EditCommitted"/> it parses and validates, and an empty commit is REJECTED with
/// <see cref="BreadcrumbBarEditEventArgs.KeepEditing"/> so the box stays open with the text intact.
/// </para>
/// <para>
/// <b>Keyboard.</b> The whole trail is ONE tab stop: Tab in, then <c>←</c>/<c>→</c> move the active chip (focus
/// only — arrowing along a trail never navigates), <c>Home</c>/<c>End</c> jump to the ends, <c>Enter</c>/<c>Space</c>
/// activate the active chip, and <c>F2</c> enters edit mode (<c>Enter</c> commits, <c>Esc</c> reverts). In chip mode
/// <c>Esc</c> is deliberately left UNHANDLED, so it falls through to the shell's quit binding.
/// </para>
/// <para>
/// <b>The drop-downs are pickers.</b> <c>↓</c> (or a press on a <c>▸</c>) drops the segment's child list as a
/// scrollable, fuzzy-filtered list: type to filter it, <c>↑</c>/<c>↓</c> to move, <c>Enter</c> to drill,
/// <c>Esc</c> to clear the filter and again to dismiss. This page fills it with plain
/// <see cref="TrailNode"/>s, so each row is the node's <c>ToString()</c>; a host that wants icons and kind
/// labels puts <see cref="CompletionItem"/>s in <see cref="BreadcrumbBarDropDownEventArgs.Children"/> instead.
/// </para>
/// </summary>
public sealed class BreadcrumbViewModel : PageViewModel
{
    /// <summary>The separator this page renders the trail with in edit mode. It is a page convention: the bar
    /// neither writes nor reads it.</summary>
    private const string TextSeparator = " / ";

    /// <summary>The same separator as a bare character — what the commit parse splits on, so a user who types
    /// <c>a/b</c> without the padding spaces is understood too.</summary>
    private const char TextSeparatorChar = '/';

    /// <summary>The canned hierarchy, deepest-last. <see cref="DeeperCommand"/> appends the next entry.</summary>
    private static readonly TrailNode[] Chain =
    [
        new("Universe", "everything"),
        new("Laniakea", "supercluster"),
        new("Milky Way", "galaxy"),
        new("Orion Arm", "spiral arm"),
        new("Solar System", "system"),
        new("Earth", "planet"),
        new("Europe", "continent"),
        new("Netherlands", "country"),
        new("Delft", "city")
    ];

    /// <summary>The siblings offered behind each depth's <c>▸</c> separator (index-aligned with <see cref="Chain"/>).</summary>
    private static readonly string[][] Siblings =
    [
        ["Universe"],
        ["Laniakea", "Perseus-Pisces", "Shapley"],
        ["Milky Way", "Andromeda", "Triangulum"],
        ["Orion Arm", "Perseus Arm", "Sagittarius Arm"],
        ["Solar System", "Alpha Centauri", "Trappist-1"],
        ["Mercury", "Venus", "Earth", "Mars", "Jupiter"],
        ["Africa", "Antarctica", "Asia", "Europe", "North America"],
        ["Belgium", "Denmark", "Netherlands", "Norway", "Portugal"],
        ["Amsterdam", "Delft", "Rotterdam", "Utrecht"]
    ];

    private const int InitialDepth = 5;
    private const int MinimumBarWidth = 14;
    private const int MaximumBarWidth = 72;
    private const int BarWidthStep = 4;

    private BreadcrumbBar? _bar;
    private int? _barWidth = 34;

    public BreadcrumbViewModel()
    {
        Trail = [];

        // The commands come BEFORE the first ResetTrail: seeding the trail re-queries their CanExecute, and a
        // not-yet-assigned command field would take the whole page down in the shell's constructor.
        DeeperCommand = new RelayCommand(() => SetDepth(Trail.Count + 1), () => Trail.Count < Chain.Length);
        ShallowerCommand = new RelayCommand(() => SetDepth(Trail.Count - 1), () => Trail.Count > 1);
        ResetCommand = new RelayCommand(() => { ResetTrail(InitialDepth); Report("Trail reset."); });
        NarrowerCommand = new RelayCommand(() => SetBarWidth(-BarWidthStep), () => (_barWidth ?? 0) > MinimumBarWidth);
        WiderCommand = new RelayCommand(() => SetBarWidth(+BarWidthStep), () => (_barWidth ?? 0) < MaximumBarWidth);

        ResetTrail(InitialDepth);
    }

    public override string Title => "Breadcrumb Bar";

    public override string Summary =>
        "A trail over a synthetic hierarchy — no paths anywhere. Deeper/Narrower fold the ancestors behind “…”; " +
        "one tab stop with ←/→ inside, Home/End jump, Enter activates a chip, F2 edits (Enter commits, Esc reverts).";

    /// <summary>The trail's segments, deepest last. The bar's <c>ItemsSource</c>; replacing it IS navigation.</summary>
    public ObservableCollection<TrailNode> Trail { get; }

    /// <summary>Appends the next canned segment.</summary>
    public RelayCommand DeeperCommand { get; }

    /// <summary>Drops the deepest segment.</summary>
    public RelayCommand ShallowerCommand { get; }

    /// <summary>Restores the starting depth.</summary>
    public ICommand ResetCommand { get; }

    /// <summary>Shrinks <see cref="BarWidth"/> — the cheap way to provoke the fold.</summary>
    public RelayCommand NarrowerCommand { get; }

    /// <summary>Grows <see cref="BarWidth"/>.</summary>
    public RelayCommand WiderCommand { get; }

    /// <summary>The bar's width in cells, bound to <c>BreadcrumbBar.Width</c> (an <c>int?</c> cell count, hence the
    /// nullable). Narrowing it is what makes the leading-overflow fold demonstrable without resizing the terminal.</summary>
    public int? BarWidth { get => _barWidth; private set => Set(ref _barWidth, value); }

    /// <summary>The deepest segment — the one the trail is "about" (<c>:current</c> in the chips).</summary>
    public string CurrentNode => Trail.Count == 0
        ? "(empty)"
        : $"{Trail[^1].Label} — {Trail[^1].Kind} · depth {Trail.Count} of {Chain.Length}";

    /// <summary>The live readout of the last thing the bar reported.</summary>
    public override string? Status
    {
        get;
        protected set => Set(ref field, value);
    } = "Tab into the trail, then ←/→ to move · Enter activates · F2 edits.";

    // ───────────────────────────── the control wiring ─────────────────────────────
    //
    // Six routed events, none of which a binding can carry: the page's view hands the bar over here (see
    // Pages/GalleryBreadcrumbBar.cs) instead of the view-model reaching into the tree.

    /// <summary>Attaches to the page's bar (idempotent).</summary>
    /// <param name="bar">The breadcrumb bar this page is showing.</param>
    internal void Connect(BreadcrumbBar bar)
    {
        if (ReferenceEquals(_bar, bar))
            return;

        Disconnect(_bar);
        _bar = bar;

        bar.ItemActivated += OnItemActivated;
        bar.EditingStarted += OnEditingStarted;
        bar.EditCommitted += OnEditCommitted;
        bar.EditCanceled += OnEditCanceled;
        bar.DropDownOpening += OnDropDownOpening;
        bar.ChildActivated += OnChildActivated;
    }

    /// <summary>Detaches from <paramref name="bar"/> (a no-op unless it is the connected one).</summary>
    /// <param name="bar">The breadcrumb bar leaving the tree.</param>
    internal void Disconnect(BreadcrumbBar? bar)
    {
        if (bar is null || !ReferenceEquals(_bar, bar))
            return;

        bar.ItemActivated -= OnItemActivated;
        bar.EditingStarted -= OnEditingStarted;
        bar.EditCommitted -= OnEditCommitted;
        bar.EditCanceled -= OnEditCanceled;
        bar.DropDownOpening -= OnDropDownOpening;
        bar.ChildActivated -= OnChildActivated;
        _bar = null;
    }

    // Activating a chip truncates the trail at that depth — the classic breadcrumb gesture, and the proof that the
    // HOST owns navigation. The activated chip itself always survives the edit (only the segments after it go), so
    // the element that is mid-click is never the one being removed.
    private void OnItemActivated(object? sender, BreadcrumbBarItemEventArgs e)
    {
        if (e.Item is not TrailNode node || e.Index < 0)
            return;

        var wasCurrent = e.Index == Trail.Count - 1;
        SetDepth(e.Index + 1);

        Report(wasCurrent
            ? $"Activated “{node.Label}” — already the current segment, so the trail is unchanged."
            : $"Activated “{node.Label}” (index {e.Index}) — the page truncated the trail to that depth.");
    }

    // The bar seeds edit mode with its own Text; this page replaces it with a rendering it can parse back.
    private void OnEditingStarted(object? sender, BreadcrumbBarEditEventArgs e)
    {
        e.Text = string.Join(TextSeparator, Trail.Select(node => node.Label));
        Report("F2 → edit mode. The page supplied the text; the bar has never parsed a thing.");
    }

    private void OnEditCommitted(object? sender, BreadcrumbBarEditEventArgs e)
    {
        var labels = e.Text.Split(TextSeparatorChar, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (labels.Length == 0)
        {
            // Validation rejected it: KeepEditing holds the box open with the text intact rather than silently
            // discarding the edit or leaving an empty trail behind.
            e.KeepEditing = true;
            Report("Commit rejected — a trail needs at least one segment. KeepEditing kept the box open.");
            return;
        }

        Trail.Clear();
        foreach (var label in labels)
            Trail.Add(NodeFor(label));

        RaiseTrailChanged();
        Report($"Committed — the page parsed {labels.Length} segment(s) out of the text and rebuilt the trail.");
    }

    private void OnEditCanceled(object? sender, BreadcrumbBarEditEventArgs e)
        => Report("Esc → edit canceled. The typed text was discarded; the committed trail is untouched.");

    // A chip's "▸" was pressed (or ↓ was struck on it). The list belongs to the segment the affordance sits ON, and
    // it is that segment's CHILDREN — the things you can drill INTO from there — not its peers. Siblings[N] is the
    // peer set at depth N, i.e. the child list of depth N-1, so the chevron on segment N opens Siblings[N + 1].
    // Anchoring Siblings[N] to segment N was an off-by-one: it made the chevron beside a segment offer the list that
    // segment already sits in, and left the root with nothing to open at all.
    private void OnDropDownOpening(object? sender, BreadcrumbBarDropDownEventArgs e)
    {
        var childDepth = e.Index + 1;
        if (e.Index < 0 || childDepth >= Siblings.Length)
            return; // the deepest level has no children to enumerate — the affordance stays inert

        foreach (var label in Siblings[childDepth])
            e.Children.Add(NodeFor(label));
    }

    private void OnChildActivated(object? sender, BreadcrumbBarDropDownEventArgs e)
    {
        if (e.SelectedChild is not TrailNode child || e.Index < 0 || e.Index >= Trail.Count)
            return;

        // Picking a CHILD drills: truncate to the anchor segment, then append the pick one level below it. The
        // anchor chip itself always survives, so the closing list still has a live element to fall back to.
        SetDepth(e.Index + 1);
        Trail.Add(child);
        RaiseTrailChanged();
        Report($"Drilled into “{child.Label}” from the ▸ drop-down on segment {e.Index}.");
    }

    // ───────────────────────────── trail mechanics ─────────────────────────────

    private void ResetTrail(int depth)
    {
        Trail.Clear();
        for (var i = 0; i < depth && i < Chain.Length; i++)
            Trail.Add(Chain[i]);

        RaiseTrailChanged();
    }

    private void SetDepth(int depth)
    {
        depth = Math.Clamp(depth, 1, Chain.Length);

        while (Trail.Count > depth)
            Trail.RemoveAt(Trail.Count - 1);

        while (Trail.Count < depth)
            Trail.Add(Chain[Trail.Count]);

        RaiseTrailChanged();
    }

    private void SetBarWidth(int delta)
    {
        BarWidth = Math.Clamp((_barWidth ?? MinimumBarWidth) + delta, MinimumBarWidth, MaximumBarWidth);
        NarrowerCommand.RaiseCanExecuteChanged();
        WiderCommand.RaiseCanExecuteChanged();
    }

    private void RaiseTrailChanged()
    {
        Raise(nameof(CurrentNode));
        DeeperCommand.RaiseCanExecuteChanged();
        ShallowerCommand.RaiseCanExecuteChanged();
    }

    // A label typed in edit mode (or picked from a sibling list) keeps its canned kind when the hierarchy knows it,
    // and is admitted as a free-form segment otherwise — the trail is data, not a schema.
    private static TrailNode NodeFor(string label)
    {
        foreach (var node in Chain)
        {
            if (string.Equals(node.Label, label, StringComparison.OrdinalIgnoreCase))
                return node;
        }

        foreach (var siblings in Siblings)
        {
            foreach (var sibling in siblings)
            {
                if (string.Equals(sibling, label, StringComparison.OrdinalIgnoreCase))
                    return new TrailNode(sibling, "sibling");
            }
        }

        return new TrailNode(label, "custom");
    }

    private void Report(string message) => Status = message;

    /// <summary>
    /// One segment of the trail. A view-model, not a string — which is the page's whole argument: the bar renders
    /// it through the ordinary <c>ItemTemplate</c> chain and reports it back verbatim on activation, so nothing
    /// anywhere has to agree on a separator character.
    /// </summary>
    /// <param name="Label">The chip's text.</param>
    /// <param name="Kind">What this segment IS in the hierarchy ("galaxy", "planet", "city").</param>
    public sealed record TrailNode(string Label, string Kind)
    {
        /// <summary>The <c>▸</c> drop-down shows a plain child's <c>ToString()</c> (a host that wants an icon or a
        /// kind label supplies a <c>CompletionItem</c> instead), so the row reads as the label rather than as a
        /// type name — and it is what the fuzzy filter matches on.</summary>
        public override string ToString() => Label;
    }
}
