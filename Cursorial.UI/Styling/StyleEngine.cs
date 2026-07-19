using System.Runtime.InteropServices;
using System.Text;

using Cursorial.Output;
using Cursorial.Terminal;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// Fork B's matcher + activation engine (design doc §3.3): the per-application orchestrator that
/// turns structural events (attach, class/name changes, <c>Styles</c> mutation, explicit
/// <c>Style</c> assignment) into armed <see cref="StyleRuleFrame"/>s, and interaction-state /
/// pseudo-class flips into frame activation edges. Installed as the production
/// <see cref="IInteractionStateObserver"/> (SD22) and as the frame loop's
/// <see cref="IStyleFrameHooks"/> (B1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two phases</b> (doc §3.3): Phase 1 (structural match) is rare and allowed to allocate —
/// candidate gathering through each scope's <see cref="StyleScopeIndex"/>, the structural walk over
/// the styling-parent chain (<c>LogicalParent ?? VisualParent</c>, SD7), the template barrier
/// (invariant 5, subject-only — SD8), arm-time frame creation with <c>UnmetCount</c>-equivalent
/// truth initialized from current state (SD18). Phase 2 (flips) is hot and allocation-free: an
/// interest-mask AND, then per interested frame a recompute-from-truth reconcile that toggles
/// <see cref="ValueFrame.SetActive"/>.
/// </para>
/// <para>
/// <b>Timing</b> (SD12): structural events re-match synchronously at the mutation site;
/// interaction deliveries apply synchronously at batch commit; flips raised <em>during</em>
/// application queue and drain to fixpoint (generation cap 16) before the outermost mutation
/// returns; flips raised during the frame's layout/render phases queue and surface via
/// <see cref="HasPendingActivations"/> until the next <see cref="FlushPendingActivations"/> (B1).
/// </para>
/// </remarks>
internal sealed class StyleEngine : IStyleFrameHooks, IInteractionStateObserver
{
    private readonly UIApplication _app;

    // The DFS-order base for the app.Theme leg of the Theme(2) channel: large enough to exceed any BuiltIn
    // framework theme-style count, so every app-theme rule sorts above every BuiltIn rule of equal
    // layer/specificity (app.Theme overrides BuiltIn — R2/B13). 2^16 ≫ BuiltIn's handful of theme styles, and
    // ≪ the 27-bit order field, so it never saturates.
    private const int AppThemeOrderBase = 1 << 16;

    // Library-contributed selector styles (the ThemeContributions tier) sort WITHIN the Theme layer above the
    // BuiltIn leg (order base 0 — a library refines the framework default) and below the app-theme leg
    // (AppThemeOrderBase — the app always wins). Each contribution gets a distinct 4096-wide order slot in
    // registration order, so a later-registered library wins a same-target tie (the resource-tier "last wins",
    // applied to styles). The slot is clamped one stride below AppThemeOrderBase so a contribution can never
    // reach the app-theme leg no matter how many register; past ~15 contributions the overflow shares the top
    // slot (inter-contribution order degrades to gather order — a non-issue at realistic library counts, and the
    // above-BuiltIn / below-app invariants still hold). Both are ≪ the 27-bit order field.
    private const int ContributionThemeOrderBase = 1 << 12; // 4096 — the first contribution's DFS start index.
    private const int ContributionThemeStride = 1 << 12;    // 4096 — per-contribution order-slot width.

    // The pending-reconcile queue (retained lists, swap-drained — zero steady-state allocation).
    private List<UIElement> _pending = [];
    private List<UIElement> _drainScratch = [];
    private int _applying;
    private bool _draining;

    // SD24: the in-flight structural re-match guard. A structural mutation (class/name/explicit
    // Style/Styles) raised by user code that runs DURING an element's own arm pass (a setter
    // notification fires a class change on the same element) must NOT re-enter ReMatchElement
    // against that element's not-yet-committed Frames array — the nested pass would diff stale
    // state, arm duplicate frames, and the outer pass would then clobber state.Frames, orphaning
    // the nested frames in the store (a direct invariant-4 hole). Instead the request is recorded
    // and drained after the outer ApplyMatchDiff commits, looping to a structural fixpoint.
    private readonly HashSet<UIElement> _rematchInFlight = new(ReferenceEqualityComparer.Instance);
    private List<UIElement>? _deferredRematch;
    private int _structuralDepth;

    // Re-entrancy-safe scratch pools for Phase 1 gathering (S179 is bounded-not-zero, but a
    // hot-reload subtree re-match would otherwise allocate two lists per element). The pools are
    // stacks: a nested re-match (a setter notification mutating a different element's structure)
    // rents fresh lists and returns them on exit, so the depth never corrupts a parent's scratch.
    private readonly Stack<List<ScopeCandidate>> _candidateScratch = new();
    private readonly Stack<List<UIElement>> _chainScratch = new();

    private TerminalCapabilities? _capabilities;

#if DEBUG
    // Per-drain edge tracking for the §3.3 style-loop diagnostic (A→B→A within one drain).
    private readonly List<StyleRuleFrame> _drainEdges = [];
    private bool _loopWarned;
#endif

    internal StyleEngine(UIApplication app) => _app = app;

    // ───────────────────────────── IStyleFrameHooks (B1) ─────────────────────────────

    /// <inheritdoc/>
    public bool HasPendingActivations => _pending.Count > 0;

    /// <inheritdoc/>
    public void FlushPendingActivations() => DrainQueue();

    /// <inheritdoc/>
    public void OnCapabilitiesChanged(TerminalCapabilities capabilities)
    {
        // B2: record the snapshot; stamping happens at visual-root attachment. For an already
        // attached root (renegotiation) re-stamp immediately — the caps-* class swap rides the
        // ordinary class-change re-match path within the same tick (B4 P3 slice, SD14).
        _capabilities = capabilities;

        RestampCapabilityClasses();
    }

    // ───────────────────────────── interaction-state intake (SD22 / ND11) ─────────────────────────────

    /// <inheritdoc/>
    public void OnInteractionStateChanged(UIElement element, InteractionState oldState, InteractionState newState)
    {
        if (element.StyleStateInternal is not {} state)
            return; // no styling state — O(1), allocation-free (S175)

        var delta = oldState ^ newState;

        if ((delta & (state.SubjectInterest | state.AncestorInterest)) == 0)
            return; // the one-AND early-out (S174)

        RequestReconcile(element, state);
    }

    /// <summary>A custom pseudo-class flip (<see cref="PseudoClassSet"/> / <see cref="PseudoClassMapping"/>).</summary>
    internal void OnCustomPseudoClassChanged(UIElement element)
    {
        if (element.StyleStateInternal is not {} state)
            return;

        if (state is { HasCustomSubjectInterest: false, HasCustomAncestorInterest: false })
            return;

        RequestReconcile(element, state);
    }

    /// <summary>
    /// Opens an apply scope: reconciles requested inside it queue and drain at disposal (used by
    /// <see cref="PseudoClassMapping"/> to apply a retire-old/set-new class pair in one pass).
    /// </summary>
    internal StyleApplyScope DeferReconciliation()
    {
        _applying++;
        return new StyleApplyScope(this);
    }

    internal void CloseApplyScope()
    {
        _applying--;
        DrainQueue();
    }

    // ───────────────────────────── structural events (SD12 — synchronous at the mutation site) ─────────────────────────────

