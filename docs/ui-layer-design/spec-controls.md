# S8 — Control Infrastructure & the v1 Control Catalog (Cursorial.UI)

**Status:** FINAL subsystem spec (post-critique revision), conforms to `/tmp/cursorial-ui-design/DECISIONS.md` (Forks A/B/C as amended). Vocabulary is the pinned set: `UIObject → UIElement → Control → Window`, `StyledProperty<T>`, `BindingPriority`, `Style`/`Setter`/`Selector`, `InteractionState`, `PseudoClassMapping`, `ITemplateContent`, `TemplateInstance`, `AccessText`. All types in namespace `Cursorial.UI`; framework source uses `using CellStyle = Cursorial.Output.Style;`.

---

## 1. Scope

**S8 owns:**
- `Control` — the templated-control base (per DECISIONS vocabulary, `Control` *is* the templated base; there is no separate `TemplatedControl` type). Template `StyledProperty`, template application lifecycle, `OnApplyTemplate`/`OnTemplateDetaching`, `GetTemplatePart<T>`, `[TemplatePart]` validation, `TemplatedParent` stamping (the mechanical half of the template barrier).
- `ControlTemplate` / `DataTemplate` object models and `ControlTemplate.Instantiate → TemplateInstance` (consuming Fork C's `ITemplateContent` and Fork B's `Detach()` retraction contract — S8 does **not** implement the instantiation engine or the style frames; it sequences them).
- `ContentControl` + `ContentPresenter` + the DataTemplate lookup chain (pinned jointly with S7, §3.4).
- `ItemsControl` pipeline: `ItemContainerGenerator` (with the virtualization seam), `ItemsPresenter`, `ItemTemplate`, `ItemsPanel`, `ItemContainerStyle` stance; `SelectionModel` and `ListBox`.
- The **v1 catalog**: `TextBlock`, `Label`, `Decorator`/`Border`, `ButtonBase`/`Button`/`RepeatButton`/`ToggleButton`, `CheckBox`/`RadioButton`, `TextBox` (single-line), `ScrollViewer`/`ScrollBar`, `ItemsControl`/`ListBox`, `Menu`/`MenuItem`/`ContextMenu`/`Separator`, `TabControl`/`TabItem`, `ProgressBar`, `ToolTip`/`ToolTipService`, the **Window chrome template** (the template only — `Window` itself is S4's).
- The **access-key production pipeline** (requirement 6): the `AccessText` model, its three pinned producers (§3.8), `AccessTextPresenter` rendering, `Label`, and per-control extraction/registration/invocation.
- The default theme (`Themes/Default`): per-control control-theme styles + `ThemeVariant`-tiered resources.

**S8 explicitly does not own:**
- Window/popup/layer mechanics, modal scopes, light-dismiss, z-order, window dragging (S4 — consumed via `IPopupHost`/window commands).
- Focus manager, tab/directional navigation, the access-key *manager*, input routing/capture, `InteractionState` plumbing for `:focus*`/`:pointerover`/`:active-window`/`:access-keys` (S3 — consumed; S8 controls *do* write control-semantic pseudo-state: `:pressed`, `:checked`, `:selected`, …).
- Layout (`Measure`/`Arrange`, panels), the element render model, scenes/compositing, scrolling mechanics, frame clock, timers, caret emission (S1 — consumed; the **scene-granularity contract** S8 depends on is pinned in §3.5/§4).
- The property system, styling engine, binding engine, XAML loader (Forks A/B/C — consumed).
- Virtualization (deferred; the generator seam is designed for it, §3.6; the raster-band design in §3.9 removes raster cost from the deferral equation — layout cost is what eventually forces it).

---

## 2. Public API sketch

### 2.1 Control base + template machinery

```csharp
namespace Cursorial.UI;

/// Shared text properties (WPF TextElement kinship). Inherited attached properties;
/// AddOwner'd by Control and TextBlock.
public static class TextElement
{
    public static readonly AttachedProperty<IBrush?> ForegroundProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, IBrush?>("Foreground",
            defaultValue: null, inherits: true,
            effects: PropertyEffects.AffectsRender);                    // null ⇒ Brushes.Default at draw
    public static readonly AttachedProperty<TextAttributes> TextAttributesProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, TextAttributes>("TextAttributes",
            inherits: true, effects: PropertyEffects.AffectsRender);
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class TemplatePartAttribute(string name, Type type) : Attribute
{
    public string Name { get; } = name;          // convention: "PART_*"
    public Type Type { get; } = type;
    public bool IsRequired { get; init; }        // default false: controls must degrade gracefully
}

public class Control : UIElement
{
    public static readonly StyledProperty<ControlTemplate?> TemplateProperty =
        UIProperty.Register<Control, ControlTemplate?>(nameof(Template),
            effects: PropertyEffects.AffectsMeasure);                   // change ⇒ re-expand at next measure
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        UIProperty.Register<Control, IBrush?>(nameof(Background),       // NOT inherited (Fork B §6.5)
            effects: PropertyEffects.AffectsRender);
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Control>();             // inherits
    public static readonly StyledProperty<Pen?> BorderPenProperty =
        UIProperty.Register<Control, Pen?>(nameof(BorderPen),
            effects: PropertyEffects.AffectsRender);                    // nullity-escalation pattern (§3.5):
                                                                        // null↔non-null escalates to InvalidateMeasure
                                                                        // imperatively; restyle (focus pen swap) = render-only
    public static readonly StyledProperty<Margins> PaddingProperty =
        UIProperty.Register<Control, Margins>(nameof(Padding),
            effects: PropertyEffects.AffectsMeasure);

    public ControlTemplate? Template { get => GetValue(TemplateProperty); set => SetValue(TemplateProperty, value); }

    /// Theme-style lookup key (DefaultStyleKey equivalent). Default: runtime type.
    protected virtual Type StyleKey => GetType();

    /// Expands the pending template NOW (normally invoked by S1 at the head of Measure).
    /// Returns true if expansion happened on this call. Re-entrant Template sets from
    /// OnApplyTemplate are deferred to the next measure behind a guard (§3.1).
    public bool ApplyTemplate();
    protected virtual void OnApplyTemplate() { }                        // parts exist; called before MeasureOverride
    protected virtual void OnTemplateDetaching(TemplateInstance old) { } // unhook part handlers BEFORE Detach (§3.1/§3.3)
    protected T? GetTemplatePart<T>(string name) where T : UIElement;   // template namescope only; null if absent
    protected internal TemplateInstance? TemplateInstance { get; }
}

public static class NameScopeExtensions
{
    /// Runtime counterpart of the X4 generated x:Name fields: resolves in the element's
    /// document namescope; throws InvalidOperationException naming scope+name when absent.
    public static T RequireControl<T>(this UIElement root, string name) where T : UIElement;
}
```

```csharp
public sealed class ControlTemplate
{
    public ControlTemplate();
    public ControlTemplate(Type targetType, Func<TemplateBuildContext, UIElement> build); // code-first sugar

    public Type? TargetType { get; set; }            // XAML: enables parse-time setter folding (Fork C)
    public ITemplateContent? Content { get; set; }   // typed ITemplateContent ⇒ XAML defers automatically
    public Styles Styles { get; }                    // template-scoped styles, armed at Template layer (Fork B)
    public bool IsSealed { get; }
    public void Seal();                              // freezes; validates TargetType/Content non-null

    /// Build → stamp TemplatedParent on the built subtree (template barrier; foreign non-null
    /// TemplatedParent throws — shared-subtree misuse, §3.2) → arm Styles → return the retraction
    /// handle. The ONLY entry point; called by Control.ApplyTemplate.
    public TemplateInstance Instantiate(Control owner);
}

// TemplateInstance & ITemplateContent are the DECISIONS-pinned shapes (Fork B graft / Fork C):
//   ITemplateContent { object Build(in TemplateBuildContext ctx); }
//   TemplateInstance { UIElement Root; INameScope NameScope; void Detach(); }
// Detach() = cookie/frame retraction through the store (never set-back) + TemplateBinding teardown
// + presenter auto-alias observer teardown (§2.2).

public class DataTemplate
{
    public Type? DataType { get; set; }              // implicit-template key (resource scope, §3.4)
    public ITemplateContent? Content { get; set; }
    public UIElement Build(object? data);            // fresh namescope; sets DataContext = data on root;
                                                     // TemplatedParent stays null (app-styleable, §3.2)
}
```

#### AccessText (requirement 6 — the coherent production pipeline; full rules in §3.8)

```csharp
public readonly record struct AccessText(string Text, char Key, int KeyIndex)
{
    public bool HasKey => KeyIndex >= 0;

    /// "_File" → ("File",'F',0); "__" = literal '_'. The FIRST underscore whose following
    /// character is a BMP letter or digit becomes the mnemonic; an underscore before anything
    /// else (non-BMP, combining cluster, punctuation) stays literal with NO key — deterministic
    /// rejection, never an exception. Key matching is simple-case-folded (char.ToLowerInvariant).
    public static AccessText Parse(string textWithUnderscores);
    public static explicit operator AccessText(string s) => Parse(s);   // EXPLICIT: parsing is lossy
}
```

`AccessText` has exactly **three producers, one model** (DECISIONS Fork C "two producers" extended by one runtime producer; all three call the single `Parse`):

1. **Type-driven parse-time folding (XAML/generator):** a string literal assigned to a property statically typed `AccessText` is folded by the loader (and by the X4 generator) — e.g. `AccessTextPresenter.Text`, `Label`-internal plumbing. Parallel to `ITemplateContent`-driven deferral: the *property type* is the contract.
2. **Metadata-flag folding for object-typed mnemonic slots (XAML/generator):** Fork A per-type metadata gains a boolean flag **`ParsesAccessKeyLiterals`** (Fork A REQUIRES, §4). It is set via per-type metadata override on exactly: `ButtonBase.ContentProperty` (covers Button/RepeatButton/ToggleButton/CheckBox/RadioButton), `MenuItem.HeaderProperty`, `TabItem.HeaderProperty`, `Label.ContentProperty`. The loader folds string literals to `AccessText` only when the resolved metadata **for the instance's runtime type** carries the flag. It is **not** set on `ContentControl`/`ListBoxItem`/`TextBlock` — data-shaped strings (`snake_case_file.txt`, paths, identifiers) are never mangled.
3. **Runtime extraction (`GetAccessText()`, code-first + bound-string parity):** `ContentControl.GetAccessText()` returns `AccessText` content as-is, **or parses string content iff the runtime type's `ContentProperty` metadata carries the flag** (same single source of truth as rule 2 — `button.Content = "_Save"` works). `MenuItem`/`TabItem` override to read `Header` under the same rule.

```csharp
public sealed class AccessTextPresenter : UIElement   // leaf renderer; never templated
{
    public static readonly StyledProperty<AccessText> TextProperty =
        UIProperty.Register<AccessTextPresenter, AccessText>(nameof(Text), effects: PropertyEffects.AffectsMeasure);
    public static readonly StyledProperty<TextAttributes> KeyAttributesProperty =        // cue style; themes override
        UIProperty.Register<AccessTextPresenter, TextAttributes>(nameof(KeyAttributes),
            defaultValue: TextAttributes.Underline, effects: PropertyEffects.AffectsRender);
    // Renders Text single-line; applies KeyAttributes to the KeyIndex grapheme cell when the
    // inherited AccessKeyManager.ShowAccessKeysProperty (S3) is true. AffectsRender on that property.
}

/// WPF-kinship label: mnemonic-bearing caption that focuses a target on access-key invocation.
/// Never focusable itself (Focusable = false, IsTabStop = false).
public class Label : ContentControl
{
    public static readonly StyledProperty<UIElement?> TargetProperty =
        UIProperty.Register<Label, UIElement?>(nameof(Target));   // set via {Binding ElementName=…};
                                                                  // null ⇒ S3 FocusNavigator.Next(this)
    // ContentProperty metadata override: ParsesAccessKeyLiterals = true.
    // OnAccessKey → (Target ?? next-focusable).Focus().
}
```

### 2.2 Content pipeline

```csharp
public class ContentControl : Control
{
    public static readonly StyledProperty<object?> ContentProperty =
        UIProperty.Register<ContentControl, object?>(nameof(Content), effects: PropertyEffects.AffectsMeasure);
    public static readonly StyledProperty<DataTemplate?> ContentTemplateProperty =
        UIProperty.Register<ContentControl, DataTemplate?>(nameof(ContentTemplate), effects: PropertyEffects.AffectsMeasure);

    /// Mnemonic extraction (§2.1 producer 3, §3.8). Base: AccessText content as-is; string content
    /// parsed iff this runtime type's ContentProperty metadata sets ParsesAccessKeyLiterals.
    protected virtual AccessText? GetAccessText();
}

public class HeaderedContentControl : ContentControl
{
    public static readonly StyledProperty<object?> HeaderProperty =
        UIProperty.Register<HeaderedContentControl, object?>(nameof(Header), effects: PropertyEffects.AffectsMeasure);
    public static readonly StyledProperty<DataTemplate?> HeaderTemplateProperty =
        UIProperty.Register<HeaderedContentControl, DataTemplate?>(nameof(HeaderTemplate), effects: PropertyEffects.AffectsMeasure);
}

public class HeaderedItemsControl : ItemsControl       // MenuItem's base
{
    public static readonly StyledProperty<object?> HeaderProperty = …;          // AffectsMeasure
    public static readonly StyledProperty<DataTemplate?> HeaderTemplateProperty = …;
}

public sealed class ContentPresenter : UIElement
{
    public static readonly StyledProperty<object?> ContentProperty = …;
    public static readonly StyledProperty<DataTemplate?> ContentTemplateProperty = …;
    public static readonly StyledProperty<bool> RecognizesAccessKeyProperty = …;   // WPF kinship; default false
    public UIElement? Child { get; }                  // realized visual (read-only; diagnostic surface)
}
```

**Auto-aliasing (pinned; closes original Q1).** Inside a template, when `Content`/`ContentTemplate` have **no frame or local entry** (Fork A `IsSet == false`), the presenter behaves as if `TemplateBinding`'d to `TemplatedParent.Content`/`.ContentTemplate`. Mechanism is normative: a **read-through fallback, never an installed binding** (an installed binding would create a frame and flip `IsSet`, destroying its own condition). While aliasing is active the presenter subscribes a typed property-changed observer on the templated parent for the aliased properties — **no store entry is created on the presenter** — and re-realizes on notification; each notification re-checks `IsSet` so an explicit value arriving later wins and silences the alias. Observer lifetime = template-instance lifetime, torn down in `TemplateInstance.Detach()`. Residual Fork A confirmations recorded as open question 1.

**`RecognizesAccessKey`.** When true and the resolved content is a `string`, the presenter renders it as an `AccessTextPresenter` over `AccessText.Parse(s)` (covers code-first strings *and bound strings*); when false, strings render literal via the §3.4 fallback. `AccessText`-typed content renders as `AccessTextPresenter` regardless of the flag. The default templates of every `ParsesAccessKeyLiterals`-flagged control set it true, keeping registration and rendering in lock-step; custom template authors own that pairing (documented).

### 2.3 Items pipeline + selection

```csharp
public class ItemsControl : Control
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty = …;        // binding target (R2)
    public static readonly StyledProperty<DataTemplate?> ItemTemplateProperty = …;
    public static readonly StyledProperty<ITemplateContent?> ItemsPanelProperty = …;    // builds a Panel; default vertical StackPanel
    public static readonly StyledProperty<Style?> ItemContainerStyleProperty = …;       // assigned as container.Style (Explicit layer)
    // v1 policy: runtime change to ItemTemplate / ItemsPanel / ItemContainerStyle ⇒ Reset (regenerate all containers).

    public ItemCollection Items { get; }              // direct-mode list; throws if ItemsSource set (WPF rule)
    public int ItemCount { get; }
    public ItemContainerGenerator ContainerGenerator { get; }

    protected virtual UIElement GetContainerForItemOverride() => new ContentPresenter();
    protected virtual bool IsItemItsOwnContainer(object? item) => item is UIElement;
    protected virtual void PrepareContainerForItemOverride(UIElement container, object? item, int index);
    protected virtual void ClearContainerForItemOverride(UIElement container, object? item);   // §3.6 mandatory set
    protected virtual void OnItemsChanged(ItemsChangedEventArgs e);
}

/// Index↔container map. v1 realizes eagerly; the API is range-based so a future virtualizing
/// panel can drive it without reshaping ItemsControl (the virtualization seam, §3.6).
public sealed class ItemContainerGenerator
{
    public int Count { get; }
    public UIElement Realize(int index);              // create+prepare; idempotent
    public void Unrealize(int index);                 // v1: discard, via the §3.6 normative retraction sequence
    public UIElement? ContainerFromIndex(int index);
    public int IndexFromContainer(UIElement container);
    public object? ItemFromContainer(UIElement container);
    public event EventHandler<ContainersChangedEventArgs>? ContainersChanged;
    // Subscription discipline: ItemsPresenter subscribes on tree attach, unsubscribes on detach —
    // a re-templated-away presenter must not survive on the control-lifetime generator (§3.6).
}

public sealed class ItemsPresenter : UIElement
{ /* finds its ItemsControl via TemplatedParent, instantiates ItemsPanel, parents realized containers.
     ContainersChanged subscription lifetime = own attach lifetime. */ }

public enum SelectionMode { Single, Extended }

/// Reusable index-based selection (ListBox, TabControl). Pure model: no element references.
public sealed class SelectionModel
{
    public SelectionMode Mode { get; set; } = SelectionMode.Single;
    public int SelectedIndex { get; set; }            // -1 = none; setter = Select(index)
    public IReadOnlyList<int> SelectedIndexes { get; }// sorted; allocation-stable snapshot semantics
    public int AnchorIndex { get; set; }
    public void Select(int index);                    // replace selection
    public void Toggle(int index);
    public void SelectRangeFromAnchor(int index);     // Extended only
    public void SelectAll(int count); public void Clear();
    public void ItemsInserted(int index, int count);  // collection fixups (shift indexes)
    public void ItemsRemoved(int index, int count); public void Reset();
    public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;     // adds/removes as index lists
}

public class ListBox : ItemsControl
{
    public static readonly StyledProperty<SelectionMode> SelectionModeProperty = …;
    public static readonly DirectProperty<ListBox, int> SelectedIndexProperty = …;       // two-way bindable
    public static readonly DirectProperty<ListBox, object?> SelectedItemProperty = …;    // two-way bindable
    public IReadOnlyList<object?> SelectedItems { get; }                                 // read-only view (v1)
    public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;
    public event EventHandler<ItemActivatedEventArgs>? ItemActivated;   // double-click OR Enter on focused container
    protected override UIElement GetContainerForItemOverride() => new ListBoxItem();
    // Template parts: [TemplatePart("PART_ScrollViewer", typeof(ScrollViewer))]
}

public class ListBoxItem : ContentControl
{
    public static readonly StyledProperty<bool> IsSelectedProperty = …;   // two-way by default
    static ListBoxItem() { PseudoClassMapping.Register<ListBoxItem>(IsSelectedProperty, ":selected"); }
}
```

Arg shapes `ItemCollection`, `ItemsChangedEventArgs`, `ContainersChangedEventArgs`, `SelectionChangedEventArgs`, `ItemActivatedEventArgs`, `ClickEventArgs`, `ScrollEventArgs`, `ScrollChangedEventArgs`, `AccessKeyEventArgs` are S8-owned and shaped in their C-phases (recorded; no cross-subsystem contract hangs on them).

### 2.4 Catalog signatures (abbreviated; full behavior in §3.9)

```csharp
public class TextBlock : UIElement
{
    public static readonly StyledProperty<string?> TextProperty = …;                 // AffectsMeasure; NEVER access-key-folded
    public static readonly StyledProperty<string?> MarkupProperty = …;               // BBCode markup incl. [brush=…]; wins over Text
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<TextBlock>();
    public static readonly StyledProperty<TextAttributes> TextAttributesProperty = TextElement.TextAttributesProperty.AddOwner<TextBlock>();
    public static readonly StyledProperty<TextWrapping> TextWrappingProperty = …;    // NoWrap default
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty = …;
    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty = …;    // ellipsis on NoWrap overflow
}

public class Decorator : UIElement { public UIElement? Child { get; set; } }        // logical+visual child

public class Border : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty = …;           // AffectsRender
    public static readonly StyledProperty<Pen?> BorderPenProperty = …;               // AffectsRender + nullity escalation (§3.5)
    public static readonly StyledProperty<Margins> PaddingProperty = …;              // AffectsMeasure
    public static readonly StyledProperty<string?> TitleProperty = …;                // AffectsRender + presence escalation (§3.5);
                                                                                     // DrawTitledBox; the GroupBox story
    public static readonly StyledProperty<TitlePosition> TitlePositionProperty = …;  // AffectsRender
    public static readonly StyledProperty<bool> OccludesProperty = …;                // FillOpaque + DrawBox(overwrite:true); AffectsRender
}

public enum ClickMode { Release, Press }

public abstract class ButtonBase : ContentControl
{
    public static readonly StyledProperty<ClickMode> ClickModeProperty = …;
    public static readonly StyledProperty<ICommand?> CommandProperty = …;            // System.Windows.Input.ICommand (BCL)
    public static readonly StyledProperty<object?> CommandParameterProperty = …;
    public static readonly DirectProperty<ButtonBase, bool> IsPressedProperty = …;   // read-only; → :pressed via PseudoClassSet.Set
    public event EventHandler<ClickEventArgs>? Click;
    protected virtual void OnClick();
    protected override bool IsEnabledCore => base.IsEnabledCore && _canExecute;      // effective-enabled lattice (§3.9)
    // ContentProperty metadata override: ParsesAccessKeyLiterals = true.
}

public class Button : ButtonBase
{
    public static readonly StyledProperty<bool> IsDefaultProperty = …;   // Enter in window scope → click; :default
    public static readonly StyledProperty<bool> IsCancelProperty = …;   // Esc in window scope → click
}

public class RepeatButton : ButtonBase
{   // ClickMode.Press default; repeats while held
    public static readonly StyledProperty<TimeSpan> DelayProperty = …;      // default 400 ms
    public static readonly StyledProperty<TimeSpan> IntervalProperty = …;   // default 60 ms
}

public class ToggleButton : ButtonBase
{
    public static readonly StyledProperty<bool?> IsCheckedProperty = …;     // two-way by default
    public static readonly StyledProperty<bool> IsThreeStateProperty = …;
    public event EventHandler? Checked, Unchecked, Indeterminate;
    static ToggleButton()
    {   // bool? → :checked / :indeterminate / (neither) — the multi-class projection overload,
        // already pinned in the canonical Fork B proposal §2.4 (no contract delta)
        PseudoClassMapping.Register<ToggleButton, bool?>(IsCheckedProperty,
            static v => v switch { true => ":checked", null => ":indeterminate", false => null },
            [":checked", ":indeterminate"]);
    }
}

public class CheckBox : ToggleButton { }                                    // template/theme differences only

public class RadioButton : ToggleButton
{
    public static readonly StyledProperty<string?> GroupNameProperty = …;
    // Checking one unchecks group peers; group = same logical parent when GroupName null,
    // else all same-named radios within the Window (§3.9).
}

public class TextBox : Control
{
    public static readonly StyledProperty<string> TextProperty = …;          // two-way by default; AffectsMeasure;
                                                                             // source push PER CHANGE (pinned, §3.9 + S2 REQUIRES)
    public static readonly StyledProperty<bool> IsReadOnlyProperty = …;      // → :readonly
    public static readonly StyledProperty<int> MaxLengthProperty = …;        // 0 = unlimited
    public static readonly StyledProperty<string?> PlaceholderProperty = …;  // shown when empty+unfocused; :empty
    public int CaretIndex { get; set; }              // char offset, pinned to grapheme-cluster boundaries
    public int SelectionStart { get; set; } public int SelectionLength { get; set; }
    public string SelectedText { get; set; }
    public event EventHandler? TextChanged;
    public void SelectAll(); public void Clear();
    // [TemplatePart("PART_TextPresenter", typeof(TextPresenter), IsRequired = true)]
}

public sealed class TextPresenter : UIElement
{ /* renders text+selection+placeholder for its TemplatedParent TextBox; owns horizontal scroll
     offset and caret publication via S1's caret service; calls ITerminalCaretService.Clear on
     detach (§3.9-TextBox). */ }

public enum ScrollBarVisibility { Disabled, Auto, Hidden, Visible }
public enum Orientation { Horizontal, Vertical }

public class ScrollViewer : ContentControl
{
    public static readonly StyledProperty<ScrollBarVisibility> HorizontalScrollBarVisibilityProperty = …; // Disabled default
    public static readonly StyledProperty<ScrollBarVisibility> VerticalScrollBarVisibilityProperty = …;   // Auto default
    public static readonly DirectProperty<ScrollViewer, int> HorizontalOffsetProperty = …;  // cells; hand-routed composite
    public static readonly DirectProperty<ScrollViewer, int> VerticalOffsetProperty = …;    // refresh; NOT storyboard-animatable (§3.9)
    public Size Extent { get; } public Size Viewport { get; }
    public void ScrollBy(int columns, int rows);
    public void EnsureVisible(in Rect childRect);     // childRect in content coordinates
    public event EventHandler<ScrollChangedEventArgs>? ScrollChanged;
    // Parts: [TemplatePart("PART_HorizontalScrollBar", typeof(ScrollBar))],
    //        [TemplatePart("PART_VerticalScrollBar", typeof(ScrollBar))],
    //        [TemplatePart("PART_ContentHost", typeof(ScrollContentPresenter), IsRequired = true)]
}

public sealed class ScrollContentPresenter : ContentPresenter
{ /* the S1 viewport-layer seam: content renders into a BANDED sub-scene (viewport + slack, §3.9)
     composited with CompositeParameters { Offset = anchor − scroll, Clip = viewport } — clips
     EVERYTHING incl. formatted text/fragments (drawing-core "robust route (a)"); scrolling within
     the band = re-composite, never re-raster; crossing the band = one band re-raster (re-anchor). */ }

public class ScrollBar : Control
{
    public static readonly StyledProperty<Orientation> OrientationProperty = …;   // → :horizontal / :vertical
    public static readonly DirectProperty<ScrollBar, int> ValueProperty = …;
    public static readonly StyledProperty<int> MinimumProperty = …, MaximumProperty = …;
    public static readonly StyledProperty<int> ViewportSizeProperty = …;
    public static readonly StyledProperty<int> SmallChangeProperty = …;           // 1
    public static readonly StyledProperty<int> LargeChangeProperty = …;           // viewport
    public event EventHandler<ScrollEventArgs>? Scroll;
    // Parts: PART_LineUpButton/PART_LineDownButton (RepeatButton, optional), PART_Track (UIElement, required).
    static ScrollBar() { PseudoClassMapping.Register<ScrollBar, Orientation>(OrientationProperty,
        static o => o == Orientation.Horizontal ? ":horizontal" : ":vertical", [":horizontal", ":vertical"]); }
}

public class Menu : ItemsControl { }                  // horizontal bar; registers as window main menu with S3
public class MenuItem : HeaderedItemsControl          // Header is AccessText-bearing (metadata flag set)
{
    public static readonly StyledProperty<ICommand?> CommandProperty = …;
    public static readonly StyledProperty<object?> CommandParameterProperty = …;
    public static readonly StyledProperty<string?> InputGestureTextProperty = …;  // display only (v1)
    public static readonly StyledProperty<bool> IsCheckableProperty = …;
    public static readonly StyledProperty<bool> IsCheckedProperty = …;            // → :checked
    public static readonly DirectProperty<MenuItem, bool> IsSubmenuOpenProperty = …;  // → :open (PseudoClassSet.Set)
    public static readonly DirectProperty<MenuItem, bool> IsHighlightedProperty = …;  // → :highlighted (PseudoClassSet.Set)
    public event EventHandler<ClickEventArgs>? Click;
    protected override AccessText? GetAccessText();   // reads Header, not Content
}
public sealed class ContextMenu : ItemsControl
{
    public void Open(UIElement target, CellPosition? position = null);    // via S4 IPopupHost
    public void Close();
    // Attached: ContextMenu.MenuProperty on UIElement; right-click / Menu key opens it (router default).
}
public sealed class Separator : Control { }

public class TabControl : ItemsControl
{
    public static readonly DirectProperty<TabControl, int> SelectedIndexProperty = …;
    public static readonly DirectProperty<TabControl, object?> SelectedItemProperty = …;
    public static readonly StyledProperty<DataTemplate?> ContentTemplateProperty = …;     // for non-TabItem items
    protected override UIElement GetContainerForItemOverride() => new TabItem();
    // Parts: PART_TabStrip (ItemsPresenter host), PART_ContentHost (ContentPresenter, required).
}
public class TabItem : HeaderedContentControl
{
    public static readonly StyledProperty<bool> IsSelectedProperty = …;   // → :selected
    protected override AccessText? GetAccessText();   // reads Header (metadata flag set on HeaderProperty for TabItem)
}

public class ProgressBar : Control
{
    public static readonly StyledProperty<double> MinimumProperty = …, MaximumProperty = …, ValueProperty = …;
    public static readonly StyledProperty<bool> IsIndeterminateProperty = …;      // → :indeterminate
    public static readonly StyledProperty<IBrush?> FillProperty = …;              // bar brush (gradient spans track)
    public static readonly StyledProperty<int> IndeterminateOffsetProperty =
        UIProperty.Register<ProgressBar, int>(nameof(IndeterminateOffset),
            effects: PropertyEffects.AffectsComposite);    // the storyboard target — store-routed, never a raw layer poke
}

public sealed class ToolTip : ContentControl { }      // popup content host; never focusable, hit-test transparent
public static class ToolTipService
{
    public static readonly AttachedProperty<object?> TipProperty = …;             // any content; string common
    public static readonly AttachedProperty<TimeSpan> InitialDelayProperty = …;   // 500 ms
    public static readonly AttachedProperty<bool?> ShowOnFocusProperty = …;       // null = auto: !MouseCapabilities.Motion
}
```

### 2.5 Consumer example

```xml
<!-- SaveDialog.xaml -->
<Window xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml"
        x:Class="DemoApp.SaveDialog" Title="Save File" Width="52" Height="18">
  <Window.Styles>
    <Style Selector="Button.primary"><Setter Property="Background" Value="{DynamicResource AccentBrush}"/></Style>
    <Style Selector="Button#save">
      <Style.When><DataCondition Binding="{Binding IsValid}" Value="False"/></Style.When>
      <Setter Property="IsEnabled" Value="False"/>
    </Style>
  </Window.Styles>
  <DockPanel>
    <Menu DockPanel.Dock="Top">
      <MenuItem Header="_File">    <!-- folded: MenuItem.Header metadata carries ParsesAccessKeyLiterals -->
        <MenuItem Header="_Save" Command="{Binding SaveCommand}" InputGestureText="Ctrl+S"/>
        <Separator/>
        <MenuItem Header="E_xit" Command="{Binding ExitCommand}"/>
      </MenuItem>
    </Menu>
    <Border DockPanel.Dock="Top" Title="Target" Padding="1">
      <StackPanel>
        <Label Content="File _name:"/>   <!-- Label: folds + targets next focusable (the TextBox below) -->
        <TextBox x:Name="name" Text="{Binding FileName}" Placeholder="untitled.txt"
                 ToolTipService.Tip="Name within the current directory"/>
        <CheckBox Content="_Overwrite existing" IsChecked="{Binding Overwrite}"/>
      </StackPanel>
    </Border>
    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right">
      <Button x:Name="save" Classes="primary" Content="_Save" IsDefault="True" Command="{Binding SaveCommand}"/>
      <Button Content="_Cancel" IsCancel="True" Margin="1,0,0,0"/>
    </StackPanel>
    <ListBox ItemsSource="{Binding RecentFiles}" SelectedItem="{Binding PickedRecent}">
      <ListBox.ItemTemplate>
        <!-- ListBox items: NO folding — "snake_case_file.txt" renders verbatim -->
        <DataTemplate><TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis"/></DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</Window>
```

```csharp
public partial class SaveDialog : Window
{
    public SaveDialog(SaveViewModel vm)
    {
        XamlLoader.LoadComponent(this);
        DataContext = vm;
        this.RequireControl<TextBox>("name").Focus();   // §2.1 namescope helper (or the X4 generated field)
    }
}
```

This example works by construction: `TextBox.Text` pushes to source **per change** (pinned, §3.9), so the `When`-driven `IsEnabled` on `#save` reacts per keystroke.

---

## 3. Mechanics

### 3.1 Template application lifecycle — **measure-time expansion**

Decision: templates expand **lazily at measure time**, not at tree-attach.

- `UIElement.Measure` (S1) calls `control.ApplyTemplate()` before `MeasureOverride`. Sequence inside `ApplyTemplate` when dirty:
  1. **Detach old**: `OnTemplateDetaching(oldInstance)` first (control unhooks part event handlers, timers, `CanExecuteChanged`-style external hooks — the **"unhook before rewire"** convention, §3.3) → `oldInstance.Detach()` (Fork B retracts template-scoped style frames + `TemplateBinding`s + auto-alias observers by cookie; store-owned promotion, never set-back) → S8 removes `oldInstance.Root` as visual child (subtree detach retracts everything else bottom-up: DynamicResource subscriptions, `When` watchers — the S7/Fork B detach contracts).
  2. Resolve template: `GetValue(TemplateProperty)` (a theme control-style setter normally supplies it at the Style slot; app/local values override per the lattice). `null` ⇒ no visual child, desired size = padding+border, one-time diagnostic.
  3. `template.Instantiate(this)`: `Content.Build(new TemplateBuildContext { TemplatedParent = this, NameScope = fresh })` → post-build walk stamps `TemplatedParent = this` on every element with `TemplatedParent == null`; an element with a **foreign non-null** `TemplatedParent` throws (a template returned a shared/aliased subtree across instantiations — fail loudly; nested controls' parts are exempt automatically: they don't exist yet and stamp themselves at their own expansion) → arm `template.Styles` at the Template layer against the new subtree → return `TemplateInstance`.
  4. Validate `[TemplatePart]` declarations (§3.3) — **immediately after `Instantiate`, before visual attach** (single pinned timing; `GetTemplatePart` resolves in `TemplateInstance.NameScope`, which needs no attachment).
  5. Set `instance.Root` as the **visual** child (template parts are never logical children — DataContext and selector descendant-matching flow through the logical tree; parts see the templated parent's DataContext via inheritance through the visual link, matching WPF).
  6. `OnApplyTemplate()` — parts exist. A re-entrant `Template` set from inside `OnApplyTemplate` does **not** recurse: a guard records the dirty state and expansion defers to the next measure (WPF behavior).
  7. Proceed to `MeasureOverride` — parts are measurable this same pass.

Rationale: (a) **frame coherence holds** — a `Template` change during frame N's input drain dirties measure; expansion happens in frame N's layout, before render; (b) containers generated for never-measured items cost nothing; (c) `OnApplyTemplate` ordering matches WPF/Avalonia muscle memory. Documented caveat: `GetTemplatePart` returns null before first measure; call `ApplyTemplate()` explicitly if needed earlier (tests, imperative wiring).

### 3.2 Template barrier + namescopes

- Every `ControlTemplate`-built element carries `TemplatedParent != null` ⇒ the styling engine skips it except via `/template/` (Fork B invariant; S8's job is only accurate stamping — including the foreign-parent throw in §3.1 step 3).
- **`DataTemplate`-built elements get `TemplatedParent = null`** — data-template content is app content and must be app-styleable. This is a deliberate WPF deviation (WPF sets ContentPresenter as templated parent), pinned because the hybrid styling model has no other way to reach data-templated elements (no `DataTemplate.Triggers`).
- Names: `x:Name` inside a `ControlTemplate` registers only in `TemplateInstance.NameScope`; `GetTemplatePart` resolves there and nowhere else. Document namescopes never see part names; parts never see document names (Fork C §3.6). `DataTemplate.Build` creates a fresh throwaway namescope (ElementName bindings inside resolve template-locally).

### 3.3 Template parts: convention + validation stance

- Naming convention: **`PART_` prefix** for any element the control class reaches into; declared via `[TemplatePart(name, type, IsRequired)]` on the control class (inherited).
- **Seal-time validation is impossible** (`ITemplateContent` is an opaque deferred slice until `Build`) — so the normative check is **apply-time, immediately after `Instantiate`, before visual attach** (§3.1 step 4):
  - declared part present but wrong type → `InvalidOperationException` always (names template `TargetType`, part name, expected/actual types — it would crash later anyway; fail loudly and early);
  - `IsRequired` part missing → `InvalidOperationException` always (deterministic across Debug/Release);
  - optional part missing → fine; control code must null-check and degrade (e.g. ScrollBar without arrow buttons).
- **Unhook before rewire** (normative convention): any control that wires event handlers / timers / service registrations to template parts in `OnApplyTemplate` must unhook them in `OnTemplateDetaching` (§3.1 step 1). ScrollViewer (§3.9) is the reference implementation.
- Parse-time assist (not normative): Fork C's X4 generator cross-checks `x:Name` sets in template slices against `[TemplatePart]` of the `TargetType` and reports Roslyn diagnostics. Recorded as a Fork C deliverable, not a gate.
- `TargetType` mismatch (`Instantiate` on a control not assignable to `TargetType`) throws at apply.

### 3.4 ContentPresenter + the DataTemplate lookup chain (pinned with S7)

Presenter realization (runs at measure when content/template dirty):

1. **Explicit template**: `ContentTemplate` (after auto-aliasing, §2.2) → `Build(content)`.
2. **Implicit template**: walk the resource scope — presenter → **templated-parent hop** (a template part has no logical ancestors: when `LogicalParent == null && TemplatedParent != null`, the walk hops to the templated parent and continues up *its* logical chain — explicit, pinned jointly with S7) → logical ancestors → `Window` → `Application` → built-in theme (S7's single walk, theme-variant-aware) — probing at each scope `DataTemplateKey(t)` for `t` = runtime type, then each base class up to but excluding `object`. First hit wins. (Interfaces: deferred, recorded — needs deterministic ordering rules.)
3. **Element passthrough**: `Content is UIElement e` → present `e` directly (it becomes a logical child of the templated parent ContentControl, visual child of the presenter).
4. **AccessText**: `Content is AccessText` → generate `AccessTextPresenter`. Additionally, when `RecognizesAccessKey` is true and content is a `string`, present `AccessTextPresenter` over `AccessText.Parse(s)` (§2.2).
5. **Fallback**: `TextBlock { Text = Convert.ToString(content, CurrentCulture) }`.

Realized roots from (1)/(2) get `DataContext = content`. Content change with same template type re-uses the realized subtree and just updates `DataContext` (cheap list refresh); template identity change rebuilds. `DataTemplateSelector` — deferred (the `When`-style conditional template story should be designed once, with S7).

### 3.5 Invalidation & notification flow — under the **pinned scene-granularity model**

Controls never touch Scene/CellBuffer state directly (invariant 2). All flow is:

```
SetValue/binding/style frame → Fork A store → PropertyEffects metadata
  AffectsMeasure   → InvalidateMeasure (S1 walks AffectsParentMeasure as registered)
  AffectsRender    → S1 invalidates the element's OWNING SCENE (whole-scene; see model below)
  AffectsComposite → S1 refreshes the element layer's CompositeParameters (cached raster reused)
```

**The scene-granularity contract (pinned with S1; §4 REQUIRES).** `Scene.Invalidate()` is whole-scene — the Drawing layer has **no region invalidation** and is explicitly memoryless/coarse (drawing-core §3.1). S8 therefore designs against this model, stated honestly:

- **One `Scene` per window** (and one per popup surface, S4-owned). Dedicated `IElementLayer` sub-scenes exist only for a short pinned list: **scroll viewports** (`ScrollContentPresenter`, banded — §3.9), **ProgressBar's indeterminate highlight** (exists only while `:indeterminate`), and S4's own window/popup layers. Everything else draws into its window's scene. This keeps `SceneLayer` count small and stable — the compositor's full-recomposite on layer-count change fires only at window/popup open/close (and menu transitions, §3.9-Menu), and its per-layer change-detection cost stays trivial.
- **Consequence, owned:** any `AffectsRender` in a window re-rasters the **whole window scene**. Per-control phrases like "scene-local re-raster of the control's cells" are retracted throughout; the honest statement is *window-scene re-raster, bounded as follows*: (a) hover/press pseudo-class flips occur at most once per **hit-chain change** — S3 flips `InteractionState` only on enter/leave; intra-element Move events flip nothing — so worst-case any-event motion cost is one window re-raster per element-boundary crossing, never per cell crossed; (b) re-raster repaints with **cached text layouts** (`TextFormatter` results are keyed caches, §3.9-TextBlock) — it is glyph/brush emission, not re-layout; (c) the FrameRenderer's front-buffer diff keeps **wire** cost at changed-cells-only regardless. At the pinned scale (windows ≈ 80×24…200×60, hundreds of elements) a window re-raster is a bounded O(cells) walk; junction-merge chrome (TabControl, adjacent Borders) *requires* the shared scene, so this model is also the one the visual design needs.
- **Future seam, recorded not assumed:** S1 may later add per-element repaint inside a window scene (rastering only dirty element bounds and routing the dirty-region union through `RestrictToDirtyRegions`). Every S8 behavior must be correct **without** it; it is a profiling-gated optimization (open question 2), and no S8 cost claim below depends on it.

Per-control registrations follow one rule: **geometry-bearing properties (Padding, Text, Template) are AffectsMeasure; paint-only properties (brushes, attributes, pen restyle, IsSelected visuals) are AffectsRender; offset/opacity-shaped state (scroll band offset, indeterminate sweep, caret) is AffectsComposite or frame-assembly metadata and never re-rasters** (invariant 3). State pseudo-class flips re-enter through Fork B setters and land in the same pipeline.

**The nullity-escalation pattern (conditional-geometry properties).** `PropertyEffects` is frozen per-property metadata and cannot express "measure when nullity flips, render when restyled". Pinned pattern: such properties register **`AffectsRender`** (covering every restyle — the hot path: the default theme's `:focus → Pens.Heavy` swap is render-only, no layout walk on focus moves), and the owning element's `OnPropertyChanged` **imperatively calls `InvalidateMeasure()`** iff the geometry-determining facet actually changed: `Border.BorderPen`/`Control.BorderPen` when `(old is null) != (new is null)` (footprint ±1 cell/edge); `Border.Title` when presence flips (forces the top border row; title *text* changes within presence are render-only — too-narrow degradation to a plain box is a render-time concern, per `DrawTitledBox`). Imperative `InvalidateMeasure` is an existing S1 surface; this is precise, allocation-free, and static-metadata-compatible.

### 3.6 Items pipeline

- **Items source normalization**: `ItemsSource` set → wrap in an internal `ItemsSourceView` (indexable snapshot over `IList`/`IReadOnlyList`, materializing buffer for plain `IEnumerable`); subscribes `INotifyCollectionChanged` when implemented. Direct `Items` mode uses the same view type. One code path downstream.
- **Generation (v1, eager)**: on attach/reset, `ContainerGenerator.Realize(i)` for `i ∈ [0, n)`. `Realize`: `IsItemItsOwnContainer(item)` → use item as container; else `GetContainerForItemOverride()` → `PrepareContainerForItemOverride(container, item, index)` (sets `DataContext = item` unless own-container, applies `ItemContainerStyle` as `container.Style`, presets content via the §3.4 chain with `ItemTemplate` as the explicit template). Containers are **logical children** of the ItemsControl (selector `ListBox > ListBoxItem` matches; DataContext flows).
- **`Unrealize` — normative retraction sequence** (the v1 "discard" path, in order):
  1. `ClearContainerForItemOverride(container, item)` — control-specific cleanup (mandatory override duty: unhook anything Prepare hooked).
  2. Detach the container from the logical tree (ItemsControl) and visual tree (panel) — **subtree detach is the retraction trigger**: it tears down DynamicResource subscriptions (which point *from* long-lived theme dictionaries *to* the container — a hard leak otherwise, not a GC matter), disposes `When` condition watchers, and retracts armed style frames (S7/Fork B detach contracts, cited in §4).
  3. Clear the locally-set `DataContext`.
  4. If the container is a `Control` with an expanded template: `TemplateInstance.Detach()` (template style frames + TemplateBindings + alias observers by cookie).
  A future recycle pool re-enters at `Prepare` instead of allocating; the sequence is the seam.
- **Incremental updates**: Add/Remove/Move/Replace map to `Realize`/`Unrealize` + index fixups (and `SelectionModel.ItemsInserted/Removed`); `Reset` regenerates. Runtime change to `ItemTemplate`/`ItemsPanel`/`ItemContainerStyle` ⇒ **Reset** (v1 policy — cheapest honest answer). Generator raises `ContainersChanged`; `ItemsPresenter` syncs panel children in order.
- **Subscription + reparenting discipline (re-templating an ItemsControl)**: `ItemsPresenter` subscribes to `ContainersChanged` on tree **attach** and unsubscribes on **detach** — an old presenter discarded by re-templating cannot survive on the control-lifetime generator (leak + double-parenting otherwise). Reparent sequence: old panel **releases** containers at its own subtree detach (visual-parent cleared; containers remain logical children of the ItemsControl and keep their state); the new `ItemsPresenter` **adopts** all realized containers in index order at its first measure. One direction, no overlap window.
- **Virtualization seam (designed, not built)**: the only consumers of "which indexes are realized" are the panel (via `ContainersChanged` + `Realize`) and `SelectionModel` (index-only, element-free). A future `VirtualizingStackPanel` calls `Realize`/`Unrealize` for its viewport window; `ItemsControl` v1 simply plays the role of "panel that realizes everything". No API reshaping required. **Cost model, restated honestly (with §3.9-ScrollViewer's band design):** eager v1 pays *layout* O(item count) (every container measured/arranged) and *raster* O(band ≈ 3× viewport) — virtualization pressure arrives from layout time and container memory at ~10³+ items, not from raster. Recorded in the §7 deferral rationale.
- **ItemContainerStyle stance**: it sets the container's `Style` property (Explicit layer — strongest, deliberate: a per-control override should beat app selectors). The *preferred* idiom is selectors (`ListBox.compact ListBoxItem { … }`); `ItemContainerStyle` exists for one-off composition and WPF muscle memory. `ItemContainerStyleSelector` — deferred.

### 3.7 Selection (ListBox; reused by TabControl)

State lives in `SelectionModel` (index-based; items resolved on demand through `ItemsSourceView`). `ListBoxItem.IsSelected` is a two-way mirror: model→container on changes (via `SetCurrentValue` — sets `IsSelected` without disturbing bindings, flips `:selected` via mapping); container→model when set by binding/user code (guarded against re-entrancy by an `_syncingSelection` flag; equality short-circuits in the store break cycles). **Styling `IsSelected` via style setters is unsupported** (documented stance): `SetCurrentValue` replaces the effective value in place, so a later style re-evaluation re-promoting a frame would silently replace the mirrored value — out-of-band selection changes. Selectors **react to** `:selected`; they never set it. Removal of selected items: selection moves to the nearest surviving index (WPF behavior); `SelectedItem` recomputed from index. `SelectedIndex`/`SelectedItem` are `DirectProperty` (high-frequency internal state, two-way bindable).

### 3.8 Access keys (requirement 6; consume S3)

- **Production**: the three pinned producers of §2.1 (type-driven XAML folding; metadata-flag folding on `ButtonBase.Content` / `MenuItem.Header` / `TabItem.Header` / `Label.Content`; runtime `GetAccessText()` parsing under the same metadata flag). One semantic implementation: `AccessText.Parse`. `TextBlock.Text` and unflagged `Content` slots **never** fold or parse — underscores in data are safe by construction.
- **Extraction & registration**: controls with mnemonic surfaces call `GetAccessText()`; when `HasKey`, they register `(Key, this)` with S3's access-key scope on tree attach and re-register on content/header change, unregister on detach. Menus register in their **menu scope** (active only while the menu is open); everything else registers in the window scope. `Label` registers targeting `Target ?? FocusNavigator.Next(label)` (Label semantics; S3 provides `FocusNavigator.Next(from)`).
- **Invocation**: S3 calls `IAccessKeyTarget.OnAccessKey(in AccessKeyEventArgs)`; defaults — `Button`: `OnClick()`; `ToggleButton`: toggle; `MenuItem`: open submenu or invoke; `TabItem`: select; `Label`: focus its target.
- **Visibility**: `AccessTextPresenter` underlines the `KeyIndex` grapheme (grapheme-cluster-aware column math via `GraphemeWidth`) when the inherited `AccessKeyManager.ShowAccessKeysProperty` (S3-owned attached property, set at window root) is effective-true. S3 sets it permanently true when the Alt-tracking capability is absent, toggles it with Alt down/up otherwise, and clears on `FocusEvent { HasFocus: false }` (Alt+Tab swallows the Up). Theme-level cues additionally ride the root `:access-keys` pseudo-class (Fork B).
- **Capability-gate sourcing (normative).** The gate `Keyboard.DistinguishesKeyUpDown && (Keyboard.ReportsRepeats || Protocol.Win32InputMode)` (input map §7) must be evaluated against the **negotiator's `TerminalCapabilities.Input`** (or the raw `VtInputDevice.Mode.KittyKeyboard` flags via the `IInputDeviceDecorator.Inner` walk) — **never against the assembled pipeline's decorated `InputCapabilities`**. Reason: `KeyReleaseSynthesizer` claims `DistinguishesKeyUpDown = true` and `ReportsRepeats = true` unconditionally but never covers modifier keys (no Alt Down ever arrives to synthesize from) — sourcing the gate from pipeline capabilities would put every legacy terminal into Alt-toggle mode with cues *permanently invisible*, the exact inversion of the requirement's fallback. Re-evaluated on `RenegotiateAsync`. The same sourcing rule governs the TabControl Ctrl+Tab gate (§3.9).
- S8's only jobs: produce/extract/register/invoke, and render the underline cell.

### 3.9 Catalog behavior specs

Common default-theme vocabulary (all citations = drawing-core/charts-text maps): borders via `Pen` strokes (`Pens.Light/Heavy/Double/Rounded`, weight = glyph family); group titles via `DrawTitledBox`/`PanelTitle` (degrades to plain box when `width < title+6`); floating surfaces = `FillOpaque` + `DrawBox(…, overwrite: true)` (the mandated bordered-opaque-panel idiom) + `DrawDropShadow(rect, ShadowGeometry.Drop(radius: 0, offset: 1, strength: 0.5, Bottom|Right), black)` drawn **before** the element; focus = `TextAttributes.Bold|Underline` or pen-weight swap (works at `NoColor`; pure render under the §3.5 escalation pattern); disabled = `TextAttributes.Faint`; pens per capability tier (`(*, Ansi16)`/ASCII theme dictionaries ship `Pens.Ascii` — `GlyphSet` is a consumer/theme knob, the drawing layer can't see caps).

**TextBlock.** Not templated; renders directly. `Text` fast path: `DrawText(col, row, text, Foreground ?? Brushes.Default, …)` per wrapped line (wrapping measured via `TextFormatter`-equivalent grapheme math). `Markup` path: parse once via `TextMarkup.Parse(markup, BrushMarkup.Options(defaultStyle, registry))` where the `BrushResolver` adapts S7 resource lookup (`name → TryFindResource(name) as IBrush → BrushedStyle`) — `[brush=AccentGradient]…[/brush]` resolves theme gradients; inline `linear:|radial:|conic:` works out of the box. **Staleness contract:** while `Markup` resolves registry brushes, the TextBlock subscribes to `ResourceDictionary.Changed` and re-parses/re-resolves on pulse (theme-variant flips re-resolve DynamicResources but cannot reach baked markup brushes otherwise); subscription torn down on detach / `Markup` clear. Layout: `TextFormatter.Format(doc, width, maxRows, caps)` cached **keyed on (text/markup identity, width, caps)** — so `RenegotiateAsync` (caps change) invalidates too; painted via `DrawFormattedText(ft, bounds, caps)` in **absolute scene coordinates** (the formatted-text path ignores the clip/translate stack — safe because S1 guarantees element bounds are honest, and viewport clipping happens at composite via `ScrollContentPresenter`'s sub-scene). Pseudo-classes: none. Theme: foreground inherits; no background. `Text` is never access-key-folded (§3.8); labels are `Label`'s job.

**Label.** Template: Border-less `ContentPresenter` with `RecognizesAccessKey = true`. Registers its mnemonic in the window scope targeting `Target ?? next focusable` (§3.8); `OnAccessKey` → target `.Focus()`. Never focusable. Theme: foreground inherits; `:disabled` Faint.

**Border/Decorator.** Measure = child + `Padding` + (BorderPen ≠ null ? 1 cell/edge : 0) + (Title ≠ null forces top border). `BorderPen`/`Title` follow the §3.5 nullity/presence-escalation pattern (restyle and title-text changes are render-only). Render: `Occludes` ? `FillOpaque(bounds, Background)` + `DrawTitledBox(bounds, title, pen, overwrite: true)` : `FillRectangle(bounds, Background)` + `DrawTitledBox(bounds, title, pen)` (title null ⇒ `DrawBox`). All four edges are one stroke record (corners always close); separate adjacent Borders junction-merge naturally within the same window scene (`JunctionMode.Merge` default — free TUI line-merging chrome; this is one of the reasons the §3.5 model pins per-window scenes). Pseudo-classes: none. The `Title` property *is* the GroupBox story — no separate control.

**ButtonBase / Button / RepeatButton / ToggleButton.**
- Parts: none required (templates are free-form; `ContentPresenter` auto-aliases Content, `RecognizesAccessKey` true in default templates). Pseudo-classes: `:pressed` (control-written via `PseudoClassSet.Set` — `IsPressed` is a DirectProperty, outside `PseudoClassMapping`'s styled-property domain; sanctioned for control authors per Fork B), `:default` (Button), `:checked`/`:indeterminate` (ToggleButton), plus framework `:focus`/`:pointerover`/`:disabled`.
- Mouse: down (Left) → capture (S3), `IsPressed = true` — **capture is taken for both `ClickMode` values, including `Press`** (suppresses spurious enter/leave churn during the press); move while captured → `IsPressed = (pointer over self)`; up → release capture, if over self and `ClickMode.Release` → `OnClick` (uses `e.ClickCount` only as 1; multi-clicks just re-click). `ClickMode.Press` clicks on down. **Cleanup:** `OnLostMouseCapture → IsPressed = false`, no click; the Space latch (below) is cleared on `OnLostFocus`, no click.
- Keyboard: Space down → `IsPressed = true`; Space up over → click. Enter → immediate click (no pressed latch). Access key → click. `IsDefault`/`IsCancel`: register Enter/Esc handlers in the window's input scope via S3 (active only when focus is not on an element that consumes the key, e.g. a `TextBox` consumes neither Enter nor Esc in v1 single-line, so both work).
- `OnClick`: raise `Click`, then `Command.Execute(CommandParameter)` if `CanExecute`.
- **Effective-enabled lattice (pinned; closes original Q2):** `IsEnabled` is a styleable `StyledProperty<bool>` on `UIElement` (S1) that **controls never write** (the SaveDialog `When`-style on `IsEnabled` stays intact). `UIElement.IsEnabledCore` is a protected virtual (WPF kinship) that controls *override*; `ButtonBase` includes the command's `CanExecute` there. Effective-enabled = `IsEnabled ∧ IsEnabledCore ∧ ancestor-effective`, computed by S1's inheritance plumbing and pushed as `InteractionState.Disabled` (drives `:disabled`). When `IsEnabledCore`'s inputs change, the control calls S1's `UpdateIsEnabledCore()`. No coercion slot, no write-back, no clobbered bindings.
- **`CanExecuteChanged` discipline:** subscribe on tree attach, unsubscribe on tree detach **and** on `Command` change — long-lived static commands must not pin discarded buttons.
- RepeatButton: on press, S1 `UITimer` fires after `Delay` then every `Interval`, clicking while pressed and pointer-over; timer canceled on release/capture-loss (and unhooked per the §3.3 convention).
- Theme sketch: 1-row content, `Padding (1,0)`, `[ Save ]`-style bracket glyphs optional via template; `:focus` → Bold + `Pens.Heavy` border in bordered variant (render-only); `:pressed` → background `AccentPressedBrush` (Ansi16 tier: Inverse attribute); `:default` → `Pens.Double` border.

**CheckBox / RadioButton.** Templates: glyph cell + 1 space + `ContentPresenter` (`RecognizesAccessKey = true`). Glyphs are theme **resources** (strings) so variant dictionaries swap them. **Defaults are true ASCII**: `[ ] [x] [-]` and `( ) (*)` — render identically everywhere, zero ambiguous-width risk. The `caps-unicode` tier swaps via resources to `( ) (•)` / `☐ ☑ ◪`-class glyphs — *defense-covered Unicode*: `•` is EAW-Ambiguous and relies on the renderer's ambiguous-width re-CUP defense, which is exactly why it is an opt-up tier, not the default (cf. the ambiguous-width project memory). 3-state cycle (IsThreeState): unchecked→checked→indeterminate→unchecked (WPF order). Space/click toggles; access key toggles. RadioButton: checking sets group peers' `IsChecked = false` via `SetCurrentValue` (preserves their bindings/styles — exactly what `SetCurrentValue` exists for); group = same logical parent when GroupName null, else all same-named radios within the Window. Arrow keys move+check within the group (WPF convention), consuming the event.

**TextBox** (single-line v1).
- **Caret decision: the real terminal cursor** (CellBuffer cursor state), not a drawn caret. Mechanism: `TextPresenter` publishes `(elementLocalColumn, row 0, CursorShape.BlinkingBar)` through S1's `ITerminalCaretService` whenever the TextBox has physical focus and its window is active; S1 transforms to screen cells during frame assembly (it knows layout slots + composite offsets) and writes `CursorRow/Column/Visible/Shape` on the back buffer after compositing; renderer emits cursor as its separate concern. Wins: terminal-native blink with **zero re-raster per blink-phase**, correct DECSCUSR shape control, terminal-level cursor semantics for assistive tech. A clipped-out or unfocused caret is simply not published (`CursorVisible = false`). **Lifecycle:** `TextPresenter` calls `ITerminalCaretService.Clear(this)` on detach, **and** S1 drops publications from detached owners (belt-and-braces; §4) — a removed-while-focused TextBox can never leave a stale terminal cursor. Drawn-caret fallback deferred. (Caret transform ownership — element-local publication + S1 assembly-time transform — is hereby pinned; closes original Q3.)
- Text model: `string Text`; caret = char offset **pinned to grapheme-cluster boundaries** (`StringInfo` enumeration); all horizontal math in display columns via `GraphemeWidth.ClusterWidth/StringWidth` — a wide cluster occupies 2 columns, caret sits between clusters, never inside. Horizontal scroll: presenter keeps `_scrollOffset` (columns) such that the caret column ∈ viewport, with 2-column edge slack; offset changes are AffectsRender of the presenter (window-scene re-raster under the §3.5 model — accepted: typing already re-rasters for the glyph change itself; not worth a composite layer for one row).
- **Two-way binding pushes to source per change** (pinned; Avalonia kinship, terminal-appropriate — validation-reactive UI like the SaveDialog example depends on it). An `UpdateSourceTrigger`-equivalent knob is deferred. Joint S2 contract (§4).
- Selection: anchor+active char offsets; rendered by presenter as theme `SelectionBrush` background (Ansi16/NoColor tier: `Inverse`). Keyboard: Left/Right cluster, Ctrl+Left/Right word (whitespace-delimited), Home/End, all + Shift extend; Backspace/Delete remove cluster (or selection); Ctrl+Backspace/Delete word; Ctrl+A select-all. Typed input: `Key.Character` `KeyEvent.Text` insert (replaces selection, respects `MaxLength`, rejects control chars); **`PasteEvent`** inserts whole text with newlines flattened to spaces (bracketed paste — guarded: without it pastes arrive as typing, which the same insert path absorbs).
- Clipboard stance: Copy = Ctrl+C / Ctrl+Insert, Cut = Ctrl+X / Shift+Delete → **OSC 52 write** via S1's clipboard service (`ClipboardWriter.WriteSet`) when `Output.Protocol.ClipboardWrite`; silently no-op otherwise (selection stays). Paste = primarily the terminal's own paste → `PasteEvent`; Ctrl+V / Shift+Insert attempt an OSC 52 **read** only when `ClipboardRead` negotiated (rare; async with 250 ms timeout), else no-op. Ctrl+C with no selection is *not* consumed (bubbles; app may bind quit).
- Mouse: down → caret to cell→cluster hit (capture); drag extends selection; double-click (`ClickCount == 2` on ButtonDown, from `MouseClickSynthesizer`) selects word; triple-click selects all.
- Pseudo-classes: `:readonly`, `:empty` (drives Placeholder rendering: Faint placeholder text when empty); framework `:focus` etc. Undo/redo: **deferred** (needs a coalescing edit-stack design; not load-bearing for v1).
- Theme: `Pens.Light` border (via template Border), `:focus` → `Pens.Heavy` (render-only); 1 row content; placeholder Faint.

**ScrollViewer / ScrollBar** (consumes S1 scrolling mechanics).
- **`ScrollContentPresenter` — the banded sub-scene (the pinned sizing policy).** Content renders into a dedicated `IElementLayer` sub-scene covering rows `[anchor, anchor + viewport + 2K)` of the content — **not the full extent**. Slack `K = max(viewportRows, 8)` (so the band ≈ 3× viewport; default tuning is open question 3). Viewport = `CompositeParameters { Offset = (0, anchor − verticalOffset), Clip = viewportRect }` — clips *everything* (formatted text, fragments, pen strokes), sidestepping the v1 clip-stack gap (drawing-core "robust route (a)").
  - Scrolling **within** the band = pure re-composite of the cached raster (invariant 3) — the hot path.
  - Scrolling **past** the slack = **re-anchor**: the band repositions and re-rasters once (S1 hands band-relative bounds; element draw code is unchanged). Long fast scrolls re-raster once per band crossed — accepted and documented.
  - The scene is allocated at band size and reallocated **only on viewport resize** (band size is viewport-derived; `Scene` has no resize API — re-anchoring reuses the allocation with a new translation).
  - `AffectsRender` inside the content re-rasters **the band, never the extent** — a keyboard-navigation `:selected`/`:focus` flip in a 1,000-item list costs ≤ ~3× viewport rows, not 1,000 rows; item add/remove changes the extent (scrollbar math) without touching the scene allocation.
  - v1 bands the **vertical** axis (the common case); horizontal content rasters at full width, capped (below).
- Extent & constraint cap: `Extent` = content desired size measured with the scrollable-axis constraint capped at **`LayoutLimits.MaxScrollExtent = 32,000` cells** (inside the ushort-backed `Rect`/`Size` domain with headroom); content desiring more clamps with a one-time diagnostic. Offsets clamp to `[0, extent − viewport]`.
- `HorizontalOffset`/`VerticalOffset` are `DirectProperty`: **direct properties do not flow `PropertyEffects` routing** (Fork A confirmation, §4) — the offset setters **hand-route** the `IElementLayer.Parameters` refresh (and the re-anchor check). Consequence, documented: offsets are **not storyboard-animatable** under Fork A (no smooth scroll in v1); a future smooth-scroll rides a styled proxy property or an S1 animation hook.
- Mouse wheel (router gives the deepest scrollable): `lines = WheelDeltaY / 120 × 3`; Shift+wheel or `WheelDeltaX` → horizontal. Unconsumed wheel bubbles to outer ScrollViewer.
- Keyboard (when focus is inside and the focused control didn't consume): Up/Down ±1 row, PageUp/PageDown ±viewport, Ctrl+Home/End extremes, Left/Right ±1 col. `EnsureVisible(rect)` scrolls minimally; ListBox/TextBox call it for focused container/caret.
- ScrollBar: 1 column (row) wide. Parts: optional `PART_LineUpButton`/`PART_LineDownButton` (RepeatButtons with `▲▼` glyph resources — covered by the renderer's ambiguous-width defense; ASCII tier `^v`), required `PART_Track`. Track render: `│` rail (Pen) + proportional thumb of `█` cells (min 1). Mouse on track: above/below thumb → page; thumb drag → capture + proportional value (cell-quantized); arrows → ±SmallChange (repeat). `:horizontal`/`:vertical` select glyph/orientation styling. ScrollViewer wires bars in `OnApplyTemplate` (code-behind wiring, not two-way TemplateBinding — TemplateBinding is one-way by design) and **unhooks them in `OnTemplateDetaching`** (§3.3 convention, reference implementation).
- Auto visibility: bar collapses when extent ≤ viewport (layout participates; `Auto` re-measure loop broken by the standard two-pass "remember last verdict" trick).
- FrameRenderer SU/SD eligibility, stated precisely: `TryDetectAndApplyScroll` matches only when the **entire back buffer** equals the front shifted by K rows, **no Overlay-layer fragment is registered anywhere on the buffer**, and `OrderedDither` is off. Any chrome row, scrollbar-thumb movement, or status line defeats it — a templated ScrollViewer practically never qualifies. No SU/SD claim is made for ScrollViewer; the wire savings come from the diff. (A borderless full-screen scroll surface with no overlays can still hit it — a layout property, not a ScrollViewer feature.)

**ListBox.** Template: Border → `PART_ScrollViewer` → `ItemsPresenter`. Containers focusable, list itself `IsTabStop = false`, S3 `TabNavigation = Once` on the items host (tab enters at the focused/selected item, single stop).
- Keyboard: Up/Down move focus (+select per mode: Single selects focused; Extended selects + sets anchor; +Shift range-from-anchor; +Ctrl moves focus only), Space select (Ctrl+Space toggle), Home/End, PageUp/PageDown (viewport rows), Ctrl+A select-all (Extended). **Enter on a focused container raises `ItemActivated`** (parity with double-click). Type-ahead: deferred.
- Mouse: ButtonDown on container — plain: select + anchor; Ctrl: toggle + anchor; Shift: range from anchor (Extended mode; Single ignores modifiers). Wheel scrolls without selection change. `ClickCount == 2` → `ItemActivated` (consumer hook; no default action). Drag-select: **deferred** (capture + edge auto-scroll machinery; not v1-critical).
- `:selected` on containers; theme: selected = `SelectionBrush` background + Bold (Ansi16: Inverse); focused container additionally `:focus` → underline; `EnsureVisible` on focus move. Cost per navigation step under the pinned models: one band re-raster (≤ ~3× viewport rows; §3.9-ScrollViewer) + re-composite — bounded and viewport-proportional, independent of item count.

**Menu / MenuItem / ContextMenu / Separator.**
- Structure: `Menu` (bar) is a horizontal ItemsControl of top-level `MenuItem`s; `MenuItem.Items` = submenu content; `ContextMenu` is a popup-rooted vertical menu. Submenus and ContextMenu open through **S4 `IPopupHost`** (placement: below the bar item / right of the submenu item, flip-to-fit; light-dismiss = S4).
- Menu mode + focus scope (req 4, via S3): the menu bar is a **logical focus scope**. Opening (Alt/F10 from S3's access-key manager, or mouse) pushes a menu focus scope; physical focus moves into the menu; closing pops the scope and S3 restores physical focus to the scope's saved logical focus — focus returns to where the user was, the WPF behavior. While open, S3 activates the menu's access-key scope (keys invoke without Alt).
- Keyboard: bar — Left/Right cycle top-level (wrap), Down/Enter/Space open, Esc/Alt exit menu mode. Submenu — Up/Down highlight (`IsHighlighted`, skip separators+disabled, wrap), Right open child submenu (or next top-level when leaf), Left close level (or previous top-level at depth 1), Enter/Space invoke leaf (close all, then `Click`+Command), access key invokes directly.
- Mouse: click top-level toggles; while any top-level is open, hover **switches instantly** between top-levels; hover highlights items; hover over an item with children opens its submenu after 250 ms (`UITimer`; immediate on click); click leaf invokes. Highlight flips are bit flips + a popup-scene re-raster; **but each top-level switch is a popup close + open = two layer-count changes = two full-target recomposites** (drawing-core: layer-count change forces full recomposite), plus Sixel fragment re-emission if image-bearing — the honest cost. Mitigation requested from S4 (§4): a **single reusable popup layer per menu session** (`IPopupHandle.Move`/content-swap instead of close+open), making top-level switches a re-raster + re-composite of one stable layer.
- `IsCheckable`: invoke toggles `IsChecked`; glyph column shows a check resource (`caps-unicode`: `✓`; ASCII: `x`).
- Pseudo-classes: `:highlighted`, `:open` (both DirectProperty-backed → written via `PseudoClassSet.Set`), `:checked` (mapping); Separator: none.
- Theme: popup surface = `FillOpaque(MenuBackground)` + `DrawBox(Pens.Light, overwrite: true)` + `DrawDropShadow(Drop(0, 1, 0.5, Bottom|Right))` drawn before the panel; layout columns: `[check 1][gap 1][header][fill][gesture dim][gap 1]`; `InputGestureText` right-aligned Faint; Separator = `DrawLine` light dash across the popup width (junction-merging into the side borders). Bar: highlighted top-level = Inverse or accent background.

**TabControl / TabItem.** Template: `PART_TabStrip` over `PART_ContentHost`. Selection via `SelectionModel` (Single); selecting a `TabItem` shows its `Content` in the host through the §3.4 chain (`ContentTemplate` applies to non-element content).
- Keyboard, capability-honest: **header arrows are the universal primary** — on a focused header: Left/Right move focus **and select** (selection-follows-focus, WPF tabs), Home/End. **Ctrl+PageUp / Ctrl+PageDown cycle selection from anywhere inside** — the universal chord: xterm-standard modified-key encodings (`CSI 5;5~` / `CSI 6;5~`) that survive every terminal tier, and WPF kinship. **Ctrl+Tab / Ctrl+Shift+Tab register additionally only when wire-distinguishable from Tab** (Kitty disambiguation / modifyOtherKeys ≥ 2 / Win32 input mode — on legacy terminals Tab *is* Ctrl+I and the chord cannot exist); the gate is sourced per §3.8's rule (negotiator capabilities, never decorated pipeline).
- Mouse: click header selects. `:selected` on TabItem. Theme — the terminal party trick: headers and the content border are drawn **in the same (window) scene** so Pen junctions merge: selected tab `┌─ Title ─┐` opens into the content frame (its bottom edge cell becomes `┘`/`└` junctions via the stroke accumulator; the selected tab's segment of the content top edge is omitted), unselected tabs Faint with light bottom rule. `TabStripPlacement`: Top only in v1 (Bottom trivial later; Left/Right deferred — vertical headers are weak on a cell grid).

**ProgressBar.** Never throws on bad data (the charts-layer norm): `Maximum == Minimum` ⇒ 0%; `Value` clamped to `[Minimum, Maximum]`; NaN in any of the three ⇒ 0%. Determinate render: track painted per cell; fill width `w = fraction × columns × 8` eighths → full `█` cells + one partial from the **left eighth ramp** `▏▎▍▌▋▊▉` (the same family BarChart uses; horizontal lower/left ramps are the complete ones). Mechanism stated precisely: the `Fill`/`TrackBrush` brushes are **`ColorAt`-sampled per cell against the whole track rect** (gradient spans the bar); full and partial blocks are *foreground glyphs* written via `Set` over the track background — not background fills. ASCII tier: `#` cells, no partials. Value changes → AffectsRender (window-scene re-raster under the §3.5 model; bounded, layout-cached). Indeterminate: a 25%-width highlight block lives on an S1 **element layer** (own small scene, exists only while `:indeterminate`); the default theme's `:indeterminate` style ignites a storyboard (`BeginStoryboard`, `HandoffBehavior.SnapshotAndReplace`) **targeting `IndeterminateOffsetProperty`** — a styled property animated at `BindingPriority.Animation` through the store, whose `AffectsComposite` metadata routes S1 to refresh the layer's `CompositeParameters` offset with `PingPong` — **re-composite only, zero re-raster** (invariants 2/3 hold end-to-end: no raw layer pokes); retraction stops it (`StopStoryboard`). Vertical orientation: deferred.

**ToolTip / ToolTipService.** One process-wide service instance (S8-owned singleton attached behavior; no per-element timers): consumes S3's **router-level hit-chain observation hook** (a named S3 REQUIRES item — enter/leave derived from Move hit-chain diffs, observed at the router, not via per-element virtuals). Policy under any-event motion: opening timer (default 500 ms) **starts on entering an element bearing `Tip` and is NOT reset by intra-element cell moves** (per-cell Move events are ignored once inside — cheap by construction); reset on element change. Quick-show: if a tooltip closed < 100 ms ago, the next one opens immediately. Close on: leave, any ButtonDown, any **non-modifier** KeyDown (under Kitty `ReportAllKeysAsEscapeCodes` standalone Shift/Alt/Ctrl downs are KeyEvents — squeezing Shift must not dismiss the tooltip), focus loss, owner detach. Display via S4 popup, hit-test-transparent, never focusable, placed below-right of the pointer cell (flip to fit). **Capability honesty**: `ShowOnFocus` is `bool?` — `null` (default) = auto: enabled iff `MouseCapabilities.Motion == false` (hover cannot exist); then the tooltip opens 500 ms after the element gains `:focus-visible` (keyboard focus), closes on blur/keydown. Theme: `FillOpaque` panel + light box (overwrite: true) + drop shadow; max width 40 cells, content wraps.

**Window chrome template** (the template; `Window` is S4's class). Parts: `PART_TitleBar` (UIElement strip, drag → S4 move command), `PART_Title` (TextBlock, TemplateBinding `Title`), `PART_CloseButton` (**a real, hit-testable `Button`** wired to S4's close command — not glyph art painted by `DrawTitledBox`; it renders as `[x]` in the title row), `PART_ContentHost` (ContentPresenter, required). Default visual: body Border with `Occludes = true` (`FillOpaque(WindowBackground)` — lower windows' glyphs must not bleed through) + `DrawTitledBox` with the window title embedded in the top edge (`PanelTitle`, `TitlePosition.Center`); border pen `Pens.Double` when `:active-window`, `Pens.Light` + Faint title otherwise; resize affordance: bottom-right `╝`-adjacent cell drag → S4 resize command. **Drop shadow is S4's layer concern** (the shadow falls outside the element rect; S4 sizes the window scene with +1 col/row margin and the chrome painter calls `DrawDropShadow(windowRect, Drop(0,1,0.5,Bottom|Right), black)` before the body — contract item in §4). Modal dimming = S4 setting `obscured` class on background windows (Fork B; not chrome's job). **Interim story (schedule decoupling):** S4 ships a primitive built-in chrome painter from its own phase 0; S8's chrome template replaces it at C4. The **`PART_*` name contract freezes at C0** so S4 carries no hidden dependency on S8's fourth phase.

---

## 4. Cross-subsystem contracts

### REQUIRES from S1 (tree/layout/render/composition/loop)

```csharp
// Element base (S1-owned): Measure/Arrange + MeasureOverride/ArrangeOverride (integer Size/Rect),
// visual-child management, logical-child registration, InvalidateMeasure/Arrange/Render (the
// imperative escalation surface §3.5 relies on), Bounds.
// IsEnabled: StyledProperty<bool> on UIElement + protected virtual IsEnabledCore + UpdateIsEnabledCore();
// effective-enabled = IsEnabled ∧ IsEnabledCore ∧ ancestor-effective, computed on the inheritance
// plumbing and pushed as InteractionState.Disabled.                                   ← pinned (§3.9)
// S1 calls control.ApplyTemplate() at the head of Measure for every Control.          ← load-bearing

// SCENE GRANULARITY (pinned contract, §3.5): one Scene per window (popups = S4 scenes);
// IElementLayer sub-scenes ONLY for: scroll viewports (banded), ProgressBar indeterminate,
// S4 windows/popups. AffectsRender ⇒ whole-owning-scene re-raster (Scene.Invalidate is coarse);
// S8 accepts this, bounded per §3.5. Region invalidation = recorded future seam, never assumed.

public interface IElementRenderHost      // how S8 elements draw
{
    // Render(DrawingContext ctx, Rect bounds): bounds are ABSOLUTE scene coordinates (band-relative
    // inside a banded viewport layer — S1 owns the translation); S1 guarantees bounds honesty
    // (element never asked to draw outside its slot) except inside a viewport layer, where clipping
    // is S1's composite clip. S8 relies on this for DrawFormattedText/DrawContent (which ignore the
    // intra-scene clip stack in Drawing v1).
}

public interface IElementLayer           // dedicated sub-scene seam (design-doc §3.2 nesting)
{
    Scene Scene { get; }                 // sized by S1: slot (+margins) or BAND (viewport + 2K) per LayerOptions
    CompositeParameters Parameters { get; set; }   // refreshed by AffectsComposite routing; ScrollViewer
                                                   // offsets hand-route here (direct-property path)
    void Invalidate();                   // re-raster request (band re-anchor uses this)
}
// UIElement.RequestLayer(LayerOptions) → IElementLayer. Consumers: ScrollContentPresenter (banded
// offset+clip), ProgressBar indeterminate (offset anim), S4 windows/popups (S4's own use).

public interface ITerminalCaretService   // frame-assembly caret metadata (never re-rasters)
{
    void Publish(UIElement owner, int column, int row, CursorShape shape);  // element-local coords
    void Clear(UIElement owner);
    // S1 transforms to screen cells, applies to back-buffer cursor state after compositing;
    // only the focused-window publication wins; clipped-out ⇒ CursorVisible = false;
    // S1 DROPS publications from detached owners (stale-cursor guarantee, §3.9-TextBox).
}

public interface IUITimer { … }          // UITimer Create(TimeSpan due, TimeSpan? interval, Action cb) — UI-thread,
                                         // frame-aligned. Consumers: RepeatButton, ToolTipService, menu hover-open.
public interface IClipboardService       // OSC 52 over the session sink
{
    bool CanWrite { get; } bool CanRead { get; }
    void SetText(string text);                            // ClipboardWriter.WriteSet
    ValueTask<string?> TryGetTextAsync(TimeSpan timeout); // OSC 52 read when negotiated
}
// Scroll-detection note (corrected scope): TryDetectAndApplyScroll requires the ENTIRE back buffer
// to equal the front shifted by K rows, NO Overlay-layer fragment registered ANYWHERE on the buffer,
// and OrderedDither off. S8 makes no SU/SD claims for templated controls (§3.9-ScrollViewer).
```

### REQUIRES from S3 (input routing, focus, access keys)

```csharp
// Routed input virtuals on UIElement with Handled semantics (exact arg shapes S3-owned; fields S8 needs):
//   OnKeyDown/OnKeyUp   { Key, Modifiers (lock-free), Text, IsRepeat, Handled }
//   OnTextInput          { Text }          (separated from KeyDown for IME/paste-shaped input)
//   OnMouseDown/Up/Move  { Position (element-local cells), Button, ButtonsHeld, Modifiers, ClickCount, Handled }
//   OnWheel              { WheelDeltaX/Y (1/120 units), Handled }
//   OnMouseEnter/Leave   (synthesized from Move hit-chain diffs)
//   OnPaste              { Text }          (PasteEvent routed to focused element)
//   OnGotFocus/OnLostFocus, OnLostMouseCapture (ButtonBase cleanup, §3.9)
// Pipeline guarantee: MouseClickSynthesizer installed (ClickCount populated, default target ButtonDown).
// ROUTER-LEVEL HIT-CHAIN OBSERVATION HOOK: a service-grade enter/leave stream observed at the router
// (ToolTipService consumer) — in addition to the per-element virtuals.                ← named item

public interface IInputCapture { void CaptureMouse(UIElement e); void ReleaseMouseCapture(UIElement e); }

// Focus: Focusable/IsTabStop/TabIndex properties; element.Focus(); IsFocused → :focus flips (S3 writes
// Focused/FocusWithin/FocusVisible/ActiveWindow/AccessKeyCue InteractionState bits; S8 controls write
// control-semantic pseudo-state via PseudoClassSet/PseudoClassMapping — incl. direct PseudoClassSet.Set
// for DirectProperty-backed state (:pressed/:open/:highlighted), sanctioned per Fork B).
// Logical focus scopes: IFocusScope push/pop with focus save/restore (menus); TabNavigation modes
// (Once for items hosts); FocusNavigator.Next(from) (Label targeting).

public interface IAccessKeyScope                         // window scope + menu scopes
{
    IDisposable Register(char key, IAccessKeyTarget target);
}
public interface IAccessKeyTarget { void OnAccessKey(in AccessKeyEventArgs e); }
// S3 owns AccessKeyManager.ShowAccessKeysProperty (inherited attached bool) + root :access-keys class,
// Alt-state clearing on focus-out, and the capability gate — which MUST be sourced from the
// negotiator's TerminalCapabilities.Input (or raw VtInputDevice.Mode.KittyKeyboard via the
// IInputDeviceDecorator.Inner walk), NEVER from decorated pipeline capabilities
// (KeyReleaseSynthesizer spoofs DistinguishesKeyUpDown/ReportsRepeats but never covers modifiers);
// re-evaluated on RenegotiateAsync.                                                    ← normative (§3.8)

// Window-scope key commands: RegisterWindowKey(Key key, KeyModifiers mods, UIElement owner, priority)
// (Button.IsDefault/IsCancel; TabControl Ctrl+PageUp/PageDown always, Ctrl+Tab only when
// wire-distinguishable — same capability-source rule) — focused-element handlers win over scope handlers.
```

### REQUIRES from S4 (windows/popups)

```csharp
public interface IPopupHost
{
    IPopupHandle Open(UIElement content, in PopupPlacement placement, PopupOptions options);
    // PopupPlacement { UIElement Anchor; PopupEdge Edge; CellPosition Offset; bool FlipToFit; }
    // PopupOptions   { bool LightDismiss; bool HitTestTransparent; bool TakesFocus; }
}
public interface IPopupHandle : IDisposable { event EventHandler? Closed; void Move(in PopupPlacement p); }
// Consumers: MenuItem submenus, ContextMenu (LightDismiss, TakesFocus), ToolTip (HitTestTransparent, no focus).
// REQUESTED: a single reusable popup layer per menu session (Move/content-swap instead of close+open)
// so top-level menu switches avoid two layer-count-change full recomposites + Sixel re-emission (§3.9-Menu).
// Plus: window Move/Resize/Close commands for the chrome template parts; :active-window on the window
// subtree; modal `obscured` class on background windows; window scene sized +1 col/row for the chrome
// drop shadow (S4 invokes the chrome's shadow painter before body render).
// INTERIM CHROME: S4 ships a primitive chrome painter from its phase 0; S8's template replaces it at C4;
// the PART_* name contract freezes at C0 (§3.9-Window chrome).
```

### REQUIRES from S2 (binding)

- Two-way `TextBox.Text` pushes target→source **per change** (pinned default; §3.9). `UpdateSourceTrigger`-equivalent deferred.
- Self-source and ancestor-source bindings for `When` conditions (already a Fork B numbered requirement); ElementName bindings (Label.Target).

### REQUIRES from S7 / Forks A–C (engines)

- Fork A: `UIProperty.Register*` (+ per-type metadata overrides carrying the new **`ParsesAccessKeyLiterals`** flag — §2.1/§3.8), `PropertyEffects`, `SetCurrentValue` (RadioButton group unchecking, selection mirroring), `IsSet` (ContentPresenter auto-aliasing), `DirectProperty` for high-frequency state, `DeferNotifications` during template apply/container prepare. **Confirmations:** (a) direct properties skip `PropertyEffects` routing — consumers hand-route (ScrollViewer offsets → `IElementLayer.Parameters`), and direct properties are not storyboard-animatable (no `AnimatedValueHandle` lane); composite-routed *animatable* state must be styled properties (`ProgressBar.IndeterminateOffset`); (b) an aliased read-through must not create a store entry, and a typed property-changed observer on another object is subscribable without a binding (open question 1).
- Fork B: `Style`/`Setter`/`Styles`, `PseudoClassSet` (direct `Set` sanctioned for DirectProperty-backed control pseudo-state) + `PseudoClassMapping` (incl. the multi-class projection overload — already in the canonical proposal §2.4, no delta), Template-layer arming for `ControlTemplate.Styles`, `TemplateInstance.Detach()` cookie retraction, `/template/` matching against stamped parts, `BeginStoryboard`/`StopStoryboard` ignition on activation/retraction edges (ProgressBar `:indeterminate`), subtree-detach retraction of `When` watchers.
- Fork C / S7: `ITemplateContent` + `TemplateBuildContext`, `TemplateBinding` (parse-restricted to template bodies), **AccessText folding per the two parse-time rules of §2.1** (type-driven + `ParsesAccessKeyLiterals` metadata, resolved against the instance's runtime type), resource walk `TryFindResource` + `DataTemplateKey(Type)` probing **including the templated-parent hop** (§3.4 chain is the joint pinned contract with S7), subtree-detach DynamicResource unsubscription, `ResourceDictionary.Changed` pulse (TextBlock markup re-resolution), `IValueConverter`-equipped `{Binding}` for all S8 binding targets.

### PROVIDES

- To **S4**: the Window chrome template + part-name contract (PART_TitleBar/PART_Title/PART_CloseButton/PART_ContentHost — frozen at C0); `Border`, `ContentPresenter`, `ButtonBase` for window/popup composition; `Menu` registration hook (`IMainMenu`: window-level Alt/F10 target).
- To **S7/theme authors**: the full catalog with pinned part names, pseudo-classes, and themeable resource keys (`AccentBrush`, `SelectionBrush`, `WindowBackground`, `MenuBackground`, glyph resources `CheckBoxGlyphs`, `RadioGlyphs`, `ScrollArrowGlyphs`, …) across `ThemeVariant` tiers.
- To **future virtualization**: `ItemContainerGenerator` range API + element-free `SelectionModel` + the §3.6 retraction sequence as the recycle seam.
- To **everyone**: `AccessText`/`AccessTextPresenter`/`Label`, `SelectionModel`, `ContentPresenter`/`ItemsPresenter` as reusable primitives; `TemplatePartAttribute`; `RequireControl<T>`.

---

## 5. Requirement mapping

| Req | Coverage by S8 |
|---|---|
| 1 Styling/templating | `ControlTemplate`/`DataTemplate` object models, lookup chain, part contract, re-templating with full retraction lifecycle; every catalog control fully templated with pinned pseudo-classes; default theme as Fork B control-theme styles. |
| 2 Binding | All key properties are `StyledProperty`/`DirectProperty` binding targets; `ItemsSource`, `SelectedItem/Index` (two-way), `Text` (two-way, per-change push), `IsChecked` (two-way), `ICommand` consumption with `CanExecute` → `IsEnabledCore`. |
| 3 Resource/style inheritance | Templates resolve resources through S7's scope walk (templated-parent hop pinned); theme = keyed control styles; glyph/brush resources tiered by `ThemeVariant`; `TextElement.Foreground/TextAttributes` value inheritance. |
| 4 Logical+physical focus | Controls implement focus behaviors (focusable parts, TabNavigation stances, selection-follows-focus, `EnsureVisible`); menus use S3 logical focus scopes with save/restore — physical focus returns on menu close. |
| 6 Access keys | Coherent end-to-end pipeline: three pinned producers (type-driven folding, metadata-flag folding on Content/Header/Label, runtime `GetAccessText` parsing — one `Parse`), `Label` for caption→next-focusable targeting, S3 registration (window + menu scopes), invocation defaults per control, `AccessTextPresenter` underline driven by the inherited show-property/`:access-keys`; capability gate sourced from negotiator `TerminalCapabilities` (KRS-spoof-proof), Alt-toggle on Kitty/Win32, permanent cues otherwise. |
| 7 XAML | Parameterless ctors everywhere, content properties (`[ContentProperty]`: ContentControl.Content, ItemsControl.Items, Decorator.Child), `ITemplateContent`-typed template properties (auto-deferral), `TemplateBinding` usage stance, attached `ToolTipService.Tip`/`ContextMenu.Menu`, AccessText fold metadata. |
| 8 Setters + hybrid triggers | Controls *feed* the model: `PseudoClassMapping` registrations (`:checked`, `:indeterminate`, `:selected`, `:horizontal/:vertical`, `:default`, `:readonly`, `:empty`), control-written `:pressed`/`:open`/`:highlighted` via `PseudoClassSet.Set`; default theme exercises `^:pseudo` children + `/template/` reach-in. |
| 9 Property system | Pure consumer: sparse styled properties for styleable surface, `DirectProperty` for hot state (`IsPressed`, offsets, `SelectedIndex`) with hand-routed effects where pinned, `PropertyEffects` on every registration + the nullity-escalation pattern, `SetCurrentValue` where bindings must survive control-internal writes. |
| 10 Animation | Default theme ignites storyboards on style edges (`BeginStoryboard`/`StopStoryboard`, `SnapshotAndReplace`); ProgressBar indeterminate is the reference composite-only animation (store-routed `IndeterminateOffset`, `AffectsComposite`); focus/hover transitions are attribute/color flips (window-scene re-raster, bounded) by design. |

**Invariant compliance.** *Frame coherence*: measure-time template expansion + same-frame layout (§3.1). *Engines never touch Scene/CellBuffer*: all control invalidation routes through `PropertyEffects` or the sanctioned imperative `Invalidate*` escalation (§3.5); render code touches only the `DrawingContext` handed to it; the indeterminate animation rides the store (§3.9). *Re-composite vs re-raster*: in-band scrolling, indeterminate sweep, and the caret are composite/metadata paths; caret blink costs zero frames (§3.9). *Retraction is store-owned*: re-templating = `TemplateInstance.Detach()` cookie removal; `Unrealize` follows the normative detach sequence; RadioButton/selection mirrors use `SetCurrentValue`, never save-and-restore. *Template barrier*: stamping in `Instantiate` with the foreign-parent throw (§3.2), DataTemplate exemption pinned. *Single UI thread*: everything here is UI-thread-affine; the only async (`TryGetTextAsync`) marshals back via the dispatcher. *Lower layers additive-only*: S8 consumes Drawing/Rendering/Animation exactly as shipped — no lower-layer changes requested.

---

## 6. Terminal-specific design (deviations from WPF/Avalonia)

1. **Caret = the terminal cursor**, not a rendered adorner (§3.9-TextBox): native blink via DECSCUSR shapes, zero per-blink frames; detach-safe by double guarantee. WPF's drawn caret exists because it owns pixels; we own a terminal that already has a cursor (renderer emits cursor as a separate concern — rendering-session map).
2. **Focus visuals are attributes/pen weights, not focus rectangles** — Bold/Underline/Inverse and `StrokeWeight` swaps survive `ColorDepth.NoColor`, and the §3.5 escalation pattern keeps them render-only on the hottest interaction path; there is no 1px adorner layer to draw on (Fork B §6.2 alignment).
3. **`Border.Title` replaces GroupBox** — `DrawTitledBox` makes titled frames a one-call primitive (drawing-core §2), so a dedicated HeaderedContentControl chrome class is dead weight.
4. **Three explicit surface semantics** — Border `FillRectangle` (tint, glyphs show through) vs `Occludes`/window/menu/tooltip `FillOpaque` + `DrawBox(overwrite: true)` (the mandated idiom; drawing-core gotchas 5/7). WPF has one Background; we name the difference because the compositor does.
5. **Viewport clipping at composite, not draw — banded** — `ScrollContentPresenter` uses a band-sized sub-scene + `CompositeParameters` clip/offset because the Drawing v1 clip stack doesn't cover formatted text/fragments/strokes (drawing-core "partial coverage gotcha"); in-band scrolls are re-composite-cheap, band re-anchors bound the raster cost to ~3× viewport regardless of extent.
6. **Junction-merging chrome** — TabControl headers fuse into the content frame and Separators junction into menu borders via the stroke accumulator's cross-call merge (drawing-core §7) — impossible in pixel frameworks, free here; this is a co-rationale for the pinned per-window scene model (§3.5).
7. **Block-element fractional fills** — ProgressBar uses the eighth ramps (charts map, BarChart mechanics) as per-cell foreground glyphs instead of sub-pixel widths.
8. **Capability-honest interaction, with honest sourcing** — hover (`:pointerover`, ToolTip) exists only with `MouseCapabilities.Motion` (tooltip falls back to focus-triggered via the `bool?` auto default); access-key cues toggle only on Kitty (`ReportEventTypes+ReportAllKeysAsEscapeCodes`)/Win32 paths, permanent otherwise — and the gate reads the **negotiator's** capabilities, never decorator-inflated pipeline claims (§3.8); Ctrl+Tab exists only where it is wire-distinguishable, with Ctrl+PageUp/PageDown as the universal chord; clipboard is OSC 52 write-mostly, paste is `PasteEvent`-driven (no Ctrl+V entitlement); ClickCount exists because the pipeline installs `MouseClickSynthesizer`.
9. **Integer-cell ergonomics + glyph-tier honesty** — `Padding`/`Margins` in whole cells; ScrollBar is exactly 1 cell wide; border thickness is a glyph family, never a width (drawing-core: "Weight selects a glyph family"); checkbox/radio/scroll glyphs are theme *resources* with **true-ASCII defaults** (`[ ] [x] [-]`, `( ) (*)`) — the `caps-unicode` tier opts up to defense-covered Unicode (`(•)`, `☐/☑`, `▲▼`), confining EAW-ambiguous glyph risk to the tier that accepts it (per project memory).
10. **No hover/press geometry animation** in the default theme — state flips are color/attribute changes (window-scene re-raster, bounded per §3.5, occurring only on hit-chain change); animated motion is reserved for composite-parameter paths (Fork B §6.2, invariant 3).

---

## 7. Phasing (repo §11 convention: numbered phases, deferrals recorded with reasons)

**v1 spine:**
- **C0 — template spine**: `Control`, `ControlTemplate`/`TemplateInstance` sequencing (incl. detach/re-entrancy hardening), part validation, `ContentPresenter` (+aliasing, `RecognizesAccessKey`), `ContentControl`/`HeaderedContentControl`/`HeaderedItemsControl` shells, `Decorator`/`Border`, `TextBlock`, the AccessText pipeline (`AccessText`, fold metadata flag, `AccessTextPresenter`, `Label`), `Button`. **Window-chrome `PART_*` name contract frozen here** (S4 decoupling). *Gate: Fork A P0–P1 (+ metadata flag + direct-property confirmations), Fork B S0/S4 seams, S1 element base + scene-granularity pin.*
- **C1 — interactive leaves**: `ButtonBase` completion (Command/`IsEnabledCore`, capture + cleanup), `RepeatButton`, `ToggleButton`, `CheckBox`, `RadioButton`, `TextBox`+`TextPresenter` (caret service, clipboard, selection, per-change push). *Gate: S3 routing/capture/focus + effective-enabled plumbing, S1 caret+timer services.*
- **C2 — items**: `ItemsControl`, `ItemsSourceView`, `ItemContainerGenerator` (+ normative Unrealize sequence), `ItemsPresenter` (subscription/reparent discipline), `SelectionModel`, `ListBox`/`ListBoxItem`.
- **C3 — scrolling**: `ScrollViewer`, `ScrollContentPresenter` (banded sub-scene), `ScrollBar`; ListBox scroll integration; band-slack profiling. *Gate: S1 `IElementLayer` with band sizing.*
- **C4 — popup tier**: `Menu`/`MenuItem`/`ContextMenu`/`Separator`, `ToolTip`/`ToolTipService`, Window chrome template (replaces S4's interim painter). *Gate: S4 `IPopupHost` + window commands (+ reusable menu popup layer), S3 focus scopes + access-key scopes + router observation hook.*
- **C5 — completion**: `TabControl`/`TabItem`, `ProgressBar` (indeterminate gated on storyboard ignition), default theme hardening across `ThemeVariant` tiers, adversarial review of template lifecycle + items pipeline (repo design-panel convention).

**Explicitly deferred (with reasons):**
- **ComboBox** — composes selection+popup+editable-text across three subsystems; ListBox-in-Popup recipe covers v1; add once C2–C4 are hardened.
- **Slider** — continuous drag has poor cell-grid affordance; no v1 consumer; ProgressBar covers display.
- **TreeView** — hierarchical generation/indent/expansion is a sizable design; the generator seam doesn't preclude it (nested ItemsControls prototype path recorded).
- **DataGrid** — requires virtualization (itself deferred) + column layout; out of v1 scope by design.
- **StatusBar** — a `DockPanel`+`Border`+`Separator` recipe, not a control; ships as a docs/gallery sample.
- **Virtualization** — seam designed (§3.6); engine deferred until a real consumer. Updated rationale: the band design (§3.9) caps *raster* cost at ~3× viewport regardless of item count, so eager realization's true bill is **layout time + container memory**, arriving at ~10³+ items — that, not raster, is the trigger to build it.
- Also deferred: PasswordBox/mask char, multi-line TextBox + undo stack, drag-selection + list type-ahead, `DataTemplateSelector`/`ItemContainerStyleSelector`, interface-based implicit templates, `TabStripPlacement` Left/Right, vertical ProgressBar, drawn-caret fallback, smooth scrolling (offsets are direct properties, §3.9), `UpdateSourceTrigger` knob, `Thumb`/`Track` as public primitives, RelativeSource bindings in templates (TemplateBinding-only stance), routed `InputGesture` execution on MenuItem (display-only in v1 — a command/gesture map belongs with S3).

---

## 8. Open questions (max 3, with recommendations)

1. **Fork A confirmations for ContentPresenter auto-aliasing** (the policy itself is pinned, §2.2): (a) a read-through fallback must not create a store entry; (b) the shape of the typed per-property change-observer channel on another `UIObject` (no binding, no frame). *Recommend: a lightweight `IPropertyObserver` registration on the store's notification path* — both are store-surface questions, answerable inside Fork A's existing notification design.
2. **S1 region-invalidation seam** — whether S1 eventually rasters only dirty element bounds within a window scene (routing the union through `RestrictToDirtyRegions`). *Recommend: defer until C3/C5 profiling shows window-scene re-raster as a real bottleneck at target scale* — the §3.5 model is correct without it, and the seam is purely additive (per invariant 7's spirit, applied to S1).
3. **Band slack default** (`K = max(viewportRows, 8)`, §3.9) and whether horizontal banding is ever needed. *Recommend: keep the formula through C3, then tune against the ListBox-navigation and fast-scroll benchmarks; horizontal banding only if a real wide-content consumer appears.*

---

## 9. Critique disposition

**P0-1 (AccessText pipeline type-incoherent) — ACCEPTED.** Rebuilt end-to-end (§2.1/§2.2/§3.8): three pinned producers sharing one `Parse` — type-driven folding for `AccessText`-typed properties; a new Fork A per-type metadata flag `ParsesAccessKeyLiterals` set on exactly `ButtonBase.Content`/`MenuItem.Header`/`TabItem.Header`/`Label.Content` driving both XAML folding and runtime `GetAccessText()` string parsing (code-first `Content = "_Save"` now works; data strings never mangled); `MenuItem`/`TabItem` override `GetAccessText` to read Header. Rendering closed via WPF-kinship `ContentPresenter.RecognizesAccessKey` (default templates of flagged controls set it true). The label scenario got a real surface: a new `Label` control (flag + `Target` + next-focusable default); `TextBlock.Text` never folds. The implicit operator is now `explicit` (lossy parse must be visible).

**P0-2 (invalidation language contradicts the Scene API; scene granularity unpinned) — ACCEPTED.** §3.5 now pins the granularity contract with S1 (§4): one scene per window; element layers only for scroll viewports / indeterminate bar / S4 windows+popups. All "region re-raster"/"scene-local" claims restated as whole-window-scene re-raster, with the bounding argument made explicit (pseudo-class flips only on hit-chain change, cached text layouts, diff-renderer wire economy, small stable layer count). Region invalidation recorded as a future S1 seam never assumed (open question 2).

**P0-3 (extent-sized sub-scene unbounded) — ACCEPTED.** Replaced with the banded sub-scene policy (§3.9-ScrollViewer): band = viewport + 2K rows (K = max(viewport, 8)); in-band scroll = re-composite; past-slack = one band re-raster (re-anchor); scene allocation changes only on viewport resize (no `Scene` resize API needed); `AffectsRender`/`:selected` flips re-raster the band, never the extent. Measure constraint cap pinned (`MaxScrollExtent = 32,000`, clamp + diagnostic). Cost model recorded in §3.6/§7: virtualization pressure comes from layout O(n), not raster — the deferral rationale was corrected accordingly.

**P1-4 (BorderPen metadata self-contradictory) — ACCEPTED.** Named **nullity-escalation pattern** (§3.5): conditional-geometry properties register `AffectsRender` (focus pen swaps = render-only, no layout walk on focus moves) and the owner imperatively calls `InvalidateMeasure()` iff the geometry facet flipped (pen nullity; title presence; title-text changes within presence are render-only). Applied to `Control.BorderPen`, `Border.BorderPen`, `Border.Title`.

**P1-5 (indeterminate animation bypasses the store; direct-property effects unconfirmed) — ACCEPTED.** `ProgressBar.IndeterminateOffsetProperty` declared as a `StyledProperty<int>` with `AffectsComposite`; the theme storyboard targets it through the store (invariants 2/3 hold; no raw layer pokes). Fork A confirmation recorded (§4) that direct properties skip `PropertyEffects`, so `ScrollViewer` offsets hand-route layer-parameter refresh and are documented as non-storyboard-animatable; recorded as a Fork A confirmation line in §4.

**P1-6 (retraction lifecycle holes) — ACCEPTED.** (a) Normative `Unrealize` retraction set in §3.6 (ClearContainer → DataContext/logical/visual detach → frame retraction + DynamicResource unsubscription + external-hook unhook → `TemplateInstance.Detach()` on discard); (b) `ContainersChanged` subscription lifetime = presenter attach lifetime (§2.3, §3.6); (c) container reparent sequence pinned (old panel releases on detach → new presenter adopts in index order on first measure); (d) `OnTemplateDetaching` virtual added + "unhook before rewire" convention in §3.1/§3.3; ScrollViewer cites it.

**P1-7 (effective-enabled contradiction; CanExecuteChanged leak) — ACCEPTED.** Pinned lattice (§3.9 ButtonBase, §4 S1): effective = `IsEnabled` (styleable, never control-written) ∧ `IsEnabledCore` (WPF-kinship virtual; ButtonBase overrides with CanExecute) ∧ ancestor-effective, computed by S1 and pushed as `InteractionState.Disabled`. `CanExecuteChanged` subscribe-on-attach / unsubscribe-on-detach-and-command-change mandated. Former open Q2 closed by this pin.

**P1-8 (spoofable access-key gate) — ACCEPTED.** §3.8 + §4 S3 REQUIRES: gate evaluated against negotiator `TerminalCapabilities.Input` (or raw `VtInputDevice.Mode.KittyKeyboard` via the `Inner` walk), never decorated pipeline capabilities; KRS failure mode cited; re-evaluated on `RenegotiateAsync`; same source rule extended to the TabControl chord gate.

**P1-9 (scroll-detection overstatement) — ACCEPTED.** §4 contract corrected ("no Overlay fragment anywhere on the buffer; entire back buffer must match shifted"); SU/SD claim removed from ScrollViewer §3.9 and replaced with the precise eligibility note.

**P1-10 (auto-aliasing self-referential) — ACCEPTED.** §2.2 specifies read-through fallback (never an installed binding), typed observer subscription on the templated parent with no presenter store entry, notification re-realization guarded by `IsSet`, subscription lifetime = template-instance lifetime (torn down in `Detach()`); Q1 closed, residual Fork A confirmations recorded as open question 1.

**P1-11 (Text update trigger unpinned) — ACCEPTED.** Per-change push pinned as the two-way default for `TextBox.Text` (§2.4, §3.9, §4 S2 line); `UpdateSourceTrigger` knob deferred; SaveDialog example now works by construction.

**P1-12 (Ctrl+Tab not capability-honest) — ACCEPTED.** §3.9 TabControl: header arrows = universal primary; Ctrl+PageUp/PageDown added as the universal cycle chord (xterm-standard `CSI 5;5~`/`6;5~` modified-key encoding, also WPF kinship); Ctrl+Tab registered only when wire-distinguishable (Kitty disambiguation / modifyOtherKeys ≥ 2 / Win32 input mode), gate sourced per P1-8's rule.

**P2-13 (pressed-state cleanup) — ACCEPTED.** §3.9 ButtonBase: `OnLostMouseCapture → IsPressed = false` (no click); Space latch cleared on `OnLostFocus`; `ClickMode.Press` still takes capture (stated).

**P2-14 (PseudoClassMapping overload "is a Fork B addition") — REBUTTED, with one clarification accepted.** The multi-class projection overload is already pinned in the canonical winning proposal: `proposal-styling-hybrid.md` §2.4 declares `Register<TOwner, TValue>(StyledProperty<TValue>, Func<TValue, string?>, ReadOnlySpan<string>)` with the exact `bool?`→`:checked`/`:indeterminate` example — no contract delta exists. The adjacent *real* gap the critique brushed past is accepted: mappings take `StyledProperty`, so DirectProperty-backed pseudo-state (`:pressed`, `:open`, `:highlighted`) is written via direct `PseudoClassSet.Set` in the property-changed handler — sanctioned for control authors per Fork B, noted in §2.4 and §4.

**P2-15 (ToolTipService gaps) — ACCEPTED.** (a) Router-level hit-chain observation hook added to S3 REQUIRES; (b) `ShowOnFocusProperty` is `bool?` (null = auto from `MouseCapabilities.Motion`); (c) close-on-KeyDown excludes standalone modifier keys (Kitty `ReportAllKeysAsEscapeCodes` makes bare Shift a KeyEvent).

**P2-16 (`(•)` not ASCII) — ACCEPTED.** True-ASCII defaults `( ) (*)` / `[ ] [x] [-]`; `(•)`/`☑` moved to the `caps-unicode` tier with the rationale rewritten as "defense-covered Unicode, swapped via resources" (§3.9, §6.9).

**P2-17 (ProgressBar math + mechanism wording) — ACCEPTED.** Never-throw guards (Max==Min ⇒ 0%, clamp, NaN ⇒ 0%) matching the charts norm; mechanism restated as "brush `ColorAt` sampled per cell against the track rect; partials are foreground glyphs via `Set`" — the `FillRectangle` analogy dropped.

**P2-18 (spec hygiene) — ACCEPTED.** `HeaderedItemsControl` declared (§2.2); `ItemActivated` declared on ListBox (§2.3) with Enter parity; S8-owned arg shapes noted as C-phase deliverables; `RequireControl<T>` defined as the S8 namescope helper (runtime counterpart of X4 fields); `TextElement.*` registrations carry `AffectsRender`; chrome `[x]` pinned as `PART_CloseButton` (a real, hit-testable Button — not painted glyph art); part-validation timing unified to "immediately after `Instantiate`, before visual attach" in both §3.1 and §3.3.

**P2-19 (stamping + re-entrancy hardening) — ACCEPTED.** Foreign non-null `TemplatedParent` throws during the stamp walk (nested-control carve-out preserved automatically — null at walk time); re-entrant `Template` sets in `OnApplyTemplate` defer to next measure behind a guard (§3.1).

**P2-20 (runtime items-property changes) — ACCEPTED.** `ItemTemplate`/`ItemsPanel`/`ItemContainerStyle` change ⇒ Reset (regenerate); recorded as v1 policy (§2.3, §3.6).

**P2-21 (caret detach) — ACCEPTED.** `TextPresenter` calls `ITerminalCaretService.Clear` on detach **and** S1 drops publications from detached owners (§3.9, §4).

**P2-22 (markup brush staleness) — ACCEPTED.** TextBlock subscribes to `ResourceDictionary.Changed` while its `Markup` resolves registry brushes; re-parse/re-resolve/re-format on pulse; format cache keyed on (text, width, caps) so `RenegotiateAsync` invalidates too (§3.9).

**P2-23 (SetCurrentValue vs styles on IsSelected) — ACCEPTED.** Documented stance in §3.7: styling `IsSelected` via setters is unsupported; selectors react to `:selected`, never set it; the replace-on-re-promotion behavior is named.

**P2-24 (resource walk has no logical ancestors from a presenter) — ACCEPTED.** The templated-parent hop made explicit in the §3.4 chain (step 2) and in the S7 REQUIRES line — pinned jointly.

**P2-25 (menu popup churn) — ACCEPTED.** §3.9 Menu qualifies the cost (close+open = two layer-count changes = two full-target recomposites, Sixel re-emission); S4 REQUIRES gains the single-reusable-popup-layer-per-menu-session request on `IPopupHandle`.

**P2-26 (chrome lands C4, S4 ships phase 0) — ACCEPTED.** Interim story pinned (§3.9, §4, §7): S4 ships a primitive built-in chrome painter from its phase 0; S8's template replaces it at C4; the PART_* name contract freezes at C0 so S4 has no hidden dependency.

**P2-27 (AccessText.Key is char) — ACCEPTED.** Constraint declared on `Parse` (§2.1): the mnemonic must be a BMP letter/digit; anything else leaves the underscore literal with no key (deterministic rejection); matching is simple-case-folded.