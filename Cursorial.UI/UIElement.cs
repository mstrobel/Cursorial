using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// The tree / layout / render / input node above <see cref="UIObject"/> (design doc §5.1): one node
/// class in <b>one visual tree</b> (layout, render, hit-test, composite order) plus a separate
/// <b>logical-parent</b> pointer (styling descendant combinators, resource scope, DataContext
/// inheritance). The property system's inheritance parent is
/// <c>LogicalParent ?? VisualParent</c> — content inherits through its <c>ContentControl</c>,
/// template parts through chrome to the templated control.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> The attach walk is pre-order parent-first (set <see cref="VisualRoot"/>, raise
/// <see cref="OnAttachedToTree"/>, mark layout invalid); the detach walk is bottom-up. Elements are
/// reusable — detach + reattach rebuilds all single-shot state. There is no <see cref="IDisposable"/>
/// on elements; pooled scenes (T3) release on detach, and viewmodel subscriptions release via the
/// permanent-detach teardown sweep (<see cref="TearDown"/>).
/// </para>
/// <para>
/// <b>Thread affinity.</b> All tree mutation, layout, and render runs on the single UI thread
/// (invariant 6); entry points assert via <see cref="UIObject.VerifyAccess"/> in DEBUG builds.
/// </para>
/// </remarks>
public abstract partial class UIElement : UIObject
{
    private static readonly UIElement[] NoChildren = [];

    private UIElement? _visualParent;
    private UIElement? _logicalParent;
    private UIElement? _visualRoot;
    private UIElement? _templatedParent;
    private string? _name;
    private List<UIElement>? _visualChildren;
    private List<UIElement>? _logicalChildren;
    private LayoutManager? _layoutManager; // non-null only on a root attached via LayoutManager
    private bool _effectivelyEnabled = true;

    /// <summary>The tree depth from the visual root (0 at the root or when detached) — the layout queues' heap key.</summary>
    internal int Depth;

    // ───────────────────────────── tree surface ─────────────────────────────

    /// <summary>The element's logical parent equivalent (logical parent, templated parent, or control-specific override).</summary>
    protected internal virtual UIElement? UIParent => LogicalParent ?? TemplatedParent;

    /// <summary>The element's parent in the visual tree, set by <see cref="AddVisualChild"/>.</summary>
    public UIElement? VisualParent => _visualParent;

    /// <summary>
    /// The element's logical parent (Fork B descendant combinators, S7 resource scope, S2
    /// DataContext inheritance), set by <see cref="AddLogicalChild"/>; <see langword="null"/> means
    /// inheritance falls back to <see cref="VisualParent"/>.
    /// </summary>
    public UIElement? LogicalParent => _logicalParent;

    /// <summary>The root of the attached visual tree, or <see langword="null"/> when detached.</summary>
    public UIElement? VisualRoot => _visualRoot;

    /// <summary>Whether the element is part of an attached visual tree (true between <see cref="OnAttachedToTree"/> and <see cref="OnDetachedFromTree"/>).</summary>
    public bool IsAttachedToTree => _visualRoot is not null;

    /// <summary>
    /// The template-barrier datum (invariant 5): non-null on elements stamped from a control
    /// template. Set only via <see cref="SetTemplatedParent"/> (the S8 seam) — there is no template
    /// engine at P1; this is storage plus the styling engine's barrier input.
    /// </summary>
    public UIElement? TemplatedParent => _templatedParent;

