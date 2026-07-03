namespace Cursorial.UI.Input;

/// <summary>
/// The Tab / directional navigation engine behind <see cref="FocusManager.MoveFocus"/> and
/// <see cref="FocusManager.FindNext"/> (design doc §7.7, matrix ND16/ND17). Pure queries — it never
/// moves focus itself. The stop collection is recomputed per request into one retained scratch list
/// (the doc's "recomputed per keypress, one pooled list"); only the rare <c>Once</c>-container entry
/// resolution allocates.
/// </summary>
internal sealed class FocusNavigator
{
    private readonly struct Stop(UIElement element, int tabIndex, int order, bool isOnceContainer, UIElement? entryScope)
    {
        public readonly UIElement Element = element;
        public readonly int TabIndex = tabIndex;
        public readonly int Order = order;
        public readonly bool IsOnceContainer = isOnceContainer;

        // The nearest enclosing focus scope at/below the tab container that contains this stop, or null when the
        // stop sits directly in the container's own scope (the baseline). Drives the ND33 cross-scope entry redirect.
        public readonly UIElement? EntryScope = entryScope;
    }

    private static readonly Comparison<Stop> TabOrderComparison =
        static (a, b) => a.TabIndex != b.TabIndex ? a.TabIndex.CompareTo(b.TabIndex) : a.Order.CompareTo(b.Order);

    private const int MaxScopeEntryDepth = 4;

    private readonly List<Stop> _stops = [];

    // The marker's anchor key, captured during collection when the marker element is encountered
    // (the element instance itself never needs retaining — −1 order is the "no marker" sentinel).
    private int _markerOrder;
    private int _markerTabIndex;

    // The focus scope enclosing the marker (the non-stop `from` of a Label-targeting query) — its EntryScope
    // analog, so the ND33 redirect treats a move FROM the marker as intra-scope when it stays in the same scope.
    private UIElement? _markerScope;

    // ───────────────────────────── tab order ─────────────────────────────

    /// <summary>
    /// The element Tab navigation moves to from <paramref name="from"/> (wrapping within the
    /// nearest self-or-ancestor <see cref="KeyboardNavigationMode.Cycle"/> container — the surface
    /// root acting as Cycle by default), entry-resolved for <c>Once</c> containers; null when no
    /// move is possible (no other stop, or <paramref name="from"/> outside any container).
    /// <paramref name="from"/> need not be a tab stop itself (the Label-targeting query): its
    /// document position anchors the search.
    /// </summary>
    public UIElement? NextTabStop(UIElement from, bool forward)
    {
        var container = GetTabContainer(from);

        CollectStops(container, marker: from);

        try
        {
            if (_stops.Count == 0)
                return null;

            _stops.Sort(TabOrderComparison);

            // The current stop is `from` itself, or — when `from` sits inside a Once container —
            // that container (the single stop the whole subtree collapses to, ND16).
            var currentStop = ResolveCurrentStop(from, container);
            var currentIndex = IndexOf(currentStop);
            var step = forward ? 1 : -1;

            int targetIndex;

            if (currentIndex >= 0)
            {
                targetIndex = Wrap(currentIndex + step);
            }
            else if (_markerOrder >= 0)
            {
                // `from` is not an eligible stop (Label targeting; focus on a non-stop element):
                // anchor on its (TabIndex, document-order) key.
                targetIndex = forward ? FirstIndexAfterMarker() : LastIndexBeforeMarker();
            }
            else
            {
                targetIndex = forward ? 0 : _stops.Count - 1;
            }

            // The scope the current position belongs to — the redirect fires only on a genuine entry into a
            // DIFFERENT scope (ND33). For an eligible stop that's its own collected EntryScope; for a non-stop
            // marker (`from` of a Label query) its captured enclosing scope, so an intra-scope move isn't redirected.
            var currentEntryScope = currentIndex >= 0 ? _stops[currentIndex].EntryScope : _markerScope;

            // Resolve the target, skipping Once containers that cannot produce an entry target.
            for (var attempts = 0; attempts < _stops.Count; attempts++, targetIndex = Wrap(targetIndex + step))
            {
                if (targetIndex == currentIndex)
                    return null; // wrapped back to the current stop — no move (N130)

                if (ResolveEntryAcrossScopes(_stops[targetIndex], currentEntryScope, forward) is {} resolved)
                    return resolved;
            }

            return null;
        }
        finally
        {
            ResetScratch();
        }
    }

