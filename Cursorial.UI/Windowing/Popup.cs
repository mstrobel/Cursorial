using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Input;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>The argument to <c>Popup.Closed</c> — why the popup closed (design doc §8.4).</summary>
public sealed class PopupClosedEventArgs(PopupCloseReason reason) : EventArgs
{
    /// <summary>Why the popup closed.</summary>
    public PopupCloseReason Reason { get; } = reason;
}

/// <summary>
/// A light-dismiss popup primitive (design doc §8.4): the <see cref="Popup"/> lives in the host's LOGICAL
/// tree (so its <c>Child</c> inherits DataContext/resources/styles and key/Escape routing flows back to the
/// host), but contributes nothing to the host's visual layout — when <see cref="IsOpen"/>, its
/// <see cref="Child"/> roots a separate <see cref="TopLevelSurface"/> the window manager places in the popup
/// band above all windows. The S8 menu/combobox/tooltip controls build on this.
/// </summary>
/// <remarks>
/// <b>P7-W4 scope:</b> open/close, placement (relative to the target, clamped to the screen), light dismiss,
/// Escape (the cross-tree route is free — the route builder hops the logical parent at surface roots, and the
/// Child is the Popup's logical child), and the two-way <see cref="IsOpen"/> write-back. Content-swap scene
/// reuse, hit-test-transparent tooltip surfaces, anchor tracking, and chain semantics are W4-b refinements.
/// </remarks>
[Cursorial.Markup.ContentProperty("Child")]
public class Popup : UIElement
{
    /// <summary>The element shown in the popup surface (swappable while open).</summary>
    public static readonly StyledProperty<UIElement?> ChildProperty =
        UIProperty.Register<Popup, UIElement?>(nameof(Child), changed: OnChildChanged);

    /// <summary>Whether the popup is open (two-way by default; every close reason writes back via <c>SetCurrentValue</c>).</summary>
    public static readonly StyledProperty<bool> IsOpenProperty =
        UIProperty.Register<Popup, bool>(nameof(IsOpen), changed: OnIsOpenChanged);

    /// <summary>Where the surface places relative to the target (default <see cref="PlacementMode.Bottom"/>).</summary>
    public static readonly StyledProperty<PlacementMode> PlacementProperty =
        UIProperty.Register<Popup, PlacementMode>(nameof(Placement));

    /// <summary>The placement anchor (default: the logical parent).</summary>
    public static readonly StyledProperty<UIElement?> PlacementTargetProperty =
        UIProperty.Register<Popup, UIElement?>(nameof(PlacementTarget), changed: OnPlacementTargetChanged);

    /// <summary>A target-local anchor rect override (W4-b; reserved).</summary>
    public static readonly StyledProperty<Rect?> PlacementRectProperty =
        UIProperty.Register<Popup, Rect?>(nameof(PlacementRect));

    /// <summary>Extra horizontal offset applied after placement.</summary>
    public static readonly StyledProperty<int> HorizontalOffsetProperty =
        UIProperty.Register<Popup, int>(nameof(HorizontalOffset));

    /// <summary>Extra vertical offset applied after placement.</summary>
    public static readonly StyledProperty<int> VerticalOffsetProperty =
        UIProperty.Register<Popup, int>(nameof(VerticalOffset));

    /// <summary>When true the popup is NOT light-dismissed by an outside press (default false).</summary>
    public static readonly StyledProperty<bool> StaysOpenProperty =
        UIProperty.Register<Popup, bool>(nameof(StaysOpen));

    /// <summary>
    /// When true, a light-dismiss press that lands on the <see cref="PlacementTarget"/> (the anchor) does NOT
    /// dismiss the popup — the anchor owns the toggle (default false). A drop-toggle control (ComboBox, DatePicker)
    /// sets this so clicking its face while the drop-down is open routes to the face's own toggle (which closes it),
    /// instead of the light-dismiss closing it first and the face-click then reopening it (the dismiss/toggle race).
    /// </summary>
    public static readonly StyledProperty<bool> KeepOpenOnAnchorPressProperty =
        UIProperty.Register<Popup, bool>(nameof(KeepOpenOnAnchorPress));

    /// <summary>Whether Escape closes the popup (default true).</summary>
    public static readonly StyledProperty<bool> CloseOnEscapeProperty =
        UIProperty.Register<Popup, bool>(nameof(CloseOnEscape), defaultValue: true);

    /// <summary>When true the popup's surface is transparent to hit testing — the pointer falls through to the
    /// content beneath it and the popup never appears in the surface scan (default false). The ToolTip case
    /// (doc §12.7): a tooltip floats over content but must not steal hover/clicks.</summary>
    public static readonly StyledProperty<bool> IsHitTestTransparentProperty =
        UIProperty.Register<Popup, bool>(nameof(IsHitTestTransparent));

