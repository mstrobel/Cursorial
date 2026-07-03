# S1 — Element Tree, Layout, and Render Integration (Cursorial.UI subsystem spec — FINAL)

Status: subsystem design per DECISIONS.md (binding); revised after adversarial critique (disposition appended). All types in namespace `Cursorial.UI` unless noted. `Size`, `Rect`, `Margins`, `Anchor` come from `Cursorial.Rendering`; `Scene`, `ScenePool`, `SceneLayer`, `CompositeParameters`, `DrawingContext`, `IBrush`, `Pen` from `Cursorial.Drawing`; `OutputCapabilities` from `Cursorial.Output.Capabilities`. `using CellStyle = Cursorial.Output.Style;` per Fork B.

---

## 1. Scope

**Owns:**
- `UIElement` (the tree/layout/render/input node above `UIObject`), tree plumbing (visual + logical relationships, attach/detach lifecycle, inheritance-parent wiring, `IInheritanceNode` implementation), `TemplatedParent` storage.
- Two-pass integer-cell layout: `Measure`/`Arrange`, `DesiredSize`/`Bounds`, alignment, `Margin`, `Min/Max/Width/Height`, the `LayoutMath` saturating-arithmetic contract, the `LayoutManager` pass executor.
- Invalidation routing: `PropertyEffects` flags → `InvalidateMeasure` / `InvalidateArrange` / `InvalidateVisual` / `InvalidateComposite`, including `AffectsParentMeasure`/`AffectsParentArrange` for attached layout properties, **and the routing of Fork A's inherited-change notification carrier** (§3.2).
- Core panels: `Panel`, `UIElementCollection`, `StackPanel`, `DockPanel`, `Grid` (star sizing + integer remainder policy), `Canvas`, `WrapPanel`, plus `ScrollContentPresenter` (the scroll *mechanics* element).
- Render integration: render-zone partitioning (element→Scene mapping), `RenderContext` (the `Render` virtual's drawing surface), `RenderTree` (per-window scene ownership, re-raster scheduling, `CompositeParameters` refresh, layer collection for the window manager), z-order rules, opacity groups, `Visibility` semantics (including its effect on descendant boundary layers), scene lifetime/pooling on detach, hit testing.

**Does NOT own:** control templates / `OnApplyTemplate` (S8 — I provide the `ApplyTemplate()` lifecycle hook and `TemplatedParent` plumbing); window z-stack, window chrome, modality (S4 — I provide per-window `RenderTree`s and the layer-collection contract); the frame loop, `TerminalSession`, `FrameRenderer`, the screen `CellBuffer`/`SceneCompositor` instance (S6); styling engine internals (`ClassSet`/`PseudoClassSet`/`Styles` are Fork B types I *host*); binding/`DataContext` (rides my inheritance wiring); focus and input routing (consume my hit-test/traversal surface); text/content controls.

---

## 2. Public API sketch

### 2.1 Tree decision: single hierarchy, two relationships (Avalonia-style)

One node class participates in **one visual tree** (layout, render, hit-test, composite order) and carries a **separate logical-parent pointer** (styling descendant combinators, resource scope walk, DataContext inheritance). Justification: WPF's `Visual`/`UIElement`/`FrameworkElement` stratification buys nothing at hundreds-of-elements scale and triples the API surface; a *fully* split dual tree (two node sets) makes template plumbing and traversal cost worse. The two-pointer model is exactly what Fork B's matcher assumes ("parent walk (logical tree); `/template/` hops exactly one templated-parent edge") and what the property system needs (`InheritanceParent = LogicalParent ?? VisualParent` — content elements inherit through their `ContentControl`, template parts inherit through chrome up to the templated control).

```csharp
public abstract class UIElement : UIObject, IInheritanceNode
{
    // ----- tree -----
    public UIElement? VisualParent { get; }                    // set by AddVisualChild
    public UIElement? LogicalParent { get; }                   // set by AddLogicalChild; null ⇒ falls back to VisualParent
    public UIElement? VisualRoot { get; }                      // the Window (or detached-subtree root); null when never attached
    public bool IsAttachedToTree { get; }                      // true between OnAttachedToTree/OnDetachedFromTree
    public UIElement? TemplatedParent { get; }                 // template barrier datum (Fork B); set ONLY via S8 seam below

    protected IReadOnlyList<UIElement> VisualChildren { get; } // physical order = base paint order
    protected void AddVisualChild(UIElement child, int index = -1);
    protected void RemoveVisualChild(UIElement child);
    protected void AddLogicalChild(UIElement child);           // for content models (ContentControl.Content)
    protected void RemoveLogicalChild(UIElement child);
    protected internal void SetTemplatedParent(UIElement? value);  // S8 only; throws if attached & rendered

    protected virtual void OnAttachedToTree(in TreeAttachmentEventArgs e);   // root, parent
    protected virtual void OnDetachedFromTree(in TreeAttachmentEventArgs e);
    protected virtual void OnVisualParentChanged(UIElement? oldParent, UIElement? newParent);

    // ----- styling host surface (types owned by Fork B; storage hosted here, lazily allocated) -----
    public string? Name { get; set; }                          // #name selectors + x:Name; change re-matches styles
    public ClassSet Classes { get; }
    protected PseudoClassSet PseudoClasses { get; }
    public Styles? Styles { get; set; }                        // scoped style collection (lazy)
    public Style? Style { get; set; }                          // explicit style attachment

    // ----- layout properties (all StyledProperty<T>; effects in [brackets]) -----
    public int? Width { get; set; }            // null = Auto                     [AffectsMeasure]
    public int? Height { get; set; }           //                                 [AffectsMeasure]
    public int MinWidth/MinHeight { get; set; }                // default 0       [AffectsMeasure]
    public int MaxWidth/MaxHeight { get; set; }                // default LayoutMath.Unbounded [AffectsMeasure]
    public Margins Margin { get; set; }        // negative components coerced to 0 (debug diagnostic) [AffectsMeasure]
    public HorizontalAlignment HorizontalAlignment { get; set; } // default Stretch [AffectsArrange]
    public VerticalAlignment VerticalAlignment { get; set; }     // default Stretch [AffectsArrange]
    public Visibility Visibility { get; set; }                 // custom routing, §3.8
    public bool IsHitTestVisible { get; set; }                 // default true    [no invalidation]
    public int ZIndex { get; set; }                            // siblings paint/composite order [AffectsRender + z-order recollect]
    public double Opacity { get; set; }                        // default 1.0     [AffectsComposite; <1 promotes boundary]
    public bool ClipToBounds { get; set; }                     // default false   [AffectsComposite; true promotes boundary]
    public int RenderOffsetColumn/RenderOffsetRow { get; set; }// composite-time slide, may be negative [AffectsComposite; ≠0 promotes]
    public bool IsRenderBoundary { get; set; }                 // explicit cache hint [true promotes; setting false after ANY
                                                               //   promotion does nothing — promotion is sticky until detach (§3.7)]

    // ----- layout -----
    public Size DesiredSize { get; }                           // DirectProperty; INCLUDES Margin (WPF convention)
    public Rect Bounds { get; }                                // DirectProperty; parent-relative arrange result
    public bool IsMeasureValid { get; }
    public bool IsArrangeValid { get; }
    public void Measure(Size availableSize);                   // Unbounded axes via LayoutMath.Unbounded
    public void Arrange(in Rect finalRect);                    // parent-relative slot, non-negative (Rect)
    protected virtual Size MeasureOverride(Size availableSize);// default: max of children DesiredSize
    protected virtual Size ArrangeOverride(Size finalSize);    // default: arrange children at (0,0,finalSize)
    protected virtual void OnChildDesiredSizeChanged(UIElement child); // default: InvalidateMeasure()
    public virtual bool ApplyTemplate() => false;              // S8 seam; called by Measure before MeasureOverride.
                                                               // NOTE: Collapsed elements early-out of Measure BEFORE this —
                                                               // template parts/name scope unavailable until first
                                                               // non-collapsed measure (documented for S8 / XAML FindName).
    public void InvalidateMeasure();                           // self + ancestor walk, enqueue in LayoutManager
    public void InvalidateArrange();
    public void InvalidateVisual();                            // zone scene re-raster
    public void InvalidateComposite();                         // schedules a composite-parameter refresh — never re-rasters

    // ----- coordinate translation (live parent-chain walks, O(depth), allocation-free; always
    //       post-layout-correct — no cached state, so they can never go stale) -----
    public (int Column, int Row) TranslateToWindow(int column, int row);  // element-local → window coords
    public (int Column, int Row) TranslateToLocal(int column, int row);   // window coords → element-local
    // Each boundary hop folds in RenderOffset* and (for ScrollContentPresenter content) −ScrollOffset*.

    // ----- render -----
    protected virtual void Render(RenderContext context) { }   // element-local coordinates, (0,0) = own top-left.
                                                               // DEBUG-guarded read-only: SetValue / tree mutation /
                                                               // Invalidate* from inside Render throws (§3.7).

    // ----- hit testing -----
    protected virtual bool HitTestCore(int column, int row) => true; // element-local; bounds check already done

    // ----- invalidation-sugar statics (static-ctor time; see §3.2 for attached-property semantics) -----
    protected static void AffectsMeasure<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIElement;
    protected static void AffectsArrange<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIElement;
    protected static void AffectsRender<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIElement;
    protected static void AffectsComposite<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIElement;
    protected static void AffectsParentMeasure<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIElement;
    protected static void AffectsParentArrange<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIElement;
}

public enum Visibility : byte { Visible, Hidden, Collapsed }
public enum HorizontalAlignment : byte { Stretch, Left, Center, Right }
public enum VerticalAlignment : byte { Stretch, Top, Center, Bottom }
public enum Orientation : byte { Horizontal, Vertical }
```

### 2.2 Integer-cell layout math

```csharp
public static class LayoutMath
{
    public const int Unbounded = int.MaxValue;     // measure-constraint "infinity"
    public const int MaxExtent = 65535;            // ushort Rect cap — hard ceiling for any arrange rect

    public static bool IsUnbounded(int v) => v == Unbounded;
    public static int Add(int a, int b);   // saturating: Unbounded absorbs; clamps to [0, Unbounded]
    public static int Sub(int a, int b);   // Unbounded − finite = Unbounded; clamps ≥ 0
    public static int Clamp(int v, int min, int max);
    public static Size Add(Size s, Margins m);     // per-axis saturating
    public static Size Sub(Size s, Margins m);
    public static int CenterOffset(int slot, int size) => Math.Max(0, slot - size) / 2; // floor: spare cell → right/bottom
}
```

**The int-cell contract (normative):**
- `Size` (int-backed) carries measure constraints and desired sizes; `LayoutMath.Unbounded` is the only infinity encoding; all layout arithmetic goes through `LayoutMath` (never raw `+`) so `Unbounded ± margin` can't overflow. (Render-time arithmetic on already-finite sizes inside `Render` may use raw ints.)
- `DesiredSize` may exceed the constraint (parents own overflow policy — `ScrollContentPresenter` relies on it); it is never `Unbounded` on any axis (an element that returns `Unbounded` from `MeasureOverride` trips a layout-cycle diagnostic and is clamped to `MaxExtent`).
- Arrange rects are `Rect` (ushort-backed, non-negative). Every arrange position/extent is clamped to `[0, MaxExtent]` before `Rect` construction, with a DEBUG diagnostic on clamping — a misbehaving panel can never detonate the `Rect` ctor. **Negative placement is never expressed in layout**; it is expressed at composite time (`RenderOffset*`, scroll offsets), which accept negatives.
- **Negative margins are unsupported in v1**: `Margin` components are coerced to 0 at registration-coerce time with a DEBUG diagnostic (overlap effects use `RenderOffset*`; a signed-margin vocabulary is a recorded deferral, §7).
- Layout rounding does not exist: every quantity is already an integer cell count. Where fractional shares arise (centering, star distribution) the remainder policy is pinned per-site (floor for centering, largest-remainder for Grid stars — §3.6).

### 2.3 Panels

```csharp
public abstract class Panel : UIElement
{
    public UIElementCollection Children { get; }   // sets visual AND logical parent; index = paint order
    public IBrush? Background { get; set; }        // [AffectsRender]; painted before children via FillOpaque (§3.7):
                                                   // occludes lower layers' glyphs (translucent brushes still frost).
                                                   // Glyph-transparent scrims are a custom-Render concern, not Background.
}

public sealed class UIElementCollection : IList<UIElement>   // owner-wired: add/remove → (Add|Remove)VisualChild +
{ /* throws on null/duplicate/attached-elsewhere; Move(int,int) preserves attach state;                    */
  /* every mutation invalidates the owner's cached z-order array (§3.7) + InvalidateMeasure               */ }

public class StackPanel : Panel
{
    public Orientation Orientation { get; set; }   // default Vertical    [AffectsMeasure]
    public int Spacing { get; set; }               // default 0           [AffectsMeasure]
}

public class DockPanel : Panel
{
    public bool LastChildFill { get; set; }        // default true        [AffectsMeasure]
    public static readonly AttachedProperty<Dock> DockProperty;          // [AffectsParentMeasure] — property-global (§3.2)
    public static Dock GetDock(UIElement e); public static void SetDock(UIElement e, Dock value);
}
public enum Dock : byte { Left, Top, Right, Bottom }

public class WrapPanel : Panel
{
    public Orientation Orientation { get; set; }   // default Horizontal  [AffectsMeasure]
    public int? ItemWidth { get; set; }            // uniform item size   [AffectsMeasure]
    public int? ItemHeight { get; set; }
}

public class Canvas : Panel
{
    public static readonly AttachedProperty<int?> LeftProperty, TopProperty, RightProperty, BottomProperty;
    // all [AffectsParentArrange], property-global (§3.2); children measured with Unbounded constraint;
    // offsets clamped ≥ 0 (v1 — see §7; use RenderOffset* for negative slides)
}

public class Grid : Panel
{
    public ColumnDefinitionCollection ColumnDefinitions { get; }   // mutation → InvalidateMeasure
    public RowDefinitionCollection RowDefinitions { get; }
    public static readonly AttachedProperty<int> RowProperty, ColumnProperty;       // coerce ≥0 [AffectsParentMeasure]
    public static readonly AttachedProperty<int> RowSpanProperty, ColumnSpanProperty; // coerce ≥1 [AffectsParentMeasure]
    // static Get/Set accessors per attached-property idiom; all four property-global (§3.2)
}

public readonly record struct GridLength(int Cells, GridUnitType UnitType, double StarWeight = 1.0)
{
    public static GridLength Auto { get; }
    public static GridLength Star(double weight = 1.0);
    public static GridLength FromCells(int cells);
    public static implicit operator GridLength(int cells);     // 12 ⇒ fixed 12 cells
}
public enum GridUnitType : byte { Cell, Auto, Star }

public sealed class ColumnDefinition  // RowDefinition mirrors with Height/MinHeight/MaxHeight/ActualHeight
{
    public GridLength Width { get; set; } = GridLength.Star();
    public int MinWidth { get; set; }                  // default 0
    public int MaxWidth { get; set; }                  // default LayoutMath.Unbounded
    public int ActualWidth { get; }                    // post-layout readback
    // Definitions are owner-wired: adding to a collection sets an owner-grid backpointer (a definition
    // belongs to at most one collection — re-adding elsewhere throws); every property setter calls
    // owner?.InvalidateMeasure(). Post-attach mutation is therefore fully live.
}
```

### 2.4 Scroll mechanics

```csharp
/// The scroll mechanics element (S8's ScrollViewer templates around it).
/// Always a render boundary. Single child = the scrolled content.
public class ScrollContentPresenter : UIElement
{
    public UIElement? Content { get; set; }
    public bool CanScrollHorizontally / CanScrollVertically { get; set; }  // default false/true [AffectsMeasure]
    public int ScrollOffsetColumn / ScrollOffsetRow { get; set; }          // coerced into [0, Extent−Viewport] at set time
                                                                           // AND re-coerced at end of arrange (§3.9) [AffectsComposite]
    public Size Extent { get; }       // DirectProperty: content desired size post-measure
    public Size Viewport { get; }     // DirectProperty: own arranged content size

    public static int SceneBudgetCells { get; set; } = 262_144;  // process-wide policy knob (§3.9, §8)
}
```

### 2.5 Render integration types

```csharp
/// The Render(...) drawing surface: Cursorial.Drawing vocabulary re-exposed in ELEMENT-LOCAL integer
/// cell coordinates. Coordinate translation is performed by THIS type at the call site (origin add;
/// origins are non-negative except in scroll-fallback mode, §3.9), NOT via DrawingContext.PushTranslate —
/// which the v1 Drawing push stack does not apply to formatted text, content, pen strokes, braille,
/// shadows, or titled boxes (drawing-core.md §2). ONE instance is reused per zone raster with a mutable
/// internal (origin, size) re-pointed per element — do not capture it beyond the Render call.
public sealed class RenderContext
{
    public Size Size { get; }                 // element's arranged content size
    public Rect Bounds { get; }               // (0, 0, Size) — element-local
    public OutputCapabilities Capabilities { get; }   // negotiated; auto-passed to text/content calls

    // Forwarded surface (each with the Color sibling overloads per Drawing convention):
    public void Set(int column, int row, string? grapheme, in CellStyle style);
    public void FillRectangle(in Rect region, IBrush brush); public void FillRectangle(in Rect region, Color color);
    public void FillOpaque(in Rect region, IBrush brush);    public void FillOpaque(in Rect region, Color color);
    public int  DrawText(int column, int row, ReadOnlySpan<char> text, IBrush foreground, IBrush? background = null, in CellStyle baseStyle = default);
    public void DrawLine(int x0, int y0, int x1, int y1, in Pen pen, bool overwrite = false);
    public void DrawBox(in Rect rect, in Pen pen, bool overwrite = false);
    public void DrawRectangle(in Rect rect, in Pen pen, IBrush? fill = null, bool overwrite = false);
    public void DrawTitledBox(in Rect rect, in PanelTitle title, in Pen pen, bool overwrite = false);
    public void DrawPanel(in Rect rect, in Pen pen, IBrush? fill = null, PanelTitle title = default, bool overwrite = false);
        // NOTE: DrawPanel's fill is Drawing's background-only FillRectangle; for an opaque surface use
        // FillOpaque + DrawTitledBox(overwrite: true) — the Panel.Background path does this for you.
    public void DrawDropShadow(in Rect element, in ShadowGeometry geometry, Color shadowColor);
    public void DrawInnerShadow(in Rect element, in ShadowGeometry geometry, Color shadowColor);
    public void DrawFormattedText(FormattedText text, in Rect bounds, IBrush brush);   // caps auto-supplied
    public void DrawContent(in Rect bounds, IContent content);                          // caps auto-supplied

    // User figures (pen-gradient bounds union / junction grouping). The zone painter holds an AMBIENT
    // per-element figure open around Render (§3.7); Drawing figures do NOT nest, so BeginFigure here
    // CLOSES the ambient figure, opens the user figure, and Dispose of the returned scope closes it and
    // REOPENS a fresh ambient figure. Consequence (documented): strokes drawn before / inside / after a
    // user figure form three separate junction groups. Scopes must not nest (one user figure at a time;
    // nested call throws, mirroring Drawing).
    public RenderFigureScope BeginFigure(); public RenderFigureScope BeginFigure(in Rect bounds);
    // NO PushClip/PushTranslate surface: per-element clipping is a render-boundary concern (ClipToBounds).
}

public readonly struct RenderFigureScope : IDisposable { /* close user figure + reopen ambient; double-dispose safe */ }

/// Per-window render orchestrator: owns zone partitioning, zone scenes, the flat boundary-layer list,
/// composite parameters, boundary-level absolute origin/clip caches, hit testing. One per Window root;
/// created on root attach.
public sealed class RenderTree
{
    public RenderTree(UIElement root, ScenePool scenePool, OutputCapabilities capabilities);
    public OutputCapabilities Capabilities { get; set; }    // S6 re-stamps inside the renegotiation transaction (§4)
    public void RunRenderPass();                            // promotions → re-raster dirty zones → boundary walk (§3.8)
    public int LayerCount { get; }                          // stable unless boundary set changed (full-recomposite signal)
    public void CollectLayers(List<SceneLayer> target,      // appends this window's layers bottom-up, in
        int windowOffsetColumn, int windowOffsetRow,        //   screen coordinates (window position folded in)
        double windowOpacity = 1.0);
    public UIElement? HitTest(int column, int row);         // window-local coords; mirrors composite order exactly (§3.10)
    public void InvalidateAll();                            // full re-raster (resize, renegotiation, palette swap)
    public void Detach();                                   // returns all scenes to the pool (root detach / window close)
}

public sealed class LayoutManager                           // one per visual root; owned by the Window
{
    public bool HasPendingWork { get; }
    public void RunLayoutPass(Size rootConstraint);         // measure+arrange to fixpoint (cap 16 passes + diagnostic);
                                                            // one bounded re-run if LayoutUpdated handlers dirty (§3.5)
    public event Action? LayoutUpdated;                     // post-pass (focus/overlay reposition hooks)
}
```

### 2.6 Consumer example

```csharp
public sealed class GaugePanel : UIElement
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        UIProperty.Register<GaugePanel, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<double> ValueProperty =
        UIProperty.Register<GaugePanel, double>(nameof(Value), coerce: static (_, v) => Math.Clamp(v, 0, 1));

    static GaugePanel()
    {
        AffectsRender<GaugePanel>(FillProperty, ValueProperty);   // brush/content change ⇒ zone re-raster
    }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
        => new(LayoutMath.IsUnbounded(availableSize.Columns) ? 20 : availableSize.Columns, 3);

    protected override void Render(RenderContext ctx)
    {
        ctx.DrawTitledBox(ctx.Bounds, new PanelTitle("cpu"), Pens.Rounded.WithColor(Colors.Cyan));
        var inner = new Rect(1, 1, Math.Max(0, ctx.Size.Columns - 2), 1);   // finite render math: raw ints fine
        var filled = (int)Math.Round(inner.Columns * Value);
        if (filled > 0)
            ctx.FillRectangle(inner with { Columns = filled }, Fill ?? Brushes.Green);
    }
}

// Assembly: a grid with a scrolling log and an animated slide-in side panel.
var root = new Grid
{
    ColumnDefinitions = { new() { Width = GridLength.Star(2) }, new() { Width = 30, MaxWidth = 40 } },
    RowDefinitions    = { new() { Height = 3 }, new() { Height = GridLength.Star() } },
};
var gauge = new GaugePanel { Margin = new Margins(1, 0) };
var log = new ScrollContentPresenter { Content = logStack, ClipToBounds = true };  // boundary: scroll = composite slide
var side = new StackPanel { Classes = { "sidebar" }, RenderOffsetColumn = 30, IsRenderBoundary = true };
Grid.SetColumnSpan(gauge, 2); Grid.SetRow(log, 1); Grid.SetRow(side, 1); Grid.SetColumn(side, 1);
root.Children.Add(gauge); root.Children.Add(log); root.Children.Add(side);
// storyboard (S-animation) slides the panel in WITHOUT re-rastering — RenderOffsetColumn is AffectsComposite:
//   handle = side.BeginAnimation(UIElement.RenderOffsetColumnProperty); handle.SetValue(anim.ValueAt(t)) per frame.
```

---

## 3. Mechanics

### 3.1 Tree plumbing & lifecycle

Per-element storage: `_visualParent`, `_logicalParent`, `List<UIElement> _visualChildren` (lazy), small `_logicalOnlyChildren` list (lazy), copy-on-write `UIObject[] _inheritanceChildren` (for the property fork's eager-notify span), lazily-built cached z-order index array (§3.7).

`AddVisualChild(child)`: assert UI thread (`VerifyAccess`, debug); throw if `child.VisualParent != null`; set parent; recompute `child` inheritance parent (`SetInheritanceParent(LogicalParent ?? VisualParent)`); if `this.IsAttachedToTree` → run **attach walk** on the child subtree. Attach walk (pre-order, parent-first): set `VisualRoot`, raise `OnAttachedToTree`, call styling `OnElementAttached(element)` (Fork B Phase-1 match), mark measure+arrange invalid, assign zone pointer (§3.7). Detach walk is **bottom-up** (Fork B's batch-retraction contract): styling `OnElementDetached`, release boundary scene to pool, clear zone pointer, clear sticky boundary promotion, raise `OnDetachedFromTree`, then clear `VisualRoot`. Inheritance parent is re-pointed (not cleared) on detach so a detached subtree still resolves defaults sanely; the property store's reparent diff handles value changes (Fork A §3.5).

**No `IDisposable` on elements.** The only unmanaged-ish resources an element can hold are pooled scene buffers and styling/binding cookies; all are released on detach. A subtree dropped without detach leaks nothing the GC can't reclaim (pool only tracks *returned* buffers). This matches the lower layers' stance (Scene.Dispose is pool-return, idempotent).

Elements are reusable: detach + reattach elsewhere is legal; single-shot state (scene, zone pointer, promotion, style frames) is rebuilt on attach.

`IInheritanceNode`: `InheritanceParent => LogicalParent ?? VisualParent`; `InheritanceChildren` = visual children whose inheritance parent is `this` ∪ logical-only children — maintained incrementally in `_inheritanceChildren` on every add/remove of either relationship.

### 3.2 PropertyEffects routing

`UIElement` overrides `OnPropertyChanged(in UIPropertyChangedEventArgs args)`: one lookup `PropertyEffects fx = args.Property.GetEffects(GetType())`, then:

```
if fx.AffectsMeasure        → InvalidateMeasure()
if fx.AffectsArrange        → InvalidateArrange()
if fx.AffectsRender         → InvalidateVisual()
if fx.AffectsComposite      → InvalidateComposite()
if fx.AffectsParentMeasure  → VisualParent?.InvalidateMeasure()
if fx.AffectsParentArrange  → VisualParent?.InvalidateArrange()
```

**Effects metadata has two storage lanes (normative; amends the Fork-A metadata contract — see §4):**

1. **Per-type frozen tables** (Fork A's dense-`Id`-indexed arrays) for `StyledProperty`/`DirectProperty` registered on or inherited by the owner hierarchy — declared via the `Affects*<TOwner>` sugar in the owner's static ctor before freeze.
2. **Property-global effects** (`UIProperty.GlobalEffects`, an internal field on the property itself) for **attached properties**. `DockPanel.DockProperty [AffectsParentMeasure]` changes on arbitrary child types (`Button`, `TextBlock`, …) whose per-type tables never saw — and structurally *cannot* see — the registration: the child type's table may freeze before the panel type's static ctor ever runs. The `Affects*<TOwner>` sugar therefore writes the property-global slot when handed an `AttachedProperty<T>`, and `GetEffects(Type forType)` returns `perTypeTable[id] | property.GlobalEffects`. Registration-before-first-use is structurally guaranteed: an attached property cannot be set without touching the declaring type's static field (`Grid.RowProperty` / `Grid.SetRow`), which runs its static ctor first. Without this lane, `Grid.SetRow(button, 2)` would invalidate **nothing**.

**Inherited-change routing (normative):** Fork A delivers inherited-value changes on entry-less descendants through its second carrier, not through `OnPropertyChanged`. `UIObject` exposes a matching virtual (`OnInheritedPropertyChanged(in InheritedPropertyChangedEventArgs)`, name owned by Fork A); `UIElement` overrides it and runs the **same effects dispatch** above. This is what makes R6 work: one `ShowAccessKeys`-style write at the root fans out to every inheriting descendant, and each descendant with an `AffectsRender` mapping marks its zone dirty. Cost is the inherited-notify subtree walk Fork A already performs plus a flag-set per affected element; re-raster is bounded to the zones actually containing affected elements.

No allocation, no virtual dispatch beyond the two overrides; an animation writing `RenderOffsetColumn` at 60 fps costs a flag-set per frame. The styling/property engines never see scenes or buffers (invariant 2): this routing is the *only* bridge from property changes to invalidation, and it lives in the element tree.

`InvalidateMeasure()`: if already invalid → return. Mark `IsMeasureValid = false`, enqueue self in the root's `LayoutManager` (depth-keyed), and **propagate to `VisualParent`** recursively (stops at the first already-invalid ancestor or the root). Rationale: parent desired size depends on children; the up-walk is O(depth ≤ ~12) and avoids sized-to-content analysis. `InvalidateArrange()` marks self only (+enqueue). `InvalidateVisual()` sets a render-dirty bit and adds the element's **zone root** to the render tree's dirty-zone set. `InvalidateComposite()` sets the render tree's composite-refresh flag (promoting the element if a boundary predicate now holds, §3.7) — it never touches a `Scene` (invariant 3); the boundary walk (§3.8) recomputes parameters and writes only actual differences.

### 3.3 Measure

```
Measure(availableSize):
    if Visibility == Collapsed: DesiredSize = Size.Empty; IsMeasureValid = true; return
        // NOTE: early-out precedes ApplyTemplate — collapsed controls have no template/name scope
        // until their first non-collapsed measure (S8/XAML FindName must tolerate; documented).
    if IsMeasureValid && availableSize == _lastMeasureConstraint: return        // cache hit
    ApplyTemplate()                                                              // S8 seam (no-op on UIElement)
    inner    = LayoutMath.Sub(availableSize, Margin)
    minMax   = ResolveMinMax()        // explicit Width/Height fold into both min & max (WPF rule)
    coerced  = clamp(inner, minMax)   // per axis; Unbounded passes through max
    natural  = MeasureOverride(coerced)            // panel/content logic; integer cells
    natural  = clamp(natural, minMax)              // explicit size wins over content
    DesiredSize = LayoutMath.Add(natural, Margin)  // saturating; Unbounded never escapes (diagnostic clamp)
    _lastMeasureConstraint = availableSize; IsMeasureValid = true
    if DesiredSize != previous: VisualParent?.OnChildDesiredSizeChanged(this)    // default re-invalidates parent
```

### 3.4 Arrange

```
Arrange(finalRect):                                  // parent-relative slot incl. margin space
    if Visibility == Collapsed: Bounds = Rect.Empty; IsArrangeValid = true; return
    if !IsMeasureValid: Measure(_lastMeasureConstraint)            // self-heal
    if IsArrangeValid && finalRect == _lastArrangeRect: return
    slot      = LayoutMath.Sub(finalRect.Size, Margin)
    size_a    = (alignment == Stretch on axis) ? slot : min(DesiredSize − Margin, slot)
    size_a    = clamp(size_a, ResolveMinMax())                     // explicit/min/max still bind under Stretch
    used      = ArrangeOverride(size_a)                            // children placed; returns actual content size
    used      = clamp(used, 0, MaxExtent per axis)
    offset    = AlignmentOffset(slot, used)                        // Left/Top: 0; Center: LayoutMath.CenterOffset
                                                                   // (floor — spare cell to right/bottom); Right/Bottom: slot−used
    newBounds = Rect(finalRect.Column + Margin.Left + offset.C,    // all clamped to [0, MaxExtent]
                     finalRect.Row + Margin.Top + offset.R, used)
    SetBoundsAndRoute(newBounds); _lastArrangeRect = finalRect; IsArrangeValid = true
```

`SetBoundsAndRoute` (a `DirectProperty` write via `SetAndRaise`): **size changed** → `InvalidateVisual()` and, if boundary, schedule scene recreate (scenes don't resize — `Scene.Create`/pool re-rent, drawing-core §1). **Position-only change** → boundary: `InvalidateComposite()` (cheap layer move); non-boundary: `InvalidateVisual()` on the zone (cells physically move within the raster). Consequence, documented loudly: *animate position via `RenderOffset*` (composite path), never via `Margin`/`Canvas.Left` (re-raster path)* — invariant 3's "animated slides must never re-raster" is satisfied by the `RenderOffset*`+`Opacity` properties being `AffectsComposite` and boundary-promoting.

### 3.5 Layout pass (`LayoutManager`)

Two depth-ordered queues (measure, arrange), implemented as a binary-min-heap of `(depth, element)` plus a per-element "enqueued" bit (no duplicate entries). `RunLayoutPass(rootConstraint)`:

```
for retry in 0..1:                  // retry 1 exists only for LayoutUpdated handlers that dirty layout
    for pass in 1..16:
        while measureQueue nonempty: pop shallowest e; if !e.IsMeasureValid: e.Measure(e._lastMeasureConstraint ?? rootConstraint)
        while arrangeQueue nonempty: pop shallowest e; if !e.IsArrangeValid: e.Arrange(e._lastArrangeRect ?? Rect(rootConstraint))
        if both empty: break
    else: diagnostic "layout cycle" (names the oscillating elements)     // repo's diagnostics-first convention
    LayoutUpdated?.Invoke()
    if !HasPendingWork: break
// work still pending after the bounded retry slips to the next tick + DEBUG diagnostic
```

Shallowest-first guarantees parents re-run before stale children; invalidations raised *during* the pass (e.g. a measure callback setting a property) loop within the same frame — **frame coherence (invariant 1)**: S6 calls `RunLayoutPass` after the input drain and before `RunRenderPass`, all in tick N. `LayoutUpdated` handlers that dirty layout (caret/overlay reposition) get exactly one same-tick re-run; pathological handlers that dirty every retry degrade to one-frame lag, with a DEBUG diagnostic naming them. Tree mutation *during* the pass is legal only from the element currently being measured/arranged (the `ApplyTemplate` / items-expansion path — required); the 16-pass cap + cycle diagnostic catches abuse.

### 3.6 Panel algorithms (integer policies pinned)

- **StackPanel.** Measure children with `Unbounded` on the stacking axis, the incoming constraint on the cross axis; desired = Σ(child desired) + Spacing×(n_visible−1) by saturating add. Arrange sequentially; cross-axis slot = max(own arranged size).
- **DockPanel.** Classic accumulate-from-edges; remaining rect via `LayoutMath.Sub` clamped ≥ 0; `LastChildFill` gives the final child the remainder. A dock side that exhausts space arranges subsequent children into zero-sized slots (no negative rects ever).
- **WrapPanel.** Greedy line packing on `ItemWidth ?? child.DesiredSize`; a child wider than the line wraps alone and is arranged clipped to line width.
- **Canvas.** Children measure with `(Unbounded, Unbounded)`; arrange at `(Left ?? canvasW − Right − childW ?? 0, Top ?? …)`, clamped ≥ 0 (negative coordinates are a deferral, §7 — use `RenderOffset*` for negative slides). All four attached properties are `AffectsParentArrange` (position-only — the cheap invalidation), demonstrating why `AffectsParentMeasure` and `AffectsParentArrange` are distinct flags.
- **Grid star sizing + integer remainder distribution (the pinned policy).**
  1. Fixed (`Cell`) columns take their clamped size.
  2. Auto columns: measure non-spanning children with `(Unbounded)` on that axis; auto width = max child desired, clamped to def Min/Max. A child spanning Auto columns distributes any deficit (desired − already-allocated) **evenly across its spanned Auto columns, remainder to the rightmost** (v1 simplification, refinement deferred §7).
  3. Star columns: `R = max(0, available − fixed − auto)`; ideal_i = `R · w_i / Σw` in double; `base_i = floor(ideal_i)`; leftover `R − Σbase` cells distributed one each to the **largest fractional parts, ties broken by lowest definition index** (largest-remainder / Hamilton method — deterministic, order-stable, total exactly `R`). Then clamp each to def Min/Max; any clamp surplus/deficit re-runs the distribution over unclamped stars (fixpoint bounded by definition count).
  4. Unbounded available + stars: stars get their content-desired max (star behaves as Auto under an unbounded constraint — WPF rule).
  Known property (documented): largest-remainder can move a single cell between columns as `R` animates (no hysteresis in v1); at terminal scale this is one cell of shimmer and acceptable.

### 3.7 Render zones — the element→Scene mapping policy

**Policy: one `Scene` per render boundary ("zone"), not per element.** Per-element scenes would put hundreds of layers in the z-stack; `SceneCompositor` re-composites the **whole target when layer count changes** (drawing-core §1), and element churn (items added/removed) is routine — per-element scenes would make every list mutation a full-screen recomposite, and compositing cost scales with layers intersecting the dirty union. The design doc's own guidance is "one scene per *independently-updating region*" — that is a boundary, not an element.

**Boundary predicates** (element owns its own `Scene` + `SceneLayer`):
1. window root (always; Window is a boundary by construction),
2. `Opacity < 1` (current value, or *ever-promoted* — see stickiness),
3. `RenderOffsetColumn/Row ≠ 0` or under animation (any write to these promotes),
4. `ClipToBounds == true`,
5. `ScrollContentPresenter` (always),
6. `IsRenderBoundary == true` (explicit cache hint for expensive-to-raster, rarely-changing subtrees).

**Promotion is sticky for the attached lifetime**: once promoted, an element stays a boundary until detach (setting `IsRenderBoundary = false` or returning `Opacity` to 1 does not demote — documented on the properties), so a pulsing `Opacity 1 → 0.5 → 1` animation never oscillates the layer count (z-stack stability, the compositor's full-recomposite trigger). **Guidance (normative for S8):** to fade a list of items in, animate the *container's* opacity — per-item opacity animations permanently mint N boundaries, blowing up a z-stack the compositor tunes for tens of layers; S8 items controls must not template per-item opacity/offset animations. A demotion-on-idle escape valve is a recorded deferral (§7).

**Mid-life promotion is four steps, not one** (runs at the start of the next `RunRenderPass`; the promoted element's content is already baked into its old zone's cached raster, so skipping any step double-paints):
1. `Invalidate()` the **old** zone's scene and re-raster it (now *excluding* the promoted subtree),
2. rent + raster the **new** zone scene for the promoted element,
3. rebuild `_zoneRoot` pointers for the promoted subtree (bounded walk),
4. full-target recomposite (layer count grew — compositor contract).
Cost: **two re-rasters + a full recomposite** in the promotion frame; accepted and documented (it happens once per element lifetime).

A **zone** = a boundary element plus all non-boundary descendants (descendant boundaries start their own zones). Every element caches a `_zoneRoot` pointer, assigned on attach and rebuilt for the affected subtree on promotion. The zone's `Scene` is sized to the boundary's arranged size (`ScrollContentPresenter`: to the content **extent**, §3.9), rented from the shared `ScenePool`, recreated on size change, returned on detach.

**Zero-sized boundaries (pinned):** `Scene.Create`/`ScenePool.Rent` throw for dims < 1, and Collapsed boundaries / zero-sized dock slots are legal. A boundary whose arranged bounds are empty **keeps its previous scene if it has one (else rents 1×1)** and publishes `CompositeParameters.Clip = Rect.Empty` — the layer slot survives, `LayerCount` stays stable, and collapse/expand cycles never trigger the compositor's full-recomposite-on-count-change path. The retained scene re-rasters on the next non-empty arrange (size-change recreate rule).

**Zone-edge clipping & boundary shadows (pinned):** a zone's scene covers exactly the boundary's arranged bounds. Non-boundary elements are not clipped to their *own* bounds (WPF `ClipToBounds=false` default — required for drop shadows of in-zone elements, which paint outside the element rect), but **all zone content hard-clips at the scene's extent** — child overflow beyond the zone root's arranged size is dropped at the scene edge (unlike WPF's visible overflow; documented). Consequently a *boundary* element cannot paint its own drop shadow (it would fall entirely outside its scene): **boundary-level shadows are normatively the parent zone's job** — S8 chrome/decorator patterns draw `DrawDropShadow` around a boundary child from the parent zone, and window shadows are S4 chrome. Scene inflation (shadow margin baked into the zone scene + composite offset bias) is a recorded deferral (§7).

**Zone raster** (inside `Scene.Draw`, when the zone is dirty — any member's render-dirty bit, or scene recreate):

```
PaintZone(ctx):     // scene wiped to Style.Transparent by Draw (whole-scene invalidation — Drawing is memoryless)
    renderContext = _reusable.Reset(ctx)             // ONE RenderContext per zone raster; origin/size re-pointed
    PaintElement(zoneRoot, origin = (0,0))           //   per element — no per-element allocation in the hot path
PaintElement(e, origin):
    if e.Visibility != Visible: return               // Hidden subtrees paint nothing
    open ambient figure                              // per-element figure: cross-element pen strokes never
    e.Render(renderContext.PointAt(origin, e.size))  //   junction-merge; user figures via RenderContext.BeginFigure
    close ambient figure                             //   close-ambient/open-user/reopen-ambient (§2.5)
    foreach child in CachedZOrder(e):                // ascending (ZIndex, index); cached sorted index array,
        if child is boundary: continue               //   invalidated by ZIndex change / collection mutation —
        PaintElement(child, origin + child.Bounds.Position)   //   no per-frame sort (descendant zones raster separately)
```

The DEBUG render-pass guard is armed around `PaintZone`: `SetValue` on styled properties, tree mutation, and `Invalidate*` from inside `Render` throw in DEBUG builds (an `AffectsRender` write from `Render` is a self-sustaining raster loop across frames; the render pass is read-only by contract).

`RenderContext` adds `origin` to every coordinate/Rect argument before forwarding — including `DrawFormattedText`, `DrawContent`, pen strokes, shadows, and titled boxes, which the Drawing push stack would *not* translate (the v1 partial-coverage gotcha, drawing-core "Notes"). Origins inside a zone raster are always non-negative (child `Bounds` are non-negative; `RenderOffset*` never enters the raster — it promotes); rects overflowing the scene's far edge clip safely in Drawing's write paths with correct gradient sampling (brush bounds = the full passed rect). The only negative-origin situation is the scroll budget fallback, which has its own mechanism (§3.9).

**In-zone overlap rule (documented contract):** within one scene, overlap follows the painter's algorithm for cell-writing paths, with Drawing's deferred-stroke semantics intact ("text beats decoration"; a later element's `FillRectangle` does **not** erase an earlier element's deferred border — strokes flush last and only yield to glyph-bearing cells). Elements that genuinely float over siblings (popups, drag ghosts, overlays) must be render boundaries (or windows). This is the "zone overlap caveat"; the escape hatch is one property (`IsRenderBoundary = true`).

**Surface occlusion (pinned):** `Panel.Background` paints via **`FillOpaque`**, always. `FillRectangle` cells are glyph-less and let lower *layers'* glyphs show through at composite (drawing-core "Transparency model") — and every zone composites over something (parent zone, lower windows, base), so a background-painted panel would show the text it floats over. `FillOpaque` preserves translucent alpha (frosted panels still work) while occluding under-glyphs. Borders drawn over an opaque fill need `overwrite: true` (the Drawing recipe) — S8's `Border`/chrome templates own that; control authors composing raw `DrawPanel` must follow the opaque recipe themselves (noted on `RenderContext.DrawPanel`). Deliberate glyph-transparent scrims remain available via `FillRectangle` in custom `Render`.

### 3.8 Composite parameter refresh & z-order

`RunRenderPass()` per window:
1. Execute pending boundary promotions (§3.7 four-step).
2. Re-raster dirty zones (§3.7). `Scene.RasterVersion` bumps only when actually re-rastered — the compositor's change signal stays accurate.
3. Walk the **boundary tree** (boundaries only, depth-first, **unconditionally every pass** — boundaries number in the tens and the walk is integer arithmetic; unconditional recomputation eliminates the entire class of stale-accumulated-input bugs, and the `CompositeParameters` equality gate keeps downstream cost at zero). For each boundary, accumulate across the intermediate non-boundary chain from its parent boundary: absolute origin (window coords; parent boundary origin + Σ intermediate `Bounds` offsets + own `RenderOffset*` − ancestor scroll offsets), opacity product, clip intersection, **and effective visibility (AND of every element's `Visibility == Visible` along the chain, boundaries included)**. Then publish:
   `CompositeParameters(offset: absOrigin [scroll-adjusted], opacity: round(255·Πopacity), clip: effectiveVisibility == Visible ? ownClip ∩ ancestorClips : Rect.Empty, mode: null)` — written to the layer **only when different** (equality is the compositor's change detector; an idle boundary costs a compare).
   Effective visibility in the accumulated inputs is what hides descendant **boundary layers** under a Hidden/Collapsed ancestor — they are not painted in the ancestor's zone, so the zone raster's visibility skip cannot reach them; the empty clip does (layer retained, `LayerCount` stable, old-footprint union erases the vacated cells). Oracle-matrix test pinned: hide a `StackPanel` containing a `ScrollContentPresenter` ⇒ the presenter's layer vanishes the same frame.
4. The same walk refreshes each **boundary's** cached absolute origin + clip (used by hit testing and layer collection). There is deliberately **no per-element absolute-bounds cache**: intra-zone positions are read live from `Bounds` during hit-test descent and `TranslateToWindow`'s chain walk (§3.10), which cannot go stale.

`Visibility` fast paths: **Hidden/Visible flip on a boundary** → parameters-only change via the effective-visibility clip (no re-raster, layer count stable). **Hidden flip on a non-boundary** → zone re-raster (`AffectsRender` path) *plus* the unconditional boundary walk picks up the visibility change for any boundary descendants. **Collapsed** additionally routes `InvalidateMeasure` + parent measure (space is released). Hidden and Collapsed both make the subtree non-hit-testable.

**Z-order rules (normative, fed to S4):**
- Within a zone: paint order = pre-order DFS; siblings stable-sorted by `ZIndex` then collection index (the cached z-order array); parent paints under children.
- Layer order within a window: pre-order DFS of the *boundary tree*, siblings ordered by the same (ZIndex, index) key — i.e., **a zone's own scene is always the lowest layer of its subtree ("zone-base rule")**, descendant boundary layers stack above it in document order. `RenderTree` maintains this as a flat boundary-layer list (rebuilt on boundary-set changes and on ZIndex changes of boundary-bearing subtrees — the "z-order recollect" hook); `CollectLayers` iterates it forward, hit testing iterates it backward (§3.10). Consequence: ancestor-zone content cannot paint over a boundary descendant; if needed, promote the overlapping sibling (see Open Question 1).
- Across windows: S4 concatenates `CollectLayers` output in window z-order; the bottom window's layers are first. One flat `SceneCompositor` over the screen `CellBuffer` (S6-owned). Flat compositing is what makes **window movement a parameters-only change** — drag a window and every layer slides at re-composite cost, zero re-raster.
- Layer-count change events (boundary promotion, subtree attach/detach containing boundaries, window open/close) are full-target recomposites by compositor contract; `RenderTree.LayerCount` lets S4/S6 observe churn.

**Opacity groups:** `Opacity < 1` promotes; descendant boundary layers multiply all ancestor boundary opacities into their own parameter (step 3). This fades a subtree as a unit for opaque content; it is an *approximation* of true group opacity when translucent descendants overlap (each layer composites against base independently — §0 invariant). Exact group opacity = scene nesting (composite children into the parent scene's `CellBufferView`), which the design doc records as "deferred but free" (§3.2); deferred here too (§7).

### 3.9 Scrolling mechanics

`ScrollContentPresenter` (always a boundary, always `ClipToBounds`-equivalent):
- **Measure:** child measured with `Unbounded` on scrollable axes; `Extent = child.DesiredSize`; own desired = min(extent, constraint).
- **Arrange:** child arranged at `Rect(0, 0, max(Extent, Viewport))` *in content coordinates* (non-negative — the ushort `Rect` stays happy); `Viewport = own arranged size`; **at the end of arrange both scroll offsets are re-coerced into `[0, Extent − Viewport]` via a `SetAndRaise`-style write** — content shrinking while scrolled to the bottom snaps the offset back the same frame (`AffectsComposite` fires only when the value actually moves), so the cached raster can never be left slid past the content. Set-time coercion still applies to consumer writes.
- **Composite:** zone scene sized to the **content extent**; `CompositeParameters.Offset = absoluteOrigin − ScrollOffset` (negative-capable — exactly what composite offsets are for); `Clip = absolute viewport rect ∩ ancestor clips`. A scroll-offset change is `AffectsComposite`: the cached raster slides under the clip, **zero re-raster** — the design doc's prescribed viewport mechanism, and it sidesteps the push-stack partial-coverage gotcha entirely (sub-scene composition clips cell content and strokes alike).
- **Budget fallback (degraded mode — pinned mechanism):** extent scenes are capped at `ScrollContentPresenter.SceneBudgetCells` (default 262,144 ≈ 256×1024 — **≈ 16 MB at ~64 B/`Cell`**, a deliberate dial; also each dimension ≤ `MaxExtent`). Over budget, the presenter drops to *viewport-sized* scene mode and scroll changes route to `InvalidateVisual` (re-raster per scroll). Because elements then raster at origins shifted by `−ScrollOffset`, **negative origins arise, which the manual origin-add translation cannot express** (`Rect` is ushort/non-negative). The fallback therefore switches `RenderContext` strategy per zone raster:
  - The four push-stack-covered paths (`Set`, `FillRectangle`, `FillOpaque`, `DrawText`) are forwarded **untranslated in element-local coordinates** under a per-element `DrawingContext.Push(clip, translate: origin)` — the Drawing push stack accepts negative translates and clips these paths correctly, including gradient sampling.
  - The uncovered paths (pen strokes, `DrawBox`/`DrawTitledBox`/`DrawPanel` outlines, `DrawFormattedText`, `DrawContent`, shadows) are manually translated as usual; a call whose translated rect lies **fully outside** the scene is skipped, and a call whose rect **straddles the scene's negative edge is dropped with a DEBUG diagnostic** (it cannot be partially expressed without sub-scene machinery).
  This mode is *degraded, not transparently correct*: cell-path content scrolls correctly; a straddling border/formatted-text block pops out at the viewport edge instead of clipping. The real fix is virtualization (deferred, S8 items controls); the budget knob is a performance/fidelity dial whose default gets measured against a 10k-line log view in the demo before T4 exit (§8).
- Boundaries nested inside scrolled content inherit the scroll offset through the boundary-tree walk (their layer offsets subtract ancestor scroll), and their clips intersect the viewport — scrolled-out floating children cannot escape the viewport.
- Hit testing inherits scroll handling for free: the window→zone-local transform uses the layer's effective offset, which already includes `−ScrollOffset` (§3.10).
- **Fragment caveat (corrected):** per the compositor's per-protocol table, a fragment **straddling a layer `Clip` is cropped only on Sixel** (pixel-crop re-encode); **Kitty and iTerm2 fragments are suppressed entirely under partial overlap** — an image scrolled partially off the viewport *pops out* rather than cropping on Kitty/iTerm2, and pops back in when fully visible. Fully-visible fragments slide smoothly on all protocols (Kitty cheaply via image-ID reuse; moving fragment-bearing scenes every frame is expensive on Sixel — re-encode per move). Controls hosting images in scrolled content should consider snapping images fully in/out of view; documented for S8.

### 3.10 Hit testing

`RenderTree.HitTest(column,row)` **mirrors composite order exactly** — it walks the same flat boundary-layer list the compositor consumes, topmost-first, using the boundary caches the §3.8 walk just refreshed; intra-zone descent uses live `Bounds`. Allocation-free; O(layers + path × siblings) integer-rect arithmetic.

```
HitTest(p):                                            // p window-local
    for layer in boundaryLayerList REVERSED:           // topmost layer first — exact reverse of CollectLayers
        b = layer.Boundary
        if !b.EffectiveClip.Contains(p): continue      // cached clip; Rect.Empty for hidden-ancestor boundaries ⇒ skip
        local = p − layer.EffectiveOffset              // = absOrigin (− scroll for SCP zones): scroll handled for free
        r = HitZone(b, local); if r != null: return r
    return null

HitZone(e, p):                                         // p in e's zone-local coords; e's zone only
    foreach child in CachedZOrder(e) DESCENDING:       // (ZIndex, index) — same cached array the painter uses
        if child is boundary: continue                 // boundary subtrees were handled at the layer level (above)
        if child.Visibility != Visible: continue
        if child.Bounds.Contains(p):
            r = HitZone(child, p − child.Bounds.Position); if r != null: return r
    return (e.IsHitTestVisible && Rect(e.size).Contains(p) && e.HitTestCore(p)) ? e : null
```

This makes hit-test order and visual order provably identical: a boundary child composites above *all* of its ancestor zone's rastered content (zone-base rule), and it is hit-tested above all of it too — including non-boundary siblings later in document order. `RenderOffset*` needs no intra-zone handling (non-zero promotes, so it only ever appears in layer offsets).

Cheap enough for default-on any-event motion (Move per cell crossed, input map "hit-testing must be fast"): the common case is a clip rejection per layer plus one zone descent. S-input's "element under pointer changed?" check is simply a `HitTest` re-run per Move — there is no per-element absolute-bounds cache to go stale (§3.8). `TranslateToWindow`/`ToLocal` are live O(depth) chain walks (§2.1) and are likewise always post-layout-correct.

---

## 4. Cross-subsystem contracts

**REQUIRES from the property system (Fork A / S-properties):**
- `UIObject` surface per DECISIONS (`GetValue/SetValue/ClearValue`, `SetInheritanceParent`, `DeferNotifications`, `OnPropertyChanged(in UIPropertyChangedEventArgs)` virtual with copied-value args).
- `PropertyEffects` metadata with **two lanes (contract amendment)**: per-type frozen tables + lookup `PropertyEffects UIProperty.GetEffects(Type forType)` that returns `perTypeTable[denseId] | property.GlobalEffects`, where `GlobalEffects` is a property-global slot on `UIProperty` written during the registration window — **required for attached properties**, whose effects must fire on host types whose frozen tables never saw (and cannot see) the declaring panel's registration (§3.2). Registration-time / static-ctor-time effect declaration so my `Affects*<TOwner>` sugar can write both lanes. *(Amendment note: the canonical proposal's `Register` signature lacks an effects parameter; DECISIONS mandates the flags — the sugar statics are the agreed authoring surface, and the global lane is new.)*
- **The inherited-change notification carrier** (DECISIONS Fork A's "second small carrier") must flow through an overridable `UIObject` virtual (`OnInheritedPropertyChanged(in …)`) so `UIElement` can run the same effects dispatch on entry-less descendants — R6 depends on it (§3.2). Fan-out = Fork A's existing inherited-notify subtree walk.
- `IInheritanceNode` consumption (`InheritanceParent`, `ReadOnlySpan<UIObject> InheritanceChildren`) — I implement and keep current.
- `DirectProperty` lane for `Bounds`/`DesiredSize`/`Extent`/`Viewport` (`SetAndRaise`).

**PROVIDES to the property system:** inheritance-parent wiring on every attach/detach/reparent (`SetInheritanceParent(LogicalParent ?? VisualParent)`), inheritance-children spans, single-UI-thread guarantee (debug `VerifyAccess` on all tree/layout/render mutation).

**REQUIRES from styling (Fork B / S-styling):** `ClassSet`, `PseudoClassSet`, `Styles`, `Style` types; engine entry points `OnElementAttached(UIElement)` / `OnElementDetached(UIElement)` (detach called bottom-up, batched); `IInteractionStateSink` implementation contract for `UIElement`.
**PROVIDES to styling:** logical-tree parent walk (`LogicalParent`), `TemplatedParent` (template barrier datum — engine skips elements with `TemplatedParent != null` except via `/template/`), `Name`/`Classes` change notifications, attach/detach calls at the pinned lifecycle points (attach **before** first measure so styles affect layout in the same frame), hosting slots for scoped `Styles`.

**PROVIDES to S4 (windows):** `RenderTree` per window (`CollectLayers(list, windowCol, windowRow, windowOpacity)` in screen coordinates, `LayerCount`, `HitTest`, `InvalidateAll`, `Detach`), `LayoutManager` per window, the z-order rules in §3.8. S4 owns window order, positions, the `obscured` class for modal dimming (styling does the visuals — never the compositor), and **window chrome incl. window-level drop shadows** (§3.7's boundary-shadow rule).
**REQUIRES from S4:** window z-order enumeration + positions/opacity at collect time; calling `Detach()` on window close.

**REQUIRES from S6 (frame loop):**
- The per-tick sequence: `drain input → for each window: RunLayoutPass(clientSize) + RunRenderPass() → collect ALL windows' layers (window z-order) into one list → ONE SceneCompositor.Composite(span, screenBuffer) → FrameRenderer.Render` — compositing is flat and single-target; there is no per-window `Composite`.
- Ownership of the screen `CellBuffer`, the single `SceneCompositor`, the shared `ScenePool`, the `FrameRenderer`.
- **The renegotiation transaction (pinned — `FrameRenderer` bakes its `StyleQuantizer` at construction and `SceneCompositor` is stateful-per-target with no reset API):** on `RenegotiateAsync`, S6 must, in one tick: re-stamp `RenderTree.Capabilities` + `InvalidateAll()` per window, **construct a replacement `FrameRenderer`** (with the new `OutputCapabilities`; `Reset()` alone keeps quantizing with stale caps), **construct a fresh `SceneCompositor`**, and full-redraw; styling's capability-class re-stamp (Fork B) rides the same transaction so visuals and rasters change in one coherent frame.
- **The resize transaction:** `CellBuffer.Resize` discards contents while the compositor still believes the target is retained — on resize S6 constructs a fresh `SceneCompositor` (and relies on the renderer's dimension-change full redraw), then re-layouts each window; exposed-base cells outside any layer footprint are repainted by the fresh compositor's first full pass.

**PROVIDES to S6:** `HasPendingWork`-style idle detection (no layout work + no dirty zones + no pending composite refresh ⇒ `RunRenderPass` is skipped, `Composite` returns false ⇒ zero bytes — the cheap idle frame end-to-end).

**PROVIDES to S8 (templates/controls):** `ApplyTemplate()` virtual called from `Measure` before `MeasureOverride` (**not** called while Collapsed — template parts/name scope materialize at first non-collapsed measure; S8 `FindName` and XAML tooling must tolerate); protected visual/logical child plumbing; `protected internal SetTemplatedParent`; guarantee that template-created children attach with full lifecycle (styling match, inheritance wiring) before layout of the templated control completes; the boundary-shadow pattern (§3.7) and the per-item-animation prohibition (§3.7 stickiness guidance); the `Border`-over-`FillOpaque` `overwrite: true` recipe (§3.7).

**PROVIDES to S-input/focus:** `RenderTree.HitTest` (composite-order-faithful, §3.10), `UIElement.VisualParent` chain (bubble/tunnel routing topology), `TranslateToWindow/ToLocal` (live, never stale), `IsHitTestVisible`/`Visibility` semantics, `LayoutUpdated` event (reposition carets/overlays post-layout; one same-tick re-run if handlers dirty layout, §3.5).

**REQUIRES from S-animation (storyboards):** all writes go through `AnimatedValueHandle<T>` on styled properties — never direct scene/parameter access; slide/fade storyboards target `RenderOffsetColumn/Row`/`Opacity` (the `AffectsComposite` lane).

**Lower layers (invariant 7):** consumes only existing public surface (`Scene.Create/Invalidate/Draw/Dispose`, `ScenePool.Rent`, `SceneLayer`, `CompositeParameters`, `DrawingContext` incl. `Push`/figures/`FillRectangle(region, brush, brushBounds)`, `CellBufferView`, `Rect/Size/Margins`). No additive changes required for v1.

---

## 5. Requirement mapping

- **R1 (styling/templating):** the layout/panel substrate templates expand into; `Affects*` sugar gives control authors WPF-shaped invalidation declaration; `ApplyTemplate` lifecycle seam.
- **R2 (binding):** `DataContext` inheritance rides my inheritance-parent wiring (logical-tree-preferring rule reproduces WPF's content-element expectations).
- **R3 (resource/style inheritance):** the logical-parent walk is the scope chain styling/resources traverse; attach ordering guarantees scoped `Styles` see a stable tree.
- **R4 (focus):** provides the tree topology, hit testing, and traversal order (visual order = (ZIndex, index) sort) the focus subsystem builds logical/physical focus on.
- **R5 (child windows):** per-window `RenderTree`/`LayoutManager` + flat layer collection + z-order rules are precisely the window manager's assembly contract; window move/fade = parameters-only.
- **R6 (access keys):** the inherited `ShowAccessKeys`-style flip re-renders mnemonic labels via the inherited-change carrier → effects dispatch (§3.2): one property write at the root, a subtree notify walk, zone re-rasters bounded to zones containing mnemonic labels; `:access-keys` visuals are styling's.
- **R8 (setters/triggers):** trigger-driven visual changes arrive as property changes; `PropertyEffects` routing turns them into the *minimal* invalidation (e.g. `:focus` border-brush → one zone re-raster; `:hover` offset nudge → composite-only).
- **R9 (property system):** this spec is the consumer half — effects flags (both lanes), inheritance wiring, `DirectProperty` lane usage.
- **R10 (animation):** the re-composite lane (`RenderOffset*`, `Opacity`, scroll offsets, sticky boundary promotion) guarantees animated slides/fades re-composite a cached raster and never re-raster (invariant 3); brush/content animations route to `AffectsRender` by design.

**Invariant compliance:** (1) frame coherence — §3.5/§4 sequencing, no priority tiers; (2) styling/property never touch Scene/CellBuffer — only `UIElement` routes effects, engines see properties/frames only; (3) re-composite vs re-raster — §3.4/§3.8/§3.9; (4) retraction is store-owned — the tree restores nothing, it only routes invalidation after the store promotes; (5) template barrier — `TemplatedParent` stored here, enforced by the styling engine; (6) single UI thread — debug `VerifyAccess` on tree/layout/render mutation, plus the render-pass read-only guard (§3.7); (7) additive-only lower layers — §4 last item.

---

## 6. Terminal-specific design (deviations from WPF/Avalonia, with citations)

1. **Integer cells end-to-end; no layout rounding, no DPI.** `LayoutMath.Unbounded` replaces `double.PositiveInfinity`; remainder policies replace `UseLayoutRounding` (floor-centering; Grid largest-remainder). `Rect` is ushort/non-negative (rendering-session "Gotchas") → the `MaxExtent` clamp + "negative placement only via composite offsets" rule (DECISIONS vocabulary note).
2. **Scene-per-boundary, not visual-per-element rendering.** WPF retains per-visual drawing instructions; Cursorial scenes are memoryless cached rasters with whole-scene invalidation (design-doc §3.1: "one scene per independently-updating region"), and the compositor full-recomposites on layer-count change (drawing-core §1) — hence zones, sticky promotion, the zero-size-boundary layer-slot retention, and the empty-clip hidden-layer trick (drawing-core "Notes": "use an empty/fully-clipped layer rather than removing one") — used both for Hidden boundaries and for hidden-*ancestor* boundary layers (§3.8).
3. **`RenderContext` does its own coordinate translation** because the Drawing push stack doesn't cover formatted text/content/strokes/shadows in v1 (drawing-core §2 + design-doc §12.3 gotcha); viewports/clips use sub-scene composition (`CompositeParameters.Offset/Clip`), the maps' "robust route". The scroll fallback is the one place the push stack *is* used — exactly for the four paths it covers (§3.9).
4. **Per-element figure scoping** in the zone painter: junctions never bleed across sibling controls; figures can't nest and mustn't span child draws (drawing-core "Pen/figure model") — the painter's ambient per-element figure discharges that contract for every control author at once, and `RenderContext.BeginFigure` brackets user figures with close-ambient/reopen-ambient so the no-nesting rule can never throw out of a control's `Render` (§2.5).
5. **Slide/fade as composite parameters** (`RenderOffset*`, `Opacity`): the cell grid has no sub-cell transforms (`CompositeParameters` is integer translation + opacity + clip only, drawing-core §1), so the WPF `RenderTransform` generality collapses to exactly these properties — and they map 1:1 onto the compositor's cheap path (design-doc §7: "content slide/fade re-composites a cached scene").
6. **Flat compositor topology** (no nested render targets in v1): window drag, scroll, and overlay motion are all `CompositeParameters` diffs; cost model per drawing-core ("compositing cost ∝ dirty-union area × layer count"), tuned for tens of layers, not hundreds — hence the sticky-promotion guidance against per-item boundary minting (§3.7).
7. **Hit testing is integer-rect descent in exact composite order** (boundary layers topmost-first, then zone raster content) with no geometry/path tests — `HitTestCore` virtual is the shaped-control escape hatch. Sized for default-on any-event motion (input map: Move fires per cell crossed).
8. **Hidden ≠ free re-raster:** on a cell grid, hiding non-boundary content requires repainting the zone (no per-pixel compositing of retained visuals); the Hidden-boundary empty-clip fast path is the terminal-shaped substitute, extended to boundaries under hidden ancestors.
9. **Opaque surfaces are a glyph-grid concern WPF doesn't have:** a background "fill" that doesn't occlude lower layers' *glyphs* is a terminal-specific hazard (drawing-core "Transparency model") — hence `Panel.Background` = `FillOpaque`, pinned (§3.7).
10. **No `IDisposable` element trees** — the only owned native-ish resource is pooled scene buffers, released on detach (mirrors `Scene.Dispose` = pool-return semantics, drawing-core "Lifecycle & ownership").

---

## 7. Phasing (v1 spine vs deferred, §11 convention)

**v1 spine:**
- **T0 — tree:** `UIElement` tree plumbing, lifecycle walks, inheritance wiring, `TemplatedParent`, styling host slots, `PropertyEffects` routing (both lanes + inherited carrier) + `Affects*` sugar. *(unblocks styling/binding forks)*
- **T1 — layout:** `LayoutMath`, Measure/Arrange, alignment/margins/min-max, `LayoutManager`, `StackPanel`, `DockPanel`, `Canvas`. Oracle-pinned layout test matrix (WPF-derived expected values, integer-adjusted) authored with T1.
- **T2 — panels:** `Grid` (stars + remainder policy + spans + definition owner-wiring), `WrapPanel`.
- **T3 — render:** zones, `RenderTree`, `RenderContext` (reusable instance, ambient/user figures), composite walk (incl. effective visibility), z-order + flat layer list, `Visibility` (incl. hidden-ancestor boundary test), hit testing (layer-order walk), scene pooling on detach, zero-size boundary policy, promotion four-step.
- **T4 — motion:** `ScrollContentPresenter` (incl. arrange-time offset re-coercion + degraded fallback mode), opacity groups (multiplicative), sticky promotion, budget measurement; demo in `Cursorial.Demo` per repo convention.

**Deferred (recorded with reasons):**
- **Layer splitting** (ancestor-zone content above a boundary descendant): needs multi-layer zone scenes; zone-base rule + explicit promotion covers real cases at terminal scale. Re-addable additively.
- **True group opacity via scene nesting:** design-doc §3.2 says it's "free" later (composite into a parent scene's `CellBufferView`); v1 multiplicative approximation documented.
- **Virtualizing layout / items panels:** the real answer to the scroll budget (the fallback is degraded, §3.9); belongs with S8 items controls.
- **Negative Canvas coordinates / signed arrange carriers / negative margins:** composite offsets cover the motivating cases; a signed layout vocabulary is a bigger change.
- **Zone scene inflation for boundary-level shadows/overflow:** parent-zone/S4-chrome shadows cover v1 (§3.7); inflation touches clip math, hit testing, and offsets — deferred until a control genuinely needs self-shadowing boundaries.
- **Boundary demotion-on-idle:** the escape valve for promotion-stickiness pathologies (per-item fades); needs layer-churn hysteresis to avoid re-introducing z-stack oscillation. Guidance (§3.7) covers v1.
- **Grid spanning refinement** (WPF's proportional deficit distribution) and **star-distribution hysteresis** (anti-shimmer under animation): one-cell visual artifacts only.
- **Partial intra-zone re-raster:** scene granularity is the invalidation unit by lower-layer design (whole-scene wipe on `Draw`); finer granularity = more boundaries, a consumer knob that already exists (`IsRenderBoundary`).
- **Per-element `IBlendingMode` composite modes:** `CompositeParameters.Mode` exists; exposing it as a styled property awaits a use case (custom mode instances also break parameter-equality caching if not reused — drawing-core "Mutability").

---

## 8. Open questions (≤3, with recommendations)

1. **Zone-base rule vs document-order fidelity.** Should v1 attempt any interleaving of ancestor-zone content above boundary descendants (layer splitting)? **Recommendation: no.** Keep the zone-base rule (a zone's scene is the lowest layer of its subtree); document `IsRenderBoundary = true` as the overlap escape hatch. Hit testing now mirrors the layer order exactly (§3.10), so the rule is at least *self-consistent* end-to-end. Splitting multiplies layer count and destabilizes the z-stack — the compositor's worst case — for a scenario (non-boundary sibling overlapping a scroll viewport) that's rare and self-servable.
2. **Scroll scene budget default.** 262,144 cells ≈ **16 MB of `Cell`s** (~64 B each) per big scroll region — a deliberate dial, possibly too generous as a default. **Recommendation:** ship `ScrollContentPresenter.SceneBudgetCells` (process-wide policy knob) at 262,144, measure memory + fallback-mode fidelity in the demo with a 10k-line log view, and revisit (likely downward) before T4 exit; the degraded viewport-mode fallback makes the number a performance/fidelity dial, not a correctness cliff — but its stroke/formatted-text drop rule (§3.9) means the dial *is* user-visible at the viewport edges, which strengthens the case for early virtualization.
3. **Should `Panel.Background` offer an opt-out (`BackgroundOcclusion` enum) for deliberate scrims?** **Recommendation: not in v1.** `FillOpaque` is the correct surface default (§3.7); scrim effects are a custom-`Render` `FillRectangle` away, and a property invites authors to rediscover the show-through hazard. Revisit if S8 theming hits a real case.

*(The former Open Question 2 — renegotiation ownership — is resolved and pinned as the S6 renegotiation/resize transactions in §4.)*

---

## Critique disposition

- **1 (P0, `BeginFigure` throws inside ambient figure): ACCEPTED.** `RenderContext.BeginFigure` now closes the ambient figure, opens the user figure, and its `RenderFigureScope.Dispose` reopens a fresh ambient figure; three-junction-group consequence documented (§2.5, §3.7, §6.4).
- **2 (P0, hidden ancestors don't hide descendant boundary layers): ACCEPTED.** Effective visibility added to the boundary walk's accumulated inputs; non-Visible ancestry forces `Clip = Rect.Empty` (layer retained, count stable); oracle-matrix test pinned (§3.8).
- **3 (P0, attached-property effects never fire): ACCEPTED.** Effects metadata split into per-type tables + a property-global lane on `UIProperty`; `GetEffects` merges both; static-ctor-before-first-use guarantee argued; Fork-A REQUIRES amended explicitly (§3.2, §4).
- **4 (P0, scroll fallback unimplementable as specced): ACCEPTED.** "Correct, slower" claim deleted. Fallback re-specified as a degraded mode: covered cell paths route through Drawing's `Push` (negative-translate-capable, correct gradients); uncovered paths (strokes/formatted text/content/shadows) skip when fully outside and drop with DEBUG diagnostic when straddling the negative edge; virtualization named the real fix (§3.9). (Tiled extent scenes were evaluated and rejected: tiles reintroduce the identical negative-origin straddle problem at every tile seam.)
- **5 (P1, promotion double-paint): ACCEPTED.** Promotion pinned as four steps (invalidate+re-raster old zone, rent+raster new, rebuild zone pointers, full recomposite); cost restated as two re-rasters + full recomposite (§3.7).
- **6 (P1, inherited changes bypass effects routing): ACCEPTED.** Fork-A REQUIRES now mandates the inherited-change carrier flow through an overridable virtual; `UIElement` runs the same dispatch; fan-out cost noted; R6 mapping updated (§3.2, §4, §5).
- **7 (P1, hit-test order contradicts zone-base rule): ACCEPTED.** Hit testing rewritten to walk the flat boundary-layer list topmost-first (exact reverse of `CollectLayers`), then intra-zone descent skipping boundary children — hit order now provably equals composite order; scroll handling falls out of the layer's effective offset (§3.10).
- **8 (P1, stale absolute-bounds cache): ACCEPTED.** The per-element absolute-bounds cache is removed entirely: boundary-level caches are refreshed unconditionally every render pass (§3.8); intra-zone hit testing reads live `Bounds`; `TranslateToWindow/ToLocal` are live O(depth) chain walks; S-input's fast check is a `HitTest` re-run (§3.10, §2.1).
- **9 (P1, zero-sized boundaries crash scene creation): ACCEPTED.** Pinned: empty-bounds boundary retains its previous scene (else rents 1×1) and publishes `Clip = Rect.Empty` — layer count stable across collapse/expand (§3.7).
- **10 (P1, boundary shadows / zone-edge overflow): ACCEPTED** (documentation route chosen, with grounding): zone-edge hard clip stated normatively; boundary-level shadows pinned as the parent zone's / S4 chrome's job (S8 decorator pattern noted); scene inflation recorded as a deferral with reasons (§3.7, §7).
- **11 (P1, stale scroll offset after content shrink): ACCEPTED.** Offsets re-coerced at end of arrange via `SetAndRaise`-style write; `AffectsComposite` fires only on actual movement (§2.4, §3.9).
- **12 (P1, Kitty image scroll claim wrong): ACCEPTED.** Caveat corrected: partial clip overlap = suppression (pop-in/out) on Kitty/iTerm2, pixel-crop on Sixel; fully-visible fragments slide smoothly; snap-in/out guidance added for S8 (§3.9).
- **13 (P1, renegotiation/resize recipe insufficient): ACCEPTED.** Pinned S6 transactions: renegotiate = replace `FrameRenderer` + fresh `SceneCompositor` + caps re-stamp + `InvalidateAll` + styling re-stamp in one tick; resize = fresh `SceneCompositor` + full redraw. Moved from Open Questions into §4's REQUIRES.
- **14 (P1, `Panel.Background` doesn't occlude): ACCEPTED.** Pinned: `Panel.Background` uses `FillOpaque` always (translucent brushes still frost; analysis showed even non-boundary zones composite over lower windows/base, so "only when boundary" would still leak); `overwrite: true` border interaction documented for S8; `DrawPanel` caveat noted on `RenderContext` (§2.3, §3.7, §6.9, §8 OQ3).
- **15 (P2, per-element `RenderContext` allocation): ACCEPTED.** One reusable instance per zone raster with re-pointed origin/size; capture prohibition documented (§2.5, §3.7).
- **16 (P2, sorted child order): ACCEPTED.** Cached (ZIndex, index) array per element, shared by painter and hit test, invalidated by ZIndex change ("z-order recollect") and collection mutation (§2.1, §3.7, §3.10).
- **17 (P2, `ColumnDefinition` mutation wiring): ACCEPTED.** Owner-grid backpointer; setters invalidate; one-collection ownership rule (§2.3).
- **18 (P2, negative margins): ACCEPTED.** Pinned: unsupported in v1, coerced to 0 with DEBUG diagnostic; deferral recorded (§2.2, §7).
- **19 (P2, reentrancy guards): ACCEPTED (narrowed, with grounding).** Render pass gets a full DEBUG read-only guard (`SetValue`/tree mutation/`Invalidate*` throw). Layout-pass mutation is *not* broadly guarded because `ApplyTemplate` requires tree mutation during own measure; pinned instead as "legal only from the element currently being measured/arranged", with the 16-pass cap + cycle diagnostic as the backstop (§2.1, §3.5, §3.7).
- **20 (P2, `LayoutUpdated` slip): ACCEPTED.** One bounded same-tick re-run when handlers dirty layout; residual work slips with DEBUG diagnostic (§3.5).
- **21 (P2, per-window Composite wording): ACCEPTED.** S6 sequence reworded: per-window layout/render, one concatenated layer span, ONE `Composite` (§4).
- **22 (P2, sticky promotion list-fade blowup): ACCEPTED.** Normative guidance (animate container opacity; S8 must not template per-item opacity/offset animations) + demotion-on-idle recorded as deferral (§3.7, §7).
- **23 (P2, nits): ACCEPTED.** Example fixed (raw int math in `Render`, documented as legal; `Fill` CLR wrapper added); budget math corrected to ≈ 16 MB at ~64 B/Cell; naming unified on `ScrollContentPresenter.SceneBudgetCells`; `TranslateToWindow/ToLocal` added to §2.1; `IsRenderBoundary`-false-after-promotion no-op documented on the property; Collapsed-skips-`ApplyTemplate` documented in §2.1/§3.3 and in the S8 contract (§4).