    /// <summary>The attach walk's per-element hook (B19 — before the element's first measure).</summary>
    internal void OnElementAttached(UIElement element)
    {
        if (!IsStylable(element))
            return;

        // SD14: capability classes stamp on a surface root at its visual-root attachment. ANY surface root
        // (the app root, a shown Window, an open Popup, WM chrome) — not just app.RootElement — so window/
        // popup content can match caps-* selectors (P7 multi-surface). At this point IsStylable(element) holds,
        // so VisualParent == null ⇒ this element is a surface root. The RootElementHost is STYLING-TRANSPARENT:
        // the stamp passes through to its hosted content, so the classes land where they always have (the
        // application's root element) and caps-* descendant-rule matching is unchanged by the wrapper.
        if (element.VisualParent is null)
            StampCapabilityClasses(element is RootElementHost host ? host.Content : element);

        BeginStructuralPass();

        try
        {
            ReMatchElement(element);
        }
        finally
        {
            EndStructuralPass();
        }
    }

    /// <summary>The detach walk's per-element hook (bottom-up; SD15 — the state drops entirely).</summary>
    internal void OnElementDetached(UIElement element)
    {
        if (element.StyleStateInternal is not {} state)
            return;

        RetractAllFrames(element, state);
        element.StyleStateInternal = null; // permanent drop — reattach rebuilds from scratch (SD15)
        DrainQueue();
    }

    /// <summary>Class-set mutation: re-match the element, plus the bounded subtree re-match when the class is ancestor-interesting.</summary>
    /// <param name="element">The mutated element.</param>
    /// <param name="changedClass">The single added/removed class, or <see langword="null"/> for a bulk <c>Replace</c>.</param>
    internal void OnClassesChanged(UIElement element, string? changedClass)
    {
        if (!IsStylable(element))
            return;

        BeginStructuralPass();

        try
        {
            ReMatchElement(element);

            if (changedClass is null || IsAncestorInterestingDiscriminator(element, changedClass, isClass: true))
                ReMatchSubtree(element, includeSelf: false);
        }
        finally
        {
            EndStructuralPass();
        }
    }

    /// <summary>A post-construction <see cref="UIElement.Name"/> change (S130).</summary>
    internal void OnNameChanged(UIElement element, string? oldName, string? newName)
    {
        if (!IsStylable(element))
            return;

        BeginStructuralPass();

        try
        {
            ReMatchElement(element);

            if ((oldName is not null && IsAncestorInterestingDiscriminator(element, oldName, isClass: false)) ||
                (newName is not null && IsAncestorInterestingDiscriminator(element, newName, isClass: false)))
            {
                ReMatchSubtree(element, includeSelf: false);
            }
        }
        finally
        {
            EndStructuralPass();
        }
    }

    /// <summary>An explicit <see cref="UIElement.Style"/> swap — the re-match diff keeps survivors and orders adds before removals (S133).</summary>
    internal void OnExplicitStyleChanged(UIElement element)
    {
        if (!IsStylable(element))
            return;

        BeginStructuralPass();

        try
        {
            ReMatchElement(element);
        }
        finally
        {
            EndStructuralPass();
        }
    }

    /// <summary>
    /// A control's resolved theme identity changed (the <c>Control.Theme</c> override set, a variant
    /// flip, or chain shadowing — CD13/CD15): re-match the element so the new theme's rules arm and the
    /// old theme's frames retract (the SD21 identity diff keeps shared survivors). A re-resolve to the
    /// <em>same</em> <see cref="Style"/> instance is a no-op diff (CD15 — variant flips keep the
    /// per-type theme identity, so no re-templating).
    /// </summary>
    internal void OnControlThemeChanged(UIElement element)
    {
        if (!IsStylable(element))
            return;

        BeginStructuralPass();

        try
        {
            ReMatchElement(element);
        }
        finally
        {
            EndStructuralPass();
        }
    }

    /// <summary>An element scope's <see cref="UIElement.Styles"/> changed (SD21 — coarse re-match with identity diff).</summary>
    internal void OnScopeStylesInvalidated(UIElement scopeOwner)
    {
        if (!IsStylable(scopeOwner))
            return;

        BeginStructuralPass();

        try
        {
            ReMatchSubtree(scopeOwner, includeSelf: true);
        }
        finally
        {
            EndStructuralPass();
        }
    }

    /// <summary>The application <see cref="UIApplication.Styles"/> collection changed (SD21).</summary>
    internal void OnAppStylesInvalidated()
    {
        var roots = StylableSurfaceRoots();
        if (roots.Count == 0) return;

        BeginStructuralPass();

        try
        {
            foreach (var root in roots) // app styles affect every surface (P7 multi-surface)
                ReMatchSubtree(root, includeSelf: true);
        }
        finally
        {
            EndStructuralPass();
        }
    }

    /// <summary>
    /// The application <see cref="UIApplication.Theme"/> was reassigned, or its <c>Styles</c> slot mutated
    /// (R2/B13 / C100): coarse re-match so the Theme(2) leg re-reads the new theme rules. The SD21 identity
    /// diff (owner = the theme dictionary) retracts the previous theme's frames and arms the new ones; a
    /// BuiltIn rule the new theme does not itself redefine survives untouched. Distinct from a variant flip,
    /// which stays resource-only (CD15). Re-entrancy-safe like the app.Styles path: a theme.Styles mutation
    /// raised by user code during a re-match defers and drains at the structural fixpoint (SD24).
    /// </summary>
    internal void OnThemeStylesInvalidated()
    {
        var roots = StylableSurfaceRoots();
        if (roots.Count == 0) return;

        BeginStructuralPass();

        try
        {
            foreach (var root in roots) // theme styles affect every surface (P7 multi-surface)
                ReMatchSubtree(root, includeSelf: true);
        }
        finally
        {
            EndStructuralPass();
        }
    }

    // ───────────────────────────── structural-pass scope (SD24 re-entrancy fence) ─────────────────────────────

    /// <summary>Opens a structural-mutation pass. Nested structural mutations defer; the outermost close drains them to fixpoint, then the reconcile queue.</summary>
    private void BeginStructuralPass() => _structuralDepth++;

    private void EndStructuralPass()
    {
        try
        {
            // Drain any structural re-matches that nested user code deferred (SD24), to a fixpoint
            // bounded by the same generation cap as the flip drain. Only the outermost pass drains.
            if (_structuralDepth == 1 && _deferredRematch is { Count: > 0 })
            {
                var generation = 0;

                while (_deferredRematch is { Count: > 0 })
                {
                    if (++generation > 16)
                    {
                        _deferredRematch = null; // abandon the stuck work so the recovered engine isn't wedged
                        throw BuildStructuralCapException();
                    }

                    var batch = _deferredRematch;
                    _deferredRematch = null; // re-entrant defers accumulate into a fresh list

                    foreach (var element in batch)
                    {
                        // Skip elements detached since they deferred — OnElementDetached already
                        // retracted their frames and dropped the state (SD15).
                        if (!IsStylable(element))
                            continue;

                        ReMatchElement(element);
                    }
                }
            }
        }
        finally
        {
            // Always restore the depth — a cap throw must not wedge the engine into a permanent
            // defer-everything state.
            _structuralDepth--;
        }

        if (_structuralDepth == 0)
            DrainQueue();
    }