    /// <summary>The popup's drop shadow (default <see cref="WindowShadow.Default"/>).</summary>
    public static readonly StyledProperty<WindowShadow> ShadowProperty =
        Window.ShadowProperty.AddOwner<Popup>();

    private bool _open;
    private WindowManager? _manager;
    private UIElement? _restoreFocusTo; // the element focused when this popup opened — restored on close (W4-b)

    static Popup()
    {
        // The Popup's role in the HOST tree is purely structural: it is the logical parent of Child and the
        // placement anchor, and it contributes nothing visual (MeasureOverride => 0×0). But a parent panel still
        // ARRANGES it (a Stretch child fills its cell), giving the closed popup full Bounds with no rendered
        // content — an invisible hit-target layered over whatever sits beneath it (the ComboBox/DatePicker face),
        // stealing hover/clicks. The popup's REAL content is hit-tested on its own TopLevelSurface when open, so
        // the host-tree element must never be a hit leaf. (It has no visual children here — Child is logical-only —
        // so gating the leaf gates the whole element.)
        IsHitTestVisibleProperty.OverrideMetadata<Popup>(new(DefaultValue: false, Coerce: (_, _) => false));

        AddGlobalEffects(PropertyEffects.BindsTwoWayByDefault, IsOpenProperty);
        // ShadowProperty.OverrideDefaultValue<Popup>(WindowShadow.None);
    }

    /// <summary>Creates a popup.</summary>
    public Popup() {}

    /// <inheritdoc cref="ChildProperty"/>
    public UIElement? Child { get => GetValue(ChildProperty); set => SetValue(ChildProperty, value); }

    /// <inheritdoc cref="IsOpenProperty"/>
    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    /// <inheritdoc cref="PlacementProperty"/>
    public PlacementMode Placement { get => GetValue(PlacementProperty); set => SetValue(PlacementProperty, value); }

    /// <inheritdoc cref="PlacementTargetProperty"/>
    public UIElement? PlacementTarget { get => GetValue(PlacementTargetProperty); set => SetValue(PlacementTargetProperty, value); }

    /// <inheritdoc cref="PlacementRectProperty"/>
    public Rect? PlacementRect { get => GetValue(PlacementRectProperty); set => SetValue(PlacementRectProperty, value); }

    /// <inheritdoc cref="HorizontalOffsetProperty"/>
    public int HorizontalOffset { get => GetValue(HorizontalOffsetProperty); set => SetValue(HorizontalOffsetProperty, value); }

    /// <inheritdoc cref="VerticalOffsetProperty"/>
    public int VerticalOffset { get => GetValue(VerticalOffsetProperty); set => SetValue(VerticalOffsetProperty, value); }

    /// <inheritdoc cref="StaysOpenProperty"/>
    public bool StaysOpen { get => GetValue(StaysOpenProperty); set => SetValue(StaysOpenProperty, value); }

    /// <inheritdoc cref="KeepOpenOnAnchorPressProperty"/>
    public bool KeepOpenOnAnchorPress { get => GetValue(KeepOpenOnAnchorPressProperty); set => SetValue(KeepOpenOnAnchorPressProperty, value); }

    /// <inheritdoc cref="CloseOnEscapeProperty"/>
    public bool CloseOnEscape { get => GetValue(CloseOnEscapeProperty); set => SetValue(CloseOnEscapeProperty, value); }

    /// <inheritdoc cref="IsHitTestTransparentProperty"/>
    public bool IsHitTestTransparent { get => GetValue(IsHitTestTransparentProperty); set => SetValue(IsHitTestTransparentProperty, value); }

    /// <inheritdoc cref="ShadowProperty"/>
    public WindowShadow Shadow { get => GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }

    /// <summary>The surface hosting the open popup's child (WM-owned; null while closed).</summary>
    internal TopLevelSurface? PopupSurface { get; set; }

    /// <summary>The pointer cell captured at the open-time placement of a <see cref="PlacementMode.Pointer"/> popup
    /// (WM-owned; null while closed). A content-growth re-fit re-places against THIS origin, not the live pointer —
    /// a mini toolbar whose icons realize a frame late must grow in place, not follow the mouse.</summary>
    internal (int Column, int Row)? PointerPlacementOrigin { get; set; }

    /// <summary>The effective placement anchor — explicit <see cref="PlacementTarget"/> or the logical parent.</summary>
    internal UIElement? EffectiveTarget => PlacementTarget ?? LogicalParent;

    /// <summary>Raised after the popup opens.</summary>
    public event EventHandler? Opened;

    /// <summary>Raised after the popup closes, with the reason.</summary>
    public event EventHandler<PopupClosedEventArgs>? Closed;

