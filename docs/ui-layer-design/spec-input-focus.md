# S3 — Input Routing, Focus, Access Keys, Commands (FINAL)

Subsystem spec for `Cursorial.UI` (namespace `Cursorial.UI.Input` unless noted). Conforms to `/tmp/cursorial-ui-design/DECISIONS.md` (vocabulary used verbatim: `UIObject`/`UIElement`/`Control`/`Window`, `StyledProperty<T>`, `UIPropertyKey<T>`, `InteractionState`, `AccessText`). Source-of-truth citations: input.md (esp. §7), rendering-session.md §7, design-doc.md. Incorporates the adversarial-critique resolutions listed in "Critique disposition" at the end.

---

## 1. Scope

**S3 owns:**
- The **dispatch pipeline** from the moment S6's pump hands an `InputEvent` (a `Cursorial.Input.Events` record) to the UI thread during frame N's input drain: classification, hit testing, route building, tunneling/bubbling, `Handled` semantics.
- The **UI event vocabulary**: `PreviewKeyDown/KeyDown/PreviewKeyUp/KeyUp`, `PreviewTextInput/TextInput` (incl. bracketed paste), `PreviewMouseDown/MouseDown/PreviewMouseUp/MouseUp/PreviewMouseMove/MouseMove/PreviewMouseWheel/MouseWheel`, `MouseEnter/MouseLeave`, `GotFocus/LostFocus`, `LostMouseCapture`; the `RoutedEvent` registry and per-element handler store.
- **Hit testing** over arranged geometry across S4's z-ordered surface stack (windows + popups), incl. modal occlusion and light-dismiss hooks; **mouse capture**; **hover-chain maintenance** (Enter/Leave diffing).
- **Signed cell geometry**: the `CellRect` carrier for everything hit-testing touches (composite-inclusive positions can be negative; Rendering's `Rect` is ushort-backed/non-negative per the DECISIONS geometry note and is reserved for arranged, pre-composite geometry).
- **InteractionState writes** for `PointerOver`, `Focused`, `FocusWithin`, `FocusVisible`, `AccessKeyCue` (and the protected surface control authors use for `Pressed`, which fans into a dispatcher-held pressed-holder set so terminal focus loss can clear `Pressed` window-wide — styling contract C8).
- **Keyboard focus**: physical focus (one element per app), logical focus scopes (WPF `FocusManager.IsFocusScope` semantics), `Focusable`/`IsTabStop`/`TabIndex`, Tab navigation with trapped (Cycle) scopes for modals, arrow-key directional navigation, read-only `IsFocused`/`IsKeyboardFocusWithin` via internal `UIPropertyKey<bool>`, the active-root record, the focus-visuals contract (`:focus`/`:focus-within`/`:focus-visible` are pseudo-classes; no adorner layer).
- **Access keys** (requirement 6, full design): the `AccessText` data model (XAML fold target), `AccessKeyManager` with a flat registry + activation-time scope resolution, the capability-gated Alt-held cue window with runtime self-correction for unverified Kitty pushes, the `:access-keys` root pseudo-class write, Alt+key chord activation on legacy terminals, multi-match cycling.
- **Commands**: `ICommand` (BCL `System.Windows.Input.ICommand`) on Button-likes, `CanExecute → IsEnabled` coupling via the `IsEnabledCore` seam, `KeyBinding`/`InputBindings` checked during bubble.

**S3 explicitly does NOT own:**
- The pump, queue, and frame loop (S6). S3 never touches `TerminalSession`, `VtInputDevice`, or transformers; it receives already-marshaled events. S6 configures the device pipeline (must include `WithClickSynthesis`; see §4).
- Window activation policy, z-order, popup open/close, light-dismiss *policy* (S4). S3 defines *what focus does* when S4 reports activation changes, and provides the hit-test/blocked-press/outside-press/surfaces-changed hooks S4's policies consume.
- `:active-window` (S4 writes it) and `:disabled` (property system pushes effective-IsEnabled per styling contract C5).
- Styling reaction to interaction state (S5 consumes `InteractionState`); rendering of underlines (S5's presenter pipeline reads the attached `ShowUnderline` property); Scene/CellBuffer anything (invariant 2).
- `ResizeEvent`, `DeviceResponseEvent`, `UnknownEvent` consumption — classified `NotUiInput` and returned to S6 for onward routing (device responses must reach whatever issued the query, per input.md §1).
- `PointerEvent` (pen/touch): no source emits it today; deferred (§7).

---

## 2. Public API sketch

### 2.1 Geometry: the signed carrier

```csharp
namespace Cursorial.UI;

/// Signed cell-rect. Rendering's Rect is ushort-backed/non-negative ("negative placement is expressed
/// via composite offsets" — DECISIONS); composite-inclusive geometry is exactly where negatives live
/// (window sliding in from off-screen, scrolled content under a negative PushTranslate). All
/// hit-testing-facing bounds use CellRect; Rect remains the arranged (pre-composite) carrier in S2.
public readonly record struct CellRect(int Column, int Row, int Columns, int Rows)
{
    public CellPosition Position => new(Column, Row);
    public Size Size => new(Columns, Rows);
    public int ColumnEnd => Column + Columns;     // exclusive
    public int RowEnd => Row + Rows;              // exclusive
    public bool Contains(int column, int row);
    public bool Contains(CellPosition position);
    public CellRect Translate(int columns, int rows);     // plain int math; never throws
    public static CellRect FromRect(in Cursorial.Rendering.Rect rect);   // widening, always safe
}
```

### 2.2 Routed events

```csharp
namespace Cursorial.UI.Input;

public enum RoutingStrategy : byte { Direct, Bubble, Tunnel }

public abstract class RoutedEvent
{
    public string Name { get; }
    public RoutingStrategy Strategy { get; }
    public Type OwnerType { get; }
    public Type ArgsType { get; }
    public int GlobalIndex { get; }                 // dense index into per-element handler stores
}

public sealed class RoutedEvent<TArgs> : RoutedEvent where TArgs : RoutedEventArgs
{
    public static RoutedEvent<TArgs> Register(string name, RoutingStrategy strategy, Type ownerType);
}

public class RoutedEventArgs
{
    public RoutedEventArgs() { }                                   // caller-owned construction
    public RoutedEventArgs(RoutedEvent routedEvent, UIElement source);
    public RoutedEvent RoutedEvent { get; internal set; }
    public UIElement OriginalSource { get; internal set; }
    public UIElement Source { get; internal set; }   // v1: always == OriginalSource (no template source adjustment; deferred §7)
    public bool Handled { get; set; }
    // OWNERSHIP: framework dispatches RENT pooled instances (per-type free-list, so nested RaiseEvent
    // rents a distinct instance — see §3.3); pooled args are valid only during their dispatch and are
    // debug-stamped (stale access throws). Caller-CONSTRUCTED args (new) are caller-owned: never
    // pooled, never stamped, valid after RaiseEvent returns. Copy values out to retain pooled args;
    // wrapped device records are immutable and always retainable.
}
```

### 2.3 Event args (each wraps the immutable device record — records are safe to retain, pooled args are not)

```csharp
public sealed class KeyEventArgs : RoutedEventArgs
{
    public KeyEvent Device { get; }                          // Cursorial.Input.Events.KeyEvent (retainable)
    public Key Key => Device.Key;
    public KeyModifiers Modifiers => Device.Modifiers;       // lock-free; match shortcuts on THIS (input.md gotcha)
    public KeyModifiers ExtendedModifiers => Device.ExtendedModifiers;
    public ReadOnlyMemory<char> Text => Device.Text;         // Key.Character identity = (Key, Text)
    public bool IsRepeat => Device.IsRepeat;
    public int RepeatCount => Device.RepeatCount;
    public uint? RawCode => Device.RawCode;
}

public sealed class TextInputEventArgs : RoutedEventArgs
{
    public ReadOnlyMemory<char> Text { get; }                // composed text or whole paste
    public bool FromPaste { get; }                           // true when sourced from PasteEvent (bracketed paste)
}

public class MouseEventArgs : RoutedEventArgs
{
    public MouseEvent Device { get; }
    public InputSurface Surface { get; }                     // the hit surface (or capture target's surface)
    public CellPosition SurfacePosition { get; }             // surface-local; may be negative under capture
    public CellPosition GetPosition(UIElement relativeTo);   // element-local int cells; may be negative.
        // Computed through terminal coordinates (terminal − relativeTo's surface origin − relativeTo's
        // LayoutBounds origin), so it is well-defined even when relativeTo sits on a different surface
        // than the event; cross-surface use is legal and documented (no throw).
    public MouseButtons ButtonsHeld => Device.ButtonsHeld;
    public KeyModifiers Modifiers => Device.Modifiers;
}

public sealed class MouseButtonEventArgs : MouseEventArgs
{
    public MouseButton Button => Device.Button;
    public int ClickCount => Device.ClickCount;
        // Multi-click count is carried by the event ClickCountTarget selects — ButtonDown in the
        // mandated pipeline — so this reads >1 ONLY on MouseDown; on MouseUp it is always 1.
        // Double-click logic belongs in OnMouseDown.
}

public sealed class MouseWheelEventArgs : MouseEventArgs
{
    public int WheelDeltaY => Device.WheelDeltaY;            // 1/120-notch units (input.md)
    public int WheelDeltaX => Device.WheelDeltaX;
    public int LinesPerNotch { get; set; } = 3;              // consumer hint; ScrollViewer converts
}

public enum FocusNavigationMethod : byte { Programmatic, Pointer, Tab, Directional, AccessKey, Restore }

public sealed class FocusChangedEventArgs : RoutedEventArgs
{
    public UIElement? OldFocus { get; }
    public UIElement? NewFocus { get; }
    public FocusNavigationMethod Method { get; }
}
```

### 2.4 `UIElement` input surface (partial — S3's slice of the shared class)

```csharp
namespace Cursorial.UI;

public partial class UIElement : UIObject, IInteractionStateSink
{
    // --- routed events (registry fields; CLR event sugar add/remove → AddHandler/RemoveHandler) ---
    public static readonly RoutedEvent<KeyEventArgs>        PreviewKeyDownEvent, KeyDownEvent, PreviewKeyUpEvent, KeyUpEvent;
    public static readonly RoutedEvent<TextInputEventArgs>  PreviewTextInputEvent, TextInputEvent;
    public static readonly RoutedEvent<MouseButtonEventArgs> PreviewMouseDownEvent, MouseDownEvent, PreviewMouseUpEvent, MouseUpEvent;
    public static readonly RoutedEvent<MouseEventArgs>      PreviewMouseMoveEvent, MouseMoveEvent;
    public static readonly RoutedEvent<MouseEventArgs>      MouseEnterEvent, MouseLeaveEvent;          // Direct
    public static readonly RoutedEvent<MouseWheelEventArgs> PreviewMouseWheelEvent, MouseWheelEvent;
    public static readonly RoutedEvent<FocusChangedEventArgs> GotFocusEvent, LostFocusEvent;            // Bubble
    public static readonly RoutedEvent<RoutedEventArgs>     LostMouseCaptureEvent;                      // Direct

    public void AddHandler<TArgs>(RoutedEvent<TArgs> routedEvent, EventHandler<TArgs> handler,
                                  bool handledEventsToo = false) where TArgs : RoutedEventArgs;
    public void RemoveHandler<TArgs>(RoutedEvent<TArgs> routedEvent, EventHandler<TArgs> handler) where TArgs : RoutedEventArgs;
    public void RaiseEvent(RoutedEventArgs args);            // builds + walks the route per strategy
    protected TArgs RentEvent<TArgs>(RoutedEvent<TArgs> routedEvent) where TArgs : RoutedEventArgs, new();
        // pooled-args rental for control authors; a rented args must be passed to exactly one
        // RaiseEvent call, which returns it to the pool on completion (§3.3)

    // --- class-handler stance: the On* virtuals ARE the class-handler stage (invoked at every route
    //     node, before that node's instance handlers, skipped once Handled). No open static
    //     class-handler registry in v1 (deferred §7).
    protected virtual void OnPreviewKeyDown(KeyEventArgs e) { }
    protected virtual void OnKeyDown(KeyEventArgs e) { }
    protected virtual void OnPreviewKeyUp(KeyEventArgs e) { }
    protected virtual void OnKeyUp(KeyEventArgs e) { }
    protected virtual void OnTextInput(TextInputEventArgs e) { }
    protected virtual void OnPreviewMouseDown(MouseButtonEventArgs e) { }
    protected virtual void OnMouseDown(MouseButtonEventArgs e) { }
    protected virtual void OnMouseUp(MouseButtonEventArgs e) { }
    protected virtual void OnMouseMove(MouseEventArgs e) { }
    protected virtual void OnMouseWheel(MouseWheelEventArgs e) { }
    protected virtual void OnMouseEnter(MouseEventArgs e) { }
    protected virtual void OnMouseLeave(MouseEventArgs e) { }
    protected virtual void OnGotFocus(FocusChangedEventArgs e) { }
    protected virtual void OnLostFocus(FocusChangedEventArgs e) { }
    protected virtual void OnLostMouseCapture(RoutedEventArgs e) { }

    // --- focus / hit-test properties ---
    public static readonly StyledProperty<bool> FocusableProperty;        // default false; controls override metadata
    public static readonly StyledProperty<bool> IsTabStopProperty;        // default true (relevant only when Focusable)
    public static readonly StyledProperty<int>  TabIndexProperty;         // default int.MaxValue; ties → document order
    public static readonly StyledProperty<bool> IsHitTestVisibleProperty; // default true

    // read-only, framework-written. The UIPropertyKey<bool> write capabilities are INTERNAL —
    // holding the key IS the write right (DECISIONS Fork A); publishing them would let any consumer
    // desync focus state from the FocusManager. Only the read-only StyledProperty handles are public.
    public static StyledProperty<bool> IsFocusedProperty { get; }             // FocusManager writes via internal key
    public static StyledProperty<bool> IsKeyboardFocusWithinProperty { get; } // FocusManager writes along chain
    public bool IsFocused => GetValue(IsFocusedProperty);
    public bool IsKeyboardFocusWithin => GetValue(IsKeyboardFocusWithinProperty);
    public bool IsPointerOver { get; }                       // reads the InteractionState bit; not a styled property

    public InputBindingCollection InputBindings { get; }     // lazy-alloc; checked during bubble (§3.2)

    public bool Focus(FocusNavigationMethod method = FocusNavigationMethod.Programmatic);  // → FocusManager.SetFocus
    public bool CaptureMouse();                              // → InputDispatcher.CaptureMouse(this)
    public void ReleaseMouseCapture();

    // --- interaction-state seam (IInteractionStateSink per styling spec §2.5; explicit impl) ---
    // Framework services + control authors only (e.g. ButtonBase sets Pressed). Pressed flips
    // additionally fan into the dispatcher's pressed-holder set so FocusEvent{HasFocus:false} can
    // clear Pressed window-wide (styling contract C8) — including keyboard-held press visuals that
    // never took mouse capture.
    protected void SetInteractionState(InteractionState state, bool active);

    // --- enabled coupling seam (consumed by the property system's effective-IsEnabled pipeline) ---
    protected internal virtual bool IsEnabledCore => true;   // ButtonBase overrides → command CanExecute
    protected void InvalidateIsEnabledCore();                // property system recomputes effective enabled + :disabled

    // --- hit-test seam ---
    protected internal virtual bool HitTestCore(CellPosition windowPosition) => true;  // bounds already verified by walker
}
```

### 2.5 The dispatcher (S6-facing entry point)

```csharp
public enum InputDispatchResult : byte { Dispatched, NotUiInput }
public enum InputModality : byte { Keyboard, Pointer }

public sealed class InputDispatcher
{
    public InputDispatcher(IInputSurfaceProvider surfaces, FocusManager focus, AccessKeyManager accessKeys);

    /// Called by S6 once at startup and again after every TerminalSession.RenegotiateAsync
    /// (capabilities snapshot is replaced atomically — re-read, never cache; input.md §6).
    /// Renegotiation parks the pump (~500 ms; keystrokes dropped) — a swallowed Alt Up can vanish
    /// with no FocusEvent, so this call also unconditionally clears Alt-bracket/sticky-cue state
    /// (delegated to AccessKeyManager.OnCapabilitiesChanged).
    public void OnCapabilitiesChanged(TerminalCapabilities capabilities);

    /// THE entry point. UI thread only (VerifyAccess), called during frame N's input drain.
    /// Key/Mouse/Focus/Paste events are dispatched synchronously; Resize/DeviceResponse/Unknown/
    /// Pointer return NotUiInput for S6 to route onward. Synthesized MouseEventKind.Click is ignored
    /// (pipeline contract forbids it; defensive no-op).
    public InputDispatchResult ProcessEvent(InputEvent inputEvent);

    /// Re-run hover diffing without pointer movement. S6 calls this once per rendered frame, after
    /// layout AND composite parameters are final (§4) — covers content moving under a stationary
    /// cursor via layout, composite-offset animation (invariant 3: slides never re-layout), scrolling,
    /// and surface-stack changes. No-op until the first real mouse event has been observed
    /// (LastPointerPosition == null). Also executes any hover work deferred by detach (§3.9).
    public void UpdateHover();

    /// S4 MUST call on any surface-stack mutation (open/close/modal-state/z-order change), even
    /// mid-drain with no mouse event in flight. Synchronously re-validates mouse capture (force-
    /// release if the target's surface closed or became modal-blocked) and marks hover dirty so the
    /// frame's UpdateHover re-diffs.
    public void OnSurfacesChanged();

    // capture
    public UIElement? MouseCaptureTarget { get; }
    public bool CaptureMouse(UIElement element);             // false if element not attached/visible
    public void ReleaseMouseCapture(UIElement element);      // no-op unless element holds capture

    // pointer state
    public CellPosition? LastPointerPosition { get; }        // terminal coords; null until first mouse event
    public MouseButtons ButtonsHeld { get; }
    public InputModality LastModality { get; }               // feeds :focus-visible

    // hit testing (public — S4 uses for cursor-shape/light-dismiss policy; tooltips later)
    public UIElement? HitTest(CellPosition terminalPosition, out InputSurface surface);

    public event Action<bool>? TerminalFocusChanged;         // raised from FocusEvent; S4 listens

    internal void OnElementDetached(UIElement element);      // tree hygiene (§3.9); UIElement detach calls all services
}
```

### 2.6 Surfaces (the S4 contract shape)

```csharp
/// One hit-testable plane: a Window or a Popup. Provided by S4, topmost first.
/// Surfaces are HIT-OPAQUE within Bounds: a point inside a surface's bounds never falls through to a
/// surface below — if no descendant claims it, the surface ROOT is the hit (windows/popups are drawn
/// as opaque rectangles; click-through to a lower window on a cell the upper window paints would be
/// visually incoherent). Blocked surfaces are likewise opaque but yield no hit element (§3.4).
public readonly record struct InputSurface(
    UIElement Root,
    CellRect Bounds,              // terminal cells, SIGNED — must reflect the surface's VISUAL position
                                  // including any composite offset S4 animates (a window mid-slide
                                  // hit-tests where it is drawn, possibly at negative origin)
    bool IsBlockedByModal,        // a modal above this surface's ownership chain is open
    bool IsLightDismiss,          // popup/menu: presses outside → NotifyOutsidePress
    object? SurfaceId);           // S4's correlation key

public interface IInputSurfaceProvider
{
    /// Snapshot valid for the duration of one ProcessEvent call. Topmost first.
    ReadOnlySpan<InputSurface> GetSurfacesTopmostFirst();

    /// A press landed outside this light-dismiss surface. Return true to swallow the press
    /// (menus), false to let it also dispatch to what it hit (combo-box style click-through).
    bool NotifyOutsidePress(in InputSurface surface, CellPosition terminalPosition);

    /// A press landed on a modal-blocked surface; S4 may flash/bonk the blocking modal.
    void NotifyBlockedPress(in InputSurface blockedSurface, CellPosition terminalPosition);
}
```

### 2.7 Focus

```csharp
public sealed class FocusManager
{
    public UIElement? FocusedElement { get; }                          // physical keyboard focus; one per app

    /// The active surface root, recorded from OnWindowActivated/OnWindowDeactivated. This is the
    /// load-bearing definition of "active surface root" used by key-dispatch fallback, paste
    /// targeting, and the access-key fallback scope. Null at startup / when all windows are closed;
    /// with null ActiveRoot and null FocusedElement, key/paste events are DROPPED (returned as
    /// Dispatched with an empty route — deterministic; never routed to "topmost", which is not
    /// activation under S4 policies like always-on-top palettes).
    public UIElement? ActiveRoot { get; }

    public bool SetFocus(UIElement target, FocusNavigationMethod method = FocusNavigationMethod.Programmatic);
    public void ClearFocus();                                          // rare; keys then target ActiveRoot

    // logical focus scopes (WPF FocusManager.IsFocusScope semantics)
    public static readonly AttachedProperty<bool> IsFocusScopeProperty;        // Window=true, Menu/Popup roots=true, ToolBar=true
    public static readonly AttachedProperty<UIElement?> FocusedElementProperty; // scope memory (framework-written)
    public static UIElement GetFocusScope(UIElement element);          // nearest self-or-ancestor scope (root fallback)
    public static UIElement? GetFocusedElement(UIElement scope);       // the scope's remembered element

    public bool MoveFocus(FocusNavigationDirection direction);
        // From FocusedElement; when FocusedElement is null, Next/Previous start from ActiveRoot's
        // first/last tab-ordered focusable (directional returns false). False if no candidate.

    // S4 calls these on activation changes (S4 owns the policy; S3 owns the consequence)
    public void OnWindowActivated(UIElement windowRoot);   // records ActiveRoot; restore scope memory, else first tab-ordered focusable, else none
    public void OnWindowDeactivated(UIElement windowRoot); // clears ActiveRoot if it matches; physical focus leaves; logical memory retained

    internal void OnElementDetached(UIElement element);
}

public enum FocusNavigationDirection : byte { Next, Previous, Up, Down, Left, Right }

public static class KeyboardNavigation
{
    public static readonly AttachedProperty<KeyboardNavigationMode> TabNavigationProperty;         // default Continue; Window/Popup roots default Cycle
    public static readonly AttachedProperty<DirectionalNavigationMode> DirectionalNavigationProperty; // default None
}
public enum KeyboardNavigationMode : byte { Continue, Cycle, None }
public enum DirectionalNavigationMode : byte { None, Contained, Cycle }
```

Focus visuals contract: there is **no adorner layer**. `:focus` (physical), `:focus-within` (ancestor chain), `:focus-visible` (focus arrived via keyboard-like navigation) are the only focus visuals mechanism; control templates style them (e.g. `^:focus { TextAttributes: Bold|Underline }` per the styling proposal's terminal conventions). `IsFocused`/`IsKeyboardFocusWithin` exist as properties for `When` data-conditions and code, not as a parallel styling path.

### 2.8 Access keys (requirement 6)

```csharp
/// The one data model, two producers (DECISIONS Fork C): the XAML loader folds AccessText-TYPED
/// property literals (Header="_File") at parse time; code calls Parse. Object-typed content
/// properties are NOT folded by the loader — string content acquires its AccessText at presentation
/// time via ContentPresenter.RecognizesAccessKey (same Parse, §3.7 "registration"). "__" escapes a
/// literal underscore; first remaining '_' marks the key.
public sealed record AccessText(string Text, char Key, int KeyIndex)
{
    public bool HasKey => KeyIndex >= 0;                       // Key normalized via char.ToUpperInvariant
    public static AccessText Parse(string raw);                // "_File" → ("File", 'F', 0); "Save __As" → ("Save _As", '\0', -1)
    public static AccessText Literal(string text);             // (text, '\0', -1)
    public static implicit operator AccessText(string raw) => Parse(raw);
}

public enum AccessKeyMode : byte
{
    AltHeld,         // capability-gated: cue toggles with physical Alt bracketing (+ chord-flash self-correction, §3.7)
    AlwaysVisible,   // legacy fallback: cue permanently on (requirement 6's "permanently visible otherwise")
}

public sealed class AccessKeyManager
{
    public AccessKeyMode Mode { get; }                         // derived in OnCapabilitiesChanged (§3.7)
    public bool IsCueActive { get; }                           // == the :access-keys bit on the active scope/window roots

    /// Flat registry: char → list of targets. NO scope is captured at registration time — scope
    /// membership is resolved at ACTIVATION time by walking each candidate's ancestor chain against
    /// the live scope stack (§3.7), so attach-vs-PushScope ordering and reparenting are non-issues.
    public void Register(char key, UIElement target);          // target should implement IAccessKeyTarget
    public void Unregister(char key, UIElement target);

    // scope stack: base scope follows window activation; S4 pushes/pops popup & menu scopes (LIFO)
    public void PushScope(UIElement scopeRoot);
    public void PopScope(UIElement scopeRoot);
    public void OnWindowActivated(UIElement windowRoot);

    /// Style/template target: the default theme contains
    ///   :access-keys ContentPresenter { AccessKeyManager.ShowUnderline: true }
    /// so requirement 6 is pure styling downstream of the cue bit (styling proposal §"capability-honest").
    public static readonly AttachedProperty<bool> ShowUnderlineProperty;   // default false

    public event Action? EnterMenuMode;                        // Alt tap (down+up, no intervening key) on AltHeld terminals

    public void OnCapabilitiesChanged(TerminalCapabilities capabilities);
        // re-derives Mode AND unconditionally clears Alt side bits + sticky cue (renegotiation parks
        // the pump; an Alt Up released during the park vanishes with no FocusEvent)

    internal bool ProcessKeyDown(KeyEventArgs e);              // dispatcher's unhandled tail (§3.2)
    internal void OnAltDown(Key side); internal void OnAltUp(Key side);   // LeftAlt/RightAlt bracketing; Synthesized events excluded (§3.2)
    internal void OnStaleAltInferred();                        // dispatcher's pre-stage inference guard (§3.2)
    internal void OnFocusChanged(FocusNavigationMethod method);// FocusManager.SetFocus calls; Pointer clears sticky cue
    internal void OnTerminalFocusLost();                       // FocusEvent{HasFocus:false} → clear Alt-held UNCONDITIONALLY (input.md §7)
    internal void OnElementDetached(UIElement element);
}

public sealed class AccessKeyEventArgs : EventArgs
{
    public char Key { get; }
    public bool IsMultiMatch { get; }       // true → focus-only cycling step; false → invoke
    public UIElement Target { get; }
}

public interface IAccessKeyTarget       // Button, MenuItem, Label (forwards focus to Target), TabItem…
{
    bool IsAccessKeyEligible { get; }   // effectively visible && effectively enabled
    void OnAccessKey(AccessKeyEventArgs e);
}
```

### 2.9 Commands & key bindings

```csharp
// ICommand = System.Windows.Input.ICommand (System.ObjectModel — BCL, no WPF dependency).
// No RoutedCommand / CommandManager.RequerySuggested in v1 (deferred §7) — MVVM-style commands
// raise their own CanExecuteChanged.

public sealed record KeyGesture(Key Key, KeyModifiers Modifiers = KeyModifiers.None, string? Character = null)
{
    // Key.Character gestures MUST carry Character; named keys must not.
    public static KeyGesture Parse(string gesture);            // "Ctrl+S", "Alt+Enter", "F5", "Ctrl+Shift+P"
    public bool Matches(KeyEventArgs e);
    // Matching: e.Modifiers (lock-free, NEVER ExtendedModifiers — input.md gotcha) must equal Modifiers
    // exactly (ModifierMask). Named keys: Key equality. Character keys: ordinal case-insensitive
    // compare of Character against e.Text — (Key, Text) is the identity of printable keys.
}

public class InputBinding
{
    public KeyGesture? Gesture { get; set; }
    public ICommand? Command { get; set; }
    public object? CommandParameter { get; set; }
}
public sealed class KeyBinding : InputBinding
{
    public KeyBinding() { }
    public KeyBinding(KeyGesture gesture, ICommand command);
}
public sealed class InputBindingCollection : Collection<InputBinding> { }
```

### 2.10 Consumer example — `ButtonBase` (control author exercising the whole surface)

```csharp
public abstract class ButtonBase : ContentControl, IAccessKeyTarget
{
    // Content stays object? (inherited from ContentControl) — the standard content model (icon+text
    // panels, arbitrary content) is preserved. String content is presented through the template's
    // ContentPresenter with RecognizesAccessKey = true, which parses it via AccessText.Parse at
    // presentation time, renders per ShowUnderlineProperty, and registers/unregisters the key with
    // AccessKeyManager on behalf of its nearest IAccessKeyTarget templated parent (this button).
    // ButtonBase therefore contains NO access-key registration code of its own (joint S3/S5/S8
    // contract, §4); it only implements IAccessKeyTarget.

    public static readonly StyledProperty<ICommand?> CommandProperty =
        UIProperty.Register<ButtonBase, ICommand?>(nameof(Command));   // default effects — Command is
        // THE canonical binding target (Command="{Binding Save}"); it carries no visual effects flags
    public static readonly StyledProperty<object?> CommandParameterProperty =
        UIProperty.Register<ButtonBase, object?>(nameof(CommandParameter));

    public static readonly RoutedEvent<RoutedEventArgs> ClickEvent =
        RoutedEvent<RoutedEventArgs>.Register("Click", RoutingStrategy.Bubble, typeof(ButtonBase));

    static ButtonBase()
    {
        FocusableProperty.OverrideDefault<ButtonBase>(true);
    }

    private bool _canExecute = true;
    private bool _isPressed;
    protected internal override bool IsEnabledCore => _canExecute;   // CanExecute → IsEnabled coupling

    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(in args);
        if (args.Property == CommandProperty && IsAttachedToTree)
        {
            if (args.GetOldValue<ICommand?>() is { } old) old.CanExecuteChanged -= OnCanExecuteChanged;
            if (args.GetNewValue<ICommand?>() is { } cmd) cmd.CanExecuteChanged += OnCanExecuteChanged;
            UpdateCanExecute();
        }
        else if (args.Property == CommandParameterProperty) UpdateCanExecute();
    }

    protected override void OnAttachedToTree()   // hook on attach; symmetric detach (leak-bounded)
    {
        base.OnAttachedToTree();
        if (Command is { } cmd) { cmd.CanExecuteChanged += OnCanExecuteChanged; UpdateCanExecute(); }
    }
    protected override void OnDetachedFromTree()
    {
        if (Command is { } cmd) cmd.CanExecuteChanged -= OnCanExecuteChanged;
        base.OnDetachedFromTree();
    }
    private void OnCanExecuteChanged(object? s, EventArgs e) => UpdateCanExecute();
    private void UpdateCanExecute()
    {
        var can = Command?.CanExecute(GetValue(CommandParameterProperty)) ?? true;
        if (can != _canExecute) { _canExecute = can; InvalidateIsEnabledCore(); }  // property system flips :disabled
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.Button != MouseButton.Left) return;
        CaptureMouse(); SetPressed(true); Focus(FocusNavigationMethod.Pointer); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!ReferenceEquals(Input.MouseCaptureTarget, this)) return;
        var p = e.GetPosition(this);                          // element-local; may be negative under capture
        SetPressed((uint)p.Column < (uint)LayoutBounds.Columns && (uint)p.Row < (uint)LayoutBounds.Rows);
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.Button != MouseButton.Left || !ReferenceEquals(Input.MouseCaptureTarget, this)) return;
        var clicked = _isPressed; SetPressed(false); ReleaseMouseCapture(); e.Handled = true;
        if (clicked) OnClick();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Activate on Down, not Up: KeyUp exists only on Kitty/Win32 terminals (input.md §7) — never gate
        // core activation on it. IsRepeat guards held-key autofire.
        if ((e.Key == Key.Enter || e.Key == Key.Space) && !e.IsRepeat && e.Modifiers == KeyModifiers.None)
        { OnClick(); e.Handled = true; }
    }
    protected override void OnLostMouseCapture(RoutedEventArgs e) => SetPressed(false);

    bool IAccessKeyTarget.IsAccessKeyEligible => IsEffectivelyVisible && IsEffectivelyEnabled;
    void IAccessKeyTarget.OnAccessKey(AccessKeyEventArgs e)
    { Focus(FocusNavigationMethod.AccessKey); if (!e.IsMultiMatch) OnClick(); }

    protected virtual void OnClick()
    {
        RaiseEvent(RentEvent(ClickEvent));                    // pooled rental, returned when RaiseEvent completes
        if (Command is { } cmd) { var p = GetValue(CommandParameterProperty); if (cmd.CanExecute(p)) cmd.Execute(p); }
    }
    private void SetPressed(bool value)
    { if (_isPressed != value) { _isPressed = value; SetInteractionState(InteractionState.Pressed, value); } }
    // Pressed flips fan into the dispatcher's pressed-holder set (C8): terminal focus loss clears
    // Pressed even when set without capture (e.g. a Space-held press visual on Kitty).
}
```

---

## 3. Mechanics

### 3.1 Event classification (`ProcessEvent`)

UI thread, synchronous, called per queued event during frame N's drain. Switch on record type:

| Device event | Action |
|---|---|
| `KeyEvent` Down | pre-stage (§3.2 step 1) → key dispatch (§3.2) |
| `KeyEvent` Up | pre-stage (Alt tracking) → Preview/KeyUp route to focused element (no tail processing) |
| `MouseEvent` ButtonDown/Up/Move/Drag/Wheel | mouse dispatch (§3.4); `Kind == Click` → ignore (pipeline contract: S6 must not enable `SynthesizeClickEvents`) |
| `FocusEvent` | `HasFocus:false`: `AccessKeyManager.OnTerminalFocusLost()` (clears Alt-held + sticky + cue in AltHeld mode), release mouse capture (Direct `LostMouseCapture`), clear hover chain (Leave events + `PointerOver` bits), clear **every element in the pressed-holder set** (`Pressed` off; the set covers keyboard-held press visuals that never took capture — C8); keyboard focus is **retained** (state restores intact on refocus). Then raise `TerminalFocusChanged(hasFocus)` for S4. |
| `PasteEvent` | `PreviewTextInput`/`TextInput` with `FromPaste = true` routed at the focused element (or `FocusManager.ActiveRoot`; both null → dropped). Without `Protocol.BracketedPaste` pastes look like typing — nothing to classify (input.md gotcha; text controls own any heuristics). |
| `ResizeEvent`, `DeviceResponseEvent`, `UnknownEvent`, `PointerEvent` | return `NotUiInput` (S6 routes resize to layout/S4, device responses to their issuers, logs unknowns) |

Modality bookkeeping: any dispatched `MouseEvent` sets `LastModality = Pointer` and `LastPointerPosition`; any `KeyEvent` sets `Keyboard`.

### 3.2 Key dispatch order (Down)

1. **Pre-stage** (synchronous, before any routing; all parts skip `KeyEvent { Synthesized: true }` — a stray `KeyReleaseSynthesizer` must not corrupt the Alt bracket, mirroring the synthesized-Click defense):
   a. **Alt tracking**: `Key.LeftAlt`/`Key.RightAlt` Down/Up updates the cue state machine (§3.7), then is *also* routed normally (a handler may care).
   b. **Stale-Alt inference**: any Down for a key *other than* LeftAlt/RightAlt whose `Modifiers` lacks `Alt` while a side bit is set ⇒ the bracket's Up was lost (the terminal's per-event modifier state is ground truth) ⇒ `OnStaleAltInferred()` clears side bits (+ cue unless sticky).
   c. **Sticky-cue Esc**: when the sticky cue (menu mode) is active, `Key.Escape` Down clears sticky + cue and is **consumed** (handled; not routed) — menu mode is modal to Esc, so a focused TextBox that would otherwise handle Esc in step 4 cannot strand the cue.
2. **Target** = `FocusManager.FocusedElement` ?? `FocusManager.ActiveRoot`. Both null → the event is dropped (returns `Dispatched`, empty route).
3. **Tunnel** `PreviewKeyDown` root→target. If `Handled`: bubble phase runs handledEventsToo-subscribers only; skip steps 5–7.
4. **Bubble** `KeyDown` target→root. At each node: `OnKeyDown` virtual → instance handlers → **InputBindings sweep**: if still unhandled and the node has `InputBindings`, first `KeyGesture.Matches(e)` whose command `CanExecute` → `Execute`, `Handled = true`.
5. **Unhandled tail — access keys**: `AccessKeyManager.ProcessKeyDown(e)` (§3.7 activation rules). Handles the Alt-held unmodified key (capable terminals), the `Modifiers == Alt` chord (all terminals), sticky-cue keys, and the menu-mode unmatched-key swallow.
6. **Unhandled tail — navigation**: `Tab`/`Shift+Tab` (Modifiers ∈ {None, Shift}) → `MoveFocus(Next/Previous)` (§3.6); arrow keys with `Modifiers == None` → directional navigation if the focused element sits in a container with `DirectionalNavigation != None`. Navigation marks handled only when it actually moved focus (so a TextBox that handled its arrows in step 4 is never disturbed, and an unhandled arrow at a window without directional nav stays unhandled).
7. **TextInput synthesis**: if still unhandled, `Key == Key.Character`, `Text` non-empty, and `(Modifiers & (Control|Alt|Super|Hyper|Meta)) == 0` (Shift allowed) → tunnel/bubble `PreviewTextInput`/`TextInput` at the focused element. The modifier mask keeps chords out of text widgets even when Kitty `ReportAssociatedText` attaches text to them; AltGr-composed characters arrive on terminals as plain text without the Alt bit, so they pass.

KeyUp: pre-stage (1a) + steps 2–4 only (vocabulary completeness for Kitty/Win32 paths); never drives activation logic in framework controls.

### 3.3 Route building, pooling, Handled

- Route = visual-parent walk from target to its surface root, written into a pooled `List<UIElement>` (`_routeScratch`, cleared not reallocated; nested dispatch rents a fresh scratch from a small free-list). Depth is ~10–20 at "hundreds of elements" scale.
- Tunnel iterates the list in reverse; bubble forward; `Direct` skips the walk.
- Per node: virtual `On*` (class stage) then instance handlers in registration order from the element's `EventHandlerStore` (lazy array of `(RoutedEvent.GlobalIndex, Delegate, bool handledEventsToo)` entries; most elements have none — null store, zero cost). Once `Handled`, only `handledEventsToo` entries are invoked (virtuals are not).
- Preview/main pairs share one pooled args instance (WPF input-event semantics: handling the tunnel suppresses the bubble, modulo handledEventsToo).
- **Args pooling — ownership rules**: the dispatcher keeps a small **per-concrete-type free-list stack** (not a single instance). A framework dispatch (or `RentEvent<TArgs>`) **pops** an instance (or allocates on empty), initializes it, and **pushes it back when its `RaiseEvent`/dispatch completes** — so a legal nested `RaiseEvent` of the same args type simply rents a *different* instance; nothing is reset mid-route. Pool depth is naturally bounded by dispatch nesting (debug-asserted cap 16). Debug builds stamp rented args with a monotonic version invalidated at return; touching a stale pooled args throws. **Caller-constructed args** (`new RoutedEventArgs(...)` passed to public `RaiseEvent`) are **caller-owned**: never pooled, never stamped, valid after `RaiseEvent` returns. A rented args must be passed to exactly one `RaiseEvent`. The wrapped device records (`KeyEvent`, `MouseEvent`) are immutable and retainable (input.md buffer-lifetime contract) — handlers copy the record, never the pooled args.
- Handler exceptions **propagate** out of `ProcessEvent` to S6's drain loop (fail fast; S6 decides crash-vs-log); all pooled resources and interaction scopes along the unwind path are `using`-protected. The pump itself is unaffected (it is on the other side of the queue).
- Re-entrancy: `RaiseEvent` during dispatch is legal (synchronous, nested); `ProcessEvent` re-entry is a programming error (debug assert).

### 3.4 Mouse dispatch, hit testing, hover, capture

**Hit test** (`HitTest(p)`), in terminal coordinates:

```
for surface in provider.GetSurfacesTopmostFirst():
    if !surface.Bounds.Contains(p): continue
    if surface.IsBlockedByModal:
        if press: provider.NotifyBlockedPress(surface, p)
        return (null, surface)                  // blocked surfaces OCCLUDE: terminate the scan —
                                                // hover/wheel/drag must not bleed to surfaces below
    hit = Descend(surface.Root, p - surface.Bounds.Position)
    return (hit ?? surface.Root, surface)       // surfaces are hit-opaque within Bounds: a point no
                                                // descendant claims hits the surface ROOT, never a
                                                // surface below (windows/popups are opaque rects)
return (null, default)

Descend(e, p):                                  // p in window coords (signed int math)
    if !e.IsEffectivelyVisible or !e.IsHitTestVisible: return null
    inBounds = e.LayoutBounds.Contains(p)        // LayoutBounds is a CellRect incl. composite offset (§4)
    if !inBounds and e.ClipsToBounds: return null   // prune
    for child in e.VisualChildren in REVERSE paint order:   // topmost child first
        r = Descend(child, p); if r != null: return r
    return inBounds && e.HitTestCore(p) ? e : null
```

Tests pinned: hover over a modal's blocked owner produces no hover chain anywhere; a press on a window's empty area hits the window root, not the window below. Cost per event: O(depth × children-scanned) integer rect tests, zero allocation. This is the budget for any-event motion tracking (Move per cell crossed, on by default — input.md perf notes). Non-clipping subtrees defeat pruning; v1 contract is `ClipsToBounds = true` default on panels (a named S2 REQUIRES, §4).

**Light dismiss**: on `ButtonDown`, before dispatch, the sweep covers **every `IsLightDismiss` surface above the hit surface — or all of them when nothing was hit** (press on empty terminal area must still close menus): `provider.NotifyOutsidePress(...)` per surface; if any returns true the press is swallowed (menus close and eat the click). Because surfaces are hit-opaque, a press *inside* a light-dismiss surface's bounds always hits that surface (at worst its root) and is never simultaneously an outside press for it.

**Dispatch target**: capture target if capture held, else hit element. Wheel always targets the *hit* element (pointer position, not focus — WPF semantics). No hit and no capture → event dropped. Positions: `SurfacePosition = terminal − surface.Bounds.Position`; `GetPosition(rel)` computes through terminal coordinates (§2.3) — plain int math on `CellPosition`/`CellRect` (may be negative; Rendering's `Rect` is never used for these carriers per the DECISIONS geometry note).

**Hover chain** (Enter/Leave + `:pointerover`): the dispatcher retains `_hoverChain` (pooled list, root→leaf, from the last hit). On every Move/Drag (and `UpdateHover()` once per rendered frame, §2.5): hit test → find common prefix with the old chain → **phase 1 (state)**: inside one `using`-protected `BeginInteractionUpdate` batch (styling spec §3.5), clear `PointerOver` on the removed suffix and set it on the added suffix; copy both suffixes into pooled snapshot arrays; dispose the scope. **Phase 2 (events)**: raise `MouseLeave` (Direct) over the removed-suffix snapshot **deepest-first**, then `MouseEnter` (Direct) over the added-suffix snapshot **outermost-first**. Handlers therefore observe post-restyle state, never iterate the live pooled chain, and a handler that detaches elements only marks the deferred hover refresh (§3.9) — no mutation of a list under iteration; a throwing handler cannot leave the interaction scope open (already disposed). Then route `PreviewMouseMove`/`MouseMove` to the dispatch target. PointerOver is set on the full chain (element + ancestors), matching the styling spec's `InteractionState` doc. If `MouseCapabilities.Motion` is false the bits simply never set — capability-honest, no polyfill (styling proposal).

**During capture**: hit testing still runs (cheap) so the hover chain and `:pointerover` stay honest, but all events route to the capture target with positions translated into its surface. Element-only capture (no subtree mode in v1).

**Capture lifecycle**: granted only to attached, effectively-visible elements. Force-released (with Direct `LostMouseCaptureEvent`) on: explicit release, target detach (§3.9), `FocusEvent{HasFocus:false}`, and — via `OnSurfacesChanged()` (§2.5), which S4 **must** call on any surface open/close/modal-state/z-order change — the target's surface closing or becoming modal-blocked. The surfaces-changed hook is the named detection mechanism: a modal opened by keyboard mid-drag releases the capture that same call, not whenever the next mouse event happens to arrive. Buttons release explicitly on MouseUp (control logic, not framework policy).

**ClickCount** arrives baked into `MouseEvent` by `MouseClickSynthesizer` upstream (S6 pipeline contract: `ClickCountTarget.ButtonDown`, `SynthesizeClickEvents = false`); the dispatcher adds no click timing of its own (the synthesizer is deterministic off event timestamps — input.md §4). Consequence (documented on `MouseButtonEventArgs.ClickCount`): the count surfaces on `MouseDown` only; `MouseUp` always reads 1.

### 3.5 Focus transitions

`SetFocus(target, method)` algorithm:

1. `VerifyAccess`. Validate: attached, `Focusable`, effectively enabled, effectively visible; else return false (no ancestor fallback — callers decide).
2. Same element → update `FocusVisible` if `method` is keyboard-like, notify `AccessKeyManager.OnFocusChanged(method)`, return true.
3. Compute old chain (focused→root) and new chain; find divergence.
4. One `BeginInteractionUpdate` batch: old element clears `Focused|FocusVisible`, old-only ancestors clear `FocusWithin`; new element sets `Focused` (+`FocusVisible` when `method ∈ {Tab, Directional, AccessKey, Restore}` or (`Programmatic` ∧ `LastModality == Keyboard`)); new-only ancestors set `FocusWithin`. Mirror into properties via the **internal** `UIPropertyKey`s: `SetValue(IsFocusedKey, …)` on old/new, `SetValue(IsKeyboardFocusWithinKey, …)` along the changed chain segments (read-only, store-owned, never a style; invariant 4 untouched).
5. Update logical scope memory: `scope = GetFocusScope(target)`; set `FocusManager.FocusedElementProperty` on `scope` to `target` (only the *nearest* scope records — the parent window's memory survives a menu excursion, which is exactly the restore point).
6. Notify `AccessKeyManager.OnFocusChanged(method)` (`Pointer` clears the sticky cue — the named wire for "focus change via pointer", §3.7). Raise `LostFocus` (bubble from old) then `GotFocus` (bubble from new), each carrying `(OldFocus, NewFocus, Method)`. State commits before events (handlers observe consistent state). Re-entrant `SetFocus` from handlers is allowed, last-wins, depth-capped at 8 with a debug diagnostic (mirrors the styling engine's loop-diagnostic posture).

**Scope restore** (`OnWindowActivated`): records `ActiveRoot = windowRoot`; candidate = scope memory if still valid (attached/focusable/enabled/visible), else first element in tab order, else none (keys target `ActiveRoot`). `method = Restore`. `OnWindowDeactivated`: clears `ActiveRoot` when it matches; physical focus moves when S4 activates the next window; logical memory is the whole point. Menus: S4 pushes the menu popup as a focus scope on open; on close, S4 re-activates the owner window → memory restore brings focus back to the pre-menu element. Terminal-level `FocusEvent` does **not** move keyboard focus (§3.1).

### 3.6 Tab and directional navigation

**Tab**: navigation container = nearest self-or-ancestor with `TabNavigation == Cycle`, else the active surface root (which defaults to `Cycle` — on a terminal there is no "next app control" to continue to, so **every window/popup root traps by default**; modal trapping falls out for free, and S4 need do nothing extra for modals). With `FocusedElement == null`, `MoveFocus(Next/Previous)` starts at `ActiveRoot`'s first/last tab-ordered focusable. Collect eligible elements (`Focusable && IsTabStop && effectively enabled && effectively visible`) by DFS in visual order; stable-sort by `TabIndex` (default `int.MaxValue` ⇒ document order). Move to `(index ± 1) mod n`. `TabNavigation == None` containers contribute no descendants. Recomputed per keypress — O(n) over hundreds of elements, allocation: one pooled list.

**Directional** (arrows): container = nearest ancestor with `DirectionalNavigation != None`; candidates = the same eligibility filter within it, excluding current. Direction filter on arranged cell rects (e.g. Up: candidate's bottom edge strictly above current's top-edge row — evaluated on `LayoutBounds`); score = `facing-edge distance + 2 × orthogonal-range gap` (0 when the cross-axis ranges overlap); lowest wins, ties resolved by tab order. `Cycle` wraps to the farthest candidate on the opposite side when none found; `Contained` stops. Deliberately a policy *hook*, not a global: lists/menus/toolbars opt in; free arrows elsewhere stay with controls.

### 3.7 Access-key engine

**Capability gate**, evaluated in `OnCapabilitiesChanged` (startup + after `RenegotiateAsync`):

```csharp
var k = capabilities.Input.Keyboard;
var p = capabilities.Input.Protocol;
Mode = (k.DistinguishesKeyUpDown && k.ReportsRepeats) || p.Win32InputMode
     ? AccessKeyMode.AltHeld : AccessKeyMode.AlwaysVisible;
```

`ReportsRepeats == true` is the runtime-testable proxy that exactly captures `kittyEnabled ∧ ReportEventTypes ∧ ReportAllKeysAsEscapeCodes` (input.md §7, verbatim); `Win32InputMode` covers Windows Terminal/ConPTY VK_MENU bracketing. `KeyboardCapabilities.DistinguishesKeyUpDown` alone (e.g. from `KeyReleaseSynthesizer`) deliberately does **not** qualify — no Alt Down ever arrives to synthesize from (input.md §7). `OnCapabilitiesChanged` also **unconditionally clears** side bits, sticky flag, chord-flash latch, and (in AltHeld mode) the cue: renegotiation parks the pump ~500 ms and an Alt Up released during the park vanishes with no `FocusEvent`. On a mode flip: AltHeld→AlwaysVisible sets the cue permanently; reverse clears it pending real Alt.

**Chord-flash self-correction (AltHeld mode).** The Kitty push is family-gated but **unverified** (no DECRQM exists — input.md §6); a family-matched terminal that never actually delivers standalone Alt events would otherwise leave the cue permanently invisible — strictly worse than AlwaysVisible. Runtime evidence-based correction, timer-free: track `_sawStandaloneAlt` (any real LeftAlt/RightAlt Down observed since the last `OnCapabilitiesChanged`). When an Alt-chord activation arrives in AltHeld mode with no side bit set and `!_sawStandaloneAlt`, the bracket is presumed missing: turn the cue ON (sticky) *before* processing the chord — a single match activates and clears it; a multi-match leaves the cue up for cycling (Office-style "highlight on first Alt-modified event", exactly input.md §7's prescribed degradation) — and latch `_bracketUnobserved` so subsequent chords behave the same. A later real standalone Alt Down clears the latch permanently (until renegotiation). The gate truth-table tests cover this row.

**Cue state machine (AltHeld mode)** — tracked per side (`_leftAltDown`, `_rightAltDown`) plus `_stickyCue`, `_altWasChordless` (all updates ignore `Synthesized` key events, §3.2):

- `LeftAlt/RightAlt` Down → side bit set; cue ON (set `AccessKeyCue` on the active access-key scope root *and* the active window root, one `BeginInteractionUpdate`); `_altWasChordless = true`; `_sawStandaloneAlt = true`.
- Any other KeyDown while Alt held → `_altWasChordless = false` (and may activate, below). A non-Alt-modified KeyDown while a side bit is set ⇒ stale bracket ⇒ clear side bits (§3.2 step 1b).
- Alt Up with the other side not held → cue OFF unless `_altWasChordless` flips `_stickyCue` ON (**Alt tap**: cues stay, `EnterMenuMode` raised — the menu control, if registered, takes focus; Esc or a second tap exits). Sticky cue clears on: activation, Esc (consumed in the §3.2 pre-stage), second Alt tap, `OnFocusChanged(Pointer)` (wired from `FocusManager.SetFocus`, §3.5).
- `FocusEvent{HasFocus:false}` → **unconditionally** clear side bits, sticky flag, cue (Alt+Tab swallows the Up — input.md §7 caveat).

**AlwaysVisible mode**: the cue bit is set on **every surface root** and never cleared — window roots at attach/`OnWindowActivated`, popup/menu roots at `PushScope` (popup surfaces are separate trees; window-only stamping would leave `:access-keys ContentPresenter` unmatched inside popups on legacy terminals). Requirement 6's fallback is thereby pure styling — the same `:access-keys` rule renders underscores permanently.

**`:access-keys` write**: the cue is `InteractionState.AccessKeyCue` set on root elements via `IInteractionStateSink`; descendant selectors (`:access-keys ContentPresenter`) flip `ShowUnderlineProperty` on presenters (styling proposal). S3 never touches a glyph.

**Registration & where AccessText lands** (joint S3/S5/S8/XAML contract): the XAML loader folds `AccessText`-**typed** property literals at parse time (DECISIONS Fork C); object-typed content properties keep their strings. Inside control templates, a `ContentPresenter` with `RecognizesAccessKey = true` (default in Button/MenuItem/Label/TabItem templates) applies `AccessText.Parse` to string content at presentation time, renders the underline per `ShowUnderlineProperty`, and calls `AccessKeyManager.Register/Unregister` on behalf of its nearest `IAccessKeyTarget` templated parent — registration lives where the `AccessText` lands, controls carry no registration code. Storage is a **flat** `Dictionary<char, List<UIElement>>` (keys uppercased invariant) with **no scope captured at registration time** — eliminating any attach-vs-`PushScope` ordering contract and making reparenting self-correcting. Detach unregisters (presenter's job, with manager-side `OnElementDetached` backstop).

**Activation** (`ProcessKeyDown`, unhandled tail):

```
eligible iff:
  (Mode == AltHeld && (anyAltDown || _stickyCue) && e.Modifiers is None or Alt && e.Key == Key.Character)
  || (e.Modifiers == KeyModifiers.Alt && e.Key == Key.Character)   // legacy chord; works everywhere;
                                                                   // triggers chord-flash when the bracket
                                                                   // was never observed (above)
key = char.ToUpperInvariant(e.Text.Span[0])
scope = top of scope stack (else FocusManager.ActiveRoot)
matches = registry[key] where IsAccessKeyEligible
          && resolved-scope(target) == scope        // ACTIVATION-TIME scope resolution: walk the
                                                    // target's ancestor chain; the first ancestor that
                                                    // is a live scope-stack root or the active window
                                                    // root is its scope
0 → if _stickyCue: swallow (handled; cue stays — WPF menu-mode bonk; typing must not leak into
        TextInput while cues are up)
    else: false (not handled)
1 → target.OnAccessKey(args { IsMultiMatch = false }); cue OFF (unless physical Alt still held); handled
n → cycle: focus next match after the currently focused one in tab order,
    via OnAccessKey(args { IsMultiMatch = true }) — focus moves, nothing invokes;
    Enter then activates the focused control through its normal KeyDown path; handled
```

Matching keys on `(Key.Character, Text)` per the input-map gotcha; exact-`Alt` modifier match keeps Ctrl+Alt (AltGr) sequences out. Because this runs in the unhandled tail, a focused TextBox that *handles* plain characters never loses `F` to a mnemonic, while Alt+F still reaches the manager (text controls must not handle Alt-modified keys — control-author contract, and TextInput synthesis already excludes them, §3.2).

### 3.8 Commands

Lifecycle is entirely control-side against two seams (see `ButtonBase`, §2.10):
- **Subscription**: hook `CanExecuteChanged` on attach + on `Command` change while attached; unhook on detach + on change. Strong handlers with symmetric detach — the leak window is bounded by tree membership (recorded trade-off vs WPF's weak events; revisit only with evidence).
- **IsEnabled coupling**: `IsEnabledCore` virtual ANDs into the property system's effective-enabled computation; `InvalidateIsEnabledCore()` triggers recompute → the property system pushes the `Disabled` interaction flip and `IsEffectivelyEnabled` change (styling contract C5). A disabled-by-command button styles via `:disabled` identically to a property-disabled one. Frame coherence: a `CanExecuteChanged` raised from a command during the drain lands in the same frame; raised from a background thread it must be marshaled by the app (single-UI-thread invariant; document `UIDispatcher.Post` as the route — S6's surface).
- **KeyBinding sweep** is part of bubble (§3.2 step 4): element-scoped shortcuts compose with focus naturally (a binding on the Window fires for any unhandled key in that window; a binding on a pane only while focus is inside it).

### 3.9 Tree-change hygiene (`OnElementDetached`, fan-in from `UIElement` detach)

- **Hover**: if in `_hoverChain` → truncate chain at it (clear `PointerOver` for the suffix in one batch; Leave raises from snapshot, per §3.4's two-phase rule when this occurs inside a raise phase) and mark hover dirty; the refresh runs at the frame's `UpdateHover()` (the named executor — S6 calls it once per rendered frame, §2.5/§4).
- **Capture**: target detached → force release (`LostMouseCapture`).
- **Pressed holders**: detached element removed from the pressed-holder set.
- **Focus**: focused element detached → focus the nearest still-attached focusable ancestor, else the scope root's first tab-ordered focusable, else clear (`method = Programmatic`, no `FocusVisible`).
- **Scope memory**: if any focus scope's `FocusedElementProperty` points at the detached element, clear it eagerly (lazy-only validation would pin detached subtrees — virtualized lists churning content under a long-lived window must not accumulate strong refs). Restore-time validation remains as backstop.
- **Access keys**: manager backstop-unregisters (presenters already unregister on detach).

### 3.10 Threading & frame placement

Everything above is synchronous on the single UI thread inside S6's input-drain phase: handler property writes, `InteractionState` flips (→ synchronous local restyle → `PropertyEffects`-routed invalidation), and focus changes are all visible to frame N's measure/arrange/render (invariant 1 — no priority tiers). One scoped exception, recorded as policy: `UpdateHover()` runs *after* frame N's layout/composite finalize, so a hover-triggered style change whose setters carry `AffectsMeasure`/`AffectsArrange` renders in frame **N+1** (one-frame hover-restyle latency; no bounded re-layout loop — deliberate, matching invariant 1's spirit and the 20–60 fps budget; pointer-move-driven hover during the drain is unaffected). `VerifyAccess` (debug) guards `ProcessEvent`, `UpdateHover`, `OnSurfacesChanged`, `SetFocus`, `CaptureMouse`, `Register`. S3 holds no locks and spawns no timers; the pressed-holder set, hover-dirty flag, and chord-flash latch are plain fields on UI-thread services; even `TerminalFocusChanged` fires synchronously on the UI thread.

---

## 4. Cross-subsystem contracts

### REQUIRES from S6 (session/pump/loop)
- Device pipeline assembled **before** the pump as: `session.Input.WithClickSynthesis(new MouseClickOptions { ClickCount = ClickCountTarget.ButtonDown, SynthesizeClickEvents = false })`. No `KeyReleaseSynthesizer` in the default pipeline (its synthetic Ups would lie to the access-key gate and flicker at OS-repeat granularity — input.md gotchas; the dispatcher additionally ignores `Synthesized` key events for Alt bracketing as defense-in-depth).
- Calls, on the UI thread: `dispatcher.ProcessEvent(e)` per drained event (routing `NotUiInput` results onward: `ResizeEvent`→layout/S4, `DeviceResponseEvent`→issuer, `UnknownEvent`→log); `dispatcher.UpdateHover()` **once per rendered frame, after layout and composite parameters are final** (covers layout moves, composite-offset slides, scrolls, surface-stack changes, and detach-deferred hover work; cheap — the hit path is budgeted for per-cell motion); `dispatcher.OnCapabilitiesChanged(caps)` + `accessKeys.OnCapabilitiesChanged(caps)` at startup and after every `RenegotiateAsync` (snapshot is replaced — never cached).
- A marshal-to-UI-thread primitive (`UIDispatcher.Post`) documented for app authors raising `CanExecuteChanged` cross-thread.

### REQUIRES from S4 (window manager)
- `IInputSurfaceProvider` (§2.6): topmost-first surfaces with **signed `CellRect` terminal-coordinate `Bounds` reflecting composite offsets** (a window mid-slide hit-tests at its drawn position, possibly negative origin), `IsBlockedByModal` per ownership chain, `IsLightDismiss` on popups; `NotifyOutsidePress` / `NotifyBlockedPress` implementations.
- Calls `dispatcher.OnSurfacesChanged()` on **every** surface-stack mutation (open/close/modal-state/z-order change) — the capture-revalidation and hover-refresh trigger; not optional.
- Calls `focus.OnWindowActivated/OnWindowDeactivated(windowRoot)` and `accessKeys.OnWindowActivated(windowRoot)` on activation changes; `accessKeys.PushScope/PopScope(popupRoot)` on menu/popup open/close (no ordering constraint vs. content attach — scope resolution is activation-time); subscribes `dispatcher.TerminalFocusChanged`.
- Writes `InteractionState.ActiveWindow` itself (not S3); window roots register `IsFocusScope = true` and `TabNavigation = Cycle` defaults.

### REQUIRES from S2 (tree/layout)
- On `UIElement`: `CellRect LayoutBounds { get; }` in **window coordinates including the element's accumulated composite offset** (signed — scrolled content's first partially-visible child sits at a negative row; hit testing tracks `AffectsComposite` slides without re-arrange). Arranged (pre-composite) geometry may remain `Rect` internally; the boundary S3 consumes is `CellRect`.
- `bool ClipsToBounds` with **`true` as the panel default** — a named perf contract: it is what makes hit-test pruning effective under any-event motion; S2 owns the default and documents the cost of opting out.
- `bool IsEffectivelyVisible`; `IReadOnlyList<UIElement> VisualChildren` in paint order; `VisualParent`; attach/detach hooks `OnAttachedToTree`/`OnDetachedFromTree` plus detach fan-in to `InputDispatcher`/`FocusManager`/`AccessKeyManager.OnElementDetached`; per-element service access (`Input`, `FocusManager`, `AccessKeys` via the element's root/host).

### REQUIRES from S1 (property system)
- `UIPropertyKey<bool>` registration for `IsFocused`/`IsKeyboardFocusWithin` with **internal-only key exposure** (the key is the write capability — Fork A; the public surface is the read-only `StyledProperty` handles); `AttachedProperty<T>` for `FocusManager.*`, `KeyboardNavigation.*`, `AccessKeyManager.ShowUnderline`; `OverrideDefault<T>` metadata for `Focusable`.
- The effective-IsEnabled pipeline consults `UIElement.IsEnabledCore` and exposes `InvalidateIsEnabledCore()`; it owns the `Disabled` interaction flip and `IsEffectivelyEnabled` (styling contract C5 — S3 only triggers recomputes).

### REQUIRES from S5 (styling)
- `IInteractionStateSink` + `BeginInteractionUpdate` on `UIElement` exactly as the hybrid proposal §2.5 (flags `PointerOver/Pressed/Focused/FocusWithin/FocusVisible/AccessKeyCue` consumed; flips are synchronous local restyles).
- C8 alignment: "Pressed cleared window-wide on terminal focus loss" is implemented by S3's pressed-holder set (fed by the `SetInteractionState(Pressed, …)` seam), not solely by `LostMouseCapture` — C8's wording should cite the set.
- Default theme rules: `:access-keys ContentPresenter { AccessKeyManager.ShowUnderline: true }` and focus-visual conventions on control templates; `ContentPresenter.RecognizesAccessKey` plumbing per §3.7.

### REQUIRES from XAML (loader/generator)
- Access-key literal fold for `AccessText`-typed properties produces `Cursorial.UI.Input.AccessText` via its `(Text, Key, KeyIndex)` ctor (DECISIONS Fork C); object-typed content properties are not folded (presentation-time `RecognizesAccessKey` path, §3.7); `KeyGesture.Parse` registered as the type converter for `KeyBinding.Gesture`.

### PROVIDES
- **To all control/app authors**: `RoutedEvent` infra, the event vocabulary, `On*` virtuals, `AddHandler(handledEventsToo)`, `RentEvent`/args-ownership rules, `Focus()/CaptureMouse()`, `InputBindings`, `IAccessKeyTarget`, `AccessText`, `CellRect`, the `IsEnabledCore`+command pattern.
- **To S4**: `dispatcher.HitTest(...)` (cursor-shape policy, tooltip targeting later), `TerminalFocusChanged`, `OnSurfacesChanged` semantics, focus-restore + access-key-scope behaviors invoked by its activation calls.
- **To S5**: all interaction-state writes listed above, batched.
- **To S6**: the one entry point `ProcessEvent` + `UpdateHover` + `OnCapabilitiesChanged`; `InputDispatchResult` so S6 stays the router of non-UI events.

---

## 5. Requirement mapping

| Req | Coverage |
|---|---|
| **4 — logical & physical focus** | §2.7/§3.5–3.6 in full: single physical focus, `IsFocusScope` scopes with memory + activation restore + eager detach-clear, `ActiveRoot` record, `Focusable/IsTabStop/TabIndex`, Tab with default-trapping window roots, directional policy, read-only `IsFocused`/`IsKeyboardFocusWithin` via internal `UIPropertyKey`. |
| **6 — access keys** | §2.8/§3.7 in full: `AccessText` fold target + `RecognizesAccessKey` presentation path, manager + activation-time scoping, the exact input.md §7 capability gate (Kitty `ReportEventTypes+ReportAllKeysAsEscapeCodes` via the `ReportsRepeats` proxy, or `Win32InputMode`), chord-flash self-correction for unverified pushes, LeftAlt/RightAlt Down→Up bracketing with stale-bracket inference, `FocusEvent{HasFocus:false}` + renegotiation unconditional clears, permanently-visible fallback on every surface root, `:access-keys` root write, legacy Alt+chord, multi-match cycling, menu-mode key swallow. |
| **5 — modal/modeless** | Input half: modal surface **occlusion** (blocked surfaces opaque to all pointer events) + blocked-press notification, capture force-release via `OnSurfacesChanged`, default-Cycle focus trapping, focus restore on owner re-activation. Default/cancel-button (Enter/Esc) recorded as an S8 deliverable on `InputBindings` (§7). (Activation policy itself = S4.) |
| **1/8 — styling & hybrid triggers** | S3 is the producer side of every interaction pseudo-class except `:active-window`/`:disabled`; batched flips keep the styling hot path as designed. |
| **2 — data binding** | Commands via BCL `ICommand` (MVVM-compatible; `Command` is a first-class binding target); `IsFocused` etc. bindable/`When`-conditionable as read-only properties. |
| **10 — animation** | Indirect: interaction/pseudo-class edges are the storyboard ignition points (`BeginStoryboard`/`StopStoryboard` on activation/retraction — Fork B); S3 just supplies clean edges, and hit testing tracks composite-animated geometry (`CellRect` bounds + per-frame `UpdateHover`). |
| 3, 7, 9 | Not S3's (resources, XAML, property storage) beyond the contracts above. |

**Invariant compliance**: (1) frame coherence — all dispatch synchronous in the drain phase; the one documented N+1 case is hover restyle from post-layout `UpdateHover` (§3.10); (2) S3 never references `Scene`/`CellBuffer`/`DrawingContext` — its only outputs are routed events, `InteractionState` bits, and read-only property writes; (3) re-composite vs re-raster — respected by *consuming* composite-inclusive `CellRect` bounds; S3 triggers no invalidation directly; (4) retraction-is-store-owned — S3 sets no styled values, ever (interaction bits and internal `UIPropertyKey` writes only); (5) template barrier — a styling-engine concern; input routes the visual tree freely (and v1's no-source-adjustment stance keeps the two cleanly separate); (6) single UI thread — `VerifyAccess` everywhere, no timers, no locks; (7) lower layers untouched — S3 consumes existing `Cursorial.Input` surface as-is; the only pipeline requirement (`WithClickSynthesis`) uses shipped transformers.

---

## 6. Terminal-specific design (deviations from WPF/Avalonia)

1. **Alt-held is a negotiated capability, not an assumption — and the negotiation itself is distrusted.** WPF gets WM_SYSKEYDOWN unconditionally; here standalone Alt events exist *only* under Kitty (`ReportEventTypes + ReportAllKeysAsEscapeCodes`, family-gated) or Win32 input mode — on xterm/tmux/Alacritty **no event ever fires for Alt alone** (input.md §7). Hence the dual-mode `AccessKeyManager`, the requirement-6 "permanently visible" fallback, the universal chord activator (`Modifiers == Alt`) — and, because the Kitty push is unverified (no DECRQM), the chord-flash self-correction that recovers cue discoverability on family-matched terminals that don't actually deliver the bracket (§3.7).
2. **KeyUp is vocabulary, not foundation.** `KeyEventKind.Up` exists only on Kitty/Win32/synthesizer chains (input.md §1); framework controls activate on Down (`ButtonBase`), and nothing in focus/access-key/command logic depends on Up except the Alt bracket, which is exactly the capability-gated part — and which additionally tolerates a *lost* Up via stale-bracket inference and renegotiation/focus-loss clears.
3. **Hit testing is integer-cell, signed, and budgeted for any-event motion.** DECSET 1003 is on by default — Move fires per cell crossed (input.md perf) — so the hit path is a pruned rect-descent with pooled scratch and pooled args, zero steady-state allocation; no transforms, no sub-cell geometry. Signed math rides `CellRect`/`CellPosition`; Rendering's ushort `Rect` never carries possibly-negative composite-inclusive geometry (DECISIONS geometry note).
4. **Click timing is upstream.** Multi-click counts come from `MouseClickSynthesizer` (deterministic, timestamp-based) rather than dispatcher timers — there is no `GetDoubleClickTime()` on a terminal (input.md §4).
5. **Capture is routing policy, not OS capture.** Terminals keep reporting drags (with possibly negative coords) regardless; capture only decides routing, and is force-cleared on terminal focus loss (the release event may never arrive) and on surface-stack changes that close or block the target's surface.
6. **Focus visuals are styling, hover is capability-honest.** No adorner layer (cell grid: focus visuals = attributes/colors in templates via `:focus*`); `:pointerover` silently absent when `MouseCapabilities.Motion` is false (styling proposal's honesty rule) — which is why focus parity in themes matters more here than in WPF.
7. **TextInput is synthesized from the key stream + bracketed paste**, not an IME pipeline: unhandled `Key.Character` Downs (modifier-masked) plus `PasteEvent` as a single `FromPaste` TextInput; without bracketed paste, paste is indistinguishable from typing and S3 makes no pretense otherwise (input.md gotcha).
8. **Window roots trap Tab by default.** There is no OS to Tab out to; `Continue` at the root would be meaningless, and modal focus trapping becomes the zero-cost default rather than a special mode.
9. **Surfaces are opaque rectangles.** Terminal windows/popups paint every cell of their bounds; hit testing models that (hit-opaque within bounds, blocked surfaces occlude) instead of WPF's per-pixel transparency.
10. **In-band protocol traffic flows through the dispatcher untouched** — `DeviceResponseEvent`/`UnknownEvent` are classified `NotUiInput` and handed back (a UI router "must pass them through to whatever issued the query" — input.md §1).

---

## 7. Phasing (per the repo's §11 convention: deferred items recorded with reasons)

**v1 spine** (order of implementation): `CellRect` + `RoutedEvent` registry + handler store + route walker + pooled-args free-list (ownership rules + debug stamps) → key/mouse/focus/paste vocabulary + `ProcessEvent` classification + pre-stage → hit testing (occlusion + surface opacity) + hover chain (two-phase diff) + capture + `OnSurfacesChanged` + `PointerOver` feeding → `FocusManager` (physical + scopes + `ActiveRoot` + restore + detach hygiene) + `IsFocused`/`IsKeyboardFocusWithin` + Tab navigation (Continue/Cycle/None) → `AccessText` + `AccessKeyManager` (both modes, chord-flash, flat registry + activation-time scoping, sticky cue + wired clears, cycling) → commands (`IsEnabledCore` coupling) + `KeyGesture`/`KeyBinding` → directional navigation (None/Contained/Cycle). Oracle-pinned tests first for: gesture matching across legacy-C0 vs Kitty encodings of the same chord (e.g. Ctrl+S both ways); the access-key gate truth table over `(DistinguishesKeyUpDown, ReportsRepeats, Win32InputMode)` **plus the chord-flash row** (AltHeld mode, chord with no observed bracket); modal-occlusion hover (no hover chain anywhere over a blocked owner); nested-`RaiseEvent` pooling; focus-restore scenarios.

**Explicitly deferred:**
- **Template source adjustment** (`args.Source` ≠ `OriginalSource` outside template boundaries) — Avalonia ships without it; revisit when control templates exist and authors hit it. Re-addable without breaking (Source is already a separate slot).
- **Static class-handler registry** (`EventManager.RegisterClassHandler` analogue) — `On*` virtuals cover control authoring; an open registry invites ordering ambiguity for no current consumer.
- **Cancelable Preview focus events** (`PreviewGotFocus` veto) — validation-before-commit covers the common cases; cancelable focus has gnarly re-entrancy. Needs a real consumer first.
- **RoutedCommand / CommandManager.RequerySuggested** — focus-routed command targeting is heavy machinery; BCL `ICommand` + element `KeyBinding`s cover stated requirements. Cut rung recorded as re-addable.
- **Default-button / cancel-button** (Enter/Esc activation on modals, `IsDefault`/`IsCancel`) — an S8 control-library deliverable built on window-root `InputBindings` + the unhandled tail; recorded here so requirement 5's dialog ergonomics aren't silently lost.
- **MouseBinding / gesture vocabulary beyond keys** — trivially additive to `InputBinding` later.
- **Subtree mouse capture mode** — element capture suffices for buttons/drags/menus (light dismiss handles menus).
- **`KeyboardNavigationMode.Once/Local`**, focus-scope `TabNavigation` interplay beyond Cycle — WPF's full matrix is famously confusing; ship the three modes that map to real terminal layouts.
- **Mouse cursor shape on hover** (OSC 22 via `MouseCursorWriter`) — natural follow-up on the hover chain; needs S4 policy for which surface owns the pointer shape.
- **Tooltips / hover-delay service** — requires a timer service; builds on `MouseEnter` + the frame clock when S5/animation land theirs.
- **`PointerEvent` (pen/touch) routing** — no source emits it today (input.md §1); vocabulary reserved.
- **Drag-and-drop** — out of scope until a consumer exists.
- **Non-clipping hit-test pruning** (cached subtree bounds) — v1 relies on the S2 `ClipsToBounds` panel default (now a named contract); add cached union bounds if profiling shows scans.

---

## 8. Open questions (with recommendations)

1. **Should Alt-tap sticky cue mode (menu mode) ship in v1, or only cue-while-held?** *Recommendation: ship it* (adopted in this spec) — it is ~3 fields of state in an already-required state machine, it is the only keyboard-discoverable path to menus on capable terminals, and the `EnterMenuMode` event cleanly defers the menu-focus behavior to the menu control. The clears are now fully wired (Esc pre-stage, `OnFocusChanged(Pointer)`, focus-loss, renegotiation, unmatched-key swallow), so the residual risk is gone. If the menu control slips, the event simply has no subscriber.
2. **Do Alt-modified character KeyDowns ever produce `TextInput`?** (Affects ESC-prefix terminals where Alt+F is the *only* Alt observable.) *Recommendation: no, never* (adopted — §3.2 step 7's mask) — reserve `Modifiers.Alt` text exclusively for access keys and `KeyBinding`s; AltGr characters arrive without the Alt bit on terminals so real text is unaffected. Documented as a control-author contract: text widgets must not handle Alt-modified keys.
3. **Where is `:focus-visible` set on a scope restore whose original focus came from a pointer click?** (Window re-activated → memory restores a button last focused by mouse.) *Recommendation: `Restore` always sets `FocusVisible`* (adopted — §3.5 step 4) — the user just performed a keyboard/window-management action, and over-showing focus on a terminal (one underline/bold cell) is cheap while under-showing strands keyboard users; recorded as a divergence from Chrome-style heuristics.

---

## Critique disposition

| # | Sev | Disposition |
|---|---|---|
| 1 | P0 | **ACCEPTED.** Introduced signed `CellRect` in `Cursorial.UI` (§2.1); `InputSurface.Bounds` and `UIElement.LayoutBounds` now `CellRect`; S2/S4 REQUIRES updated; Rendering's `Rect` reserved for arranged (pre-composite) geometry; `ButtonBase` pressed-tracking rewritten on element-local coords. |
| 2 | P0 | **ACCEPTED.** Blocked surfaces occlude: point-in-bounds terminates the scan with `(null, surface)` for all event kinds; press additionally notifies. Pinned test: hover over owner-behind-modal yields no hover chain. |
| 3 | P0 | **ACCEPTED.** Pool is a per-type free-list (nested raises rent distinct instances; depth debug-capped); ownership rule stated: framework dispatches rent, caller-`new` args are caller-owned (never pooled/stamped); protected `RentEvent<TArgs>` added; `ButtonBase.OnClick` uses it. |
| 4 | P1 | **ACCEPTED.** Chord-flash self-correction (§3.7): Alt-chord with no observed bracket in AltHeld mode flips the cue on (sticky) before activation and latches `_bracketUnobserved`; real Alt Down clears the latch; truth-table test row added. Timer-free per §3.10. |
| 5 | P1 | **ACCEPTED.** (a) `OnCapabilitiesChanged` unconditionally clears side bits/sticky/latch/cue; (b) stale-Alt inference guard in the §3.2 pre-stage (non-Alt-key Down lacking the Alt bit while a side bit is set clears the bracket; LeftAlt/RightAlt events excluded — their own modifier reporting is protocol-edge). |
| 6 | P1 | **ACCEPTED.** S6 contract broadened to `UpdateHover()` once per rendered frame after layout+composite finalize (the simpler "every frame" option — the hit path is budgeted for per-cell motion); detach-deferred hover work named as executing inside `UpdateHover`; `OnSurfacesChanged` also marks hover dirty. |
| 7 | P1 | **ACCEPTED.** `InputDispatcher.OnSurfacesChanged()` added to the S4 REQUIRES (mandatory on open/close/modal/z-order); synchronously re-validates capture and feeds the hover refresh. |
| 8 | P1 | **ACCEPTED.** `UIPropertyKey` fields are internal; only `IsFocusedProperty`/`IsKeyboardFocusWithinProperty` public; S1 REQUIRES updated. |
| 9 | P1 | **ACCEPTED.** `FocusManager.ActiveRoot` defined, recorded from activation calls; null behavior pinned: key/paste events with no focused element and null active root are dropped (never routed to topmost — topmost ≠ active under S4 policies). |
| 10 | P1 | **ACCEPTED.** Flat `char → List<UIElement>` registry; scope resolved at activation time by walking the candidate's ancestor chain against the live scope stack / active window root; PushScope-ordering contract deleted; reparenting self-corrects. |
| 11 | P1 | **ACCEPTED.** `AccessKeyManager.OnFocusChanged(method)` added and called from `SetFocus` (Pointer clears sticky); Esc handled as a consuming pre-stage when sticky cue is active (§3.2 step 1c), not a tail check. |
| 12 | P1 | **ACCEPTED.** (a) `Command` registered with default (no) effects — it is the canonical binding target. (b) Content stays `object?`; the access-key fold remains type-driven (`AccessText`-typed properties); string content acquires `AccessText` at presentation time via `ContentPresenter.RecognizesAccessKey`, which also owns registration on behalf of the nearest `IAccessKeyTarget` templated parent — keeping the `:access-keys ContentPresenter` rule the real pipeline. Joint S3/S5/S8/XAML contract recorded (§3.7, §4). |
| 13 | P1 | **ACCEPTED.** Two-phase hover diff: bit flips inside a `using`-protected batch, scope disposed, then Leave/Enter raised from pooled snapshot arrays; detach during the raise phase only marks the deferred refresh. |
| 14 | P2 | **ACCEPTED.** AlwaysVisible cue set on every *surface* root: window roots at attach/activation, popup roots at `PushScope`. |
| 15 | P2 | **ACCEPTED** (registry variant). `SetInteractionState(Pressed, …)` fans into a dispatcher-held pressed-holder set; `FocusEvent{HasFocus:false}` clears all holders (covers keyboard-held press visuals without capture); C8 wording aligned (§4 S5 REQUIRES). Chosen over the doc-only rule because a stuck `Pressed` after Alt+Tab is otherwise unrecoverable until an Up that may never arrive. |
| 16 | P2 | **ACCEPTED.** The Alt-bracket state machine ignores `KeyEvent { Synthesized: true }` (pre-stage flag check), symmetric with the synthesized-Click defense. |
| 17 | P2 | **ACCEPTED.** `FocusManager.OnElementDetached` eagerly clears scope memories pointing at the detached element; restore-time validation kept as backstop. |
| 18 | P2 | **ACCEPTED.** `LastPointerPosition` is `CellPosition?`; `UpdateHover` no-ops until the first real mouse event. |
| 19 | P2 | **ACCEPTED** (broadened). Sweep defined as "all light-dismiss surfaces above the hit surface, or all when nothing was hit"; and *all* surfaces (not just light-dismiss) are hit-opaque within bounds — a Descend miss hits the surface root. Terminal windows are opaque rectangles; click-through to a lower window on a painted cell would be incoherent, and the broader rule also fixes ordinary window fall-through. |
| 20 | P2 | **ACCEPTED.** Sticky-cue (menu mode) unmatched character keys are swallowed (handled, cue stays — WPF bonk semantics); they never reach TextInput synthesis. |
| 21 | P2 | **ACCEPTED.** `ClipsToBounds = true` panel default promoted into the REQUIRES-from-S2 table as a named perf contract. |
| 22 | P2 | **ACCEPTED** (all five): (a) `MoveFocus` with null focus starts from `ActiveRoot`'s first/last tab-ordered focusable; (b) `GetPosition` defined through terminal coordinates — well-defined cross-surface, documented, no throw; (c) `ClickCount` documented as >1 on MouseDown only (ButtonUp reads 1 under `ClickCountTarget.ButtonDown`); (d) hover-restyle-from-`UpdateHover` policy pinned: renders frame N+1, no bounded re-layout (§3.10); (e) default/cancel-button recorded as a deferred S8 deliverable on `InputBindings` (§7). |

No findings rebutted.