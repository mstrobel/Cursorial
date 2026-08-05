# S4 — Windows, Popups, and the Window Manager (Cursorial.UI)

Status: subsystem spec, **v2 (final — post-critique)**. Conforms to `/tmp/cursorial-ui-design/DECISIONS.md` (binding), built against `/tmp/cursorial-ui-maps/{drawing-core,rendering-session,input,design-doc}.md`. Root namespace `Cursorial.UI`; `using CellStyle = Cursorial.Output.Style;` inside framework source per Fork B.

---

## 1. Scope

**Owns**
- `Window` — top-level templated control: title/chrome stance, sizing models (fixed / content-sized / maximized-to-screen), move/resize chrome interactions, Closing/Closed lifecycle, `DialogResult`.
- `WindowManager` — the window list; z-order policy (owner-banded activation order); the whole-screen `SceneCompositor` assembly (layer list + desktop base); activation (`ActiveWindow`, `:active-window` interaction bit, `.obscured` class flips, physical-focus handoff *calls into* S3); screen-resize policy (re-clamp/re-layout, compositor rebuild); shutdown close-all; the **deferred-topology queue** (reentrancy policy for show/close/open during framework passes, §3.11).
- **Modal** windows: `ShowDialogAsync` (await-able result), the modal stack, input scoping (only the topmost modal + its owned closure receive input), owner chains, nested modals.
- **Modeless** windows: `Show` / `Activate` / `Close`, owned-above-owner z banding.
- `Popup` — the light-dismiss primitive (placement vs. a target element/rect with flip/clamp, light-dismiss interception, popup chains, hit-test-transparent tooltip surfaces). The contract S8's Menu/ComboBox/ToolTip/ContextMenu consume.
- Window/popup drop shadows (`DrawDropShadow` into the surface scene) and the **FillOpaque occluding-panel** stance for chrome.
- The `TopLevelSurface` abstraction: the Window↔Scene ownership story, scene sizing (content + shadow margins), scene recreation on resize (with gesture-scoped over-allocation, §3.9), deferred disposal/pooling on close.
- Top-level **mouse gating for uncaptured events**: surface-from-point, modal blocking, light dismiss — consulted by S3 before element hit-testing. Captured events bypass the WM (§3.3, §4).