    private InvalidOperationException BuildStructuralCapException()
        => new("Styling structural re-match did not reach fixpoint within 16 generations (design doc §3.3, SD24) — " +
               "a structural mutation (class/name/Style/Styles change) raised from a style-driven property notification " +
               "keeps re-triggering a re-match. Break the cycle by not mutating the matched element's selector inputs from " +
               "its own style-applied property observers.");

    // ───────────────────────────── the reconcile queue (SD12 fixpoint) ─────────────────────────────

    private void RequestReconcile(UIElement element, ElementStyleState state)
    {
        if (state.QueuedForReconcile)
            return;

        state.QueuedForReconcile = true;
        _pending.Add(element);

        if (_applying == 0)
            DrainQueue();
    }

    private void DrainQueue()
    {
        if (_draining || _applying > 0 || _pending.Count == 0 || _app.InDeferredStylingPhase)
            return; // deferred-phase flips surface via HasPendingActivations and wait for the hook flush (B1)

        _draining = true;
#if DEBUG
        _drainEdges.Clear();
        _loopWarned = false;
#endif
        var generation = 0;

        try
        {
            while (_pending.Count > 0)
            {
                if (++generation > 16)
                    throw BuildGenerationCapException();

                var batch = _pending;
                _pending = _drainScratch; // re-entrant flips accumulate into the fresh list
                _drainScratch = batch;

                foreach (var element in batch)
                {
                    if (element.StyleStateInternal is not {} state)
                        continue; // detached/retracted since it queued

                    state.QueuedForReconcile = false;
                    ReconcileElement(element, state);
                }

                batch.Clear();
            }
        }
        finally
        {
            _draining = false;
#if DEBUG
            _drainEdges.Clear();
#endif
        }
    }

    private InvalidOperationException BuildGenerationCapException()
    {
        var trace = new StringBuilder(
            "Styling activation did not reach fixpoint within 16 generations (design doc §3.3 — a style loop). Pending: ");

        var first = true;

        foreach (var element in _pending)
        {
            if (element.StyleStateInternal is not {} state)
                continue;

            foreach (var frame in state.Frames)
            {
                if (!first)
                    trace.Append(" -> ");

                trace.Append('\'').Append(frame.Rule.SelectorText.Length == 0 ? "(explicit)" : frame.Rule.SelectorText).Append('\'');
                first = false;
            }
        }

        return new InvalidOperationException(trace.ToString());
    }

    // ReSharper disable once UnusedParameter.Local
    private void ReconcileElement(UIElement element, ElementStyleState state)
    {
        _applying++;

        try
        {
            var frames = state.Frames; // snapshot — a nested re-match may replace the array

            foreach (var frame in frames)
                ReconcileFrame(frame);

            if (state.Dependents is {} dependents)
            {
                // ReSharper disable once ForCanBeConvertedToForeach
                // Index loop tolerant of unregistration during reconciliation.
                for (var i = 0; i < dependents.Count; i++)
                    ReconcileFrame(dependents[i].Owner);
            }
        }
        finally
        {
            _applying--;
        }
    }

    private void ReconcileFrame(StyleRuleFrame frame)
    {
        if (frame.Store is null)
            return; // removed by a nested re-match since the snapshot

        var satisfied = ComputeSatisfied(frame);
        if (satisfied == frame.IsActive) return;

        _applying++;

        try
        {
            TrackDrainEdge(frame);

            if (satisfied)
            {
                frame.Activate();
                RunEdgeActions(frame, frame.Rule.DeclaringStyle.EnterOrNull, entering: true);
            }
            else
            {
                frame.Deactivate();
                RunEdgeActions(frame, frame.Rule.DeclaringStyle.ExitOrNull, entering: false);
            }
        }
        finally
        {
            _applying--;
        }
    }

    private static bool ComputeSatisfied(StyleRuleFrame frame)
    {
        // Recompute-from-truth (immune to ordering drift; O(requirements), allocation-free — the
        // counter-decrement shape of doc §3.3 is an optimization this recompute replaces 1:1).
        if (frame.Owner is not {} owner)
            return true;

        var rule = frame.Rule;

        if ((owner.InteractionStateInternal & rule.SubjectStateBits) != rule.SubjectStateBits)
            return false;

        foreach (var pseudo in rule.SubjectCustomPseudoClasses)
        {
            if (!owner.HasCustomPseudoClass(pseudo))
                return false;
        }

        if (frame.AncestorRequirements is {} requirements)
        {
            foreach (var requirement in requirements)
            {
                if (!requirement.IsSatisfied())
                    return false;
            }
        }

        // When data conditions: every armed condition's watch value must satisfy its verdict
        // (unresolved ⇒ unmet — doc §3.3). The watches are live (B16); this is a pure read of their
        // last-delivered values, allocation-free, ordering-immune.
        if (frame.WhenRequirements is {} whenRequirements)
        {
            foreach (var requirement in whenRequirements)
            {
                if (!requirement.IsMet)
                    return false;
            }
        }

        return true;
    }

    private static void RunEdgeActions(StyleRuleFrame frame, EdgeActionCollection? actions, bool entering)
    {
        if (actions is not { Count: > 0 } || frame.Owner is not {} owner)
            return;

        // Rule-document order, on EVERY edge (SD16). No exception guard at P3 — S5 adds the
        // no-throw contract with the (igniter, scope) registry at P8 (B5).
        foreach (var action in actions)
        {
            if (entering)
                action.OnActivated(owner);
            else
                action.OnRetracted(owner);
        }
    }

#if DEBUG
    private void TrackDrainEdge(StyleRuleFrame frame)
    {
        if (!_draining || _loopWarned)
            return;

        foreach (var edge in _drainEdges)
        {
            if (ReferenceEquals(edge, frame))
            {
                // A→B→A within one drain: name the re-toggled rule and its most recent partner.
                var partner = _drainEdges[^1];
                StyleDebugDiagnostics.WarnStyleLoop(frame.Rule, partner.Rule);
                _loopWarned = true;
                return;
            }
        }

        _drainEdges.Add(frame);
    }
#else
    private static void TrackDrainEdge(StyleRuleFrame frame)
    {
    }
#endif

    // ───────────────────────────── Phase 1 — structural matching ─────────────────────────────

    // An element is stylable when it is attached under a LIVE surface root — its visual root owns a
    // LayoutManager. This covers the chrome-less application root AND every shown Window / open Popup
    // surface uniformly (P7 — the single-root "VisualRoot == app.RootElement" test ignored window/popup
    // surfaces, leaving their controls untemplated and zero-sized). The LayoutManager is the right signal
    // (not RenderTreeHost): AttachAsRoot sets it before the styling-attach walk, whereas the RenderTree —
    // which requires an already-attached root — is constructed only afterward, so RenderTreeHost is still
    // null when styles must first arm.
    private static bool IsStylable(UIElement element)
        => element.IsAttachedToTree && element.GetLayoutManager() is not null; 