    /// <summary>The container's first (<paramref name="forward"/>) or last tab-ordered, entry-resolved stop; null when none.</summary>
    public UIElement? FirstOrLastTabStop(UIElement container, bool forward)
    {
        CollectStops(container, marker: null);

        try
        {
            if (_stops.Count == 0)
                return null;

            _stops.Sort(TabOrderComparison);

            var step = forward ? 1 : -1;

            // Entry from outside any scope (no current stop) — always redirect into a target scope's memory (ND33).
            for (var i = forward ? 0 : _stops.Count - 1; i >= 0 && i < _stops.Count; i += step)
            {
                if (ResolveEntryAcrossScopes(_stops[i], currentScope: null, forward) is {} resolved)
                    return resolved;
            }

            return null;
        }
        finally
        {
            ResetScratch();
        }
    }

    private UIElement ResolveCurrentStop(UIElement from, UIElement container)
    {
        UIElement? onceAncestor = null;

        for (var node = from; node is not null && !ReferenceEquals(node, container); node = node.VisualParent ?? node.UIParent)
        {
            if (KeyboardNavigation.GetTabNavigation(node) == KeyboardNavigationMode.Once)
                onceAncestor = node; // keep the highest Once below the container — the one collection stopped at
        }

        return onceAncestor ?? from;
    }

    private int IndexOf(UIElement element)
    {
        for (var i = 0; i < _stops.Count; i++)
        {
            if (ReferenceEquals(_stops[i].Element, element))
                return i;
        }

        return -1;
    }

    /// <summary>The focus scope of <paramref name="element"/>'s stop (ND33), or null when it is not a collected stop.</summary>
    private UIElement? EntryScopeOf(UIElement element)
    {
        var index = IndexOf(element);
        return index >= 0 ? _stops[index].EntryScope : null;
    }

    private int Wrap(int index)
    {
        var count = _stops.Count;
        return ((index % count) + count) % count;
    }

    private int FirstIndexAfterMarker()
    {
        for (var i = 0; i < _stops.Count; i++)
        {
            var stop = _stops[i];

            if (stop.TabIndex > _markerTabIndex || (stop.TabIndex == _markerTabIndex && stop.Order > _markerOrder))
                return i;
        }

        return 0; // wrap
    }

    private int LastIndexBeforeMarker()
    {
        for (var i = _stops.Count - 1; i >= 0; i--)
        {
            var stop = _stops[i];

            if (stop.TabIndex < _markerTabIndex || (stop.TabIndex == _markerTabIndex && stop.Order < _markerOrder))
                return i;
        }

        return _stops.Count - 1; // wrap
    }

    /// <summary>
    /// The element focused when INTERNAL (within-scope) resolution lands on <paramref name="stop"/>: the element
    /// itself for ordinary stops; for a <c>Once</c> container the entry chain. Used by <see cref="ResolveScopeEntry"/>'s
    /// descendant scan — the ND33 cross-scope redirect is applied separately, only at the top-level entry sites
    /// (<see cref="ResolveEntryAcrossScopes"/>).
    /// </summary>
    private static UIElement? ResolveEntry(in Stop stop, int depth)
    {
        if (!stop.IsOnceContainer)
            return stop.Element;

        return ResolveScopeEntry(stop.Element, depth, forward: true);
    }