**Does NOT own**
- Screen `CellBuffer` / `FrameRenderer` / frame-loop lifecycle (S6 — we provide the surface list, `OnLayoutCompleted`, and `CompositeFrame`).
- Focus mechanics, element hit-testing inside a surface, **mouse capture** (S3 owns capture and routes captured events directly — we only get `ObservePointerPosition` for them), key routing, access-key chord handling (S3 — we call its hooks on activation/block/close and consume its capture service for drags).
- Element tree, layout, render-into-scene rasterization (element/layout subsystem + S6's raster pass — we hand them a `Scene` and a content rect).
- Styling engine internals — we only flip `InteractionState.ActiveWindow` and the `obscured` class through the published sinks (DECISIONS Fork B: "modal dimming = window manager sets `obscured` class").
- Menus/tooltip *behavior* (S8 — owns when to open/close; we own the surface, placement, and dismissal mechanics).

---

## 2. Public API sketch

### 2.1 Window

```csharp
namespace Cursorial.UI;

public enum WindowState : byte { Normal = 0, Maximized = 1 }                 // Minimized: deferred (§7)
public enum SizeToContent : byte { Manual = 0, Width = 1, Height = 2, WidthAndHeight = 3 }
public enum WindowStyle : byte { TitleBar = 0, None = 1 }                    // template-selection hint
public enum WindowStartupLocation : byte { Manual = 0, CenterScreen = 1, CenterOwner = 2 }
public enum WindowCloseReason : byte { Programmatic, ChromeAction, OwnerClosed, ManagerShutdown }

/// <summary>Shadow stance for a top-level surface. default = no shadow (ShadowGeometry default is a no-op).</summary>
public readonly record struct WindowShadow(ShadowGeometry Geometry, Color Color)
{
    public static WindowShadow None => default;
    public static WindowShadow Default { get; }        // Drop(radius:1, offset:1, strength:0.5), Color.FromRgba(0,0,0,255)
    public bool IsNone { get; }                        // Strength == 0 || Edges == None || Color non-RGB
    public Margins GetMargins();                       // per-edge cells the surface scene grows beyond content
}

public class Window : Control
{
    // chrome / identity
    public static readonly StyledProperty<string?>      TitleProperty;        // AffectsRender
    public static readonly StyledProperty<WindowStyle>  WindowStyleProperty;
    public static readonly StyledProperty<object?>      ContentProperty;      // AffectsMeasure
    public static readonly StyledProperty<WindowShadow> ShadowProperty;       // surface-geometry change (custom handler)

    // placement — integer cells; Left/Top are SIGNED (Rect is ushort-backed; signed placement is
    // expressed via CompositeParameters offsets, per DECISIONS vocabulary note)
    public static readonly StyledProperty<int>  LeftProperty;                 // AffectsComposite
    public static readonly StyledProperty<int>  TopProperty;                  // AffectsComposite

    // sizing: Width/Height/MinWidth/MinHeight/MaxWidth/MaxHeight are NOT re-registered here.
    // Window AddOwner-s the element/layout subsystem's registrations with overridden metadata
    // (window defaults + WM change handlers). Duplicate registration is a shadowing hazard — see §4.
    public static readonly StyledProperty<SizeToContent> SizeToContentProperty;   // default WidthAndHeight
    public static readonly StyledProperty<WindowState>   WindowStateProperty;
    public static readonly StyledProperty<WindowStartupLocation> WindowStartupLocationProperty;
    public static readonly StyledProperty<bool>   CanMoveProperty;            // default true
    public static readonly StyledProperty<bool>   CanResizeProperty;          // default true
    public static readonly StyledProperty<bool>   CanCloseProperty;           // default true (chrome ✕)
    public static readonly StyledProperty<double> OpacityProperty;            // AffectsComposite; default 1.0;
        // coerced to [0,1] in metadata (coercion inside effective-value computation, Fork A).
        // Window-only in v1: UIElement has no element opacity (deferred to scene nesting, design-doc §3.2).

    public static readonly DirectProperty<Window, bool> IsActiveProperty;     // read-only

    public Window? Owner { get; set; }              // settable until first Show*; then throws (immutable thereafter)
    public WindowManager? Manager { get; }          // non-null while shown
    public bool IsShown { get; }
    public bool IsModal { get; }
    public bool IsActive { get; }
    public Size ActualSize { get; }                 // realized content size (excludes shadow)
    public object? DialogResult { get; set; }       // setting non-null while shown modal requests Close()

    public void Show();                                          // show + Activate() (WPF semantics; §3.3 redirect applies)
    public void Show(WindowManager manager);                     // manager = Owner?.Manager ?? Application.Current.WindowManager
    public Task<object?> ShowDialogAsync(CancellationToken cancellationToken = default);
    public Task<TResult?> ShowDialogAsync<TResult>(CancellationToken cancellationToken = default);
    public bool Activate();                                      // FALSE when modal-blocked: activation silently
                                                                 // redirects to the gate (gate's stamp bumps); no
                                                                 // ModalAttention from programmatic redirects (§3.3)
    public void Close();                                         // Closing (cancelable) → Closed; reentrancy-guarded (§3.5)
    public void Close(object? dialogResult);                     // sets DialogResult then Close()

    public event EventHandler<WindowClosingEventArgs>? Closing;
    public event EventHandler? Closed;
    public event EventHandler? Activated;
    public event EventHandler? Deactivated;

    // Raised on the gating modal when a *user press* lands on a blocked window. ROUTED event so
    // themes can attach a flash storyboard (EventTrigger-equivalent in the storyboard vocabulary — §4 S5 REQUIRES).
    public static readonly RoutedEvent ModalAttentionEvent;
    public event EventHandler<RoutedEventArgs>? ModalAttention;
}

public sealed class WindowClosingEventArgs : EventArgs
{
    public WindowCloseReason Reason { get; }  // why closing started (a "Cancelled" reason is incoherent — removed)
    public bool CanCancel { get; }            // false for OwnerClosed / ManagerShutdown / ct-forced close
    public bool Cancel { get; set; }          // ignored when !CanCancel
}
```

### 2.2 Chrome contract (template-flexible, no PART_ names)

```csharp
[Flags]
public enum WindowHitTestRole : byte { None = 0, Drag = 1, Close = 2, Maximize = 4, ResizeSE = 8 }

public static class WindowChrome
{
    /// <summary>Attached to any element inside a Window template; Window listens to bubbling
    /// ButtonDown/Drag routed events and interprets them per role.</summary>
    public static readonly AttachedProperty<WindowHitTestRole> HitTestRoleProperty;
    public static WindowHitTestRole GetHitTestRole(UIElement element);
    public static void SetHitTestRole(UIElement element, WindowHitTestRole value);
}
```

The default theme template (S8-built): an occluding root — **`FillOpaque` background + `DrawTitledBox(overwrite: true)`** per the drawing-core occluding-panel idiom (a `DrawPanel`-style `FillRectangle` would let lower windows' glyphs show through; windows must occlude, not tint) — a title-bar row (`HitTestRole=Drag|Maximize-on-double-click`, `TemplateBinding Title`, ✕ button with `HitTestRole=Close`), a `ContentPresenter`, and a `◢` grip (`HitTestRole=ResizeSE`) when `CanResize`. `WindowStyle.None` selects a chrome-less template (content only, still opaque-filled).

### 2.3 Popup

```csharp
public enum PlacementMode : byte { Bottom = 0, Top, Right, Left, Center, Pointer }
public enum PopupCloseReason : byte { Programmatic, LightDismiss, EscapeKey, HostDeactivated,
                                      HostBlocked, HostClosed, ScreenResized }
public sealed class PopupClosedEventArgs(PopupCloseReason reason) : EventArgs
{
    public PopupCloseReason Reason { get; } = reason;
}

/// <summary>Light-dismiss top-level primitive. Lives in the host window's LOGICAL tree (DataContext,
/// resources, and styles inherit normally) but renders nothing in place: when open, Child becomes the
/// root of a separate TopLevelSurface with its own Scene, placed by the WindowManager.</summary>
public class Popup : UIElement
{
    public static readonly StyledProperty<UIElement?>    ChildProperty;
    public static readonly StyledProperty<bool>          IsOpenProperty;          // BindsTwoWayByDefault: every
        // close reason writes false through SetCurrentValue AND back through a two-way binding, so a
        // VM-bound IsFilterOpen stays truthful after light dismiss (see §4 property-system REQUIRES).
    public static readonly StyledProperty<PlacementMode> PlacementProperty;       // default Bottom
    public static readonly StyledProperty<UIElement?>    PlacementTargetProperty; // default: logical parent
    public static readonly StyledProperty<Rect?>         PlacementRectProperty;   // target-LOCAL anchor override
    public static readonly StyledProperty<int>           HorizontalOffsetProperty, VerticalOffsetProperty;
    public static readonly StyledProperty<bool>          StaysOpenProperty;       // default false ⇒ light dismiss
    public static readonly StyledProperty<bool>          CloseOnEscapeProperty;   // default true; handled when chain has focus
    public static readonly StyledProperty<WindowShadow>  ShadowProperty;          // default WindowShadow.Default

    public bool IsOpen { get; set; }
    public void Open();
    public void Close();                                  // reason Programmatic
    public event EventHandler? Opened;
    public event EventHandler<PopupClosedEventArgs>? Closed;
}
```

**Guarantees S8 can rely on** (the Menu/ComboBox/ToolTip/ContextMenu contract):
1. Placement is computed against the target's *screen* rect with flip-then-clamp (§3.6); the popup never extends past the screen; its size is clamped to the screen.
2. Opening a popup **never changes window activation**; the host window stays `:active-window`. Popup content may take physical focus (S3 focus scopes) — menus do, tooltips don't. A popup whose child has `IsHitTestVisible=false` produces a **hit-test-transparent surface**: `SurfaceFromPoint` skips it and mouse events fall through to whatever is beneath, so a tooltip never steals hover or clicks from the window under it.
3. `StaysOpen=false` popups close as a chain on any uncaptured `ButtonDown` outside every open light-dismiss surface (innermost→outermost, reason `LightDismiss`); the dismissing press is swallowed (it does not also click what's underneath). A press *inside* chain A additionally dismisses every *other* light-dismiss chain (only one menu system open at a time) — that press still routes into chain A. Hit-test-transparent surfaces are ignored when computing "outside." Host deactivation, host blocking (a modal gates the host), host close, and screen resize also close them with the corresponding reason. On close the WM invokes `OnSurfaceClosed(popupSurface)`: S3 releases capture/focus references into the popup tree and restores the host window's remembered logical focus (the menu-focus round-trip).
4. A popup whose `PlacementTarget` lives inside another popup's child joins that popup's **chain** (submenu nesting); chains dismiss together.
5. Anchor tracking: if the host window moves or the target re-arranges, the popup is repositioned **in the same frame**, during `OnLayoutCompleted` (§3.6, §4) — a composite-offset change only, no re-raster.
6. Popups sort in a **global band above all windows**, ordered by (chain-root open order, chain depth) so chains stay contiguous even when an unrelated popup opens between a menu and its later submenu. `StaysOpen` popups whose host flips to modal-blocked are closed (reason `HostBlocked`) — nothing input-swallowing ever renders above the gate.
7. A popup surface inherits its host window's composite `Opacity` (an `.obscured`/fading host dims its popups with it). Independent popup opacity is not a v1 surface — fade popup content via styling if needed.

### 2.4 WindowManager and the surface abstraction

```csharp
/// <summary>One top-level composited surface: a shown Window or an open Popup's child.</summary>
public sealed class TopLevelSurface
{
    public UIElement Root { get; }                 // the Window, or the Popup's Child wrapper
    public Window HostWindow { get; }              // self for windows
    public bool IsPopup { get; }
    public bool IsHitTestTransparent { get; }      // popup child IsHitTestVisible=false (tooltips); skipped by
                                                   // SurfaceFromPoint and by light-dismiss "outside" tests
    public int Left { get; }  public int Top { get; }   // SIGNED screen cells (content origin, shadow excluded)
    public Size Size { get; }                           // content size
    public Scene? Scene { get; }                        // null until first raster pass
    public bool Contains(int column, int row);          // content rect only — shadow cells are not hit-testable

    // S6 raster-pass integration:
    public void InvalidateVisual();                                 // element-tree render invalidation lands here
    public void Render(ISurfaceRasterizer rasterizer);              // EnsureScene → shadow → tree raster (§3.4)
}

public interface ISurfaceRasterizer                                  // implemented by S6's render pipeline
{
    // CONTRACT: the rasterizer must position ALL draw paths in absolute scene coordinates offset by
    // contentRect.Position. It may NOT lean on PushTranslate for that offset: DrawFormattedText,
    // DrawContent, deferred Pen strokes, chart braille, and shadows ignore the clip/translate stack
    // (drawing-core v1 gotcha). With left/top shadow margins, contentRect.Position is non-zero.
    void Rasterize(UIElement root, DrawingContext context, in Rect contentRect);
}

public sealed class WindowManager : IWindowTopology, IScreenComposition
{
    public WindowManager(IRenderHost renderHost);
    public void AttachFocusHooks(IWindowFocusHooks focusHooks);      // construction order: WM first, S3 builds
                                                                     // its router against IWindowTopology, then
                                                                     // attaches hooks. Throws if attached twice
                                                                     // or if a Show* happens before attach.

    public IReadOnlyList<Window> Windows { get; }       // z order, bottom→top
    public Window? ActiveWindow { get; }                // null when no input-enabled window is shown (§3.3)
    public Window? TopmostModal { get; }
    public Size ScreenSize { get; }
    public IBrush DesktopBackground { get; set; }       // solid ⇒ uniform compositor base; else backdrop buffer (§3.7)
    public event EventHandler? ActiveWindowChanged;

    // ---- S6 contract (IScreenComposition) ----
    public IReadOnlyList<TopLevelSurface> Surfaces { get; }          // z order; stable within a frame
    public void OnLayoutCompleted();                                 // after layout, before raster: SizeToContent
                                                                     // resolution, popup anchor compare + reposition (§3.6)
    public bool CompositeFrame(in CellBufferView target);            // assemble layers → SceneCompositor.Composite
    public void OnScreenResized(int columns, int rows);              // after CellBuffer.Resize, before layout (§3.8)
    public void InvalidateAllSurfaces();                             // RenegotiateAsync / theme-variant flip path
    public Task CloseAllAsync();                                     // shutdown: top-down snapshot sweeps, CanCancel=false (§3.11)

    // ---- S3 contract (IWindowTopology) ----
    public TopLevelSurface? SurfaceFromPoint(int column, int row);   // topmost non-transparent hit; O(surfaces), allocation-free
    public bool IsInputEnabled(Window window);                       // modal gate
    public MouseRoutingDecision FilterMouseEvent(in MouseEvent e);   // S3 calls for every UNCAPTURED mouse event;
                                                                     // captured events bypass the WM entirely (§3.3, §4)
    public void ObservePointerPosition(in CellPosition screen);      // S3 calls for CAPTURED events (one field store;
                                                                     // keeps PlacementMode.Pointer fresh during drags)
    public void OnTerminalFocusChanged(bool hasFocus);               // S3 forwards FocusEvent
}

public enum MouseRoutingKind : byte { Route, Swallowed }
public readonly record struct MouseRoutingDecision(
    MouseRoutingKind Kind, TopLevelSurface? Surface, CellPosition SurfaceLocalPosition);
```

### 2.5 Consumer example

```csharp
// startup (S1 wires session/loop; shown for shape)
var main = new ShellWindow { WindowState = WindowState.Maximized, WindowStyle = WindowStyle.None };
main.Show();

// inside ShellWindow — a modal confirmation, awaited inline (no nested pump; the frame loop keeps running):
private async void OnDeleteInvoked(object? sender, RoutedEventArgs e)
{
    var dialog = new ConfirmDialog($"Delete '{ViewModel.Selected.Name}'?")
    {
        Owner = this,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,   // SizeToContent=WidthAndHeight by default
    };
    bool? ok = await dialog.ShowDialogAsync<bool?>();                // owner gets `.obscured` + input-blocked
    if (ok == true) ViewModel.DeleteSelected();
}
// ConfirmDialog's Yes button handler: Close(true);  No: Close(false);  Esc/✕: Close() ⇒ result null.
```

```xml
<Window xmlns="https://cursorial.dev/ui" Title="Files" Width="64" Height="22" SizeToContent="Manual">
  <Window.Styles>
    <!-- composite-level modal dim: Opacity is AffectsComposite — cached raster reused, zero re-raster -->
    <Style Selector="Window.obscured"><Setter Property="Opacity" Value="0.7"/></Style>
  </Window.Styles>
  <DockPanel>
    <Button x:Name="FilterButton" Content="_Filter" Click="OnFilterClick"/>
    <Popup x:Name="FilterPopup" PlacementTarget="{Binding ElementName=FilterButton}"
           Placement="Bottom" IsOpen="{Binding IsFilterOpen}">  <!-- two-way by default: dismissal writes back -->
      <Border Classes="menu"><!-- menu items; light-dismissed automatically --></Border>
    </Popup>
    <ListBox ItemsSource="{Binding Files}"/>
  </DockPanel>
</Window>
```

---

## 3. Mechanics

### 3.1 Data structures

```csharp
sealed class WindowEntry {                      // one per shown Window
    Window Window; TopLevelSurface Surface;
    Window? Owner;  List<WindowEntry> Owned;    // owner forest
    ulong ActivationStamp;                      // monotonic counter, bumped on successful Activate
    bool IsModal; bool IsClosing;               // IsClosing = the §3.5 reentrancy guard
    TaskCompletionSource<object?>? DialogTcs; CancellationTokenRegistration DialogCtr;
    (int Left, int Top, Size Size)? NormalPlacement;   // saved across Maximize
}
List<WindowEntry> _shown;                       // insertion order (stable tiebreak)
List<Window> _modalStack;                       // open order; gate = last
List<PopupEntry> _openPopups;                   // PopupEntry { Popup, Surface, HostEntry, ParentPopup, ChainRoot, Depth, CachedAnchor }
List<TopLevelSurface> _zOrder;                  // rebuilt only on topology/activation change
SceneLayer[] _layerScratch;                     // cached; re-allocated only when surface count changes
SceneCompositor _compositor;                    // recreated on screen resize / DesktopBackground change
ScenePool _popupScenePool;                      // popup/tooltip scenes churn; window scenes use Scene.Create
List<Scene> _pendingDispose;                    // disposed AFTER CompositeFrame (§3.5)
Queue<Action> _deferredTopology;                // show/close/open/close requested during framework passes (§3.11)
CellPosition _lastPointerCell;                  // PlacementMode.Pointer; updated by FilterMouseEvent (first line)
                                                // and ObservePointerPosition — never goes stale over blocked/desktop
bool _terminalHasFocus = true;                  // last-known FocusEvent state; gates the :active-window bit (§3.3)
```

All WM state is UI-thread-affine (`VerifyAccess` in debug — invariant 6).

### 3.2 Z-order policy (stability-first)

Z list = **window groups bottom→top, then the global popup band**:

1. Roots = shown windows with `Owner == null`. Group stamp = max `ActivationStamp` over the root's owned closure. Roots sort ascending by group stamp (most recently activated group on top).
2. Within a group, DFS: emit owner, then owned subtrees ascending by subtree-max stamp — **owned windows always above their owner**.
3. Popup band: all open popups, sorted by (chain-root open order, depth) — chains contiguous, parents below their submenus — above every window. Hit-test-transparent popups (tooltips) are single-element chains.

Modal-on-top is **emergent, not special-cased**: `ShowDialogAsync` activates the modal (newest stamp ⇒ its group is topmost), and blocked windows can never re-activate (activation redirects to the gate, §3.3), so no blocked group can ever earn a newer stamp than the modal's. Nested modals: each newer modal activates ⇒ stays above. `StaysOpen` popups of blocked hosts are closed at gate-engage (`HostBlocked`), so nothing input-swallowing sits above the gate in the popup band.

`_zOrder` is recomputed **only** on show/close/activate/popup-open/close — never per frame (`Owner` is immutable after first `Show*`, so there is no owner-change trigger). Per-frame work is filling `_layerScratch` from `_zOrder` (a `for` loop, zero allocation). **Layer-count stability** (drawing-core: changing layer count forces a full-target recomposite): the count changes only at window/popup open/close — an acceptable, rare full recomposite. Within a surface's lifetime its slot is one layer; moving/fading/restacking changes only `CompositeParameters` or slot order ⇒ incremental footprint recomposite.

### 3.3 Activation, modal stack, input scoping

```
Activate(w):
  if !shown or closing → false
  if !IsInputEnabled(w):                       // modal-blocked: SILENT redirect — no ModalAttention from
      Activate(gate); return false             // programmatic paths; only user presses raise it (FilterMouseEvent)
  if w == ActiveWindow → true
  old = ActiveWindow; ActiveWindow = w; entry(w).ActivationStamp = ++_counter; RebuildZ();
  if old != null: clear InteractionState.ActiveWindow on old root; old.Deactivated;
                  _focusHooks.OnWindowDeactivated(old)
                  // S3 parks focus and clears AccessKeyCue/PointerOver/Pressed window-wide — contract
                  // W-DC ("window-deactivation clearing"), a DISTINCT trigger from styling C8's
                  // terminal-focus-out clearing; S3 implements both (§4)
  if _terminalHasFocus: set InteractionState.ActiveWindow on w's root   // never re-light while terminal unfocused;
                                                                        // bit applied on OnTerminalFocusChanged(true)
  w.Activated; _focusHooks.OnWindowActivated(w)     // S3 restores the window's remembered logical focus → physical
  CloseLightDismissPopupsNotHostedBy(w); _renderHost.RequestFrame(); → true
```

**Show.** `Show()` = register entry + `Activate(this)` (WPF semantics). Showing a window whose owner sits in a blocked group: the window joins the blocked set (enabled-set recompute below) and the activation redirects silently to the gate — it appears behind the modal's group, `Show` does not throw. A `ShowActivated=false` opt-out is deferred (§7).

**Modal stack.** `ShowDialogAsync`: `VerifyAccess`; validate (not shown, not closed); **if `ct.IsCancellationRequested` → return a canceled task immediately, no side effects**. `Owner ??= ActiveWindow`; create `TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously)`; push onto `_modalStack`; `Show` (+ its `Activate`); recompute the **enabled set** = topmost modal + its transitively-owned windows (+ popups hosted by those). For every shown window flipping blocked⇄enabled: set/clear the **`obscured` class** on its root (DECISIONS Fork B mechanism), close its light-dismiss popups (`HostBlocked` is implied by deactivation? no — reason `HostBlocked`), close its `StaysOpen` popups (`HostBlocked`), and call `_focusHooks.OnWindowBlocked(w)` for newly-blocked windows — **S3 releases any pointer capture held inside them** (a drag in progress when a modal opens from a timer/async continuation must not keep feeding a now-blocked tree). Then register cancellation:

```csharp
DialogCtr = ct.Register(static s => ((DialogState)s!).Sync.Post(static s2 =>
    { var w = ((DialogState)s2!).Window; if (w.IsShown) w.ForceClose(); }, s),
    state, useSynchronizationContext: false);
```

The registration callback **always `Post`s to the UI `SynchronizationContext`** (S1 REQUIRES) — `Close` mutates `_modalStack`/`_zOrder`/styling sinks and must never run on the canceling thread (invariant 6). `ForceClose` = `Close` with `Reason = Programmatic, CanCancel = false`; the dialog task then transitions via `tcs.TrySetCanceled(ct)`. The posted close tolerates the already-closed race (the `IsShown` check + `Try*` completion). On close: pop (out-of-order pops allowed — gate recomputed), restore classes per the recomputed enabled set, dispose `DialogCtr`, complete the TCS — **activation transfers only per §3.5's `wasActive` rule** (closing a non-gate, non-active modal recomputes the enabled set and classes but does not touch activation, raise events, or reorder Z). Continuations resume via the UI `SynchronizationContext` (S1) — they execute inside a later frame's work-drain, so frame coherence holds (invariant 1): no nested message pump exists or is needed; **the frame loop is the pump**.

**Input scoping** — `FilterMouseEvent` is called by S3 **only for uncaptured mouse events** (capture check comes first in S3 — see §4; must be cheap — any-event motion fires per cell crossed):

```
_lastPointerCell = e.Position                 // FIRST, before any swallow branch (Pointer placement stays fresh)
s = SurfaceFromPoint(pos)                     // top-down scan of _zOrder; content rects only (shadows excluded);
                                              // hit-test-transparent surfaces skipped; O(n), no alloc
if e.Kind == ButtonDown and open light-dismiss chains exist:
    if s is not part of any light-dismiss chain:
        close ALL chains (innermost→outermost, reason LightDismiss); return Swallowed
    else:
        close every light-dismiss chain EXCEPT s's chain; fall through (press routes into s)   // §2.3 g.3
if s == null:                                 // desktop
    return Swallowed                          // (deferred: desktop context menu on ButtonDown)
if !IsInputEnabled(s.HostWindow):
    if e.Kind == ButtonDown: gate.RaiseModalAttention(); Activate(gate)   // the ONE ModalAttention source
    return Swallowed                          // Move/Drag/Wheel over blocked windows: swallowed, no hover
if e.Kind == ButtonDown and s.HostWindow != ActiveWindow: Activate(s.HostWindow)
return Route(s, pos − (s.Left, s.Top))        // S3 hit-tests elements within the surface; the routed args also
                                              // carry the original SCREEN position (chrome drag math needs it, §3.9)
```

Keys never consult geometry: S3 routes to the focus scope of `ActiveWindow` (input-enabled by construction whenever non-null). **When `ActiveWindow == null`** (last window closed, or none shown yet): no `:active-window` bit exists anywhere; S3 offers key events to its Application-scope input bindings (global shortcuts) and otherwise discards them; the next `Show`/`Activate` re-establishes routing. Popup keyboard interception is S3 focus-scope mechanics; `CloseOnEscape` is an ordinary routed-key handler on `Popup` — it works because routed events bubble from the popup child's root across the logical link to the `Popup` element (explicit element-subsystem REQUIRES, §4).

`OnTerminalFocusChanged(false)`: record `_terminalHasFocus = false`, clear the `ActiveWindow` interaction bit (visual only — logical `ActiveWindow` retained), S3 clears cue/hover state per its FocusEvent contract (styling C8); `true` records and restores the bit on the current `ActiveWindow`. `Activated`/`Deactivated` events do not fire for terminal focus.

### 3.4 Surface scenes: sizing, shadow, raster

A surface's scene is **content size grown by `Shadow.GetMargins()`** (drop shadows occupy cells outside the element — drawing-core §12.6), clamped to **≥ 1×1** (`Scene.Create` throws below that; a `SizeToContent` window with empty content still gets a 1×1 surface; a popup whose child measures 0×0 defers surface creation until it measures non-empty). Scene placement offset = `(Left − margins.Left, Top − margins.Top)` via `CompositeParameters` (signed offsets are legal there; `Rect` is not signed). `Contains` tests the content rect only.

`TopLevelSurface.Render(rasterizer)` — called by S6's raster pass, z order, after layout + `OnLayoutCompleted`:

```csharp
var scene = EnsureScene();                  // recreates when required size changed (Scene has no resize API;
                                            // gesture-scoped over-allocation during interactive resize — §3.9)
if (_visualDirty) { scene.Invalidate(); _visualDirty = false; }
if (scene.IsDirty)                          // skip entirely when clean: no closure allocation on idle surfaces
{
    _rasterizer = rasterizer;               // fields, so the cached delegate captures nothing per call
    scene.Draw(_drawDelegate ??= ctx =>     // ONE delegate per surface lifetime
    {
        if (!_shadow.IsNone) ctx.DrawDropShadow(_contentRect, _shadow.Geometry, _shadow.Color);  // BEFORE content
        _rasterizer.Rasterize(Root, ctx, _contentRect);   // contentRect = (margins.Left, margins.Top, Size)
    });
}
```

The shadow lives in the **same scene** as the window: its translucent background rides to the compositor, which darkens whatever is below at composite time (lower windows, desktop) — stable under the §0 compositing invariant, and it moves for free with the layer offset when the window is dragged. `AffectsComposite` properties (`Left`/`Top`/`Opacity`) never touch the scene: the next `CompositeFrame` picks the new parameters and the compositor diffs them (invariant 3 — drags and fades re-composite a cached raster; **only** content/brush changes re-raster). Per the `ISurfaceRasterizer` contract (§2.4), the rasterizer offsets all drawing by `contentRect.Position` in absolute scene coordinates — `PushTranslate` does not cover formatted text/content/strokes/shadows in Drawing v1.

### 3.5 Window↔Scene ownership on close (lifecycle state machine)

`NotShown → Shown(scene: null) → Rastered → Closing → Closed`.

- Window scenes: `Scene.Create` (long-lived, owner-held); popup scenes: `_popupScenePool.Rent` (churny).
- On size change: new scene created, old pushed to `_pendingDispose` (interactive-resize exception: §3.9).
- **`Close()` is reentrancy-guarded**: if `IsClosing` or not shown, return immediately — a `Closing` handler calling `Close()` on its own window (directly or via `Close(result)`) is a no-op, not a recursion.
- On `Close(reason)`: set `IsClosing`; raise `Closing` (cancelable unless reason/force forbids — on cancel, clear `IsClosing` and return). If not canceled: close owned windows first (reason `OwnerClosed`, `CanCancel=false`, depth-first), close hosted popups (`HostClosed`); record `wasActive = (this == ActiveWindow)`; if modal, pop from `_modalStack` (out-of-order allowed), recompute gate + enabled set, flip `obscured` classes; `_focusHooks.OnSurfaceClosed(surface)` (S3 releases capture/focus references into the dying tree); raise `Closed`; remove entry; `RebuildZ()`; detach the content tree (element-subsystem detach retracts styles/bindings — retraction is store-owned, invariant 4); push scene to `_pendingDispose`; dispose `DialogCtr`; complete any dialog TCS (`TrySetResult(DialogResult)` / `TrySetCanceled(ct)` for ct-forced closes); `RequestFrame`.
- **Activation handoff (applies to every close, modal or modeless):** if `wasActive`, transfer activation to the first of — owner (if shown, not closing, input-enabled) → current gate (if not closing) → topmost remaining group by stamp → **`null`**. The handoff runs `Activate(next)` (old-side notifications are subsumed: the closing window's `Deactivated` does not fire — `OnSurfaceClosed` already released its focus state). `next == null` ⇒ `ActiveWindow = null`, `ActiveWindowChanged` raised, key routing enters the §3.3 no-active-window state. Mid-cascade handoffs may bounce between closing windows; correctness comes from the *last* close in the cascade running its handoff against the final list. If `!wasActive`: no activation transfer, no `Activated`/`Deactivated`, no Z reorder beyond entry removal — closing a background window or an out-of-order modal never steals activation or raises `ModalAttention`.
- `_pendingDispose` is drained **after** `CompositeFrame` returns: the compositor's per-slot change detection may still hold last frame's `Scene` references, and a pooled buffer must not be re-rented into a scene that could alias a reference used for this frame's diff. Disposal after composite makes reuse provably safe.
- A closed `Window` cannot be re-`Show`n (throws) — matches WPF; state is left for GC.

### 3.6 Popup placement (flip/clamp)

On open and on every reposition trigger:

1. **Anchor** `A` (screen cells) = `PlacementRect` (target-local) ?? target bounds, transformed via the element-subsystem's `TranslatePoint(target → surface root)` + the host surface's `(Left, Top)`. `Pointer` mode anchors a 1×1 rect at `_lastPointerCell` (kept fresh by `FilterMouseEvent` and `ObservePointerPosition`).
2. **Measure** child with `availableSize = ScreenSize`; desired size clamped to screen (and to ≥ 1×1 once non-empty; an empty-measuring child defers the surface, §3.4).
3. **Candidate** per mode (e.g. `Bottom`: `(A.Left + HOffset, A.RowEnd + VOffset)`); if it overflows the screen on the placement axis and the **flipped** candidate (`Top`) fits strictly better, flip; then **clamp** both axes into `[0, screen − size]`.
4. Store `(Left, Top)`; cache the anchor's screen rect.

Reposition triggers, evaluated in **`OnLayoutCompleted`** (after layout, before the raster pass — so this frame's composite uses this frame's geometry; guarantee 5): cached anchor rect changed (host moved / target re-arranged — a per-open-popup rect compare, cheap), child desired-size change, screen resize (`StaysOpen` popups re-place; light-dismiss popups close, reason `ScreenResized`). Repositioning is a `CompositeParameters` offset change — re-composite only.

### 3.7 Desktop base & whole-screen composite

`DesktopBackground`: a `SolidColorBrush` ⇒ `new SceneCompositor(new CellStyle { Background = color })` (uniform base — the compositor's reset-to-base target, per the §0 invariant "anything under the scenes must be the base, not ad-hoc paint"). Any other brush ⇒ sample per cell into a screen-sized backdrop `CellBuffer` (once per resize/brush change) ⇒ `new SceneCompositor(backdrop)`. Compositor recreation ⇒ next composite is full — acceptable (rare).

`CompositeFrame(target)`:
```
for i in 0.._zOrder.Count:
    s = _zOrder[i]                                  // raster pass already ran: s.Scene non-null
    op = (byte) Math.Clamp(255.0 * s.HostWindow.Opacity, 0, 255)    // metadata coercion makes this belt-and-braces
    _layerScratch[i] = new SceneLayer(s.Scene,
        new CompositeParameters(s.Left − s.Margins.Left, s.Top − s.Margins.Top,
                                opacity: op, clip: s.GestureClip, mode: null))
        // GestureClip: non-null only during interactive resize over-allocation (§3.9); popups inherit the
        // HOST window's opacity (guarantee 7)
changed = _compositor.Composite(_layerScratch.AsSpan(0, count), target);
drain _pendingDispose; return changed;              // false ⇒ S6's FrameRenderer diff emits nothing (idle path)
```

### 3.8 Screen resize (`ResizeEvent` policy)

S6 receives the `ResizeEvent`, resizes the screen `CellBuffer` (**contents discarded** — rendering-session), then calls `OnScreenResized(cols, rows)` *before* that frame's layout pass:

1. `ScreenSize` updated; backdrop re-rendered if brushed; **compositor recreated** (its retained-target assumption is broken by `Resize`; the fresh instance full-recomposites, and `FrameRenderer` full-redraws on dimension change anyway — the two coincide in one frame).
2. Per window: `Maximized` ⇒ size = screen (layout invalidated, scene recreated). `Normal` ⇒ **re-clamp**: `Top ∈ [0, rows−1]`, `Left ∈ [−(width − MinVisible), cols − MinVisible]` (`MinVisible = 4` — the title bar must stay grabbable); windows larger than the screen with `CanResize` get `SetCurrentValue(Width/Height, clamped)` (SetCurrentValue: replaces effective value without changing its source — DECISIONS P3 graft — so bindings/styles survive).
3. Light-dismiss popups close (`ScreenResized`); `StaysOpen` popups re-place.
4. `RequestFrame`.

### 3.9 Sizing models & chrome interactions

- `SizeToContent` axes: measure content at screen-size constraint; window size = desired + template chrome insets, clamped to Min/Max and screen (and ≥ 1×1); resolved in `OnLayoutCompleted`. `Manual` uses `Width`/`Height`. `Maximized` overrides everything with screen bounds; `NormalPlacement` is saved/restored across state flips.
- **Drag coordinate space (normative).** Chrome drag handlers compute in **screen space, anchored at press time**: at `ButtonDown` the handler snapshots `screenAtPress` (from the routed args' `ScreenPosition` — S3 carries both surface-local `Position` and screen `ScreenPosition` on routed mouse args, §4) and the window's placement/size at press. Each captured `Drag` computes `delta = screenNow − screenAtPress` and applies it to the press-time snapshot. Surface-local positions are **never** used for drag deltas — during a move drag the surface origin changes every event, and local-space deltas would feed back through the very offset being changed.
- **Move drag**: `ButtonDown` on a `Drag`-role element (when `CanMove` && state==Normal) ⇒ S3 pointer capture on that element; each captured `Drag` updates `SetCurrentValue(Left/Top, clamp(pressPlacement + delta))`, **clamped live** with the §3.8 formula (`Top ∈ [0, rows−1]`, `Left ∈ [−(width−MinVisible), cols−MinVisible]`) — a window can never be dragged to an unrecoverable position (there is no OS to rescue it); release ends. Pure composite churn — never a re-raster, even at 60 fps mouse rates.
- **Resize drag** (`ResizeSE` role, `CanResize`): each captured `Drag` does `SetCurrentValue(Width/Height, clamp(pressSize + delta, Min..Max, screen))`. Property sets land during the input drain; layout happens once per frame regardless of event volume (frame loop coalesces — frame coherence). **Scene allocation discipline:** during the gesture (capture-scoped "resize in progress" flag set at the role's ButtonDown, cleared at release) `EnsureScene` **over-allocates** — scene dims are rounded up to a 16-column × 8-row quantum (clamped to screen + shadow margins), so per-frame size changes reuse the same scene; the layer's `GestureClip` (target coords) bounds the composite union to the actual footprint, and cells outside the content rect are transparent anyway. At capture release the scene is recreated once at exact size and the clip dropped. Without this, an SE-grip drag would recreate an O(cols×rows) `Cell[]` every frame for the whole gesture.
- `Close` role ⇒ `Close()` (reason `ChromeAction`, gated on `CanClose`); `Drag`-role with `ClickCount == 2` (S3's default pipeline includes `MouseClickSynthesizer`) toggles `WindowState`.

### 3.10 Invalidation / notification flow (summary)

| Change | Route | Cost |
|---|---|---|
| `Left/Top/Opacity` (AffectsComposite) | property metadata → Window handler → `RequestFrame` | param diff in compositor; footprint recomposite |
| Content/brush property (AffectsRender) | element tree → `surface.InvalidateVisual()` → `Scene.Invalidate` in raster pass | one surface re-raster + footprint recomposite |
| `Width/Height/SizeToContent/WindowState/Shadow` | layout invalidation → scene recreate (reference swap; gesture over-allocation §3.9) | re-raster + old∪new footprint recomposite |
| Show/Close/popup open/close | `RebuildZ`, layer count changes | full-target recomposite (rare, accepted) |
| Activate | `RebuildZ` (order only), interaction-bit/class flips | restyle (engine-scoped) + footprint recomposites |
| `obscured` flip | class set → styling engine frames (store-owned retraction) | e.g. `Opacity` setter ⇒ composite-only dim |

WM never calls into the styling/property engines except through the published sinks; styling/property engines never touch Scene/CellBuffer (invariant 2) — the WM **is** the element-tree-side router that turns metadata effects into scene/composite operations.

### 3.11 Reentrancy & shutdown policy

Topology mutations (`Show`, `Close`, popup open/close — e.g. a `When`-condition flipping `Popup.IsOpen` during measure, or a binding doing it during the raster pass) requested **during the layout / OnLayoutCompleted / raster / composite passes** are not applied inline: they are enqueued on `_deferredTopology` and drained at the **next frame's input-drain boundary** (plus `RequestFrame`). This keeps `Surfaces`, `_zOrder`, and `_layerScratch` stable while S6 iterates them. Frame coherence (invariant 1) is preserved as worded: a property set during frame N's *input drain* applies in frame N; these sets happened during frame N's *render passes* and were never part of N's drain. Mutations requested during the input drain or ordinary app code apply immediately.

`CloseAllAsync` (shutdown): snapshot the shown list, close top-down with `Reason = ManagerShutdown, CanCancel = false`; open dialogs complete with `null`. Windows shown *during* the sweep (by `Closed` handlers etc.) are collected and closed in follow-up sweeps until the list is empty; each sweep uses the same forced-close semantics, so a hostile handler cannot veto shutdown — only delay it by the number of windows it spawns.

---

## 4. Cross-subsystem contracts

**REQUIRES from S3 (input routing & focus):**
```csharp
public interface IWindowFocusHooks
{
    void OnWindowActivated(Window window);      // restore window's remembered logical focus → physical
    void OnWindowDeactivated(Window window);    // contract W-DC: park focus; clear AccessKeyCue/PointerOver/
                                                // Pressed window-wide. DISTINCT from styling C8 (terminal
                                                // focus-out clearing) — S3 implements BOTH triggers.
    void OnWindowBlocked(Window window);        // modal gate engaged: release pointer capture held inside it
    void OnSurfaceClosed(TopLevelSurface s);    // window OR popup close: release capture/focus refs into the
                                                // dying tree; for popups, restore the host window's remembered
                                                // logical focus (the menu round-trip)
}
```
- **Capture-first routing (normative ordering):** S3 checks active pointer capture **before** consulting the WM. Captured mouse events bypass `FilterMouseEvent` entirely and route directly to the capture owner, with `Position` translated against the *owner's* surface origin at event time (coordinates may be negative / outside the surface); S3 calls `wm.ObservePointerPosition(screen)` for them (one field store). `FilterMouseEvent` is consulted only for uncaptured events. Capture can only be granted to an element that received a routed `ButtonDown` — which the WM already gated — so no separate grant-time modal check is needed; `OnWindowBlocked` covers the mid-gesture case where a window flips to blocked while one of its elements holds capture.
- Routed mouse event args carry **both** surface-local `Position` and screen-space `ScreenPosition` (chrome/scrollbar drag math anchors on the latter, §3.9).
- S3 forwards `FocusEvent` → `OnTerminalFocusChanged`; routes keys to `ActiveWindow`'s focus scope; implements the §3.3 no-active-window key policy (Application-scope bindings, else discard).

**PROVIDES to S3:** `IWindowTopology` (§2.4) — `SurfaceFromPoint`, `IsInputEnabled`, `FilterMouseEvent` (uncaptured only), `ObservePointerPosition`, `ActiveWindow`. Mouse coordinates handed onward are surface-local + screen.

**REQUIRES from S6 (screen & frame loop):**
```csharp
public interface IRenderHost { Size ScreenSize { get; } void RequestFrame(); }
// Frame ordering guarantee: input/work drain (incl. WM deferred-topology drain) → layout (dirty roots in
// Surfaces order) → windowManager.OnLayoutCompleted() → surface.Render(rasterizer) per surface (z order) →
// windowManager.CompositeFrame(screenBuffer) → FrameRenderer.Render.
// On ResizeEvent: CellBuffer.Resize THEN OnScreenResized THEN that frame's layout.
```
**PROVIDES to S6:** `IScreenComposition` (§2.4): `Surfaces` (z order), `OnLayoutCompleted` (the WM's post-layout slot: SizeToContent resolution, popup reposition), `CompositeFrame` (returns the compositor's no-work `false` for the idle path), `OnScreenResized`, `InvalidateAllSurfaces` (renegotiate/theme flip), `CloseAllAsync` (shutdown). The WM **owns the `SceneCompositor`**; S6 owns the target buffer + `FrameRenderer` and never writes inside the compositor's territory (retained-target rule). Construction order: WM constructed against `IRenderHost`; S3 attaches via `AttachFocusHooks` before the first `Show*`.

**REQUIRES from element/layout subsystem:** `Measure/Arrange` on roots; `TranslatePoint(element → root)`; routed-event bubbling for chrome roles; **cross-tree bubbling: routed events bubble from a popup child's visual root to the `Popup` element via the logical-parent link** (load-bearing for `CloseOnEscape` and the whole S8 menu contract); render-invalidation routed to `TopLevelSurface.InvalidateVisual`; tree attach/detach (close path); **`Width`/`Height`/`Min*`/`Max*` registered at the element/layout level and AddOwner-able by `Window`** (Window contributes overridden metadata — defaults + WM change handlers — never duplicate registrations).

**REQUIRES from styling engine:** `IInteractionStateSink` + `ClassSet` on elements (P1); `InteractionState.ActiveWindow` defined (`:active-window` — the canonical name for the assignment's ":active"); the `obscured` class is matchable; `Window.Styles`/`Window.Resources` participate in the scope walk (styling owns the walk; `Window` just carries the collections).

**REQUIRES from property system:** `StyledProperty`/`AttachedProperty`/`DirectProperty` registration + `AddOwner` with metadata override, `SetCurrentValue`, **`SetCurrentValue` writes propagate through two-way bindings to the source** (WPF semantics — `Popup.IsOpen` dismissal write-back depends on it; flag for the Fork A conformance matrix, see Open Q1), `BindsTwoWayByDefault` metadata (on `IsOpenProperty`), metadata coercion (on `OpacityProperty`), `AffectsComposite` metadata routing (DECISIONS: mandatory flag) delivering change callbacks to the Window/WM handlers.

**REQUIRES from S5 (animation/storyboards):** an `EventTrigger`-equivalent in the storyboard vocabulary that can attach `BeginStoryboard` to a **routed event** — `Window.ModalAttentionEvent` is the first consumer (theme flash). Without it the recipe degrades to code-behind subscription (still supported).

**REQUIRES from S1 (application/dispatcher):** UI `SynchronizationContext` (dialog-task continuations AND the cancellation-registration `Post`, §3.3), `Application.Current.WindowManager` ambient, shutdown invoking `CloseAllAsync`.

**PROVIDES to S8 (controls):** the `Popup` contract + guarantees (§2.3); `WindowChrome.HitTestRoleProperty` for custom chrome; the routed `ModalAttentionEvent` for theme flash storyboards.

**Lower layers:** consumed strictly through existing public API (`Scene`, `ScenePool`, `SceneCompositor`, `CompositeParameters`, `DrawDropShadow`, `FillOpaque`, `CellBufferView`) — zero lower-layer changes (invariant 7).

---

## 5. Requirement mapping

- **R5 (modal & modeless child windows) — primary.** `Show`/`Activate`/`Close`, owner forest with owned-above-owner banding, `ShowDialogAsync` await-able results (thread-safe cancellation via UI-context post), nested modal stack with topmost-gate input scoping, owner-cascade close, uniform activation handoff on every close, `DialogResult`.
- **R4 (logical/physical focus) — supporting.** Activation hands physical focus to S3, which restores the window's remembered logical focus (WPF model); per-window focus memory is S3's, triggered by our hooks (including the popup-close focus restore); blocked windows can never own focus because they can never activate, and `OnWindowBlocked` strips capture from them.
- **R6 (access keys) — supporting.** The cue is window-scoped: S3 toggles `AccessKeyCue` on the **active window's** root (we expose which window that is), we guarantee cue/hover clearing on deactivation (contract W-DC) and on terminal focus-out (C8), and `Popup` hosts the menus the cue opens. Permanent-underline fallback is pure styling via capability classes (Fork B) — no WM involvement.
- **R1/R8 (styling/templating, triggers/selectors).** `Window` is a templated `Control` (template barrier honored: chrome styles reach parts via `/template/`); `:active-window` and `.obscured` are the WM-driven styling hooks; the blessed `.obscured { Opacity: 0.7 }` recipe dims at composite granularity. Chrome is fully retemplatable via `WindowChrome.HitTestRole` (no PART-name coupling).
- **R10 (animation).** `Left`/`Top`/`Opacity` are `AffectsComposite` ⇒ storyboarded window slides/fades re-composite a cached raster, never re-raster (invariant 3 by construction); `ModalAttentionEvent` is routed so themes can ignite flash storyboards.
- **R7/R2 (XAML, binding).** `Window` is a XAML root; `Popup` sits in the logical tree so `DataContext`/resources/`ElementName` bindings work inside popup content; `Popup.IsOpen` is two-way-by-default so dismissal stays truthful in the VM; `ShowDialogAsync<T>` keeps dialog results in async/await land.
- **R3 (resource/style inheritance).** `Window` is the scope-walk hop between elements and `Application` (styling proposal §"scope chain"); popups inherit through their logical parent.
- **R9 (property system).** All window/popup state is `StyledProperty`/`DirectProperty` per Fork A (layout properties reused via `AddOwner`, not redeclared); user-gesture writes use `SetCurrentValue` so they never destroy bindings.
- **Invariants:** frame coherence (§3.3, §3.9, §3.11 — sets during drain are visible to the same frame's layout/composite; render-pass mutations defer to the next drain); styling never touches scenes (§3.10); re-composite vs re-raster (§3.4, §3.10); retraction store-owned (class/bit removal only — WM never "sets old values back"); template barrier (§2.2); single UI thread (§3.1, §3.3 cancellation post); additive-only lower layers (§4).

---

## 6. Terminal-specific design (deviations from WPF/Avalonia)

1. **`ShowDialog` is `Task`-based, not a nested message pump.** One UI thread + a frame loop (rendering-session §7's de-facto pattern); `await` *is* the modal pump. No `DispatcherFrame`, no dispatcher priorities (DECISIONS invariant 1). Cancellation marshals onto the UI context — nothing about dialogs ever runs off-thread.
2. **We are the window manager.** No OS HWNDs: every window/popup is a `Scene` layer in one `SceneCompositor` z-stack over a desktop base style (drawing-core: base = what the union resets to). Popups therefore **cannot overhang the screen** — flip/clamp is mandatory, not best-effort, and popup size clamps to the screen.
3. **Z-stack count stability is a first-class design force** (drawing-core: "changing the layer count recomposites the whole target — keep the z-stack length stable"). One layer per surface for its whole lifetime; restack/move/fade are parameter diffs; count changes only at open/close.
4. **Windows occlude, not tint:** chrome roots use `FillOpaque` + `DrawTitledBox(overwrite: true)` — `DrawPanel`'s `FillRectangle` is background-only and would let lower windows' glyphs bleed through (drawing-core §12.4, "Transparency model").
5. **Shadows are cell-band silhouettes**, drawn into the window's own scene with grown margins (`DrawDropShadow` before content); they darken lower layers' *backgrounds* at composite time but cannot dim a lower glyph's foreground (drawing-core) — so modal dimming is **not** a shadow trick: it's the `.obscured` class plus composite `Opacity` toward the desktop base. Shadows require RGB colors and quietly no-op on palette themes — gated in code (`TopLevelSurface.ShadowsEnabled`) on the RGB tiers, `Ansi256` and up, not by a theme capability class.
6. **Window geometry is integer cells; position is signed.** `Left`/`Top` are plain ints expressed through `CompositeParameters` offsets because `Rect` is ushort-backed/non-negative (DECISIONS vocabulary). No sub-cell anything: window move animations step whole cells (cell-quantized `RectInterpolator`/`SizeInterpolator` exist in Drawing for size tweens).
7. **Drag-move never re-rasters; drag-resize never reallocates mid-gesture.** A window drag at any-event-motion rates is pure `CompositeParameters` churn over a cached raster — the §0 invariant makes a translucent shadow stable while sliding; an interactive resize re-rasters into a quantum-over-allocated scene (§3.9). Caveat carried from design-doc §8: dragging a window containing Sixel images re-anchors fragments and re-encodes per frame — image-heavy windows should prefer Kitty-class terminals or stay put.
8. **Resize is destructive end-to-end:** `CellBuffer.Resize` discards contents and `Scene` has no resize — screen resize means compositor rebuild + full recomposite + full redraw in one coordinated frame; window re-clamp keeps title bars reachable (MinVisible=4 cells) because there is no OS to rescue an off-screen window — and the same clamp runs **live during move drags** for the same reason.
9. **Hover gating must be O(surfaces):** with DECSET-1003 motion on by default, `FilterMouseEvent` runs per cell crossed; it is a top-down rect scan with zero allocation, and blocked/desktop motion is swallowed before any element hit-test. Captured motion skips even that — one field store (`ObservePointerPosition`) and straight to the capture owner.
10. **Terminal focus ≠ window activation:** `FocusEvent` toggles the `:active-window` *visual* bit only; logical activation, focus memory, and the modal gate are unaffected — and programmatic `Activate` while the terminal is unfocused does **not** re-light the bit (it is applied when focus returns). Alt-cue clearing on focus-out is delegated to S3 per C8.

---

## 7. Phasing (per the repo's §10/§11 convention)

**v1 spine**
- **W0** — `TopLevelSurface`, scene sizing/recreate/deferred-dispose, cached draw delegate, layer assembly (opacity clamp), desktop base (solid + brushed backdrop), `CompositeFrame`, `OnScreenResized`, `OnLayoutCompleted` plumbing.
- **W1** — `Window` lifecycle (Show=show+activate, Close with reentrancy guard + universal activation handoff incl. the null-active state), owner forest, activation + stamps + z rebuild, `:active-window` bit (terminal-focus gated), focus hooks (incl. W-DC), startup location, sizing models (Manual/SizeToContent/Maximized, ≥1×1 clamp).
- **W2** — modal stack, `ShowDialogAsync` (+ typed overload; cancellation via UI-context post; pre-canceled token path), `.obscured` + `OnWindowBlocked` flips, input gating + `FilterMouseEvent` (capture-first contract with S3), routed `ModalAttentionEvent`.
- **W3** — `Popup`: placement (Bottom/Top/Right/Left/Center/Pointer), flip/clamp, chains (chain-root band ordering), light dismiss (cross-chain rule), hit-test-transparent surfaces, same-frame reposition via `OnLayoutCompleted`, pool-backed scenes, close reasons + `PopupClosedEventArgs`, two-way `IsOpen` write-back, popup-close focus restore, cross-tree Escape routing.
- **W4** — chrome: `WindowChrome.HitTestRole`, move drag (screen-space anchored, live clamp), SE resize grip (gesture over-allocation + GestureClip), close/maximize actions, double-click title toggle, drop shadows + capability gating.
- **W5** — hardening: deferred-topology queue + reentrancy tests, shutdown `CloseAllAsync` (snapshot sweeps), `InvalidateAllSurfaces` on renegotiate, `WindowDiagnostics.DumpZOrder()` (z list + stamps + modal stack + enabled set in one string — acceptance-test target), demo (`windows` command: overlapping windows, dialog incl. cancellation, context menu, tooltip click-through, drag/resize).

**Deferred (recorded with reasons)**
- *Minimized state / taskbar* — no shell surface to minimize to; re-addable as a `WindowState` value (additive).
- *`ShowActivated=false`* — no driving consumer; additive property, plugs into the existing Show path.
- *Topmost band* — no driving consumer; additive `WindowBand` enum slot in the z key when needed.
- *Window open/close transition storyboards* — needs a close-deferral protocol with the animation subsystem (hold `Closing` until storyboard completes); the property surface (`Opacity`, `Left/Top`) already supports hand-rolled transitions.
- *Edge resize on all borders + keyboard move/resize (Alt+F7 style)* — SE grip covers v1; edge bands need hit-test margins around windows (cheap but fiddly with shadows).
- *Light-dismiss event pass-through option* — WPF parity (swallow) is the safe default; pass-through is an additive `Popup` flag.
- *Independent popup surface opacity* — v1 inherits the host's (guarantee 7); additive parameter if a consumer appears.
- *Modal scrim layer slot* — `.obscured` + composite opacity satisfies DECISIONS; a dedicated translucent scrim scene would add a layer-count toggle. Re-evaluate if themes demand backdrop tinting beyond per-window dim.
- *Live desktop backdrop (scene-based base)* — base is reconstructable-region by contract; a live backdrop needs base invalidation plumbing the compositor doesn't expose; uniform/brushed base covers v1.
- *Per-window scene stacks* — the `Surfaces`/layer contract already tolerates >1 layer per window; v1 implements exactly 1 (simplest stable z-stack). Heavy independently-animating children come later without contract change (design-doc §3.2 scene nesting is the alternative).
- *Window snapping/tiling presets, placement persistence* — app-level conveniences; not spine.
- *Desktop context menu* — `s == null` ButtonDown is swallowed in v1; additive hook later.

## 8. Open questions (max 3, with recommendations)

1. **`SetCurrentValue` ↔ two-way binding write-back (property-system confirmation).** Guarantee 3/13 of the popup contract assumes a `SetCurrentValue(IsOpenProperty, false)` on dismissal propagates to a two-way binding's source (WPF behaves this way). Recommendation: pin this in Fork A's oracle-pinned precedence matrix ("SetCurrentValue on a two-way-bound property updates the source, preserves the binding"); if the store design can't honor it, `Popup` falls back to an explicit `SetValue(..., BindingPriority.LocalValue)`-through-binding write on close — decide before W3.
2. **`SceneCompositor` ownership — WM or S6?** Recommendation: **WM** (this spec): the compositor's identity is coupled to the layer list, base, and resize policy the WM owns; S6 passes the target `CellBufferView` per frame and owns only buffer + `FrameRenderer`. Needs a one-line confirmation in S6's spec so neither side constructs two compositors for one target.
3. **`.obscured` dimming at reduced color depth:** composite `Opacity` scales RGB alphas only — palette-colored themes won't visibly dim. Recommendation: document the recipe as RGB-tier (`Ansi256` and up — the Ansi256 theme spine is RGB, so composite `Opacity` blends there; the render-tree gates cut below it); the Ansi16/NoColor theme dictionaries (capability-shaped `ThemeVariant`, Fork B) ship `.obscured` setters using `TextAttributes.Faint` + a darker background palette index instead. No WM mechanism change — pure theme content.

---

## Critique disposition

- **P0-1 (capture vs FilterMouseEvent) — ACCEPTED.** Contract inverted: S3 checks capture first; captured events bypass the WM, route to the capture owner in its surface space (out-of-bounds legal), and only ping `ObservePointerPosition`. Added `OnWindowBlocked` for mid-gesture modal engagement. (§2.4, §3.3, §4, §6.9.)
- **P0-2 (dangling ActiveWindow on modeless close) — ACCEPTED.** Universal `wasActive` activation handoff on every close (owner → gate → topmost → null, skipping closing windows); null-active key-routing state defined (Application-scope bindings, else discard). (§3.3, §3.5.)
- **P0-3 (cancellation off-thread) — ACCEPTED.** CTR callback `Post`s to the UI `SynchronizationContext`; `TrySetCanceled(ct)` + `IsShown` guard for races; pre-canceled token returns a canceled task before any side effect. (§3.3.)
- **P0-4 (tooltip surfaces opaque to input) — ACCEPTED.** `TopLevelSurface.IsHitTestTransparent` from the popup child's `IsHitTestVisible=false`; skipped by `SurfaceFromPoint` and by light-dismiss "outside" computation. (§2.3 g.2/3, §2.4.)
- **P1-5 (Activate return contradiction) — ACCEPTED.** Redirect + `return false`; redirect bumps the gate's stamp; pseudo-code and doc comment aligned. (§2.1, §3.3.)
- **P1-6 (out-of-order modal close steals activation) — ACCEPTED.** Handoff only when `wasActive`; `ModalAttention` now raised solely from the user-press path in `FilterMouseEvent`, never from programmatic redirects. (§3.3, §3.5.)
- **P1-7 (resize scene churn) — ACCEPTED via the over-allocation arm; the pooling arm REBUTTED**: `ScenePool.Rent` resizes the buffer on dimension mismatch and `CellBuffer.Resize` reallocates the `Cell[]`, so pooling across a gesture's per-frame sizes saves nothing. Gesture-scoped 16×8-quantum over-allocation + `GestureClip`, exact recreate at release. (§3.9, §3.7.)
- **P1-8 (drag coordinate space) — ACCEPTED.** Screen-space, press-anchored drag math is normative; routed mouse args carry `ScreenPosition`; surface-local deltas explicitly forbidden for drags. (§3.9, §4.)
- **P1-9 (unrecoverable negative drag) — ACCEPTED.** Live clamp during move drag with the §3.8 formula. (§3.9, §6.8.)
- **P1-10 (popup focus release + cross-tree routing) — ACCEPTED.** `OnSurfaceClosed` fires for popup closes with host-focus-restore semantics; explicit element-subsystem REQUIRES for popup-child → `Popup` cross-tree bubbling. (§2.3 g.3, §4.)
- **P1-11 (Show semantics / owner-change trigger) — ACCEPTED.** `Show()` = show + `Activate()` (silent redirect when blocked); `ShowActivated=false` deferred; "owner-change" deleted from the z-trigger list (`Owner` immutable after first `Show*`). (§2.1, §3.2, §3.3, §7.)
- **P1-12 (Width/Height/Opacity ownership) — ACCEPTED.** Window AddOwner-s the element/layout registrations with overridden metadata; named REQUIRES added; `Opacity` documented Window-only in v1. (§2.1, §4.)
- **P1-13 (IsOpen two-way) — ACCEPTED.** `BindsTwoWayByDefault` on `IsOpenProperty`; all close reasons write through; the SetCurrentValue-propagation dependency surfaced as Open Q1 + property-system REQUIRES. (§2.3, §4, §8.)
- **P1-14 (no post-layout WM slot) — ACCEPTED.** `IScreenComposition.OnLayoutCompleted()` added between layout and raster; guarantee 5 reworded to same-frame, composite-only. (§2.4, §3.6, §4.)
- **P1-15 (reentrancy policy) — ACCEPTED.** Deferred-topology queue for mutations during render passes (drained at next input-drain boundary, coherence-compatible); `IsClosing` guard on `Close`; `CloseAllAsync` snapshot sweeps with forced semantics. (§3.1, §3.5, §3.11.)
- **P2-16 (per-frame closure) — ACCEPTED.** Skip `Draw` when clean; one cached delegate per surface lifetime over fields. (§3.4.)
- **P2-17 (opacity byte wrap) — ACCEPTED.** Metadata coercion to [0,1] + `Math.Clamp` at layer assembly; default 1.0 stated. (§2.1, §3.7.)
- **P2-18 (popup band vs modal/chain contiguity) — ACCEPTED (cheap option).** Band ordered by (chain-root open order, depth); `StaysOpen` popups of blocked hosts closed with new reason `HostBlocked`. (§2.3 g.6, §3.2.)
- **P2-19 (multi-chain dismissal scope) — ACCEPTED.** A press inside chain A dismisses every other chain; press still routes into A. (§2.3 g.3, §3.3.)
- **P2-20 (zero-size scenes) — ACCEPTED.** Surface content clamped ≥ 1×1; empty-measuring popup children defer surface creation. (§3.4, §3.6.)
- **P2-21 (ModalAttention vs theme storyboards) — ACCEPTED.** Promoted to routed `ModalAttentionEvent`; S5 REQUIRES names the routed-event `EventTrigger`-equivalent seam; code-behind subscription still works. (§2.1, §4.)
- **P2-22 (C8 label drift) — ACCEPTED.** Window-deactivation clearing named contract **W-DC**, distinct from styling C8 (terminal focus-out); both listed in the S3 hooks contract. (§3.3, §4.)
- **P2-23 (API loose ends) — ACCEPTED.** `WindowCloseReason.Cancelled` removed; `PopupClosedEventArgs` defined; `Opacity` default stated; WM ctor split (`WindowManager(IRenderHost)` + `AttachFocusHooks`, ordering specified); `_lastPointerCell` updates first in `FilterMouseEvent` and via `ObservePointerPosition` under capture. (§2.1, §2.3, §2.4, §3.3.)
- **P2-24 (contentRect vs Push-stack gotcha) — ACCEPTED.** `ISurfaceRasterizer` contract now states absolute-coordinate positioning offset by `contentRect.Position`, no `PushTranslate` reliance. (§2.4, §3.4.)
- **P2-25 (Activate while terminal unfocused) — ACCEPTED.** Bit-set gated on `_terminalHasFocus`; applied on focus return; logical activation unaffected. (§3.1, §3.3, §6.10.)
- **P2-26 (popup opacity inheritance) — ACCEPTED.** Made explicit as guarantee 7: popups inherit the host window's composite opacity (the blocked-host concern is mooted by P2-18's `HostBlocked` close); independent popup opacity recorded as deferred. (§2.3, §3.7, §7.)