    /// <summary>
    /// Every currently-stylable surface root — the chrome-less application root plus every shown Window, open
    /// Popup, and WM-chrome surface (P7 multi-surface). Capability-class stamping and app/theme-wide re-match
    /// must cover all of them, not just <see cref="UIApplication.RootElement"/>. (At an app root's own attach
    /// the WM has not registered its surface yet, so <see cref="OnElementAttached"/> stamps the attaching root
    /// directly; this enumerates the post-registration set for the rarer caps/styles/theme-change events.)
    /// </summary>
    private List<UIElement> StylableSurfaceRoots()
    {
        var roots = new List<UIElement>();

        if (_app.WindowManager is {} wm)
        {
            var surfaces = wm.Surfaces;

            foreach (var surface in surfaces)
            {
                // The RootElementHost is styling-transparent — its hosted content is the stylable root.
                var root = surface.Root is RootElementHost host ? host.Content : surface.Root;

                if (IsStylable(root))
                    roots.Add(root);
            }
        }
        else if (_app.RootElement is {} root && IsStylable(root))
        {
            roots.Add(root); // pre-compose fallback (no window manager yet)
        }

        return roots;
    }

    /// <summary>Re-runs Phase 1 for one element and applies the SD21 identity diff to its armed frames.</summary>
    private void ReMatchElement(UIElement element)
    {
        // SD24: if this element is already mid-arm higher in the stack, a user-code structural
        // mutation re-entered. Defer — re-running here would diff the not-yet-committed Frames array
        // and orphan frames in the store. The deferred re-match runs once the outer pass commits.
        if (!_rematchInFlight.Add(element))
        {
            _deferredRematch ??= [];

            if (!_deferredRematch.Contains(element))
                _deferredRematch.Add(element);

            return;
        }

        var matches = RentCandidateList();

        try
        {
            GatherMatches(element, matches);
            ApplyMatchDiff(element, matches);
        }
        finally
        {
            ReturnCandidateList(matches);
            _rematchInFlight.Remove(element);
        }
    }

    /// <summary>The visual-subtree re-match walk (scope mutation / ancestor-interesting changes).</summary>
    private void ReMatchSubtree(UIElement element, bool includeSelf)
    {
        if (includeSelf)
        {
            if (!IsStylable(element))
                return;

            ReMatchElement(element);
        }

        if (element.VisualChildrenList is not {} children)
            return;

        foreach (var child in children)
        {
            if (!IsStylable(child))
                continue; // mid-attach-walk children arm in their own attach step

            ReMatchElement(child);
            ReMatchSubtree(child, includeSelf: false);
        }
    }

    private void GatherMatches(UIElement element, List<ScopeCandidate> matches)
    {
        var candidates = RentCandidateList();
        var chain = RentChainList();

        try
        {
            // The styling-parent chain, subject-first (SD7); chain depth feeds scopeDepth (SD6).
            for (var node = element; node is not null; node = node.StylingParent)
                chain.Add(node);

            var chainCount = chain.Count;

            // The RootElementHost is styling-transparent (a root-surface implementation detail): it neither
            // contributes styles nor counts as a scope level, so depths stay relative to the APPLICATION
            // root — identical to the pre-wrapper numbering and symmetric with window-surface chains.
            if (chainCount > 0 && chain[chainCount - 1] is RootElementHost)
                chainCount--;

            // Scoped(4) collections on the chain, self-inclusive (S70); App(3) below them.
            for (var i = 0; i < chainCount; i++)
            {
                if (chain[i].StylesOrNull is { Count: > 0 } scoped)
                {
                    var depth = chainCount - 1 - i;
                    scoped.GetOrBuildIndex(StyleLayer.Scoped, depth).GatherCandidates(element, candidates, chain[i]);
                }
            }

            if (_app.StylesOrNull is { Count: > 0 } appStyles)
                appStyles.GetOrBuildIndex(StyleLayer.App, 0).GatherCandidates(element, candidates, _app);

            // Theme(2) — the theme-styles channel (design doc §11.8 #3 / C100 / CD30), two legs both armed
            // below App so an app style always wins (C102). The BuiltIn FRAMEWORK leg first: it is the
            // always-present fallback (sealed, its (Theme, 0) index warmed once in CursorialTheme's static
            // init — a one-time gather at match is exact), so its infrastructure rules (requirement 6's
            // access-key cue, the caps-* layers) survive even under a partial custom theme.
            if (Themes.CursorialTheme.BuiltIn.Styles is { Count: > 0 } builtInStyles)
                builtInStyles.GetOrBuildIndex(StyleLayer.Theme, 0).GatherCandidates(element, candidates, Themes.CursorialTheme.BuiltIn);

            // Theme(2), contributed leg (design doc §11.3a/§11.8, amended): library-shipped selector styles from
            // the ThemeContributions tier, gathered ABOVE the BuiltIn leg and BELOW the app.Theme leg and App, so
            // a library refines the framework default while the app always wins. Each contribution gets its own
            // registration-ordered slot (later wins a tie); a contribution that ships only Type-keyed themes (no
            // Styles) is skipped, and the whole leg short-circuits when no library has contributed. The owner is
            // each contribution dictionary itself (the SD21 frame-identity component), so a late registration's
            // re-arm retracts the right frames.
            if (Themes.ThemeContributions.HasContributions)
            {
                var contributions = Themes.ThemeContributions.Snapshot;
                for (var i = 0; i < contributions.Length; i++)
                {
                    if (contributions[i].Styles is not { Count: > 0 } contributedStyles)
                        continue;

                    var orderBase = Math.Min(
                        ContributionThemeOrderBase + i * ContributionThemeStride,
                        AppThemeOrderBase - ContributionThemeStride);

                    contributedStyles.GetOrBuildIndex(StyleLayer.Theme, 0, orderBase)
                                     .GatherCandidates(element, candidates, contributions[i]);
                }
            }

            // Then the user-facing app.Theme leg (R2/B13): UIApplication.Theme's own Styles slot. It is gathered
            // with AppThemeOrderBase so its rules' DFS order sorts ABOVE every BuiltIn framework rule within the
            // Theme layer — the resource model's "app.Theme layers over BuiltIn" applied to styles: an app-theme
            // rule that redefines an identical BuiltIn rule WINS (larger key), while a BuiltIn rule the theme
            // does NOT redefine is unaffected (no competing candidate). The owner is the theme dictionary itself
            // — the SD21 frame-identity component, distinct from BuiltIn so a theme swap retracts the right
            // frames. Re-armed on theme reassignment / theme.Styles mutation via OnThemeStylesInvalidated; CD15
            // keeps a variant flip resource-only. Styles in the theme's own slot are consumed; Styles nested in
            // its MergedDictionaries are not flattened in v1.
            if (_app.Theme?.Styles is { Count: > 0 } themeStyles)
                themeStyles.GetOrBuildIndex(StyleLayer.Theme, 0, AppThemeOrderBase).GatherCandidates(element, candidates, _app.Theme);

            // Template(1): the owning control template's Styles, scoped to the templated parent (CD30,
            // doc §12.2 step 3). Gathered only for template parts (TemplatedParent != null with a
            // template-styles slot); the rules carry the /template/ hop so they survive the barrier below.
            if (element.TemplatedParent is { TemplateStylesForArming: { Count: > 0 } templateStyles } templatedParent)
            {
                templateStyles.GetOrBuildIndex(StyleLayer.Template, 0)
                              .GatherCandidates(element, candidates, templatedParent);
            }

            // Invariant 5 (SD8): the barrier tests the subject only — a templated part is skipped for
            // every rule without a /template/ hop, BEFORE structural evaluation.
            var barred = element.TemplatedParent is not null;

            foreach (var candidate in candidates)
            {
                if (candidate.Rule.Branch is not {} branch)
                    continue; // selector-less keyed styles never match through the index

                if (barred && !candidate.Rule.HasTemplateHop)
                    continue;

                // A Template-layer rule's '^' subject anchor binds to its scope owner (the templated
                // parent) — the template's Styles are authored as '^ /template/ part' (CD30): after the
                // template hop the '^' compound matches the owner. App/Scoped rules pass a null anchor.
                var anchor = candidate.Layer == StyleLayer.Template ? candidate.ScopeOwner as UIElement : null;

                if (MatchBranch(element, branch, anchor, bindings: null))
                    matches.Add(candidate);
            }
        }
        finally
        {
            ReturnCandidateList(candidates);
            ReturnChainList(chain);
        }

        // ControlTheme(0) — the per-type control theme (a selector-less Style rooted at '^', CD13/CD30):
        // resolved by ControlThemeKey through the chain (or the explicit Control.Theme override), armed
        // element-addressed exactly like the Explicit channel but at the weakest style layer so app
        // styles always beat the theme. Children rules ('^:pressed', '^:checked') arm anchored to the
        // element; the selector-less root rule arms always-active.
        if (element is IControlThemeHost themeHost && ResolveControlTheme(themeHost) is {} controlTheme)
            GatherElementAddressedStyle(element, controlTheme, StyleLayer.ControlTheme, matches);

        // Explicit(5) — element-addressed, exempt from the barrier (SD8/S88); skips the index.
        if (element.ExplicitStyleOrNull is {} explicitStyle)
            GatherElementAddressedStyle(element, explicitStyle, StyleLayer.Explicit, matches);
    }