    /// <summary>
    /// Resolves the focus target for ENTERING a scope/container (ND16 for <c>Once</c>, ND33 generalized to any
    /// <c>IsFocusScope</c>): scope memory (validated, within) → the direction-appropriate eligible descendant
    /// (<paramref name="forward"/> ⇒ first, backward ⇒ last, so a reverse crossing continues in document order) →
    /// the container itself when focusable → null (the stop is skipped). The <c>Once</c> ladder always passes
    /// <paramref name="forward"/> = true (ND16's "entry either direction" = first).
    /// </summary>
    private static UIElement? ResolveScopeEntry(UIElement scope, int depth, bool forward)
    {
        if (depth >= MaxScopeEntryDepth)
            return null;

        if (FocusManager.GetFocusedElement(scope) is {} memory
            && FocusManager.IsValidFocusTarget(memory)
            && IsWithin(memory, scope))
        {
            return memory;
        }

        // Rare path — entering a scope allocates a local stop list (Tab is not a hot path). `inner` is the flat
        // document-order descendant set; a forward crossing takes the first eligible, a backward crossing the last.
        var inner = new List<Stop>();
        var order = 0;
        CollectInto(scope, inner, marker: null, navigator: null, currentScope: null, ref order);
        inner.Sort(TabOrderComparison);

        if (forward)
        {
            for (var i = 0; i < inner.Count; i++)
                if (ResolveEntry(inner[i], depth + 1) is {} resolved)
                    return resolved;
        }
        else
        {
            for (var i = inner.Count - 1; i >= 0; i--)
                if (ResolveEntry(inner[i], depth + 1) is {} resolved)
                    return resolved;
        }

        return FocusManager.IsValidFocusTarget(scope) ? scope : null;
    }

    /// <summary>
    /// Top-level entry resolution with the ND33 cross-scope redirect. A <c>Once</c> target resolves through the one
    /// <c>Once</c> ladder (no double redirect — a <c>Once</c>+<c>IsFocusScope</c> host is handled here, not twice).
    /// Otherwise the redirect fires only on a GENUINE entry — descending INTO a scope the current position is not
    /// already inside. An intra-scope move (same scope) OR an outward / pass-through move toward an ENCLOSING scope's
    /// own member (the target's scope already contains <paramref name="currentScope"/>) returns the raw element — the
    /// trap-prevention gate, so navigation can pass THROUGH and OUT OF a scope's members without snapping to memory.
    /// </summary>
    private static UIElement? ResolveEntryAcrossScopes(in Stop target, UIElement? currentScope, bool forward)
    {
        if (target.IsOnceContainer)
            return ResolveScopeEntry(target.Element, 0, forward: true);

        if (target.EntryScope is {} scope
            && !ReferenceEquals(scope, currentScope)
            && !(currentScope is {} cur && IsWithin(cur, scope)))
        {
            return ResolveScopeEntry(scope, 0, forward);
        }

        return target.Element;
    }

    /// <summary>
    /// The focus target inside <paramref name="scope"/> when entering it (the ND33 ladder: remembered focus →
    /// first tab-ordered eligible descendant → the element itself when focusable → null). The seam behind
    /// <see cref="FocusManager.ResolveFocusEntry"/> — used to resolve an access-key proxy's non-focusable target.
    /// </summary>
    internal static UIElement? ResolveEntryInto(UIElement scope) => ResolveScopeEntry(scope, 0, forward: true);

    private static bool IsWithin(UIElement element, UIElement container)
    {
        for (var node = element.VisualParent ?? element.UIParent; node is not null; node = node.VisualParent ?? node.UIParent)
        {
            if (ReferenceEquals(node, container))
                return true;
        }

        return false;
    }

    // ───────────────────────────── stop collection ─────────────────────────────

    private void CollectStops(UIElement container, UIElement? marker)
    {
        _markerOrder = -1;
        _markerScope = null;
        var order = 0;
        CollectInto(container, _stops, marker, this, currentScope: null, ref order);
    }