    /// <summary>Opens the popup.</summary>
    public void Open() => SetCurrentValue(IsOpenProperty, true);

    /// <summary>Closes the popup (reason <see cref="PopupCloseReason.Programmatic"/>).</summary>
    public void Close() => SetCurrentValue(IsOpenProperty, false);

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) => default; // logical-only: zero in the host layout

    /// <summary>
    /// The bridge hop (§8.4): when the popup has no logical/templated parent, its owner is the
    /// <see cref="PlacementTarget"/> — so styling, resources, inheritance, and routed events all reach the
    /// owner. The target is honored only while it is attached to a live tree: a detached / never-attached
    /// target is not a valid parent and would otherwise let the parent-chain walks traverse stale topology
    /// (or, if it pointed back into this popup's subtree, cycle — these walks carry no visited-set).
    /// </summary>
    protected internal override UIElement? UIParent
        => base.UIParent ?? (PlacementTarget is { IsAttachedToTree: true } target ? target : null);

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && _open && CloseOnEscape && e.Key == Key.Escape)
        {
            CloseCore(PopupCloseReason.EscapeKey);
            e.Handled = true;
        }
    }

    /// <summary>Closes the popup for <paramref name="reason"/> (the WM's light-dismiss/host-event path + Escape).</summary>
    internal void CloseCore(PopupCloseReason reason)
    {
        if (!_open)
            return;

        // On-close focus restore (W4-b): only when the popup actually held keyboard focus (a menu/context-menu
        // with a focused item) — a hover-only popup or a tooltip never had focus, so its trigger is left alone.
        // Capture the verdict + target BEFORE closing tears the focus chain down.
        var restoreFocusTo = (Child?.IsKeyboardFocusWithin ?? false) ? _restoreFocusTo : null;
        _restoreFocusTo = null;

        UnwatchTarget(); // stop watching the anchor's detach — we're closing anyway
        _open = false;
        _manager?.ClosePopup(this);
        _manager = null;
        SyncOwnerInheritanceBridge(); // drop the owner bridge now that we're closed
        if (IsOpen)
            SetCurrentValue(IsOpenProperty, false); // write-back; _open is already false so this does not re-enter

        // Return focus to the trigger (Esc-in-submenu → parent header, context-menu dismiss → the right-clicked
        // element). Guarded on still-attached so a torn-down target is skipped; nested popups chain their
        // restores so the outermost lands on the original trigger. The !_open guard handles re-entrancy: a Closed
        // observer (or the IsOpen write-back's change handler) that re-opens this popup mid-close must keep the
        // focus it just placed inside the re-opened content, not yank it back to the stale trigger.
        if (restoreFocusTo is { IsAttachedToTree: true } && !_open)
            restoreFocusTo.Focus(FocusNavigationMethod.Restore);

        Closed?.Invoke(this, new PopupClosedEventArgs(reason));
    }

    private void OpenCore()
    {
        if (Child is null)
            return; // nothing to show — defer until a Child is set

        _manager = UIApplication.Current?.WindowManager;
        if (_manager is null)
            return;

        // Capture the focused element BEFORE opening (the trigger) — restored on close if the popup takes focus (W4-b).
        _restoreFocusTo = UIApplication.Current?.FocusManager.FocusedElement;

        _open = true;
        _manager.OpenPopup(this);
        SyncOwnerInheritanceBridge(); // bridge DataContext/inherited props to the owner (standalone case)
        WatchTarget();                // self-close if the anchor leaves the tree while we're open (surface-orphan gap)
        Opened?.Invoke(this, EventArgs.Empty);
    }

    // ── Detach self-close (the framework-wide surface-orphan gap) ────────────────────────────────────
    // The Child roots its OWN popup surface (a separate tree), so when the popup's host context leaves the tree (a page
    // navigation / tab switch / template swap), NOTHING else closes the orphaned surface. TWO complementary hooks close
    // it, because "leaves the tree" has two shapes:
    //   (1) the POPUP ELEMENT itself detaches — the common in-template case (a BarPopupButton/ComboBox/Menu/DatePicker
    //       whose popup part sits deep inside the swapped subtree). Handled by OnDetachedFromTree below: the recursive
    //       visual-detach walk fires on every descendant, so a NESTED popup self-closes (this is the headline scenario).
    //   (2) the popup element SURVIVES but its ANCHOR detaches — a popup rooted at a stable location whose
    //       PlacementTarget is removed independently. Handled by the WatchTarget logical-detach watch. NOTE the
    //       DetachedFromLogicalTree event fires ONLY on the directly-removed node (not recursively), so this leg covers
    //       a directly-removed anchor; a deeply-nested surviving-popup anchor is the residual edge (no visual-detach
    //       EVENT exists to watch on an arbitrary element — only the element's own OnDetachedFromTree virtual).
    // Closing on the visual-detach walk is safe: MenuItem.OnDetachedFromTree closes its submenu the same way, and a WM
    // topology mutation during layout/render defers via the §8.8 deferred-topology queue. Every leg funnels through the
    // idempotent CloseCore, so the two hooks (and the control-level closes) compose without double-retraction.

    private UIElement? _watchedTarget;

    private void WatchTarget()
    {
        var target = EffectiveTarget;
        if (ReferenceEquals(_watchedTarget, target))
            return;

        UnwatchTarget();
        _watchedTarget = target;
        if (target is not null)
            target.DetachedFromLogicalTree += OnTargetDetached;
    }

    private void UnwatchTarget()
    {
        if (_watchedTarget is { } target)
            target.DetachedFromLogicalTree -= OnTargetDetached;
        _watchedTarget = null;
    }

    private void OnTargetDetached(object? sender, LogicalTreeAttachmentEventArgs e) => CloseCore(PopupCloseReason.TargetDetached);

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        // Hook (1): the popup element itself left the tree (its ancestor page/tab/template was swapped) — self-close so
        // the separate Child surface is never orphaned. Idempotent with the control-level closes (SetDropDownOpen,
        // SetSubmenuOpen) and the anchor watch. Close BEFORE base so the WM release runs while our state is intact.
        if (_open)
            CloseCore(PopupCloseReason.TargetDetached);
        base.OnDetachedFromTree(in e);
    }

    private static void OnPlacementTargetChanged(UIObject sender, UIElement? oldValue, UIElement? newValue)
    {
        var popup = (Popup)sender;
        popup.SyncOwnerInheritanceBridge();

        // Re-anchor the detach watch (hook 2) to the new target when the popup is re-anchored WHILE OPEN — else the
        // watch stays armed on the old target (a spurious close if the old one later detaches, and no close if the new
        // one does). WatchTarget re-reads EffectiveTarget and no-ops when it is unchanged.
        if (popup._open)
            popup.WatchTarget();
    }

    /// <summary>
    /// Bridges the property-system inheritance parent (DataContext + inherited properties) to the
    /// <see cref="PlacementTarget"/> owner while the popup is open AND has no tree parent — the placement-
    /// target-only popup/tooltip case <see cref="UIParent"/> exists for; the inheritance graph is bidirectional,
    /// so the owner's later DataContext changes propagate to the popup's <c>Child</c> live. When the popup is in
    /// the logical/visual tree the tree owns its inheritance parent (LogicalParent wins; WPF/Avalonia parity),
    /// so this is a no-op there.
    /// </summary>
    private void SyncOwnerInheritanceBridge()
    {
        if (LogicalParent is not null || VisualParent is not null)
            return; // in-tree: the tree manages the inheritance parent

        SetInheritanceParent(_open && PlacementTarget is { IsAttachedToTree: true } target ? target : null);
    }

    private static void OnIsOpenChanged(UIObject sender, bool oldValue, bool newValue)
    {
        var popup = (Popup)sender;
        if (newValue && !popup._open)
            popup.OpenCore();
        else if (!newValue && popup._open)
            popup.CloseCore(PopupCloseReason.Programmatic);
    }

    private static void OnChildChanged(UIObject sender, UIElement? oldValue, UIElement? newValue)
    {
        var popup = (Popup)sender;

        // The Child is the Popup's LOGICAL child (so it inherits DataContext + key/Escape routing flows back
        // through the logical parent at the surface root); it is the popup surface's visual root, not a visual
        // child of the Popup.
        if (oldValue is { } oldChild && ReferenceEquals(oldChild.LogicalParent, popup))
            popup.RemoveLogicalChild(oldChild);
        if (newValue is { } newChild && newChild.LogicalParent is null)
            popup.AddLogicalChild(newChild);

        // Content swap while open: W4-a re-hosts the new child (scene-reusing swap is W4-b). Clearing the
        // Child while open closes the popup cleanly (write-back + Closed) — there is nothing left to show.
        if (popup._open)
        {
            if (newValue is null)
                popup.CloseCore(PopupCloseReason.Programmatic);
            else if (popup._manager is { } manager)
            {
                manager.ClosePopup(popup);
                manager.OpenPopup(popup);
            }
        }
    }

    /// <summary>
    /// Event raised the first time the content of the popup is fully rendered and ready for interaction.
    /// </summary>
    public event EventHandler<EventArgs>? ContentRendered; 
    
    /// <summary>Raises the <see cref="ContentRendered"/> event.</summary>
    internal void RaiseContentRendered()
    {
        ContentRendered?.Invoke(this, EventArgs.Empty);
    }
}