    /// <summary>
    /// Arms a selector-less or single-compound <c>^</c>-rooted style element-addressed at
    /// <paramref name="layer"/> (the shared shape of the Explicit and ControlTheme channels): a
    /// selector-less rule is always-active; a single-compound <c>^</c>-anchored rule (incl. its
    /// <c>^:pseudo</c> state forms) matches against the element itself. This channel matches the
    /// subject against the styled element only — it does <b>not</b> reach into a template or down
    /// the tree: a multi-compound reach-in rule (<c>^ /template/ part</c>, <c>^ &gt; child</c>) can
    /// never match here and is dropped with a DEBUG diagnostic (#115). Template-part rules belong in
    /// <see cref="Controls.ControlTemplate.Styles"/> (the <c>Template(1)</c> layer, CD30), which the
    /// part-arming pass in <see cref="GatherMatches"/> consumes.
    /// </summary>
    private void GatherElementAddressedStyle(UIElement element, Style style, StyleLayer layer, List<ScopeCandidate> matches)
    {
        var rules = style.CompiledRules;

        for (var r = 0; r < rules.Length; r++)
        {
            var rule = rules[r];
            var key = StyleSortKey.Create(layer, rule.Names, rule.ClassLike, rule.Types, scopeDepth: 0, order: r);

            if (rule.Branch is not {} branch)
            {
                matches.Add(new ScopeCandidate(rule, key, layer, element));
                continue;
            }

            // Element-addressed channels match the subject against the element itself, so only a
            // single-compound rule can apply. A multi-compound reach-in rule — the #115 footgun:
            // a `^ /template/ part` authored in a control theme's Children instead of the template's
            // Styles — would silently never match; warn (DEBUG) and skip rather than drop it silently.
            if (branch.Compounds.Length != 1)
            {
                StyleDebugDiagnostics.WarnElementAddressedReachIn(rule, layer);
                continue;
            }

            if (MatchCompound(element, branch.Subject, anchor: element))
                matches.Add(new ScopeCandidate(rule, key, layer, element));
        }
    }

    /// <summary>
    /// Resolves an element's control theme (design doc §11.3, CD13): the explicit <c>Control.Theme</c>
    /// override wins, else a chain lookup by <see cref="IControlThemeHost.ControlThemeKey"/> (exact-key,
    /// no base probing). Returns <see langword="null"/> on a miss — the control degrades to its own
    /// template/render with a one-time diagnostic owned by the control.
    /// </summary>
    private static Style? ResolveControlTheme(IControlThemeHost host)
    {
        Style? theme = host.ThemeOverride;

        if (theme is null && host.Element.TryFindResource(host.ControlThemeKey, out var value))
            theme = value as Style;

        // A theme style must be sealed before its rules compile (CompiledRules throws otherwise). A
        // BuiltIn theme is sealed by its dictionary; an explicit Control.Theme override is sealed here.
        theme?.Seal();
        return theme;
    }

    // ───────────────────────────── Phase 1 scratch pools (re-entrancy-safe) ─────────────────────────────

    private List<ScopeCandidate> RentCandidateList()
        => _candidateScratch.Count > 0 ? _candidateScratch.Pop() : [];

    private void ReturnCandidateList(List<ScopeCandidate> list)
    {
        list.Clear();
        _candidateScratch.Push(list);
    }

    private List<UIElement> RentChainList()
        => _chainScratch.Count > 0 ? _chainScratch.Pop() : new List<UIElement>(8);

    private void ReturnChainList(List<UIElement> list)
    {
        list.Clear();
        _chainScratch.Push(list);
    }

    private void ApplyMatchDiff(UIElement element, List<ScopeCandidate> matches)
    {
        var state = element.StyleStateInternal;
        var existing = state?.Frames ?? [];
        var consumed = existing.Length == 0 ? null : new bool[existing.Length];

        var resulting = new List<StyleRuleFrame>(matches.Count);
        var addedAny = false;

        _applying++;

        try
        {
            foreach (var match in matches)
            {
                StyleRuleFrame? survivor = null;

                if (consumed is not null)
                {
                    for (var i = 0; i < existing.Length; i++)
                    {
                        var candidate = existing[i];

                        if (!consumed[i] &&
                            ReferenceEquals(candidate.Rule, match.Rule) &&
                            candidate.SortKey == match.Key &&
                            ReferenceEquals(candidate.ScopeOwner, match.ScopeOwner))
                        {
                            survivor = candidate;
                            consumed[i] = true;
                            break;
                        }
                    }
                }

                if (survivor is not null)
                {
                    // SD21: survivors keep their frames, cookies, and activation state — silent
                    // for their properties. Ancestor-state bindings are recomputed (the re-match
                    // may have been triggered by an ancestor structure change).
                    resulting.Add(survivor);

                    if (survivor.Rule.AncestorStateCompounds.Length > 0)
                    {
                        UnbindAncestorRequirements(survivor);
                        BindAncestorRequirements(element, survivor);
                        ReconcileFrame(survivor);
                    }

                    continue;
                }

                var frame = new StyleRuleFrame(element, match.Rule, match.Key, match.Layer, match.ScopeOwner);

                if (match.Rule.AncestorStateCompounds.Length > 0)
                    BindAncestorRequirements(element, frame);

                element.AddFrame(frame);
                resulting.Add(frame);
                addedAny = true;

                // When data conditions (doc §3.3 / §6.8): arm one watch per condition AFTER AddFrame so
                // the state exists for the callback's reconcile request. The initial synchronous
                // delivery rides inside Watch (here, _applying > 0) and merely populates each watch's
                // value; activation is decided by ComputeSatisfied below — no flicker.
                if (match.Rule.HasWhenConditions)
                    BindWhenRequirements(element, frame);

                // SD18 arm-time truth: a rule whose requirements already hold activates within the
                // same arm pass — one notification per affected property, no inactive flicker.
                if (ComputeSatisfied(frame))
                {
                    frame.Activate();
                    RunEdgeActions(frame, frame.Rule.DeclaringStyle.EnterOrNull, entering: true);
                }
            }

            // Removals AFTER additions: a same-value handover (style swap, S133) promotes once with
            // no Default flash — the new frame masks before the old one retracts.
            if (consumed is not null)
            {
                for (var i = 0; i < existing.Length; i++)
                {
                    if (!consumed[i])
                        RemoveFrame(element, existing[i]);
                }
            }
        }
        finally
        {
            _applying--;
        }

        var removedAny = consumed is not null && Array.IndexOf(consumed, false) >= 0;

        if (resulting.Count == 0)
        {
            if (state is not null)
            {
                state.Frames = [];
                state.RebuildSubjectInterest();

                if (state.IsEmpty)
                    element.StyleStateInternal = null;
            }

            return;
        }

        state ??= element.StyleStateInternal ?? (element.StyleStateInternal = new ElementStyleState());

        if (addedAny || removedAny || state.Frames.Length != resulting.Count)
            state.Frames = [.. resulting]; // identity-stable when the match set is unchanged (S103/S128)

        state.RebuildSubjectInterest();
    }