    /// <summary>
    /// The element's name for <c>x:Name</c> / <c>#name</c> selector lookup. Changes notify the
    /// styling engine (S130 — a rename re-matches <c>#name</c> rules); namescopes arrive with S2
    /// (P4). Comparison is ordinal (SD2).
    /// </summary>
    public string? Name
    {
        get => _name;
        set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal))
                return;

            var oldName = _name;
            _name = value;
            OnStylingNameChanged(oldName, value);
        }
    }

    /// <summary>The element's visual children in physical order — the base paint order.</summary>
    protected IReadOnlyList<UIElement> VisualChildren => _visualChildren ?? (IReadOnlyList<UIElement>)NoChildren;

    /// <summary>
    /// The number of visual children — the public, allocation-free, read-only visual-tree accessor
    /// (the WPF <c>VisualChildrenCount</c> analog; pairs with <see cref="GetVisualChild"/>). Cross-assembly
    /// consumers iterate the visual tree through this pair rather than the mutable internal list.
    /// </summary>
    public int VisualChildrenCount => _visualChildren?.Count ?? 0;

    /// <summary>
    /// Gets the visual child at <paramref name="index"/> in physical (paint) order — the WPF
    /// <c>GetVisualChild</c> analog (pairs with <see cref="VisualChildrenCount"/>).
    /// </summary>
    /// <param name="index">A zero-based index in <c>[0, <see cref="VisualChildrenCount"/>)</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the valid range.</exception>
    public UIElement GetVisualChild(int index)
    {
        var children = _visualChildren;
        if (children is null || (uint)index >= (uint)children.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return children[index];
    }

    /// <summary>The visual-children list for internal allocation-free iteration (may be null).</summary>
    internal List<UIElement>? VisualChildrenList => _visualChildren;

    /// <summary>The logical-children list for internal allocation-free iteration (may be null) — RadioButton group enumeration (CD27).</summary>
    internal List<UIElement>? LogicalChildrenList => _logicalChildren;

    /// <summary>
    /// The visual children iterated by the template-expansion <c>TemplatedParent</c> stamp (S8):
    /// the pre-attach built subtree's children. Allocation-free — empty when none.
    /// </summary>
    internal IReadOnlyList<UIElement> VisualChildrenForTemplateStamp =>
        _visualChildren ?? (IReadOnlyList<UIElement>)NoChildren;

    /// <summary>
    /// Evaluates whether this element is an ancestor of <paramref name="element"/> along the composed
    /// <b>ownership</b> chain — <c>VisualParent ?? UIParent</c>, the same walk the focus chain, access-key
    /// scope resolution, and window-manager dismissal ancestry use. The <see cref="UIParent"/> fallback fires
    /// only where the visual chain ends, which is what lets the relation span a popup seam: content on a
    /// popup surface connects to its owner through visual hops <i>within</i> the surface, one bridge hop at
    /// its root (logical/templated parent, or a <c>Popup</c>'s placement target), then visual hops again.
    /// </summary>
    /// <param name="element">The element to evaluate.</param>
    /// <returns><c>true</c> if <paramref name="element"/> is an ownership-chain descendant of this element.</returns>
    /// <remarks>
    /// For purposes of this method, an element is considered its own ancestor. This is <b>not</b> the same
    /// relation as <see cref="IsLogicalAncestorOf"/> OR'd with <see cref="IsVisualAncestorOf"/>: the popup
    /// seam is spanned only by the <i>alternating</i> chain — the pure-visual walk stops at the popup surface
    /// root, and the pure-logical walk breaks inside template chrome whose parts carry no logical link of
    /// their own. Use the specialized forms when a single-tree relation is the actual question.
    /// </remarks>
    public bool IsAncestorOf(UIElement? element) =>
        element != null && (element == this || IsAncestorOf(element.VisualParent ?? element.UIParent));

    /// <summary>
    /// Evaluates whether this element is a logical ancestor of <paramref name="element"/>.
    /// </summary>
    /// <param name="element">The element to evaluate.</param>
    /// <returns><c>true</c> if <paramref name="element"/> is a logical descendant of this element.</returns>
    /// <remarks>For purposes of this method, an element is considered its own ancestor.</remarks>
    public bool IsLogicalAncestorOf(UIElement? element) => 
        element != null && (element == this || IsLogicalAncestorOf(element.UIParent));

    /// <summary>
    /// Evaluates whether this element is a visual ancestor of <paramref name="element"/>.
    /// </summary>
    /// <param name="element">The element to evaluate.</param>
    /// <returns><c>true</c> if <paramref name="element"/> is a visual descendant of this element.</returns>
    /// <remarks>For purposes of this method, an element is considered its own ancestor.</remarks>
    public bool IsVisualAncestorOf(UIElement? element) => 
        element != null && (element == this || IsVisualAncestorOf(element._visualParent));

    /// <summary>
    /// Adopts <paramref name="child"/> into the visual tree at <paramref name="index"/> (−1 =
    /// append; index = paint order). Sets the child's visual parent, rewires its inheritance parent
    /// (<c>LogicalParent ?? VisualParent</c>), and — when this element is attached — runs the
    /// pre-order attach walk over the child subtree. Does <b>not</b> invalidate this element's
    /// measure: owner-wired collections (<see cref="UIElementCollection"/>) and content models own
    /// that contract.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="child"/> already has a visual parent.</exception>
    protected void AddVisualChild(UIElement child, int index = -1)
    {
        ArgumentNullException.ThrowIfNull(child);
        VerifyAccess();
        RenderPassGuard.ThrowIfActive();

        if (child._visualParent is not null)
        {
            throw new InvalidOperationException(
                $"Cannot add '{child.GetType().Name}' as a visual child of '{GetType().Name}': it already has a " +
                $"visual parent ('{child._visualParent.GetType().Name}'). Remove it from its current parent first.");
        }

        for (var ancestor = this; ancestor is not null; ancestor = ancestor._visualParent)
        {
            if (ReferenceEquals(ancestor, child))
                throw new InvalidOperationException("Adding this visual child would create a cycle in the visual tree.");
        }

        var children = _visualChildren ??= [];
        if (index < 0 || index >= children.Count)
            children.Add(child);
        else
            children.Insert(index, child);

        InvalidateZOrder();

        child._visualParent = this;
        child.SetInheritanceParent(child.UIParent ?? this); // UIParent ?? VisualParent — matches StylingParent + bridges popups
        child.OnVisualParentChanged(null, this);

        if (IsAttachedToTree)
        {
            AttachSubtree(child, _visualRoot!, Depth + 1);
            // New content paints into existing zones (or mints new boundary layers): the rebuild
            // walk assigns zone pointers and marks the affected zones dirty next pass.
            GetRenderTree()?.MarkLayersDirty();
        }
        else
        {
            child.UpdateEffectiveEnabled();
        }
    }

    /// <summary>
    /// Removes <paramref name="child"/> from the visual tree: runs the bottom-up detach walk (when
    /// attached), clears the visual parent, and re-points the child's inheritance parent at its
    /// logical parent (or null).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="child"/> is not a visual child of this element.</exception>
    protected void RemoveVisualChild(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        VerifyAccess();
        RenderPassGuard.ThrowIfActive();

        if (!ReferenceEquals(child._visualParent, this) || _visualChildren is null || !_visualChildren.Remove(child))
            throw new InvalidOperationException($"'{child.GetType().Name}' is not a visual child of this '{GetType().Name}'.");

        InvalidateZOrder();
        if (child.IsAttachedToTree)
        {
            // The vacated pixels must repaint: a non-boundary subtree marks its owning zone dirty;
            // a boundary subtree drops its layer (count change → the compositor's full recomposite).
            if (child.GetRenderTree() is { } tree)
            {
                if (!ReferenceEquals(child.ZoneRoot, child))
                    child.ZoneRoot?.Zone?.MarkRasterDirty();
                tree.MarkLayersDirty();
            }

            DetachSubtree(child);
        }

        child._visualParent = null;
        child.SetInheritanceParent(child.UIParent); // UIParent (?? null) — LogicalParent/TemplatedParent/PlacementTarget bridge
        child.OnVisualParentChanged(this, null);
        child.UpdateEffectiveEnabled();
    }

    /// <summary>
    /// Visual adoption <b>without</b> logical reparenting (design-doc punch 43): <c>ItemsPresenter</c>
    /// hosts generated containers visually while they remain logical children of the
    /// <c>ItemsControl</c>. The delegation is correct by construction — <see cref="AddVisualChild"/>
    /// never touches <see cref="LogicalParent"/> (logical adoption is a separate
    /// <see cref="AddLogicalChild"/> call; only <see cref="AdoptChild"/> composes both), and its
    /// inheritance rewire is <c>LogicalParent ?? this</c>, so an item already logically parented
    /// elsewhere keeps that parent as its inheritance parent. Pinned by
    /// <c>AddVisualChildOnly_PreservesExistingLogicalParent_AndInheritance</c>.
    /// </summary>
    internal void AddVisualChildOnly(UIElement child, int index = -1) => AddVisualChild(child, index);

    /// <summary>Visual disownment without logical reparenting — the inverse of <see cref="AddVisualChildOnly"/>.</summary>
    internal void RemoveVisualChildOnly(UIElement child) => RemoveVisualChild(child);

    /// <summary>
    /// When true, this element's owner-wired <see cref="UIElementCollection"/> adopts children <b>visually only</b>
    /// (logical parentage is owned elsewhere) — the WPF <c>Panel.IsItemsHost</c> contract: an items-host panel's
    /// children are generated containers that stay logical children of the <c>ItemsControl</c> (punch 43).
    /// </summary>
    internal bool AdoptsChildrenVisualOnly { get; set; }

    /// <summary>
    /// Full adoption for owner-wired collections (<see cref="UIElementCollection"/>): logical
    /// parent first (so the attach walk observes the final inheritance topology), then visual
    /// adoption at <paramref name="index"/>.
    /// </summary>
    internal void AdoptChild(UIElement child, int index)
    {
        AddLogicalChild(child);
        AddVisualChild(child, index);
    }

    /// <summary>Full disownment for owner-wired collections: visual detach (bottom-up walk) first, then logical.</summary>
    internal void DisownChild(UIElement child)
    {
        RemoveVisualChild(child);
        RemoveLogicalChild(child);
    }

    /// <summary>Reorders an existing visual child to <paramref name="newIndex"/> without detaching it (collection <c>Move</c> support).</summary>
    internal void MoveVisualChild(UIElement child, int newIndex)
    {
        VerifyAccess();
        RenderPassGuard.ThrowIfActive();

        if (_visualChildren is not { } children || !children.Remove(child))
            throw new InvalidOperationException($"'{child.GetType().Name}' is not a visual child of this '{GetType().Name}'.");

        children.Insert(Math.Clamp(newIndex, 0, children.Count), child);
        InvalidateZOrder();

        if (IsAttachedToTree && GetRenderTree() is { } tree)
        {
            // Reorder changes the paint order within this element's zone — and may reorder sibling
            // boundary layers in the flat list.
            ZoneRoot?.Zone?.MarkRasterDirty();
            tree.MarkLayersDirty();
        }
    }

    /// <summary>
    /// Adopts <paramref name="child"/> as a logical child (content models: <c>ContentControl.Content</c>,
    /// panel children via <see cref="UIElementCollection"/>). Re-points the child's inheritance
    /// parent at this element and raises <see cref="AttachedToLogicalTree"/> on the child.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="child"/> already has a logical parent.</exception>
    protected void AddLogicalChild(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        VerifyAccess();

        if (child._logicalParent is not null)
        {
            throw new InvalidOperationException(
                $"Cannot add '{child.GetType().Name}' as a logical child of '{GetType().Name}': it already has a " +
                $"logical parent ('{child._logicalParent.GetType().Name}').");
        }

        (_logicalChildren ??= []).Add(child);
        child._logicalParent = this;
        child.SetInheritanceParent(this);
        child.UpdateEffectiveEnabled();
        child.AttachedToLogicalTree?.Invoke(child, new LogicalTreeAttachmentEventArgs(child, oldParent: null, newParent: this));
    }

    /// <summary>
    /// Removes <paramref name="child"/> from this element's logical children, re-points its
    /// inheritance parent at its visual parent (or null), and raises
    /// <see cref="DetachedFromLogicalTree"/> on the child.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="child"/> is not a logical child of this element.</exception>
    protected void RemoveLogicalChild(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        VerifyAccess();

        if (!ReferenceEquals(child._logicalParent, this) || _logicalChildren is null || !_logicalChildren.Remove(child))
            throw new InvalidOperationException($"'{child.GetType().Name}' is not a logical child of this '{GetType().Name}'.");

        child._logicalParent = null;
        child.SetInheritanceParent(child.UIParent ?? child._visualParent); // bridge (TemplatedParent/PlacementTarget) before VisualParent
        child.UpdateEffectiveEnabled();
        child.DetachedFromLogicalTree?.Invoke(child, new LogicalTreeAttachmentEventArgs(child, oldParent: this, newParent: null));
    }

    /// <summary>
    /// Stamps the template-barrier datum (invariant 5) — the S8 seam, called during template
    /// expansion <em>before</em> the part attaches to the tree. There is no template engine at P1.
    /// </summary>
    /// <exception cref="InvalidOperationException">The element is already attached to a tree.</exception>
    protected internal void SetTemplatedParent(UIElement? value)
    {
        VerifyAccess();

        var oldValue = _templatedParent;

        if (IsAttachedToTree && !ReferenceEquals(oldValue, value))
        {
            throw new InvalidOperationException(
                "TemplatedParent can only be stamped while the element is detached — template parts are " +
                "marked during template expansion, before they attach (S8 contract).");
        }

        if (ReferenceEquals(oldValue, value))
            return;

        _templatedParent = value;

        var args = new TemplatedParentChangedEventArgs(this, oldValue, value);
        OnTemplatedParentChanged(args);
        TemplatedParentChanged?.Invoke(this, args);
    }

    /// <summary>
    /// Raised when <see cref="TemplatedParent"/> is stamped (the S2 seam — a <c>TemplateBinding</c> /
    /// <c>RelativeSource.TemplatedParent</c> binding installed before the stamp re-resolves here).
    /// </summary>
    public event EventHandler<TemplatedParentChangedEventArgs>? TemplatedParentChanged;

    /// <summary>Raised when the element gains a logical parent (the S2 seam — DataContext/namescope wiring rides this).</summary>
    public event EventHandler<LogicalTreeAttachmentEventArgs>? AttachedToLogicalTree;

    /// <summary>Raised when the element loses its logical parent (the S2 seam).</summary>
    public event EventHandler<LogicalTreeAttachmentEventArgs>? DetachedFromLogicalTree;

    /// <summary>Raised when the element gains a visual parent (the S2 seam — DataContext/namescope wiring rides this).</summary>
    public event EventHandler<TreeAttachmentEventArgs>? AttachedToTree;

    /// <summary>Raised when the element loses its visual parent (the S2 seam).</summary>
    public event EventHandler<TreeAttachmentEventArgs>? DetachedFromTree;

    // ───────────────────────────── lifecycle walks ─────────────────────────────

    private void OnAttachedToTreeCore(in TreeAttachmentEventArgs e)
    {
        OnAttachedToTree(e);
        AttachedToTree?.Invoke(this, e);
    }

    /// <summary>
    /// Called when this element's <see cref="TemplatedParent"/> is assigned or cleared.
    /// Invoked just prior to the <see cref="TemplatedParentChanged"/> event being raised.
    /// </summary>
    protected virtual void OnTemplatedParentChanged(in TemplatedParentChangedEventArgs e)
    {
    }

    /// <summary>
    /// Called when this element becomes part of an attached visual tree (pre-order, parent-first —
    /// the element's ancestors have already attached). Layout is invalid at this point; styling
    /// attach (Fork B, P3) is ordered before the first measure so styles affect same-frame layout.
    /// </summary>
    protected virtual void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
    }
    
    private void OnDetachedFromTreeCore(in TreeAttachmentEventArgs e)
    {
        OnDetachedFromTree(e);
        DetachedFromTree?.Invoke(this, e);
    }


    /// <summary>
    /// Called when this element leaves the attached visual tree (bottom-up — descendants detach
    /// first, per Fork B's batch-retraction contract).
    /// </summary>
    protected virtual void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
    }

    /// <summary>Called when the element's <see cref="VisualParent"/> changes (both attach and detach directions).</summary>
    protected virtual void OnVisualParentChanged(UIElement? oldParent, UIElement? newParent)
    {
    }

    /// <summary>Attaches this element as the visual root of a single-root tree (the P1 stand-in — see <see cref="LayoutManager"/>).</summary>
    internal void AttachAsRoot(LayoutManager manager)
    {
        if (_visualParent is not null)
            throw new ArgumentException("A root element must not have a visual parent.", nameof(manager));
        if (IsAttachedToTree)
            throw new InvalidOperationException("This element is already attached to a tree.");

        _layoutManager = manager;
        AttachSubtree(this, this, depth: 0);
    }

    /// <summary>Detaches this root element (test/teardown path; reverses <see cref="AttachAsRoot"/>).</summary>
    internal void DetachRoot()
    {
        if (_layoutManager is null)
            throw new InvalidOperationException("This element is not an attached root.");

        RenderTreeHost?.Detach(); // returns all zone scenes to the pool while the tree is still attached
        DetachSubtree(this);
        _layoutManager = null;
    }

    /// <summary>The pre-order parent-first attach walk (design doc §5.1 lifecycle).</summary>
    private static void AttachSubtree(UIElement element, UIElement root, int depth)
    {
        element._visualRoot = root;
        element.Depth = depth;

        // Mark layout invalid (no enqueue: the parent that adopted this subtree re-measures it
        // top-down; collection mutation invalidates the owner, which is the queue entry).
        element.IsMeasureValid = false;
        element.IsArrangeValid = false;
        element._hasArrangedVisible = false; // §9.5: a re-attach re-parks transitions until the next real arrange

        element.UpdateEffectiveEnabled();
        element.OnAttachedToTreeCore(new TreeAttachmentEventArgs(root, element._visualParent));

        // S7 resource subscription re-resolution (CD16): force one re-resolve per producer, before
        // styling arms (a control-theme/setter resource read must see fresh values).
        element.OnResourcesAttached();

        // Fork B's styling attach (B19): pre-order with the walk, so every element arms before its
        // first measure — styles affect layout in the same frame.
        element.OnStylingAttached();

        // S5 transitions (§9.5): arm any attached TransitionCollection now (parked) so the initial style
        // application that follows in this same synchronous activation is swallowed, not transitioned.
        TransitionManager.OnElementAttached(element);

        if (element._visualChildren is { } children)
        {
            for (var i = 0; i < children.Count; i++)
                AttachSubtree(children[i], root, depth + 1);
        }
    }

    /// <summary>The bottom-up detach walk (design doc §5.1 lifecycle): descendants first, then self.</summary>
    private static void DetachSubtree(UIElement element) => DetachSubtree(element, element);

    /// <param name="element">The element being detached at this step of the walk.</param>
    /// <param name="detachingRoot">
    /// The subtree root the walk started from — threaded to the input services so focus repair
    /// skips every doomed element (matrix ND30: the bottom-up walk leaves ancestors momentarily
    /// attached-looking; they must never become repair targets).
    /// </param>
    private static void DetachSubtree(UIElement element, UIElement detachingRoot)
    {
        if (element._visualChildren is { } children)
        {
            for (var i = 0; i < children.Count; i++)
                DetachSubtree(children[i], detachingRoot);
        }

        // Release render state before the detach notification (doc §5.1 ordering): boundary scene
        // back to the pool, zone pointer cleared, sticky promotion cleared — a reattach rebuilds all
        // single-shot state.
        element.Zone?.ReleaseScene();
        element.Zone = null;
        element.ZoneRoot = null;
        element.IsPromotedBoundary = false;

        var root = element._visualRoot!;
        element._visualRoot = null;
        element.OnDetachedFromTreeCore(new TreeAttachmentEventArgs(root, element._visualParent));
        element.Depth = 0;

        // S7 resource subscription teardown (design doc §11.6): unregister this element's producer
        // nodes from the (about-to-be-orphaned) root's registry — O(own subscriptions).
        element.OnResourcesDetached();

        // Fork B's styling detach (B19): bottom-up with the walk — batched cookie retraction (the
        // store promotes per property; invariant 4) and the per-element state drop (SD15). Runs
        // while the element already reads as detached, before the enabled recompute can flip
        // interaction bits against dropped state.
        element.OnStylingDetached();

        // S5 detach-stop (design doc §9.6): retract + evict every animation/storyboard targeting this element;
        // store-owned retraction, no Completed. Idempotent against Fork B's retraction on the same detach.
        AnimationScheduler.CurrentOrNull?.OnElementDetached(element);

        // S5 transitions (§9.5): drop the winning-base subscriptions + re-park (a re-attach must not transition
        // its initial application). The in-flight transition's animation instance was retracted just above.
        TransitionManager.OnElementDetached(element);

        element.UpdateEffectiveEnabled();

        // S3 detach fan-in (doc §7.10): capture force-release, focus hygiene (hover/pressed
        // truncation join at their stages). After the detach notification so services observe
        // the element already detached.
        NotifyInputServicesDetached(element, detachingRoot);
    }

    private bool _tornDown; // set once by TearDown() — permanent-detach is one-shot (idempotent + re-entrancy safe)

    // ───────────────────────────── permanent detach (teardown sweep) ─────────────────────────────

    /// <summary>
    /// The permanent-detach teardown sweep (design doc §5.1 CONTRACT): for app-discarded subtrees
    /// (and window close, via S4 at P7), runs bottom-up per element:
    /// <c>ValueStore.TearDown()</c> → <c>BindingOperations.TearDown(element)</c>. Pooled scenes and
    /// style cookies already release on ordinary detach; this sweep is what releases S2's strong
    /// INPC subscriptions to long-lived viewmodels — a dropped subtree is only leak-free if it runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both legs run: <c>ValueStore.TearDown()</c> evicts every store entry (firing
    /// <c>OnEvicted</c> per entry), then <c>BindingOperations.TearDown(element)</c> disposes the
    /// remaining registry-tracked binding expressions (DirectProperty targets, watches anchored here)
    /// — the S2 sweep half that closed the recorded P1 gap (binding-matrix B108/B166). Detaching the
    /// subtree from its parent is the caller's responsibility.
    /// </para>
    /// <para>
    /// Teardown is deliberately <b>not</b> folded into <c>Children.Remove</c> /
    /// <see cref="RemoveVisualChild"/>: elements are reusable (detach + reattach rebuilds all
    /// single-shot state — doc §5.1), so an automatic sweep on remove would break legal
    /// remove-then-reattach flows. Discarding a subtree is a caller-visible decision; forgetting
    /// this call pins viewmodel subscriptions alive (the DEBUG <c>BindingLeakTracker</c> flags it).
    /// </para>
    /// </remarks>
    public void TearDown()
    {
        VerifyAccess();

        // Idempotence + re-entrancy guard: teardown is permanent and one-shot. The flag is set BEFORE
        // the child sweep so a child's OnTearDown that tears down a subtree referencing THIS element
        // (the canonical case: a control owning a field-held Popup whose Child == the control — see the
        // Lifecycle & Teardown authoring guide) short-circuits the re-entrant call instead of looping.
        // Also absorbs a double teardown (window-close sweep + a defensive owner sweep of the same tree).
        if (_tornDown)
            return;
        _tornDown = true;

        if (_visualChildren is { } children)
        {
            for (var i = 0; i < children.Count; i++)
                children[i].TearDown();
        }

        if (_logicalChildren is { } logical)
        {
            for (var i = 0; i < logical.Count; i++)
            {
                // Logical-only children (visual children were swept above).
                if (!ReferenceEquals(logical[i]._visualParent, this))
                    logical[i].TearDown();
            }
        }

        OnTearDown(); // subclass-owned release of non-binding external subscriptions (e.g. ItemsControl's source view)
        TearDownValueStore();
        Data.BindingOperations.TearDown(this); // the second sweep leg (doc §5.1 / §6.5)

        // InputBindings are non-UIElement UIObjects anchored on this element (a Command="{Binding}" anchors on
        // its owner's DataContext, BD13); they are neither visual nor logical children, so the sweep above never
        // reaches them. Tear down each gesture's bindings here — disposing the owner-side DataContext observer and
        // the InheritanceParentChanged subscription the binding installed.
        if (_inputBindings is { } inputBindings)
        {
            for (var i = 0; i < inputBindings.Count; i++)
                Data.BindingOperations.TearDown(inputBindings[i]);
        }

        // Registered teardown participants — the InputBindings case generalized (ITearDownParticipant): other
        // non-child UIObject graphs anchored on this element (attached behaviors/triggers/actions) release
        // their external subscriptions and item bindings here. Snapshot: a participant may unregister
        // during its own callback.
        if (_tearDownParticipants is { Count: > 0 })
        {
            var participants = _tearDownParticipants.ToArray();
            for (var i = 0; i < participants.Length; i++)
                participants[i].OnTearDown(this);
        }
    }

    private List<ITearDownParticipant>? _tearDownParticipants;

    /// <summary>Registers a <see cref="ITearDownParticipant"/> to run during this element's <see cref="TearDown"/>.
    /// Idempotent per instance; unregister when the association ends.</summary>
    public void RegisterTearDownParticipant(ITearDownParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        _tearDownParticipants ??= new List<ITearDownParticipant>();
        if (!_tearDownParticipants.Contains(participant))
            _tearDownParticipants.Add(participant);
    }

    /// <summary>Removes a previously registered teardown participant (a miss is a no-op).</summary>
    public void UnregisterTearDownParticipant(ITearDownParticipant participant)
        => _tearDownParticipants?.Remove(participant);

    /// <summary>
    /// Releases subclass-owned resources during <see cref="TearDown"/> — raw external subscriptions a control holds
    /// across attach/detach that bindings don't cover (the canonical case: <c>ItemsControl</c> unhooking its bound
    /// <c>ItemsSource</c>'s collection-changed handler so a live viewmodel collection no longer pins the control).
    /// Runs after the child sweep and before the value-store/binding sweep. <b>Not</b> called on transient detach —
    /// detach + re-attach must rebuild this state (doc §5.1).
    /// </summary>
    protected virtual void OnTearDown()
    {
        // A UIProperty is not ownership-enforced — the ContextMenu.Menu ATTACHED property can be set on
        // ANY UIElement, not just a Control, and its value roots its OWN light-dismiss surface (an
        // off-tree subtree the window-close sweep never reaches). UIElement owns this check so every
        // element — Control or not — releases an attached context menu with itself. Idempotent, so a
        // menu shared across owners disposes once (give a pooled/shared menu an explicit lifetime).
        // See the Lifecycle & Teardown authoring guide.
        Controls.ContextMenu.GetMenu(this)?.TearDown();
    }

    // ───────────────────────────── effective IsEnabled (S1-owned) ─────────────────────────────

    /// <summary>
    /// The effective enabled state: <c>IsEnabled &amp;&amp; IsEnabledCore &amp;&amp; parentEffective</c>,
    /// computed over the inheritance wiring (<c>LogicalParent ?? VisualParent</c>). A change
    /// re-evaluates descendants; the styling push (<c>InteractionState.Disabled</c>, S3's seam)
    /// rides <see cref="OnIsEffectivelyEnabledChanged"/> when input lands (P2/P3).
    /// </summary>
    public bool IsEffectivelyEnabled => _effectivelyEnabled;

    /// <summary>
    /// The control-author enabled gate (design doc §5.1): a control whose commands/state make it
    /// non-interactive overrides this and calls <see cref="InvalidateIsEnabledCore"/> when the
    /// input changes.
    /// </summary>
    protected virtual bool IsEnabledCore => true;

    /// <summary>Re-evaluates <see cref="IsEffectivelyEnabled"/> after an <see cref="IsEnabledCore"/> input changed.</summary>
    protected void InvalidateIsEnabledCore()
    {
        VerifyAccess();
        UpdateEffectiveEnabled();
        RepairFocusAfterStateInvalidation(); // ND28: a command-disabled control must not retain key focus
    }

    /// <summary>Called when <see cref="IsEffectivelyEnabled"/> changes — the S3 interaction-state seam (P2).</summary>
    protected virtual void OnIsEffectivelyEnabledChanged(bool isEffectivelyEnabled)
    {
    }

    private void UpdateEffectiveEnabled()
    {
        var parent = _logicalParent ?? _visualParent;
        var value = IsEnabled && IsEnabledCore && (parent?._effectivelyEnabled ?? true);
        if (value == _effectivelyEnabled)
            return;

        _effectivelyEnabled = value;
        SetInteractionStateInternal(InteractionState.Disabled, !value); // the :disabled producer (doc §13.2; N146)
        OnIsEffectivelyEnabledChanged(value);

        if (_visualChildren is { } children)
        {
            for (var i = 0; i < children.Count; i++)
                children[i].UpdateEffectiveEnabled();
        }

        if (_logicalChildren is { } logical)
        {
            for (var i = 0; i < logical.Count; i++)
            {
                if (!ReferenceEquals(logical[i]._visualParent, this))
                    logical[i].UpdateEffectiveEnabled();
            }
        }
    }

    // ───────────────────────────── visibility / hit-test surface ─────────────────────────────

    /// <summary>
    /// Whether this element and every visual ancestor is <see cref="Visibility.Visible"/> — a live
    /// O(depth) walk (never stale; consumed by S3's hit/focus checks).
    /// </summary>
    public bool IsEffectivelyVisible
    {
        get
        {
            for (var element = this; element is not null; element = element._visualParent)
            {
                if (element.Visibility != Visibility.Visible)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// The shaped-control hit-test escape hatch (design doc §5.8): called in element-local
    /// coordinates after the bounds check already passed. The default accepts the whole rect.
    /// </summary>
    protected virtual bool HitTestCore(int column, int row) => true;

    // ───────────────────────────── scroll-host seam (T4 — ScrollContentPresenter) ─────────────────────────────

    /// <summary>
    /// The horizontal scroll this element applies to its <b>children</b>: a child at
    /// <c>Bounds.Column</c> in content coordinates sits at <c>Bounds.Column − this</c> in the
    /// element's own frame. 0 everywhere except scroll hosts (<c>ScrollContentPresenter</c>
    /// overrides — doc §5.7). Folded by the boundary walk, hit testing, and
    /// <see cref="TranslateToWindow(int, int)"/>/<see cref="TranslateFromWindow(int, int)"/>.
    /// </summary>
    internal virtual int ChildScrollOffsetColumn => 0;

    /// <summary>The vertical sibling of <see cref="ChildScrollOffsetColumn"/>.</summary>
    internal virtual int ChildScrollOffsetRow => 0;

    // ───────────────────────────── coordinate translation ─────────────────────────────

    /// <summary>
    /// Translates element-local coordinates to window (visual-root) coordinates — the inverse of
    /// <see cref="TranslateFromWindow(int, int)"/>.
    /// </summary>
    public (int Column, int Row) TranslateToWindow(int column, int row)
    {
        for (var element = this; element is not null; element = element._visualParent)
        {
            column += element.Bounds.Column + element.RenderOffsetColumn;
            row += element.Bounds.Row + element.RenderOffsetRow;

            if (element._visualParent is { } parent)
            {
                column -= parent.ChildScrollOffsetColumn;
                row -= parent.ChildScrollOffsetRow;
            }
        }

        return (column, row);
    }

    /// <summary>
    /// Translates element-local coordinates to screen coordinates — the inverse of
    /// <see cref="TranslateFromScreen(int, int)"/>.
    /// </summary>
    public (int Column, int Row) TranslateToScreen(int column, int row)
    {
        (column, row) = TranslateToWindow(column, row);

        if (_visualRoot is {} root)
        {
            var surface = UIApplication.Current?.WindowManager?.SurfaceForElement(root);
            if (surface is not null)
                (column, row) = (column + surface.Left, row + surface.Top);
        }

        return (column, row);
    }

    /// <summary>
    /// Translates window (visual-root) coordinates to element-local coordinates —
    /// the inverse of <see cref="TranslateToWindow(int, int)"/>.
    /// </summary>
    public (int Column, int Row) TranslateFromWindow(int column, int row)
    {
        for (var element = this; element is not null; element = element._visualParent)
        {
            column -= element.Bounds.Column + element.RenderOffsetColumn;
            row -= element.Bounds.Row + element.RenderOffsetRow;

            if (element._visualParent is { } parent)
            {
                column += parent.ChildScrollOffsetColumn;
                row += parent.ChildScrollOffsetRow;
            }
        }

        return (column, row);
    }

    /// <summary>
    /// Translates screen coordinates to element-local coordinates — the inverse of
    /// <see cref="TranslateToScreen(int, int)"/>.
    /// </summary>
    public (int Column, int Row) TranslateFromScreen(int column, int row)
    {
        (column, row) = TranslateFromWindow(column, row);

        if (_visualRoot is {} root)
        {
            if (root is Window { HostSurface: {} windowHost})
                (column, row) = (column - windowHost.Left, row - windowHost.Top);
            else if (root is Popup { PopupSurface: {} popupHost})
                (column, row) = (column - popupHost.Left, row - popupHost.Top);
        }

        return (column, row);
    }

    /// <summary>
    /// Translates a rectangle from screen coordinates to element-local coordinates —
    /// the inverse of <see cref="TranslateToScreen(Rect)"/>.
    /// </summary>
    public Rect TranslateFromScreen(Rect rect)
    {
        var (column, row) = TranslateFromScreen(rect.Column, rect.Row);
        return new Rect(column, row, rect.Size);
    }

    /// <summary>
    /// Translates a rectangle from element-local coordinates to screen coordinates —
    /// the inverse of <see cref="TranslateFromScreen(Rect)"/>.
    /// </summary>
    public Rect TranslateToScreen(Rect rect)
    {
        var (column, row) = TranslateToScreen(rect.Column, rect.Row);
        return new Rect(column, row, rect.Size);
    }

    /// <summary>
    /// Translates a rectangle from window (visual-root) coordinates to element-local coordinates —
    /// the inverse of <see cref="TranslateToWindow(Rect)"/>.
    /// </summary>
    public Rect TranslateFromWindow(Rect rect)
    {
        var (column, row) = TranslateFromWindow(rect.Column, rect.Row);
        return new Rect(column, row, rect.Size);
    }

    /// <summary>
    /// Translates a rectangle from element-local coordinates to window (visual-root) coordinates —
    /// the inverse of <see cref="TranslateFromWindow(Rect)"/>.
    /// </summary>
    public Rect TranslateToWindow(Rect rect)
    {
        var (column, row) = TranslateToWindow(rect.Column, rect.Row);
        return new Rect(column, row, rect.Size);
    }

    /// <summary>The layout manager owning this element's visual root, or <see langword="null"/> when detached.</summary>
    internal LayoutManager? GetLayoutManager() => _visualRoot?._layoutManager;
}