    /// <summary>
    /// Document-order DFS over <paramref name="parent"/>'s visual descendants (the container
    /// itself is never a stop in its own collection): invisible subtrees prune;
    /// <see cref="KeyboardNavigationMode.None"/> containers exclude their descendants (the
    /// container itself may still be a stop); <see cref="KeyboardNavigationMode.Once"/> containers
    /// become one stop and never descend.
    /// </summary>
    private static void CollectInto(UIElement parent, List<Stop> stops, UIElement? marker, FocusNavigator? navigator, UIElement? currentScope, ref int order)
    {
        if (parent.VisualChildrenList is not {} children)
            return;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            order++;

            if (navigator is not null && ReferenceEquals(child, marker))
            {
                navigator._markerOrder = order;
                navigator._markerTabIndex = child.GetValue(UIElement.TabIndexProperty);
                navigator._markerScope = FocusManager.GetIsFocusScope(child) ? child : currentScope;
            }

            if (child.Visibility != Visibility.Visible)
                continue; // visibility inherits down — prune the subtree

            // A child that is itself a focus scope becomes the scope for its own stop and its subtree (ND33);
            // otherwise the enclosing scope flows down unchanged. The tab container is the baseline (null).
            var childScope = FocusManager.GetIsFocusScope(child) ? child : currentScope;

            var mode = KeyboardNavigation.GetTabNavigation(child);

            if (mode == KeyboardNavigationMode.Once)
            {
                // One stop for the whole container (ND16); requires the container enabled and a
                // tab stop, not focusable — the entry resolution finds the focus target inside.
                if (child.IsAttachedToTree && child.IsEffectivelyEnabled && child.GetValue(UIElement.IsTabStopProperty))
                    stops.Add(new Stop(child, child.GetValue(UIElement.TabIndexProperty), order, isOnceContainer: true, entryScope: childScope));

                continue;
            }

            if (IsTabEligible(child))
                stops.Add(new Stop(child, child.GetValue(UIElement.TabIndexProperty), order, isOnceContainer: false, entryScope: childScope));

            if (mode != KeyboardNavigationMode.None)
                CollectInto(child, stops, marker, navigator, childScope, ref order);
        }
    }

    private static bool IsTabEligible(UIElement element)
        => element.IsAttachedToTree
           && element.GetValue(UIElement.FocusableProperty)
           && element.GetValue(UIElement.IsTabStopProperty)
           && element.IsEffectivelyEnabled;

    /// <summary>Nearest self-or-ancestor <c>Cycle</c> container; the surface root (which traps by default) as fallback.</summary>
    private static UIElement GetTabContainer(UIElement element)
    {
        var node = element;

        while (true)
        {
            if (KeyboardNavigation.GetTabNavigation(node) == KeyboardNavigationMode.Cycle)
                return node;

            // The Tab CONTAINER boundary is the VISUAL/surface root — walk VisualParent ONLY, never the UIParent
            // bridge. A popup/window surface root has VisualParent == null but a UIParent hop to its host
            // (Popup -> PlacementTarget); crossing it would let Tab collect the HOST's stops and escape the popup
            // (a Backstage menu tabbing out into the document behind it). Window/popup roots default Cycle and trap
            // (doc §7.7 — no OS to Tab out to).
            if (node.VisualParent is not {} parent)
                return node;

            node = parent;
        }
    }

    private void ResetScratch()
    {
        _stops.Clear();
        _markerOrder = -1;
    }

    // ───────────────────────────── directional (ND17) ─────────────────────────────

    /// <summary>
    /// The directional-navigation target from <paramref name="current"/>, or null when no
    /// directional container encloses it or no candidate exists. Scoring per ND17:
    /// direction-filtered on window-translated arranged rects, score =
    /// <c>facing-edge distance + 2 × orthogonal-range gap</c>, lowest wins, ties → tab order;
    /// <see cref="DirectionalNavigationMode.Cycle"/> wraps to the farthest candidate on the
    /// opposite side.
    /// </summary>
    public UIElement? NextDirectional(UIElement current, FocusNavigationDirection direction)
    {
        UIElement? container = null;
        var mode = DirectionalNavigationMode.None;

        for (var node = current; node is not null; node = node.VisualParent ?? node.UIParent)
        {
            var m = KeyboardNavigation.GetDirectionalNavigation(node);

            if (m != DirectionalNavigationMode.None)
            {
                container = node;
                mode = m;
                break;
            }
        }

        if (container is null)
            return null; // opt-in policy (N135): arrows stay with controls

        CollectStops(container, marker: null);

        try
        {
            if (_stops.Count == 0)
                return null;

            _stops.Sort(TabOrderComparison); // candidate ties resolve by tab order (iteration order)
            var currentRect = WindowRect(current);

            var bestIndex = -1;
            var bestScore = int.MaxValue;

            for (var i = 0; i < _stops.Count; i++)
            {
                var candidate = _stops[i].Element;

                if (ReferenceEquals(candidate, current))
                    continue;

                if (TryScore(direction, currentRect, WindowRect(candidate), out var score) && score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0 && mode == DirectionalNavigationMode.Cycle)
            {
                // Wrap: the farthest candidate on the opposite side (max facing-edge distance).
                var opposite = Opposite(direction);
                var bestFacing = -1;

                for (var i = 0; i < _stops.Count; i++)
                {
                    var candidate = _stops[i].Element;

                    if (ReferenceEquals(candidate, current))
                        continue;

                    if (TryFacing(opposite, currentRect, WindowRect(candidate), out var facing) && facing > bestFacing)
                    {
                        bestFacing = facing;
                        bestIndex = i;
                    }
                }
            }

            // Right/Down enter a no-memory scope at its first member; Left/Up at its last (reverse document order).
            var dirForward = direction is FocusNavigationDirection.Right or FocusNavigationDirection.Down;
            return bestIndex >= 0 ? ResolveEntryAcrossScopes(_stops[bestIndex], EntryScopeOf(current), dirForward) : null;
        }
        finally
        {
            ResetScratch();
        }
    }

    private readonly record struct CellBox(int Column, int Row, int Columns, int Rows);

    private static CellBox WindowRect(UIElement element)
    {
        var (column, row) = element.TranslateToWindow(0, 0);
        return new CellBox(column, row, element.Bounds.Columns, element.Bounds.Rows);
    }

    private static bool TryScore(FocusNavigationDirection direction, in CellBox current, in CellBox candidate, out int score)
    {
        if (!TryFacing(direction, current, candidate, out var facing))
        {
            score = 0;
            return false;
        }

        var gap = direction is FocusNavigationDirection.Left or FocusNavigationDirection.Right
                      ? RangeGap(current.Row, current.Rows, candidate.Row, candidate.Rows)
                      : RangeGap(current.Column, current.Columns, candidate.Column, candidate.Columns);

        score = facing + 2 * gap;
        return true;
    }

    /// <summary>Direction filter + facing-edge distance: the candidate must lie strictly beyond the facing edge (ND17).</summary>
    private static bool TryFacing(FocusNavigationDirection direction, in CellBox current, in CellBox candidate, out int facing)
    {
        switch (direction)
        {
            case FocusNavigationDirection.Right when candidate.Column >= current.Column + current.Columns:
                facing = candidate.Column - (current.Column + current.Columns);
                return true;

            case FocusNavigationDirection.Left when candidate.Column + candidate.Columns <= current.Column:
                facing = current.Column - (candidate.Column + candidate.Columns);
                return true;

            case FocusNavigationDirection.Down when candidate.Row >= current.Row + current.Rows:
                facing = candidate.Row - (current.Row + current.Rows);
                return true;

            case FocusNavigationDirection.Up when candidate.Row + candidate.Rows <= current.Row:
                facing = current.Row - (candidate.Row + candidate.Rows);
                return true;

            default:
                facing = 0;
                return false;
        }
    }

    private static int RangeGap(int aStart, int aLength, int bStart, int bLength)
    {
        if (bStart >= aStart + aLength)
            return bStart - (aStart + aLength);

        if (aStart >= bStart + bLength)
            return aStart - (bStart + bLength);

        return 0; // cross-axis ranges overlap
    }

    private static FocusNavigationDirection Opposite(FocusNavigationDirection direction) => direction switch
                                                                                            {
                                                                                                FocusNavigationDirection.Right => FocusNavigationDirection.Left,
                                                                                                FocusNavigationDirection.Left => FocusNavigationDirection.Right,
                                                                                                FocusNavigationDirection.Up => FocusNavigationDirection.Down,
                                                                                                _ => FocusNavigationDirection.Up
                                                                                            };
}