    private void RetractAllFrames(UIElement element, ElementStyleState state)
    {
        _applying++;

        try
        {
            foreach (var frame in state.Frames)
                RemoveFrame(element, frame);
        }
        finally
        {
            _applying--;
        }

        state.Frames = [];
        state.RebuildSubjectInterest();
    }

    private void RemoveFrame(UIElement element, StyleRuleFrame frame)
    {
        var wasActive = frame.IsActive;

        UnbindAncestorRequirements(frame);
        UnbindWhenRequirements(frame); // dispose the When watches (watcher lifetime = armed lifetime — B16)
        element.RemoveFrame(frame);    // cookie retraction — the store promotes (invariant 4)

        if (wasActive)
            RunEdgeActions(frame, frame.Rule.DeclaringStyle.ExitOrNull, entering: false); // SD16: detach/disarm retraction is an exit edge
    }

    // ───────────────────────────── ancestor-state bindings (doc §3.3 AncestorDependency) ─────────────────────────────

    private static void BindAncestorRequirements(UIElement element, StyleRuleFrame frame)
    {
        var rule = frame.Rule;
        var branch = rule.Branch!;
        var stateCompounds = rule.AncestorStateCompounds;
        var requirements = new AncestorStateRequirement[stateCompounds.Length];

        if (!rule.HasTemplateHop && TryComputeChainPlacements(element, branch, out var chain, out var valid))
        {
            // Linear-chain placement sets: a requirement may be satisfied by ANY structurally
            // valid ancestor for ITS OWN compound (PER-COMPOUND independence — each
            // AncestorStateRequirement checks its compound's position set in isolation). For a single
            // ancestor-state compound this is exact CSS semantics. For >1 ancestor-state compound it
            // is an APPROXIMATION: two compounds can each report satisfied by the SAME ancestor
            // (CSS requires distinct, ordered positions — a false positive). That shape is rare,
            // SD23-②-flagged in DEBUG, and the matrix only pins "functional" (S76); a joint-placement
            // check on flip is deferred to P4 if a real consumer needs it (correctness review
            // finding 3).
            for (var i = 0; i < stateCompounds.Length; i++)
            {
                var compound = stateCompounds[i];
                var positions = valid[compound.CompoundIndex];

                var count = System.Numerics.BitOperations.PopCount(positions);
                var candidates = new UIElement[count];
                var index = 0;

                for (var p = 0; positions != 0; p++, positions >>= 1)
                {
                    if ((positions & 1UL) != 0)
                        candidates[index++] = chain[p];
                }

                requirements[i] = new AncestorStateRequirement(frame, compound.Bits, compound.CustomPseudoClasses, candidates);
            }
        }
        else
        {
            // Template-hop branches (or chains beyond the bitmap width): the greedy recursive walk
            // records the first successful placement — single-candidate bindings. The matrix never
            // combines /template/ with ancestor-state pseudos; this is the documented approximation.
            // A chain beyond the 64-element bitmap width is a silent degradation worth flagging in
            // DEBUG (correctness review finding 8) — distinguish it from the expected /template/ case.
            if (!rule.HasTemplateHop)
                StyleDebugDiagnostics.WarnChainTruncation(rule);

            var bindings = new UIElement[branch.Compounds.Length];

            if (!MatchBranch(element, branch, anchor: null, bindings))
            {
                // A raced structure change left the branch structurally unmatched here. Fail CLOSED:
                // bind an empty-candidate (never-satisfied) requirement so ComputeSatisfied keeps the
                // frame inactive until a follow-up re-match rebinds — never activate on subject bits
                // alone (correctness review finding 2). Unreachable on the current synchronous path
                // (binding follows a successful match in the same pass), but fails safe if that
                // invariant is ever broken.
                for (var i = 0; i < stateCompounds.Length; i++)
                {
                    var compound = stateCompounds[i];

                    requirements[i] = new AncestorStateRequirement(
                        frame, compound.Bits, compound.CustomPseudoClasses, []);
                }

                frame.AncestorRequirements = requirements;
                return; // no candidates to register as dependents
            }

            for (var i = 0; i < stateCompounds.Length; i++)
            {
                var compound = stateCompounds[i];

                requirements[i] = new AncestorStateRequirement(
                    frame, compound.Bits, compound.CustomPseudoClasses, [bindings[compound.CompoundIndex]]);
            }
        }

        frame.AncestorRequirements = requirements;

        foreach (var requirement in requirements)
        {
            foreach (var candidate in requirement.Candidates)
            {
                var candidateState = candidate.StyleStateInternal ??= new ElementStyleState();
                candidateState.AddDependent(requirement);
            }
        }
    }

    private static void UnbindAncestorRequirements(StyleRuleFrame frame)
    {
        if (frame.AncestorRequirements is not {} requirements)
            return;

        frame.AncestorRequirements = null;

        foreach (var requirement in requirements)
        {
            foreach (var candidate in requirement.Candidates)
            {
                if (candidate.StyleStateInternal is not {} state)
                    continue;

                state.RemoveDependent(requirement);

                if (state.IsEmpty)
                    candidate.StyleStateInternal = null;
            }
        }
    }

    // ───────────────────────────── When data conditions (doc §3.3 / §6.8) ─────────────────────────────

    /// <summary>
    /// Arms one <see cref="Data.BindingOperations.Watch"/> per <c>When</c> <see cref="DataCondition"/>
    /// on the styled element (the S2 data half), recording the requirements on the frame. The watch
    /// auto-rebinds on DataContext change and re-delivers; every delivery reconciles the frame so a
    /// VM-driven flip participates in the same frame (doc §6.8). The initial synchronous delivery
    /// (inside <c>Watch</c>) merely populates each watch's value — activation is decided by the
    /// caller's <see cref="ComputeSatisfied"/> with no flicker. Watcher lifetime = armed rule lifetime
    /// (ledger B16: live across deactivation, disposed at disarm/detach).
    /// </summary>
    private void BindWhenRequirements(UIElement element, StyleRuleFrame frame)
    {
        var conditions = frame.Rule.WhenConditions;
        var requirements = new WhenConditionRequirement[conditions.Length];

        for (var i = 0; i < conditions.Length; i++)
        {
            var condition = conditions[i];
            var requirement = new WhenConditionRequirement(condition);
            requirements[i] = requirement;

            // The watch callback recomputes satisfaction by reconciling the owning frame. During the
            // synchronous initial delivery (inside Watch) it is a harmless queued no-op — the frame is
            // not yet activated, and the arm pass decides activation via ComputeSatisfied; after the
            // frame is removed/detached OnWhenConditionChanged short-circuits.
            requirement.Watch = Data.BindingOperations.Watch(
                element, condition.Binding, _ => OnWhenConditionChanged(frame));
        }

        frame.WhenRequirements = requirements;
    }

    private static void UnbindWhenRequirements(StyleRuleFrame frame)
    {
        if (frame.WhenRequirements is not {} requirements)
            return;

        frame.WhenRequirements = null;

        foreach (var requirement in requirements)
            requirement.Dispose();
    }

    /// <summary>
    /// A <c>When</c> watch delivered a new value: reconcile the owning frame so its activation tracks
    /// the condition (a live <c>When</c> flip — the Phase-2 hot-path equivalent for data conditions).
    /// Guarded — a removed/disposed frame (store cleared) is skipped; a detached element has no state.
    /// Reconcile rides the existing queued/fixpoint path (synchronous when idle, queued when
    /// mid-apply — B167), identical to a pseudo/ancestor flip.
    /// </summary>
    private void OnWhenConditionChanged(StyleRuleFrame frame)
    {
        if (frame.Store is null || frame.Owner is not {} owner)
            return; // removed/disarmed since the watch was armed

        if (owner.StyleStateInternal is not {} state)
            return; // detached — OnElementDetached already retracted (SD15)

        RequestReconcile(owner, state);
    }

    /// <summary>
    /// The linear-chain placement DP over position bitmaps: <c>valid[i]</c> has bit <c>p</c> set
    /// when compound <c>i</c> can sit at chain position <c>p</c> (subject = 0) in <b>some</b>
    /// complete structural match of the branch. Returns false for chains beyond 64 elements
    /// (callers fall back to the greedy walk).
    /// </summary>
    private static bool TryComputeChainPlacements(
        UIElement element, SelectorBranch branch, out List<UIElement> chain, out ulong[] valid)
    {
        chain = [];

        for (var node = element; node is not null; node = node.StylingParent)
            chain.Add(node);

        var compounds = branch.Compounds;
        var combinators = branch.Combinators;
        var k = compounds.Length;
        var n = chain.Count;

        if (n > 64)
        {
            valid = [];
            return false;
        }

        // matches[i] bit p: compound i structurally matches chain[p].
        var matches = new ulong[k];

        for (var i = 0; i < k; i++)
        {
            var bits = 0UL;

            for (var p = 0; p < n; p++)
            {
                if (MatchCompound(chain[p], compounds[i], anchor: null))
                    bits |= 1UL << p;
            }

            matches[i] = bits;
        }

        // up[i]: compounds 0..i placeable with i at p (earlier compounds strictly above).
        var up = new ulong[k];
        up[0] = matches[0];

        for (var i = 1; i < k; i++)
        {
            var prev = up[i - 1];
            var allowed = combinators[i - 1] == SelectorCombinator.Child ? prev >> 1 : FillDown(prev) >> 1;
            up[i] = matches[i] & allowed;
        }

        // down[i]: compounds i..k−1 placeable with i at p (later compounds strictly below, subject at 0).
        var down = new ulong[k];
        down[k - 1] = matches[k - 1] & 1UL;

        for (var i = k - 2; i >= 0; i--)
        {
            var next = down[i + 1];
            var allowed = combinators[i] == SelectorCombinator.Child ? next << 1 : FillUp(next) << 1;
            down[i] = matches[i] & allowed;
        }

        valid = new ulong[k];

        for (var i = 0; i < k; i++)
            valid[i] = up[i] & down[i];

        return true;

        static ulong FillDown(ulong bits)
        {
            bits |= bits >> 1;
            bits |= bits >> 2;
            bits |= bits >> 4;
            bits |= bits >> 8;
            bits |= bits >> 16;
            bits |= bits >> 32;
            return bits;
        }

        static ulong FillUp(ulong bits)
        {
            bits |= bits << 1;
            bits |= bits << 2;
            bits |= bits << 4;
            bits |= bits << 8;
            bits |= bits << 16;
            bits |= bits << 32;
            return bits;
        }
    }

    // ───────────────────────────── the structural walk ─────────────────────────────

    private static bool MatchBranch(UIElement element, SelectorBranch branch, UIElement? anchor, UIElement[]? bindings)
        => MatchFrom(element, branch, branch.Compounds.Length - 1, anchor, bindings);

    private static bool MatchFrom(UIElement? element, SelectorBranch branch, int compoundIndex, UIElement? anchor, UIElement[]? bindings)
    {
        if (element is null)
            return false;

        if (!MatchCompound(element, branch.Compounds[compoundIndex], anchor))
            return false;

        if (bindings is not null)
            bindings[compoundIndex] = element;

        if (compoundIndex == 0)
            return true;

        switch (branch.Combinators[compoundIndex - 1])
        {
            case SelectorCombinator.Child:
                return MatchFrom(element.StylingParent, branch, compoundIndex - 1, anchor, bindings);

            case SelectorCombinator.Template:
                // SD8: the hop requires a non-null TemplatedParent matching the left compound and
                // crosses exactly one stamp edge — the walk continues FROM the templated parent.
                return element.TemplatedParent is {} templatedParent
                       && MatchFrom(templatedParent, branch, compoundIndex - 1, anchor, bindings);

            default: // Descendant — any styling ancestor, with full backtracking (S68)
                for (var parent = element.StylingParent; parent is not null; parent = parent.StylingParent)
                {
                    if (MatchFrom(parent, branch, compoundIndex - 1, anchor, bindings))
                        return true;
                }

                return false;
        }
    }

    /// <summary>The structural compound test (pseudo simples are state, not structure — skipped here).</summary>
    private static bool MatchCompound(UIElement element, SelectorCompound compound, UIElement? anchor)
    {
        if (compound.IsNesting && !ReferenceEquals(element, anchor))
            return false;

        if (compound.Type is {} type)
        {
            if (compound.IsAssignableType)
            {
                if (!type.IsInstanceOfType(element))
                    return false;
            }
            else if (element.GetType() != type)
            {
                return false;
            }
        }

        foreach (var simple in compound.Simples)
        {
            switch (simple.Kind)
            {
                case SimpleSelectorKind.Class:
                    if (element.ClassesOrNull is not {} classes || !classes.Contains(simple.Value))
                        return false;
                    break;

                case SimpleSelectorKind.Name:
                    if (!string.Equals(element.Name, simple.Value, StringComparison.Ordinal))
                        return false;
                    break;
            }
        }

        return true;
    }

    // ───────────────────────────── ancestor-interesting discriminators (S131/S132) ─────────────────────────────

    private bool IsAncestorInterestingDiscriminator(UIElement element, string name, bool isClass)
    {
        if (_app.StylesOrNull is { Count: > 0 } appStyles &&
            IndexContains(appStyles.GetOrBuildIndex(StyleLayer.App, 0), name, isClass))
        {
            return true;
        }

        // Scopes on the chain (their subtrees include this element's subtree) …
        var depth = 0;

        for (var node = element; node is not null; node = node.StylingParent)
            depth++;

        var position = depth - 1;

        for (var node = element; node is not null; node = node.StylingParent, position--)
        {
            if (node.StylesOrNull is { Count: > 0 } scoped &&
                IndexContains(scoped.GetOrBuildIndex(StyleLayer.Scoped, position), name, isClass))
            {
                return true;
            }
        }

        // … and scopes inside the subtree (their rules' ancestor compounds may reference this element).
        return SubtreeHasInterestedScope(element, name, isClass);
    }

    private static bool SubtreeHasInterestedScope(UIElement element, string name, bool isClass)
    {
        if (element.VisualChildrenList is not {} children)
            return false;

        foreach (var child in children)
        {
            if (child.StylesOrNull is { Count: > 0 } scoped)
            {
                // Depth does not affect the interest sets — probe with the cached index when one
                // exists, else build at the child's actual depth.
                var depth = 0;

                for (var node = child; node is not null; node = node.StylingParent)
                    depth++;

                if (IndexContains(scoped.GetOrBuildIndex(StyleLayer.Scoped, depth - 1), name, isClass))
                    return true;
            }

            if (SubtreeHasInterestedScope(child, name, isClass))
                return true;
        }

        return false;
    }

    private static bool IndexContains(StyleScopeIndex index, string name, bool isClass)
        => isClass
               ? index.AncestorInterestingClasses is {} classes && classes.Contains(name)
               : index.AncestorInterestingNames is {} names && names.Contains(name);

    // ───────────────────────────── capability classes (SD14) ─────────────────────────────

    /// <summary>
    /// Re-stamps the effective-tier color class when <see cref="UIApplication.ActualThemeVariant"/>'s
    /// tier changes (the P5 inversion-6 re-point, CD14): the color-tier class flip rides the
    /// variant-changed event, not a separate negotiated-caps hook. Non-color classes are unaffected.
    /// </summary>
    internal void OnEffectiveTierChanged(ColorDepth tier) => RestampCapabilityClasses();

    /// <summary>Re-stamps the full <c>caps-*</c> set on every stylable surface root — the shared body of the
    /// tier-flip re-stamp and the <see cref="UIApplication.NerdFontAvailable"/> opt-in re-stamp (CD-P2J-1).</summary>
    internal void RestampCapabilityClasses()
    {
        foreach (var root in StylableSurfaceRoots()) // re-stamp on every surface (P7)
            StampCapabilityClasses(root);
    }

    /// <summary>
    /// Stamps the <c>caps-*</c> classes on the shown root (SD14, CD14): exactly one color tier
    /// (<c>caps-truecolor</c>/<c>caps-ansi256</c>/<c>caps-ansi16</c>/<c>caps-nocolor</c>) from the
    /// <b>effective</b> tier (<see cref="UIApplication.ActualThemeVariant"/>.Tier, honoring
    /// <c>RequestedColorTier</c> — the P5 re-point of the P3 scaffolding); <c>caps-motion</c>,
    /// <c>caps-kitty-keyboard</c>, and the unconditional <c>caps-unicode</c> from the
    /// <b>effective</b> snapshot — the negotiated record with
    /// <see cref="UIApplication.CapabilityOverrides"/> folded per axis (FB-5), the same fold
    /// <see cref="UIApplication.EffectiveCapabilities"/> exposes, so classes and the app-visible
    /// snapshot can never desync. Folding at stamp time (rather than caching a rewritten record)
    /// is what makes overrides survive renegotiation for free.
    /// </summary>
    private void StampCapabilityClasses(UIElement root)
    {
        if (_capabilities is not {} negotiated)
            return; // nothing negotiated yet — the startup pre-Show call records only (B2)

        var capabilities = _app.CapabilityOverrides.Apply(negotiated); // the FB-5 per-axis fold

        var replacement = new List<string>();

        if (root.ClassesOrNull is {} existing)
        {
            foreach (var name in existing)
            {
                if (!name.StartsWith("caps-", StringComparison.Ordinal))
                    replacement.Add(name); // app-added classes are preserved; only the caps-* subset is replaced
            }
        }

        // The color-tier class follows the EFFECTIVE tier (inversion 6 — honors RequestedColorTier).
        replacement.Add(_app.ActualThemeVariant.Tier switch
                        {
                            ColorDepth.Truecolor => CapabilityClasses.Truecolor,
                            ColorDepth.Ansi256   => CapabilityClasses.Ansi256,
                            ColorDepth.Ansi16    => CapabilityClasses.Ansi16,
                            _                    => CapabilityClasses.NoColor
                        });

        // Non-color classes stay sourced from the negotiated snapshot (CD14).
        if (capabilities.Input.Mouse.Motion)
            replacement.Add(CapabilityClasses.Motion);

        if (capabilities.Input.Protocol.KittyKeyboardProtocol)
            replacement.Add(CapabilityClasses.KittyKeyboard);

        // Graphics classes (CD-P2J-1): caps-images for ANY inline-image protocol; caps-image-occlusion only for
        // Kitty graphics (z-orderable placements the framework can clip/occlude — Sixel paints inline into the cell
        // grid, iTerm2 is excluded for now pending an occlusion model).
        var graphics = capabilities.Output.Graphics;

        if (graphics.Sixel || graphics.KittyGraphics || graphics.ITerm2InlineImages)
            replacement.Add(CapabilityClasses.Images);

        if (graphics.Sixel || graphics.KittyGraphics)
            replacement.Add(CapabilityClasses.ImageClipping);

        if (graphics.KittyGraphics)
            replacement.Add(CapabilityClasses.ImageOcclusion);

        // caps-nerdfont is a no-probe opt-in (no terminal advertises Nerd Font coverage), sourced from the app's
        // user-options flag (CD-P2J-1) — app state, so it survives renegotiation.
        if (_app.NerdFontAvailable)
            replacement.Add(CapabilityClasses.NerdFont);

        // caps-emoji is probe-less like caps-nerdfont but the OPPOSITE default (FB-15, maintainer decision
        // 2026-07-04): stamped unless the user disables it. Emoji coverage in modern terminals is near-universal
        // (unlike Nerd Font PUA coverage, where default-absent rightly stays), and grid safety is owned by the
        // Icon element's 2-cell emoji measurement, not by hiding the tier. App state — survives renegotiation.
        if (_app.EmojiAvailable)
            replacement.Add(CapabilityClasses.Emoji);

        // caps-unicode is unconditional; caps-ascii is RESERVED and never stamped at P5 — no
        // negotiated glyph-capability source exists (SD14 recorded deferral).
        replacement.Add(CapabilityClasses.Unicode);

        root.Classes.Replace(CollectionsMarshal.AsSpan(replacement)); // one restyle pass (doc §3.2)
    }
}

/// <summary>
/// A <see langword="using"/>-scoped engine apply window (see
/// <see cref="StyleEngine.DeferReconciliation"/>): reconciles requested inside it queue; disposal
/// closes the window and drains to fixpoint. <c>default</c> is a harmless no-op scope.
/// </summary>
internal struct StyleApplyScope(StyleEngine engine) : IDisposable
{
    private StyleEngine? _engine = engine;

    /// <inheritdoc/>
    public void Dispose()
    {
        _engine?.CloseApplyScope();
        _engine = null;
    }
}