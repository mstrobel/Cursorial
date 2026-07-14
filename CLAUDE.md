# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Notes to AI Agents

Your context window will be automatically compacted by the CLI system as it approaches its limit. Do not stop tasks
early or stall due to token budget concerns. Complete tasks fully and trust the system compaction.

## Project intent

Cursorial will be a **cross-platform .NET library for building high-quality, visually rich terminal applications with
robust mouse support**. It is library-first (consumed by app authors), not an end-user app. Cross-platform means
Windows, macOS, and Linux terminals are all first-class — design choices that bake in assumptions about one platform
(VT sequences only, Win32 console only, etc.) need an explicit story for the others.

## Status

Twenty-five projects:

- `Cursorial.Core` — input parsing, capability negotiation, terminal session orchestration, byte-level output writers.
- `Cursorial.Rendering` — cell buffer + diff frame renderer that sits on top of `Cursorial.Core`'s output writers.
- `Cursorial.Drawing` — scene/brush/pen drawing layer (cached-raster scenes, compositor, gradients, charts); design
  doc at `docs/drawing-layer-design.md`.
- `Cursorial.Animation` — pure, time-free animation primitives (`IAnimation<T>`: elapsed → value; the consumer owns
  the clock).
- `Cursorial.UI` — the WPF/Avalonia-style UI framework layer (design doc at `docs/ui-layer-design.md`, phase plan in
  its §14). Phases 0–10 complete (property system → element tree/layout/render/app spine → input/focus → styling →
  data binding → resources/theming + first controls → XAML loader → S4 windowing → S5 animation → S8 control gallery
  → Fork C X4 generator + compiled bindings), plus the post-P9 control set; the `Cursorial.UI.Bars` command surfaces
  build on it. See "UI module status" below.
- `Cursorial.UI.Xaml.Frontend` — the **netstandard2.0** XAML parser frontend shared with the future X4/X5
  generator: the structure-of-arrays node model (`XamlDocument`), the `XmlReader` parser, the markup-extension
  grammar, diagnostics (`XamlDiagnostic`/`XamlParseException`, line+col everywhere), and the type-system seams
  (`IXamlTypeMetadataProvider`/`XamlType`/`XamlMember`/`ITypeConverter`). No `Cursorial.UI` reference.
- `Cursorial.UI.Xaml` — the net10.0 runtime loader (Fork C): `XamlLoader` (`Parse`/`Load`/`Load<T>`/`LoadComponent`),
  `ReflectionXamlMetadata`, the converter ladder, `XamlSchemaContext`, markup-extension handlers + resource
  dictionaries + deferred content/templates. References the frontend + `Cursorial.UI`. See "UI module status".
- `Cursorial.UI.Bars` — the command-surface suite over one shared `BarCommand`: a `Toolbar` with discrete overflow,
  a `Ribbon` (tabs/groups, density collapse, contextual tabs, Backstage, Quick Access Toolbar, minimize), KeyTips
  (Alt-overlay accelerators), and SuperTips. See "Cursorial.UI.Bars status" below.
- `Cursorial.UI.Dialogs` — the task-dialog suite (`TaskDialog` + `CommandLink`) over `Cursorial.UI`, with its own
  code-first control themes (`CursorialDialogThemes`) contributed via the assembly theme tier.
- `Cursorial.UI.Xaml.Generator` — the Fork C X4 Roslyn `IIncrementalGenerator` (symbol-backed parse, CUR1/CUR2 build
  diagnostics, the AOT-clean emitted metadata provider, typed code-behind, compiled-binding lowering + `x:DataType`).
- `Cursorial.UI.Themes` — the data-shipped XAML theme overlay (`Default`/`IndigoDusk`) layered over the code-first
  `CursorialTheme.BuiltIn` backstop.
- `Cursorial.Shared` — netstandard2.0 markup-metadata attributes (`[ContentProperty]`, `[XmlnsDefinition]`, …) shared
  by the loader and the generator.
- `Cursorial.UI.Hosting.Headless` — the headless host (`UIHeadlessHost` + `SyntheticTerminalHost` + capability
  presets); the integration substrate for every UI subsystem — no test needs a TTY. Published since v0.4.0
  (it also powers the Rider designer's preview host).
- `Cursorial.Core.Tests`, `Cursorial.Rendering.Tests`, `Cursorial.Drawing.Tests`, `Cursorial.Animation.Tests`,
  `Cursorial.UI.Tests`, `Cursorial.UI.Xaml.Tests`, `Cursorial.UI.Xaml.Generator.Tests`, `Cursorial.UI.Bars.Tests` — xUnit.
- `Cursorial.Demo` — interactive REPL for hands-on verification. `dotnet run --project Cursorial.Demo` opens a prompt
  with commands: `negotiate` (dump realized capabilities), `read` (stream input events to stdout), `raw` (dump
  stdin bytes verbatim with no parsing), `trace` (live raw bytes + decoded events side-by-side for protocol
  debugging), `sizing` (Kitty OSC 66 text-sizing demonstration), `probe` (XTVERSION + DA1 raw-response capture),
  drawing/animation showcases (`draw`, `animate`, `charts`, `brushtext`, `imagescene`, `imageclip`, `ui`),
  `uipanels` (Cursorial.UI panel-tree showcase on the real `UIApplication` frame loop — arrows slide a render
  boundary composite-only, `v` toggles a Visibility, `o` cycles an Opacity group),
  `uixaml` (Cursorial.UI.Xaml showcase — the entire control tree is loaded at runtime from an embedded
  `.xaml` resource: `{StaticResource}` brushes, a `{TemplateBinding}` `ControlTemplate`, access-key
  `Button`s, `{Binding}` text + status; the live P6 proof that declarative UI works),
  `windows` (Cursorial.UI S4 windowing showcase — `n` opens draggable/resizable/maximizable Windows,
  `d` a modal `ShowDialogAsync` dialog, `m` a light-dismiss Popup menu, `f` fit-all, `c` close-all;
  shrink the terminal while a window overhangs for the WM fit badge — the live P7 proof),
  `rasterbench` (headless-capable scene-raster/compositor/diff benchmark — UI design-doc probe 1), `accesskeys`
  (live access-key gate probe: Alt down/up tracking, negotiated Kitty flags, the requirement-6 gate verdict — UI
  design-doc probe 3), `motion` (Cursorial.UI S5 animation showcase — storyboard/transition/edge-action), `gallery`
  (the standalone control-gallery showcase), `inspect` (live XAML / element-tree inspector), `help`, `quit`. Each
  command opens its own raw-mode `TerminalSession` and restores cooked mode before the next prompt.
- `Cursorial.Gallery` — the standalone XAML-first MVVM control-gallery app (runtime-loaded views, page view-models
  via implicit DataTemplates); a full app, not a demo command.
- `Cursorial.Demo.XamlAot` / `Cursorial.Demo.XamlAotStrict` — the NativeAOT publish demos: the reflection loader, and
  the reflection-free build on the generated metadata provider (the AOT-clean exit gate).

## UI module status (`Cursorial.UI`)

`docs/ui-layer-design.md` is the canonical design reference (§0 invariants, §2–4 engine chapters, §5–12 subsystem
sections, §13 resolved decisions, §14 phase plan, §15 deferrals); full design-phase artifacts are archived under
`docs/ui-layer-design/`. The acronym **UI is fully capitalized in type names** (`UIElement`, `UIProperty`). The UI
styling object is `Cursorial.UI.Style`; framework source disambiguates the SGR record via
`using CellStyle = Cursorial.Output.Style;`.

**Namespace scheme** (doc §1.3 is canonical; WPF/Avalonia kinship): `Cursorial.UI` — the core (`UIObject`/`UIElement`,
the property system, element-level layout enums (`Visibility`, the alignments), render integration, hosting; the
`System.Windows` analog). `Cursorial.UI.Controls` — `Panel` and the panels (`StackPanel`, `DockPanel`, `Canvas`,
`Grid` + `GridLength`/definitions, `WrapPanel`), presenters (`ScrollContentPresenter`, later `ContentPresenter`/
`ItemsPresenter`), every control from `Control` down, and the panel-facing `Orientation`/`Dock` enums (the
`System.Windows.Controls` analog).
`UIElementCollection` stays in `Cursorial.UI` beside `UIElement` — a deliberate deviation from WPF's placement.
`Cursorial.UI.Input` — S3's routed events, dispatcher, focus, gestures. Folder layout mirrors the namespaces
(`Cursorial.UI/Controls/`).

**Phase 0 complete** (doc §14): the Fork A property engine — `UIProperty`/`StyledProperty<T>`/`AttachedProperty<T>`/
`DirectProperty<TOwner,T>`/`UIPropertyKey<T>` registration + per-type frozen metadata, two-lane `PropertyEffects`,
`UIObject` + `ValueStore` (effective/base split, priority frames at `BindingPriority` Animation > LocalValue >
StyleTrigger > Template > Style > Inherited > Default — **the completed Avalonia lattice, 2026-07-12** (precedence-matrix
PD26): a CONDITIONAL rule (any `.class`/`:pseudo`/`When` — `classLike > 0`) arbitrates at `StyleTrigger(50)`, a purely
structural rule at resting `Style(100)`, the slot beating the sort key (an active conditional rule pierces any resting
rule, layer regardless); the **Template lane** (wire 75, was 150) carries values a CONTROL template authors on its parts
(literal `SetValue`/`{TemplateBinding}`/`SetResourceReference`, reached only via `TemplateInstantiationScope` —
`DataTemplate.Build` stays LocalValue, PD24') BETWEEN the slots: state looks pierce it, resting rules cannot (the part's
resting truth; re-skin via the control's own properties or conditional rules). `SetCurrentValue` provenance is WPF-parity
(PD27): the no-producer graft reports the underlying `Default`/`Inherited` source `+cur` (never LocalValue), is invisible
to `ReadLocalValue`, and `ClearValue` undoes SCV universally — including stripping a `+cur` overlay off a producer lane.
(`ValueSource.Kind`/`ValueSourceKind` carries the within-lane provenance, PD25) — store-owned
retraction/promotion), `SetCurrentValue`, copied-value change carriers, typed/untyped observers
(incl. the winning-base observer), `BindingEntry<T>`/`BindInFrame`/`AnimatedValueHandle<T>` producer seams,
lazy-read/eager-notify inheritance over `IInheritanceNode`, untyped XAML lane + box interning + `GetValueSource`
diagnostics, and the `ValueFrame` conformance kit. The oracle-pinned precedence matrix
(`docs/ui-layer-design/precedence-matrix.md`) was authored before the engine; its rows are tests in
`Cursorial.UI.Tests/PrecedenceMatrix/`. Benchmarks (`StoreSpikeBenchmark`, asserted allocation contracts) and probe
results are recorded under the doc's §14 phase table.

**Phase 1 complete** (doc §14 — S1 T0–T4 + the S6 spine):

- **Tree + layout** — `UIElement` (visual/logical relationships, attach/detach walks, `TemplatedParent`,
  effective-enabled, `TranslateToWindow/ToLocal`), `LayoutMath`/`LayoutLimits`, Measure/Arrange with the WPF-derived
  oracle layout matrix (`docs/ui-layer-design/layout-matrix.md`, one test per row in
  `Cursorial.UI.Tests/LayoutMatrix/`), `LayoutManager` (depth-keyed heaps, internal 16-pass fixpoint — the sole
  convergence owner, `AbandonPendingLayout` parking), `UIElementCollection`, and panels: `StackPanel`, `DockPanel`,
  `Canvas`, `Grid` (+`GridLength`/definitions), `WrapPanel`.
- **Render zones** — `RenderTree` (boundary-zone partitioning over the shared `ScenePool`, whole-zone re-raster per
  the probe-1 verdict, the unconditional per-pass boundary walk publishing `CompositeParameters` on value-difference,
  `CollectLayers` bottom-up, composite-order `HitTest`), `RenderContext` (element-local; a thin veneer — one pushed
  Drawing translate scope per element render),
  `RenderPassGuard` (DEBUG read-only guard during paint), cached z-order, sticky boundary promotion, the
  empty-clip trick for hidden/zero-sized boundaries.
- **Scrolling + caret** — `ScrollContentPresenter` (always-boundary; banded scenes per doc §5.7 — band = viewport +
  2K rows, re-anchor marks the zone dirty, in-band scroll is a pure composite slide), `ITerminalCaretService` +
  `TerminalCaretService` (publication registry, live transform leg, clip-gated visibility).
- **S6 app spine** — `ITerminalHost` (+`TerminalSessionHost` over real sessions, `SyntheticTerminalHost` headless),
  `UIDispatcher` + `UISynchronizationContext` (Build-thread → UI-thread one-shot ownership hand-off; `UIObject`
  affinity now captures `UIApplication.Current?.Dispatcher` per ledger A25, falling back to the constructing thread
  id), `UIApplication` + builder (owns the screen `CellBuffer` + `FrameRenderer`; the doc §10.5 7-phase frame loop
  with the normative wake protocol; styling/animation/windowing phases are null seam fields —
  `IStyleFrameHooks`/`IAnimationFrameDriver` declared, implementations land at P3/P8; the `IInputDispatchTarget`
  seam is implemented by S3's `InputDispatcher` since P2 stage I1),
  `SingleRootLayoutSystem`/`SingleRootRenderSystem` (the P1 single-root stand-ins S4's `WindowManager` replaces at
  P7; `RootElement` is the window-content stand-in), resize pipeline (coalesced last-wins, same-frame relayout),
  device-response router, `QueueControlSequence` (the only sanctioned out-of-band byte path), the canonical teardown
  (sync-context uninstall → job cancellation → pump stop → renderer close → cursor/SGR/alt-screen restore → host
  dispose; runs on crash paths), the exception funnel (`DispatcherUnhandledException` + `IUserCodeGuard` passed into
  the draw path). Clean frames are zero-allocation (asserted).
- **`Cursorial.UI.Hosting.Headless`** — `UIHeadlessHost` (calling thread is the UI thread; manual `RunFrame`/
  `RunUntilIdle`/`AdvanceTime` stepping on a `FakeTimeProvider`; `SendKey/SendText/SendClick/SendResize/SendInput`
  direct injection; `SendBytes` through a real `VtInputDevice` on the fake clock; cell/row/byte assertions;
  teardown-byte capture), `UIHeadlessHostOptions`, `HeadlessCapabilities` presets
  (`KittyTruecolor`/`Ansi16Legacy`/`NoMotion`/`NoMouseCursorShape`).

The doc §14 P1 exit criteria are proven end-to-end in `Cursorial.UI.Tests/Integration/Phase1EndToEndTests.cs`
(UIHeadlessHost through the full spine: static panel-tree cell+byte assertions, the AffectsComposite
re-emit-without-re-raster invariant via `Scene.RasterVersion`, banded scroll across a re-anchor edge, mid-run
resize, the caret transform leg) and live in the `uipanels` demo (whose key handling now rides S3's public routed
events — the P1 `InternalsVisibleTo("Cursorial.Demo")` stopgap is removed).

**Phase 2 complete** (doc §14 — the S3 input/focus spine; normative test spec at
`docs/ui-layer-design/input-matrix.md` with the ND pinned-decision ledger, one test per row in
`Cursorial.UI.Tests/InputMatrix/Section01…Section14`). Stage I1 (routing core) landed:
the `RoutedEvent` registry + lazy per-element handler stores (`AddHandler(handledEventsToo)`), the route walker
(visual parents + the logical hop at surface roots) with pooled per-type args free-lists (DEBUG stale stamps,
`RentEvent` rentals), the UI event vocabulary (`Preview*`/main Key/TextInput/Mouse pairs, Direct
`MouseEnter`/`MouseLeave`/`LostMouseCapture`, bubbling `GotFocus`/`LostFocus`) with the `On*` virtuals as the
class-handler stage, and `Cursorial.UI.Input.InputDispatcher` — `ProcessEvent` classification (3-state
`InputDispatchResult`; key dispatch order with TextInput synthesis incl. the chord mask; paste → `TextInput
{FromPaste=true}`; the `FocusEvent{false}` cluster with focus retained + `EditCommitRequested`/`TerminalFocusChanged`;
`Kind==Click` defensive no-op; capture grant/release; `LastModality`/`LastPointerPosition`/`ButtonsHeld`), installed
as the application's default `IInputDispatchTarget` and exposed publicly as `UIApplication.InputDispatcher`.
Stage I2 (mouse) landed: mouse dispatch delegated to
S1's `RenderTree.HitTest` through the internal `IWindowTopology` consumption seam (ND5 — `SingleRootWindowTopology`
is the P2 implementation; S4 substitutes at P7), one hit test per event shared by hover + routing, ND7 disabled
gating (hit-opaque/event-transparent), the two-phase hover diff (pooled chain + pooled `HoverChainSnapshot`s,
`PointerOver` flips through `UIElement.SetInteractionStateInternal` — the minimal slice I3's `IInteractionStateSink`
batching replaces — then `MouseLeave` deepest-first / `MouseEnter` outermost-first / `HoverChanged`), per-rendered-
frame `UpdateHover()` wired into the frame loop's Phase 6 (after layout + composite finalize; hover driver forgotten
on terminal focus-out so a stale position never re-hovers), capture-first routing with hit-honest hover and
wheel-targets-hit under capture, detach hover truncation, and the public `InteractionState` enum +
`UIElement.IsPointerOver`. The Move/hover path is zero-steady-state-allocation (GC-asserted, formal ND25 rows in
`Section14`). The I3 focus stage landed: the public `FocusManager` (`UIApplication.FocusManager`) — `SetFocus` with the
doc §7.7 transition (validation without ancestor fallback, state-before-events, diverging-chain
`Focused`/`FocusWithin`/`FocusVisible` flips + the structurally read-only `IsFocused`/`IsKeyboardFocusWithin`
mirrors via private `UIPropertyKey`s, re-entrant last-wins depth-capped 8), the `:focus-visible` policy
(Tab/Directional/AccessKey/Restore always — Restore by pinned divergence — Programmatic under keyboard modality,
never Pointer; focus repair never sets it), logical focus scopes (`IsFocusScopeProperty` + `FocusedElementProperty`
memory on the nearest scope, eager clear on detach, `GetFocusScope` self-inclusive), window activation/restore
(`ShowRoot` marks the root a scope and auto-focuses memory → first tab stop with `Restore` — N115), detach focus
repair (nearest focusable ancestor → scope-root first tab stop → clear), `Focusable`/`IsTabStop`/`TabIndex`,
`MoveFocus`/`FindNext` over the `FocusNavigator` (per-keypress recompute into one retained list, stable
TabIndex-then-document-order sort, `Continue`/`Cycle`/`None`/`Once` incl. ND16 entry resolution, root-as-Cycle
trap), ND17 directional navigation (`KeyboardNavigation.TabNavigation`/`DirectionalNavigation` attached
properties; facing + 2×orthogonal-gap scoring on window-translated bounds, `Cycle` wraps farthest-opposite), and
the dispatcher's step-6 navigation tail (Tab/Shift+Tab/chordless arrows; handled only when focus moved).
The final I4 stage landed:

- **Interaction-state seam (ND11)** — `UIElement` implements the public `IInteractionStateSink`
  (`protected SetInteractionState` + `BeginInteractionUpdate()` returning a `using`-scoped `InteractionUpdateScope`);
  the internal `InteractionStateService` coalesces batched flips into one `(element, old→new)` notification per
  element per batch (first-flip order, net-zero silent, nested scopes flush at the outermost dispose, delivery
  post-commit) to the one installable per-application `IInteractionStateObserver`
  (`UIApplication.InteractionStateObserver` — P3's styling engine becomes the production instance). Hover phase 1 and
  the focus-transition commit ride one batch each; `UpdateEffectiveEnabled` pushes `InteractionState.Disabled` (the
  `:disabled` producer); `Pressed` flips fan into the dispatcher-held pressed-holder set (ND12) which terminal
  focus-out clears window-wide (C8) and detach removes.
- **Commands (doc §7.9)** — `KeyGesture` (ND13 `Parse` grammar incl. the `Space`→character-gesture pin; ND14
  exact lock-free-modifier matching, `(Key, Text)` identity for printable keys — one gesture matches legacy-C0,
  Kitty CSI-u, and ESC-prefix Alt wires), `InputBinding`/`KeyBinding`/ordered `InputBindingCollection`,
  `UIElement.InputBindings` (lazy) swept per node during the `KeyDown` bubble after virtual + instance handlers while
  unhandled (`CanExecute == false` skips without consuming — ND15; disabled nodes never execute); `ICommand` is the
  BCL interface, `IsEnabledCore`/`InvalidateIsEnabledCore` is the CanExecute coupling.
- **`AccessKeyManager` core (doc §7.8; UX at P9)** — `UIApplication.AccessKeys`: the pinned capability gate
  `(DistinguishesKeyUpDown && ReportsRepeats) || Win32InputMode` evaluated on the **negotiated** snapshot (ND23 — its
  own explicit fan-out call in startup/headless/renegotiate, which also unconditionally clears bracket/sticky/latch/
  cue), the AltHeld cue machine (per-side brackets, Alt-tap sticky + `EnterMenuMode`, second-tap/Esc-consume/pointer-
  focus/terminal-focus-out exits, stale-Alt inference with the ND26 sampled activation window, chord-flash
  self-correction), `AccessKeyCue` stamped on scope/window roots (permanent in `AlwaysVisible`), the flat case-folded
  registry with activation-time scope resolution (`PushScope`/`PopScope`/`OnWindowActivated`; detach backstop),
  single-match invoke vs multi-match focus-only tab-order cycling (ND18; the manager moves focus),
  `IAccessKeyTarget`/`AccessKeyEventArgs`, F10 ≡ Alt tap (ND19), and the `IMainMenu` registration slot.
- **Early-S5 slice (doc §9.8)** — `AnimationScheduler` (thread-ambient `Current`/`Install`, implements
  `IAnimationFrameDriver`, installed as the default `AnimationDriver` seam), `FrameClock` (frozen at `BeginFrame`),
  `UITimer` (frame-aligned, ND20 coalescing with frozen-clock re-arm, state-before-callback so a thrower never
  re-fires, idle-guard participation via `HasActiveAnimations`, `Shutdown` in teardown). S5 absorbs these unchanged
  at P8.

The P2 exit criteria (motion-storm gate, focus restore, gesture-encoding rows; modal-occlusion precursors N65/N69)
are green; `Section14` carries the ND25 allocation contracts (0 B steady-state Move/hover/key-dispatch/UpdateHover
paths) and the probe-4 motion-storm CI gate (`[Trait("Category","Benchmark")]`). The P2 integration pass adds
`Cursorial.UI.Tests/Benchmarks/MotionStormBenchmark.cs` (the loaded probe-4 storm: 300 hover-reactive leaves
writing `InteractionState.Pressed` per enter/leave with an installed observer + `HoverChanged` subscriber —
Release numbers ~2.7 µs/Move at exactly 0 B steady-state, 0.55–0.58 ms for a 200-event frame against the 33 ms
budget, recorded in the design doc's "Probe 4 / motion-storm results" blockquote) and
`Cursorial.UI.Tests/Integration/Phase2EndToEndTests.cs` (end-to-end through `UIHeadlessHost`: Tab cycling +
`Once`-scope memory + window-activation restore, hover re-target under wheel-driven scroll with no mouse motion,
capture across an out-of-bounds drag, one `KeyBinding` fired from both Ctrl+S wire encodings, the terminal
focus-out cluster, and the access-key gate verdicts under the `TestCapabilities` presets). The `uipanels` demo is
the live canary for the same surface: three focusable sidebar cards (Tab/click focus, hover highlight, visuals
from direct `IsFocused`/`IsPointerOver` reads pending P3 styling) and a Ctrl+R `KeyBinding` reset.

**P2.5 + P2.6 batches complete** (lower-layer improvements under the amended invariant 7 — only `Cursorial.Core`
has shipped; Rendering/Drawing/Animation accept first-class changes):

- **Drawing push-stack full coverage** — the clip/translate stack now covers every draw path (deferred strokes arm
  at deposit time; braille maps at plot time; shadows/titled boxes translate as units; formatted text/content paint
  through origin-mapped views with compositor-mirrored fragment cropping). `Cursorial.UI.RenderContext` is a
  translate-only veneer (one `PushTranslate` scope per element render). `Scene.RasterVersion` is public;
  `ScenePool` uses exact-size LRU buckets. Drawing doc §12/§11 updated.
- **Element-level mouse cursors** (doc §7.6) — `UIElement.Cursor : MouseCursorShape?`, hover-chain/capture
  resolution in S3, equality-gated OSC 22 emission via `QueueControlSequence`. **Restore-to-default is always
  `WriteSet(MouseCursorShape.Default)`, never the empty-payload `WriteReset` — Ghostty ignores the latter.**
- **Namespaces** — `Cursorial.UI.Controls` holds `Panel` + panels + presenters + `Orientation`/`Dock`
  (`UIElementCollection` stays in `Cursorial.UI`, a deliberate WPF deviation); brushes/pens live in
  `Cursorial.Drawing.Media`, charts in `Cursorial.Drawing.Charts`.
- **Signed margins** (P2.6, matrix §13/LD19) — negative `Margin` components are honored with WPF semantics:
  measure enlargement, `DesiredSize` clamped ≥ 0 (with the cached-natural-size arrange fix, L225), signed arrange
  origins via the new `LayoutRect` carrier (`UIElement.Bounds` is now `LayoutRect`; implicit `Rect→LayoutRect`
  widening), zone-edge bleed clipping, LD3 alignment clamps unchanged.
- **Line breaks across the text tier** (drawing doc §13) — `DrawingContext.DrawText` interprets `\r\n|\n|\r`
  (continuation at the original start column; brush samples the multi-line extent; returns `Size`, was `int`;
  `\t`→space + DEBUG diagnostic); `CellBuffer.Write` stops at the first C0/C1 control and returns columns written;
  `PanelTitle` sanitizes to its first line; the per-tier behavior table is drawing doc §13.3.

**Phase 3 complete** (doc §14 — the Fork B styling engine; normative spec at
`docs/ui-layer-design/style-matrix.md`, 180+ rows S1–S184 + the SD pinned-decision ledger, tests in
`Cursorial.UI.Tests/StyleMatrix/Section01…Section13`). In `Cursorial.UI/Styling/` (namespace `Cursorial.UI`):
`Selector`/`SelectorParser`/`Selectors` (the full §3.1 grammar — type/`.class`/`#name`/`:pseudo-class`/child `>`/
descendant/`/template/`/lists/`^`-nesting/`:is()`, the namespace-qualified type token `prefix|Type` (SD25 — CSS/Avalonia
`|` form, type-token position only; resolved by a namespace-aware `ISelectorTypeResolver`, the XAML loader's
`XamlSelectorTypeResolver` over the document root xmlns; `Selector.DefaultTypeResolver` does simple-name only and
rejects `|`), the `+ ~ :not :nth-*` NO-fence with "unsupported by design"
errors, `ISelectorTypeResolver` simple-name resolution), `Style`/`Setter`/`Styles` (BasedOn flatten + cycle check,
seal-on-attach naming the style/rule/property, `StyleSetterConverter` constant ladder, `Style.Set<T>` fluent),
`StyleSortKey.Create` (bit-exact packed `[layer][names][classLike][types][scopeDepth][order]`, min-wins ties via
document order), the matcher/activation engine (candidate-by-type indexing, the **template barrier** before scan,
arming vs activation split, `PseudoInterestMask` early-out, one `ValueFrame` per active rule at
`BindingPriority.Style` with cookie batch retraction — store-owned promotion, never set-back; runs the
`ValueFrameConformanceKit`), `PseudoClassMapping` + the `InteractionPseudoClasses` table consuming P2's
`IInteractionStateObserver` slot, the SD24 structural re-entrancy fence (defer + fixpoint, gen cap 16).
`UIElement` gained `Classes`/`PseudoClasses`/`Styles`/`Style` + notifying `Name`; `UIApplication.Styles` is the app
layer + the installed `StyleHooks`/`InteractionStateObserver`; capability classes (`caps-truecolor|ansi256|ansi16|
nocolor`, `caps-motion`, `caps-kitty-keyboard`) stamp on the root from the **negotiated** snapshot (P5/S7 re-points
to the effective tier; `caps-ascii` reserved/unstamped until a glyph-capability source exists). `StyleDiagnostics.
Explain` renders each winning value's full sort-key derivation in one line (pinned acceptance format, S164).
`IStyleEdgeAction` is a declared seam (P8 storyboards). `Phase3EndToEndTests` prove the invariant-3 RasterVersion
isolation (a `:pointerover` flip re-rasters only the hovered zone) and the motion-storm re-assert with real hover
restyle rules (Release: ~2.9 µs/Move incl. restyle, 0 B steady-state, 3.66 ms/frame vs the 33 ms budget — recorded
in the doc's "P3 motion-storm re-assert" blockquote); the `uipanels` cards now draw their hover/focus looks from
real `:pointerover`/`:focus` style rules.

**Grid track Min/Max constraint coercion** (2026-06-12, alongside P3): `Grid.InterimConstraint`/`FinalConstraint`
now clamp each track's child-measure contribution via a single `LayoutMath.Clamp(constraint, Min, Max)` (matrix
LD1 "constraint coercion") — a `MaxWidth`/`MaxHeight`-bounded Auto/Star track measures its child against the cap
instead of `Unbounded`, so the child wraps/ellipsizes to the space it will get rather than measuring full-width and
clipping at arrange. Cell tracks already passed their clamped `Size`; min-wins on a `min>max` conflict per LD18.
Regression row **L148b**.

**Phase 4 complete** (doc §14 — the S2 data-binding engine; normative spec at
`docs/ui-layer-design/binding-matrix.md`, 186 rows B1–B186 + the BD pinned-decision ledger, tests in
`Cursorial.UI.Tests/BindingMatrix/Section01…14`). In `Cursorial.UI/Data/` (namespace `Cursorial.UI.Data`;
`DataContextProperty`/`NameScope`/`UIElement.FindName` in `Cursorial.UI` per §6.3/§6.4): descriptors
(`BindingBase`/`AnchoredBinding`/`Binding`/`TemplateBinding`/`CompiledBinding<,>`/`RelativeSource`/`IValueConverter`/
`BindingMode`/`UpdateSourceTrigger`); `BindingPath.Parse` (span-based, position-carrying errors; property steps,
int/string indexers, `(Type.Property)` attached segments); `ReflectionBindingExpression` (the runtime — anchoring incl.
the **DataContext-as-target parent-anchor** special case, Source/ElementName/Self/TemplatedParent/FindAncestor with
reparent re-resolution; the full §6.6 forward pipeline + reverse lane; echo suppression incl. animation-priority
filtering; all five modes + read-only-leaf degrade; PropertyChanged/Explicit/**LostFocus** triggers — the routed
`LostFocusEvent` **and** `InputDispatcher.EditCommitRequested` terminal-focus-out pulse, focus retained;
cross-thread dirty-bitmask coalescing + `IUIDispatcher` loop-wake; eviction-aware idempotent disposal); the
**2026-06-11 source-notification ladder** (INPC → convention `[Name]Changed` event without TypeDescriptor leak →
parent-change one-time-read degradation, INPC wins) over a COW `AccessorCache`; `BindingOperations`
(`Install`/frame-hosted `Install`/`GetBindingExpression`/`SetBinding`/`ClearBinding`/**`Watch`**/`TearDown`) +
`BindingRegistry` in `UIObject.BindingHostState` (replace-and-dispose, Explain, the teardown sweep);
`BindingDiagnostics` ring/sinks/`CURSORIAL_BINDING_TRACE`/`Explain`; the DEBUG `BindingLeakTracker`. `UIElement.TearDown`
runs `ValueStore.TearDown()` then `BindingOperations.TearDown(this)` bottom-up (**the P1-gap close**, B108/B166).

**The `When`/`DataCondition` styling integration** (P4 — closes the StyleEngine's deliberate P3 Y-stage hole):
`Style.When` is a `WhenCollection` of public `DataCondition`s (binding + `Equals`-value or predicate, with `Negate`;
unknown/`UnsetValue` ⇒ unmet, fail-closed even when negated). The collection AND-composes into each `CompiledRule`
(own + `BasedOn` + nesting-parent conditions), each condition counting **1 classLike** toward specificity (SD5
realized — a `When`-guarded style beats its unguarded base). The `StyleEngine` arms one `BindingOperations.Watch` per
condition when a rule structurally matches (`BindWhenRequirements`/`WhenConditionRequirement`, parallel to the
ancestor-state requirements), gates `ComputeSatisfied` on every condition being met, and reconciles through the same
queued/fixpoint Phase-2 path on each watch delivery (synchronous when idle — a VM flip reaches layout the same frame,
invariant 1). Watcher lifetime = armed rule lifetime (ledger B16 — live across deactivation since the watch is the
re-activation predicate; **no Pause/Resume**; disposed at disarm via `RemoveFrame`→`UnbindWhenRequirements` and at
detach via `OnElementDetached`; the teardown sweep is the backstop). End-to-end rows are
`binding-matrix.md` §13a (B162a–B162h, tests in `Cursorial.UI.Tests/StyleMatrix/Section14_When.cs`); the P3
style-matrix carries a recorded P4 amendment in its §0 scope note.

**Phase 5 complete** (doc §14 — S7 resources/theming R0–R3 + S8 control infra C0/C1/C3 + the first controls;
normative spec at `docs/ui-layer-design/control-matrix.md`, 242 rows C1–C242 + the CD ledger, tests in
`Cursorial.UI.Tests/ControlMatrix/Section01…Section14`).

- **Resources / theming (S7, `Cursorial.UI/Resources/` + `Themes/`)** — `ResourceDictionary` (string/`Type`/
  `DataTemplateKey` keys, `MergedDictionaries` own-beats-merged-later-wins, `ThemeDictionaries`, deep-freeze `Seal`
  that never pulses, refcounted `BeginUpdate` coalescing, structural `Version`, in-place deferred realization via
  `IDeferredResourceEntry`); the lookup chain (`FindResource`/`TryFindResource`: element → logical ancestors with
  the template-root hop → app `Resources` → app `Theme` → `CursorialTheme.BuiltIn`); StaticResource (ambient stack)
  vs DynamicResource (`SetResourceReference`, live `ResourceSubscriptionRegistry` with pause-not-destroy + snapshot/
  tombstone sweep + scope-containment shadowing); `ThemeVariant` (`ThemeBase` from negotiated background luminance ×
  `ColorDepth` tier, `ThemeVariantProbe` truth table, flip = `ResourcesChanged` only — no style re-match), the
  `GetResourceVersion(element)` + `ActualThemeVariant` cache-key contract (TextBlock's `FormattedText` never goes
  stale on a flip). **The `UIApplication` S6+S7 merge (punch 44)**: one type carrying host/loop + theme
  (`Resources`/`Theme`/`RequestedThemeBase`/`RequestedColorTier`/`ActualThemeVariant`/`ResourcesChanged`); the
  capability color-tier class re-points to the **effective** tier (`ActualThemeVariant.Tier` — inversion 6 closed),
  app theme leg first in the capability fan-out.
- **Control infrastructure (S8 C0, `Cursorial.UI.Controls`)** — `Control` (Template `StyledProperty`,
  `ControlThemeKey:object` exact-key, Background/Foreground(inherited)/BorderPen/Padding, `ApplyTemplate` at
  measure-time, `OnApplyTemplate`/`OnTemplateDetaching`, `GetTemplatePart<T>`, `[TemplatePart]` validation),
  `ControlTemplate` + `ITemplateContent` (runtime `FuncTemplateContent`; XAML node-graph at P6) +
  `TemplateInstance` (Root/NameScope/`Detach()` store-owned retraction with a leak tracker; template namescope on
  the templated parent — punch 31; `TemplatedParent` stamping = the barrier mechanism), `TemplateBinding` fast
  path; `ContentControl` + `ContentPresenter` (Content/ContentTemplate + the DataTemplate-by-type chain ItemsControl
  reuses at P9; auto-aliasing read-through with no presenter store entry — A22; recursion guard).
- **First controls (S8 C1/C3)** — `TextBlock` (markup/`FormattedText`, the version+variant cache key), `Border`/
  `Decorator`, `ButtonBase`→`Button`/`RepeatButton`(on the `UITimer` slice)/`ToggleButton` (`Click` routed event,
  `IsPressed` via `SetInteractionState` ND12 holder, **Space/Enter activate on key DOWN** §13, Command/CanExecute→
  `IsEnabledCore`), `CheckBox`/`RadioButton` (`GroupName` mutual exclusion, `:checked`/`:indeterminate` via
  `PseudoClassMapping`), `ScrollViewer`+`ScrollBar` (wrap S1's banded `ScrollContentPresenter`; `HorizontalOffset`/
  `VerticalOffset` two-way mirrors of the SCP styled offsets; wheel scroll; `Track`/`Thumb`/`RepeatButton` line
  buttons; horizontal `Auto`→`Disabled` with a DEBUG diagnostic). `Label` carries access-key registration (lifted to
  `ContentControl`). `CursorialTheme.BuiltIn` authors the `Type`-keyed control themes in box-drawing idiom, with
  color-tier glyph resources + `caps-ascii` escapes (`☐☑`/`(•)` vs `[ ]`/`[x]`/`(*)`). `Phase5EndToEndTests` prove
  cross-`ThemeVariant`-tier rendering, template Detach retraction, DataTemplate-by-type, click via mouse+keyboard,
  the ScrollViewer banded composite-slide, invariant-3 control restyle isolation, and the TextBlock theme-flip
  cache-key; the motion-storm gate re-asserts over templated controls.

**Phase 6 complete** (doc §14 — Fork C X0–X3, the XAML runtime loader; normative spec at
`docs/ui-layer-design/xaml-matrix.md`, rows X1–X188 + the XD/C-* ledgers, tests in
`Cursorial.UI.Xaml.Tests/XamlMatrix/Section01…Section14`). Two assemblies (doc §4 / §1.3): the **netstandard2.0
`Cursorial.UI.Xaml.Frontend`** (shared with the future X4/X5 generator) and the **net10.0 `Cursorial.UI.Xaml`**
loader.

- **X0 — frontend** (`Cursorial.UI.Xaml.Frontend/`) — the structure-of-arrays `XamlDocument` (depth-first-contiguous
  records, `SubtreeLength` = O(1) deferred-slice), the single-pass `XmlReader` parser (`DtdProcessing=Prohibit`,
  element/property-element/attribute/content distinction, xmlns→CLR via `using:`/`clr-namespace:`/the default map,
  `x:` directives, attached + plain member classification, intrinsic + context-free constant folding, end-of-object
  `Setter` resolution against the lexical `TargetType`, deferred-slice capture for `ITemplateContent` content, the
  XD19 whitespace rule), the hand-rolled recursive-descent markup-extension grammar (`{}` escape, `\` escapes,
  nested MEs, the WPF leading-`{` rule, `{x:Static}` fold placeholder), diagnostics (`XamlDiagnostic`/
  `XamlParseException`, 1-based line+col, CUR-banded codes, `DidYouMean` Levenshtein), and the type-system seams
  (`IXamlTypeMetadataProvider`/`XamlType`/`XamlMember`/`ITypeConverter`).
- **X1 — instantiation** (`Cursorial.UI.Xaml/`) — `XamlLoader` (`Parse`/`Load`/`Load<T>`/`GetOrParse`/
  `LoadComponent` + `Shared`), `ReflectionXamlMetadata` (the only reflection site — activation/CLR setters/getters/
  events/`x:Static`, per-type cached, base-walk static-ctor forcing so inherited `UIProperty` registrations
  populate the registry), `XamlSchemaContext` (the default UI/Controls/Data/Drawing.Media map + app-assembly /
  default-namespace registration), the `ContentPropertyTable` (the additive `[ContentProperty]` decision —
  `ContentControl→Content`/`Decorator→Child`/`Panel→Children`/template→Content/`Style→Setters`), the
  `XamlConverters` ladder (cell/Margins/GridLength/Color/#hex/named-ANSI/Palette/`IBrush`+gradients/`Pen`/
  `TextAttributes`/`KeyGesture`/enum/bool/double), `x:Name`→document `NameScopeDictionary`, the XD8 `SetValue` at
  `LocalValue` for `UIProperty`s. **Namespace-aware Styles (XD26/#23):** root-only xmlns is enforced (a non-root
  declaration is `CUR2004`) and captured into `XamlDocument.Namespaces`; a Style's `Selector` is built at activation
  (not folded — `SelectorConverter.IsContextFree==false`) with `XamlSelectorTypeResolver` so a `prefix|Type` token binds
  the document xmlns (the `:is(t|Base)` case), an explicit `Selector` winning over a co-present (Setter-resolution-only)
  `TargetType`; `TargetType` itself now builds an **exact-type** selector (`Selectors.OfType(resolvedType)` via the
  metadata, simple-name `Selector.Parse` fallback).
- **X2/X3 — markup extensions + deferred content + resources + access keys** — the `IXamlMarkupExtensionHandler`
  (StaticResource eager / DynamicResource live-producer / Binding + TemplateBinding via `BindingOperations` / custom
  `ProvideValue`; attach through `IDeferredValue.AttachTo`/`SetResourceReference`, never a sentinel through
  `SetValue`), the public `MarkupExtension` base + `IServiceProvider` seams, the lexical `XamlResourceScopeStack`,
  `XamlModule` (the C-9 `ResourceDictionary.LoadCallback` module initializer + `IXamlResourceProvider`/
  `EmbeddedXamlResourceProvider`), `XamlDeferredResourceEntry` (the `IDeferredResourceEntry` node-graph slice,
  retry-safe), `XamlTemplateContent : ITemplateContent` (fresh subtree per `Build`, template namescope, lexical
  resource capture, TemplateBinding to the templated parent). **Access-key folding** is applied only to genuinely
  `AccessText`-typed members; object-typed `Content="_Save"` stays the raw string and the runtime
  `ContentControl.GetAccessText()` folds on demand (the three-identical-producers rule — loader fold ≡
  `AccessText.Parse`).
- **P6 integration** — `Cursorial.UI.Xaml.Tests/Integration/Phase6XamlEndToEndTests` proves the §14 exit criteria
  end-to-end through `UIHeadlessHost`/the real frame loop: (a) a themed `StackPanel`/`Grid` of Buttons + CheckBoxes + a
  `{Binding}` ScrollViewer + `{StaticResource}` brushes + a `{TemplateBinding}` `ControlTemplate` renders (cell
  assertions) and the bindings/resources resolve live (incl. a VM-update re-render); (b) a `ResourceDictionary` with
  `MergedDictionaries` + `ThemeDictionaries` + `{DynamicResource}` consumers updating on a theme-base flip; (c) a
  `ControlTemplate` expanded per target with independent template namescopes + `Detach` retraction; (d) the
  access-key fold + Alt-chord activation through the P2/P5 `AccessKeyManager`; (e) error quality (line+col on
  `XamlParseException`). The **System.Xaml oracle leg** (`SystemXamlOracleTests`, doc §4.10) is reflection-only and
  Windows-gated (`Assembly.Load("System.Xaml")`; System.Xaml is Windows-Desktop-only — vendoring rejected per
  doc §4) — it pins the whitespace-collapse (XD19/X32–X34) + `{}` brace-escape (X46/X47) rows against real
  System.Xaml on Windows CI, and **skips elsewhere with a documented reason**. Its oracle node uses a portable
  `[TypeConverter]` (System.ComponentModel) rather than the Windows-only WPF `[ContentProperty]`, so the class
  compiles cross-platform. The **`uixaml` demo** loads its entire control tree from an embedded `.xaml` resource at
  runtime — the live P6 proof.

**P6.1 fixes** (four control/input/theming bugs surfaced by hands-on demo use, each fixed at the engine/theme layer
with a regression test — no demo hacks): **(1) spacebar activation** — `ButtonBase` keyed off `Key.Space`, but a real
spacebar is `(Key.Character, " ")` on every wire (ND10; `Key.Space` is only NUL→Ctrl+Space); now matches the
modifier-free character form, with Ctrl+Space explicitly excluded (it's the lone real `Key.Space` wire). **(2)
ScrollBar arrows stole Tab** — the BuiltIn `ScrollBarTemplate`'s line `RepeatButton`s inherited `Focusable=true`;
set `Focusable=false`/`IsTabStop=false` (WPF parity — scrollbars are driven by content keyboard + mouse). **(3)
theme cycling had no visible effect** — control themes wired *constant* pens/brushes; the **R2 DynamicResource
palette spine** (`SetResource` into `ThemeKeys`) now feeds resting Foreground/BorderPen + the `:focus` `FocusPen`, so
a dark/light flip re-skins live (and the focus border resolves the palette accent, not the terminal default). **(4)
keyboard-focus never engaged a freshly-shown tree** (XAML *and* hand-built) — activation auto-focus ran
synchronously in the `RootElement` setter, **before the first layout** realized templated subtrees, so the
first-tab-stop walk found nothing and gave up; `FocusManager` now **parks** the activation and the frame loop retries
it at the post-layout boundary (`CompletePendingActivationFocus`), so the first focusable auto-focuses the same frame
its subtree materializes. (The XAML loader was exonerated — a hand-vs-loader diff proved both trees hit the identical
latent timing bug.)

**Phase 7 complete** (doc §14 — the S4 windowing layer, in `Cursorial.UI/Windowing/`). The `WindowManager`
*is* the window system (no OS HWNDs): it owns the `ScenePool` + `SceneCompositor`, implements the frame-loop
seams (`ILayoutSystem`/`IRenderSystem`/`IWindowSystem`) **and** S3's `IWindowTopology`, and replaces the P1
single-root stand-ins with no frame-loop change. A `TopLevelSurface` wraps one S1 `RenderTree` at a screen
offset; surfaces stack root → windows → popup band → fit badge, concatenated into one composite per frame.

- **W0–W2** — `TopLevelSurface` + the surface stack; `Window : ContentControl` (interim occluding chrome
  template — a background-filled title bar that drags, a docked ✕, a ◢ resize grip; `WindowStyle.None` is
  chrome-less) with modeless `Show`/`Activate`/`Close` + owner immutability; the modal stack + blocked
  (`obscured`) set + activation handoff (owner→gate→topmost→null only when `wasActive`); **`ShowDialogAsync`
  is `Task`-based with the frame loop as the pump — cancellation THROWS `OperationCanceledException` (never a
  default return), marshaled to the UI thread**.
- **W3** — the WM as the real `IWindowTopology`: `FilterMouseEvent` does surface scan → light-dismiss sweep →
  blocked-host swallow + `:modal-attention` pulse (a frame-aligned `UITimer`) + capture release →
  activation-on-press → surface-local route; `dispatcher.OnSurfacesChanged()` on every topology change.
- **W4** — `Popup : UIElement` (light-dismiss primitive): lives in the host's LOGICAL tree (Child inherits
  context; the Escape route crosses the surface root back through the logical parent), `Child` roots its own
  surface in the band above all windows; two-way `IsOpen` write-back, flip-then-clamp placement, light dismiss,
  content-swap-while-open re-host.
- **W5 + W5b** — chrome drag/resize/maximize (role-keyed via `WindowChrome.HitTestRole`, screen-anchored
  deltas, `WindowState` maximize/restore through `OnWindowStateChanged`→`ApplyMaximizeState` so the gesture
  AND a direct assignment behave identically); the **no-auto-shrink screen-resize policy** (a terminal resize
  clamps ≥ `MinVisible` cells onto both axes but never shrinks — user choice) with the WM-owned **fit badge**
  (top-right, appears after a clipping resize, `FitAllWindowsToViewport` + dismiss); `SizeToContent` re-fit
  (`SyncSurfaceSize`); the §8.8 **deferred-topology queue** (mutations during layout/render defer to the next
  `DrainDeferredTopology`); Title → OSC 2 on the active window; `CloseAllAsync` shutdown sweep (owner cascade
  + hosted-popup close) wired into teardown; `WindowDiagnostics.DumpZOrder`. `UIApplication.WindowManager` is
  public.
- **Subtree mouse capture pulled into v1** (the P2 "element-only" deferral reversed): `CaptureMode {Element,
  SubTree}` on `CaptureMouse` (input-matrix N95–N97).
- **P7 review fix (multi-surface styling)** — the P3 StyleEngine assumed a single root (`VisualRoot ==
  app.RootElement`), so NO window/popup content was styled or themed (controls measured 0×0). `IsStylable` is
  now "attached under any live surface root" (its `VisualRoot` owns a `LayoutManager` — the signal available
  before the `RenderTree` exists); capability-class stamping and app/theme-wide re-match iterate
  `StylableSurfaceRoots()` (the app root + every window/popup/chrome surface).

Tests: `Cursorial.UI.Tests/Windowing/` (`TopLevelSurfaceTests`, `WindowManagerTests`, `WindowTests`,
`WindowDataTests`, `WindowModalTests`, `WindowInputTests`, `WindowPopupTests`, `WindowResizeMoveTests`) +
`InputMatrix/Section07` subtree-capture rows; the `windows` demo is the live canary (UI suite 1822 green). The
interim chrome template is replaced by S8's themed control theme at C4.

**Phase 8 complete** (doc §14 — the S5 animation layer; normative spec at
`docs/ui-layer-design/animation-matrix.md`, rows N1–N153 + the AD pinned-decision ledger, tests in
`Cursorial.UI.Tests/AnimationMatrix/Section01…Section16`). The mechanism lives in `Cursorial.Animation`
(time-free `IAnimation<T>`, easings, interpolator registry); the orchestration in `Cursorial.UI/Animation/`.

- **A0 — scheduler core** — `AnimationScheduler` (thread-ambient `Current`, implements `IAnimationFrameDriver`:
  `BeginFrame`/`Tick`/`TickNewlyStarted`/`HasActiveAnimations`/`Shutdown`), `FrameClock` (frozen per frame),
  `AnimationInstance<T>` (the Delayed→Running→Holding/Completed/Stopped state machine: self-sample at Begin,
  final write == `ValueAt(Duration)`, perpetual guard, reentrant-Stop check after the write, HoldEnd keeps the
  handle vs Stop retracts — invariant 4), the public `AnimationHandle`/`BeginAnimation<T>` surface, completion
  at-most-once after the sampling pass (`IAnimationCompletion`), SnapshotAndReplace handoff. The
  `Interpolator.For<T>()`/`Register<T>()` registry (process-global, lock-free reads, thread-agnostic — AD12
  amended) seeds double/int/`Color` (Animation) + `PointD`/`Size`/`Rect`/`RelativePoint`/`Margins` (signed,
  LD19)/`CompositeParameters`/`IBrush`/`Pen` (Drawing, via a `[ModuleInitializer]`).
- **A1 — storyboards** — `Optional<T>`, `RepeatBehavior`, `AnimationTrack`/`AnimationTrack<T>` + sealed
  `DoubleTrack`/`Int32Track`/`ColorTrack`/`BrushTrack`/`RectTrack`/`SizeTrack`/`MarginsTrack`; `Storyboard`
  (seal-on-arm `Children`) + `StoryboardInstance` (per-`(igniter, scope)` instancing, group completion roll-up)
  + `StoryboardHandle`; `BeginStoryboard`/`StopStoryboard` `IStyleEdgeAction`s wired into Fork B's
  `Style.Enter`/`Exit` edges + `AnimationDiagnostics` (the edge-ignited no-throw `TrackError` sink). **Edge
  actions are do/undo against the pinned SD16 seam** (Enter entries get `OnActivated`, Exit entries get
  `OnRetracted`): `BeginStoryboard.OnActivated` begins, `OnRetracted` stops; for begin-on-enter + stop-on-exit
  put a `StopStoryboard` in `Exit` (or the same `BeginStoryboard` in both). Edge actions on a **nested** child
  rule fire on the nested rule's edge (`CompiledRule.DeclaringStyle` is the declaring nested style — not the
  always-active parent). `Style` seal-on-attach seals referenced storyboards via the additive
  `IStyleEdgeAction.SealReferences()` DIM. Detach-stop evicts scoped storyboards (§9.6).
- **A2** — `AnimationHandle`/`StoryboardHandle` `Pause`/`Resume`/`Seek`/`SkipToEnd` (Seek anchors to the pause
  clock while paused; storyboard `Seek` maps per-track `offset − BeginTime`, rewinding pre-start tracks to
  Delayed); `AnimationsEnabled` reduced-motion flip (AD15 — Begin-while-disabled snaps finite + born-Stopped
  perpetual; a true→false `Tick` collapses Delayed/Running/Paused); Elastic/Bounce/`CubicBezier` easings +
  `Easings.TryParse` (catalog + `cubic-bezier(...)`); DEBUG diagnostics (`AnimationDiagnostics.Warning` — the
  §9.6 never-attached leak tracker + the §9.9 perpetual-on-`AffectsMeasure` warning).
- **A3 — Transitions** (implicit animations, §9.5) — `Transition`/`Transition<T>` + sealed `Double`/`Int32`/
  `Color`/`Brush`/`Margins` transitions, `TransitionCollection`, the attached `Transition.TransitionsProperty`,
  and the per-element `TransitionManager` subscribing the Fork A winning-base channel
  (`IValueObserver<T>.OnBaseValueChanged` via `ObserverOptions.IncludeBaseChanges`): a base change starts an
  Animation-priority run `From = isAnimated ? GetValue : oldBase`, `To = newBase`, `Fill.Stop`. **The "initial
  application doesn't transition" rule is a per-element go-live latch keyed to the element's first NON-collapsed
  arrange** (`UIElement._hasArrangedVisible`, reset on attach so re-attach re-parks), flipped at the post-layout
  boundary (`UIApplication.CompletePendingTransitionGoLive`, the `CompletePendingActivationFocus` mirror); arming
  on an already-visibly-arranged element goes live at once. This is the only siting after both initial
  base-write points (attach-time own props + first-layout templated parts), which both fire the winning-base
  observer indistinguishably from a real change.

Each sub-phase was adversarially audited (the audits found and fixed 7 real bugs the green tests missed). The
`motion` demo (`Cursorial.Demo`) is the live canary: a toast Storyboard (slide + fade, selectable easing), a
hover Opacity Transition, and an edge-action perpetual pulse. UI suite 1945 green, Animation 113 green.

**Phase 9 complete** (doc §14 — the S8 control gallery + P9 closeout; normative spec at
`docs/ui-layer-design/control-matrix-p9.md`, tests in `Cursorial.UI.Tests/ControlMatrix/Section15…Section21`).
The remaining controls landed on `ItemsControl` + `ItemContainerGenerator` (index-aligned containers,
Realize/Unrealize/Insert/Remove/Move/ResetFromSource, `:alternate` row-striping via `Restripe()` after every
structural change — CD-P9-25): `Separator`, `Menu`/`MenuItem` (submenu `Popup`s, hover-open timer, access-key
mnemonics), `ContextMenu` (right-click / `Key.Menu` router default), `ToolTip`/`ToolTipService` (hover-driven,
hit-transparent never-focused popup), `TabControl`/`TabItem`, `ProgressBar`, `TextBox`, `ListBox`/`ListBoxItem`.
The closeout ran as seven workstreams (W1–W7):

- **W1 — themed window chrome (C4, punch 36)** — the S4 interim `Window.Template` default is gone; the chrome is
  a `typeof(Window)` control theme in `CursorialTheme.BuiltIn`, resolved through the control-theme chain (so an app
  `Window` style overrides it — the b13fc2a Template-lane precedence fix). The active-look (band/ink tracking
  `IsActive`) is wired in `Window.OnApplyTemplate` against the named chrome parts and unhooked in
  `Window.OnTemplateDetaching` (no handler leak on re-template).
- **W2 — `:alternate` row-striping** — `ItemContainerGenerator.Restripe()` stamps `:alternate` on odd 0-based
  containers (opt-in look — no default stripe) and re-stripes after insert/remove/move.
- **W3 — resource-inspector hook** — `ResourceDiagnostics.GetResourceKey(element, property)` is the reverse of
  `Trace`: the resource key the property's effective value resolved through — instance `SetResourceReference`
  first, else a style/theme `{DynamicResource}` setter, **gated on the winning base lane actually being Style**
  (a LocalValue/Template literal masking a resource-backed style setter reports no key — it isn't resource-backed).
- **W4 — on-close focus restore** (the last W4-b deferral) — closing a popup that held keyboard focus returns
  focus to the specific trigger (Esc-in-submenu → parent header, dismiss → opener); guarded on
  `Child.IsKeyboardFocusWithin` (a hover-only popup/tooltip never yanks focus) and `!_open` (forward-defense
  against a synchronous mid-close re-open — the binding-driven re-assert lands a turn later, so the cycle
  converges cleanly).
- **W5 — the ARCH-1 XAML theme overlay** (`Cursorial.UI.Themes.Xaml`) — every BuiltIn control theme re-authored in
  embedded `.xaml`, loaded via `CursorialXamlTheme.LoadControls()`, layered over the code-first BuiltIn (which
  stays the chain backstop). The inline controls render byte-identically to BuiltIn; the popup-rooted ones
  (Menu/ContextMenu/ToolTip/TabControl) are proven at runtime through `UIHeadlessHost`.
- **W6 — control-gallery demo + composition test** — `Cursorial.Demo`'s gallery + `P9ControlCompositionTests`
  (every P9 control in one tree, re-skinning across every color tier + a dark/light flip). A default `Label`
  control theme was added (a bare `ContentControl` has no presenter → renders blank without one).
- **W7 — closeout review** — a multi-agent adversarial review of W1–W6 surfaced 8 confirmed findings, each fixed
  with a regression test (mutation-verified where the fix was subtle): MenuItem's missing `[TemplatePart]`,
  ContextMenu's owner-detach popup leak (the menu now watches its placement target's `DetachedFromLogicalTree`,
  since the surface-rooted menu never sees the owner's detach), the W3 priority gate, the W4 re-entrancy guard,
  the W1 handler leak, the `:alternate` move re-stripe, GetResourceKey value assertions, and a duplicate
  `TabItemTemplate` in the XAML overlay. The runtime XAML-theme tests also caught a real W5 bug: the XAML
  `MenuItem` template put implicit content in a `<Popup>`, but `Popup` had no content property in the loader —
  fixed by adding **`Popup → Child`** to the loader's `ContentPropertyTable` (WPF `[ContentProperty("Child")]`
  parity), so `<Popup>child</Popup>` maps to `Popup.Child`.

The `accesskeys`/`uixaml`/`windows`/control-gallery demos are the live canaries. The access-key capability gate
on a real Kitty terminal (the `(DistinguishesKeyUpDown && ReportsRepeats) || Win32InputMode` verdict, ND23) is a
**manual verification step** — it can't be exercised headlessly and needs a hands-on Kitty session.

**TextAttributes decomposition** (2026-07-13, proposal-textattributes-decomposition.md + its adversarial
judgment): `TextElement`'s aggregate `TextAttributesProperty` (an inherited flags bag) is retired for **per-axis,
NON-inheriting attached properties** — `TextWeight{Normal,Faint,Bold}` (the shared SGR 22 reset makes Bold/Faint
one axis), `TextStyle{Normal,Italic}`, `Underline:UnderlineStyle?` (presence+shape unified), `Strikethrough`/
`Overline`/`Inverse`/`Blink`/`Concealed` bools — so the lattice arbitrates each display characteristic independently
(the motivating bug: adding Bold no longer clobbers a theme's Inverse). Renderers fold the effective axes to
`Output.TextAttributes` via `TextElement.ComposeAttributes` at paint. The axes "flow like `Background`" (owner
decision): element-level values delivered to template parts and generated leaves by explicit forwards
(`ContentPresenter` forwards all axes onto generated LABELS; glyphs/icons/carets forward Inverse ONLY — a symbol
isn't text). The interactive-cue resource split into `ThemeKeys.InteractiveCueInverse`(bool)/`InteractiveCueWeight`
(`TextWeight`). Rider: XAML 8-digit hex is now `#RRGGBBAA` (was `#AARRGGBB` — one alpha convention across the stack).
The deferred P9.3b NoColor list focus cue (Inverse+Bold) shipped as the composability proof.

**Post-P9 controls** (the S8 gallery extended; normative spec at `docs/ui-layer-design/control-matrix-p9.md`
§C10–§C15, tests in `Cursorial.UI.Tests/ControlMatrix/Section23…Section28`; code-first `CursorialTheme.BuiltIn`
themes, with the XAML overlay twins now shipped in `Cursorial.UI.Themes` (`Default`/`IndigoDusk`) over that
backstop): an **animated indeterminate
`ProgressBar`** (the marquee rides a perpetual S5 animation on a normalized `IndeterminatePhase`, §C10);
**`ComboBox`/`ComboBoxItem`** (the ListBox-in-Popup single-select drop-down, §C12 — introduced
`Popup.KeepOpenOnAnchorPress` so the anchor owns the open/close toggle, reused by `DatePicker`);
**`TreeView`/`TreeViewItem`** (tree-wide single selection coordinated directly — not via `SelectingItemsControl`'s
flat model; recursive-indent template, visible-tree keyboard nav, §C13); **`Calendar`/`CalendarDayButton`** (a
month-view picker building its 7×7 grid in code, culture-ordered, §C14); **`DatePicker`** (a date field dropping a
`Calendar` popup — the calendar variant; the standalone `Calendar` is the inline variant, §C15). Each was
adversarially audited (each finding refutation-verified through `UIHeadlessHost`): the audits found and fixed **3 (Tree)
+ 7 (Calendar) + 3 (DatePicker)** real bugs the green tests missed — see the matrix `CD-P2C-1`/`CD-P2D-1`/`CD-P2E-1`
audit notes. The control-gallery demo gained `T_ree` and `_Date` tabs.

**Phase 10 complete** (doc §14 — Fork C X4 generator + S2 compiled bindings; normative specs amended in
`binding-matrix.md` BD17/B146–B186 and `xaml-matrix.md` §15). Two halves were planned B2 → X4 → B3:

- **S2 B2 — the compiled-binding runtime: ✅ complete.** `BindingExpressionCore` was extracted from
  `ReflectionBindingExpression` (the shared lifecycle: anchoring, the source-notification ladder dispatch, the
  forward boxed pipeline, two-way / one-way-to-source write-back with echo suppression, cross-thread coalescing,
  eviction) so both lanes derive from it (`Cursorial.UI/Data/`). `CompiledBindingExpression<TSource,TValue>`
  (the second subclass) implements the typed push lane: a whole-chain `Getter` read, `Steps`-driven INPC/INCC
  subscription (the tail re-subscribes only below a changed hop, so a single-hop binding's steady-state push is
  0 B), a **typed zero-box push** via `BindingEntry<TValue>.SetValue` (OneWay — the `AnimatedValueHandle<T>`
  analog; B147) with forfeit-to-boxed on a converter/StringFormat/coercion (B182), the typed root check
  (`SourceTypeMismatch`, B153), read-only-leaf degrade (B152), and frame-hosted installs (B186).
  `CompiledBinding.CreateExpression` no longer throws — **`Binding.Compiled(...)` works end-to-end** (the
  reflective-fallback v1 producer, B185). Binding matrix 183 green (164 reflection + 19 compiled, in
  `BindingMatrix/Section12_CompiledLane` + `Section15_CompiledDescriptor`), the extraction mutation-verified,
  full UI suite 2260 green.
- **TSA — the frontend type-system abstraction (the keystone, ✅ complete):** `XamlType.ClrType` and
  `XamlMember.ValueType` are now an `IXamlType` interface (`Name`/`IsCollection`/`UnderlyingSystemType?`), not
  a concrete `System.Type` — two backends: `ReflectionXamlType` (loader; wraps `System.Type`) and
  `RoslynXamlType` (generator; wraps an `INamedTypeSymbol`, `UnderlyingSystemType` null). The `Type`-accepting
  ctors stay as conveniences that wrap into `ReflectionXamlType`, so the loader's reflection provider keeps
  passing `typeof(T)`; the parser's single-child Object-vs-Items decision reads `IXamlType.IsCollection`
  (the old `IsCollectionMember` relocated into the backend); the loader reaches the runtime `Type` through
  `UnderlyingSystemType` (a small internal `SystemType()` helper). This is what lets `XamlFrontend.Parse` run
  **inside the generator over Roslyn symbols** — the same parser the loader runs.
- **Fork C X4 — the build-time XAML generator (`Cursorial.UI.Xaml.Generator`, a netstandard2.0 Roslyn
  `IIncrementalGenerator`): the core story is complete** — symbol-backed parse, full diagnostics, the AOT-clean
  generated provider, and typed code-behind. It references the frontend via `ProjectReference` +
  `InternalsVisibleTo` (the node-graph internals).
  - **The build-time type system** is resolved purely from Roslyn symbols (never loading `Cursorial.UI` into the
    compiler): `XamlSymbolResolver` maps `(xmlns, localName)` → `INamedTypeSymbol` (the `XamlSchemaContext` default
    map); the shared `SymbolXamlModel` (extracted from the emitter — ONE source so the parse provider and the
    emitted provider can't drift) mirrors `ReflectionXamlMetadata`'s full `BuildMember` ladder: the synthetic
    `Style.TargetType`, registered `UIProperty`s via the `<Name>Property` field convention (instance AND
    **attached** — `Grid.Row` etc., a second enumeration pass), events, CLR properties.
  - **`RoslynXamlMetadata` (X4.3)** is the symbol-backed `IXamlTypeMetadataProvider`: it builds `XamlType`/
    `XamlMember` with `RoslynXamlType` identities so the parser runs in the generator, yielding the **`CUR1xxx`
    syntax AND `CUR2xxx` semantic bands** (type/member-not-found) as Roslyn build diagnostics at the `.xaml`
    line/col (X4.4). A coverage audit over a representative theme-style document asserts zero CUR2 false
    positives on valid XAML.
  - **`MetadataProviderEmitter` (X4.5)** emits one generated `IXamlTypeMetadataProvider` per compilation over the
    union closed type set, advertised via `[assembly: XamlMetadataProvider]` — PULL metadata: the lazy
    `XamlLoaderOptions.DefaultMetadataProvider` discovers the ENTRY assembly's attribute (AOT-clean), so loading
    an assembly never repoints a host's default (no `[ModuleInitializer]` — the push-install variant hijacked
    designer/test hosts). The **dual-run drift gate** (matrix X174 / the P10 exit) loads it and
    asserts a real control tree (incl. an attached `Grid.Row`) renders **byte-identically** to
    `ReflectionXamlMetadata` — zero drift. The converter is an emitted runtime `XamlConverters.For(typeof(...))`
    call (drift impossible).
  - **Code-behind (X4.6)** — for each `x:Class` document `CodeBehindEmitter` emits a `partial class` with a typed
    `internal <ElementType> <Name>` field per document-scope `x:Name` (resource-dictionary / template-scope names
    excluded) and an `InitializeComponent()` that loads the XAML through a loader bound DIRECTLY to the
    assembly's own generated provider (deterministic, no global-default coupling), then assigns the fields from
    the document name scope. An end-to-end test compiles a real `MyView : StackPanel` code-behind against the
    generated partial, instantiates it, and asserts the typed fields are populated. Generator suite 36 green;
    `Cursorial.UI.Xaml.Generator.Tests` is serialized (module-inits mutate the process-global default provider).
  - **X4.2/X4.7 + broader coverage + S2 B3 — complete:** the `CursorialXaml` MSBuild `.props`/`.targets`
    (`Cursorial.UI.Xaml.Generator/build/` — a first-class `.xaml` item → `AdditionalFiles` + `EmbeddedResource`, with
    `CursorialXamlStrictAot` auto-set under `PublishAot` so the loader's static `ReflectionXamlMetadata` reference is
    trimmed out); the `Cursorial.Demo.XamlAot` / `Cursorial.Demo.XamlAotStrict` NativeAOT-publish demos (the latter is
    the reflection-free exit gate on the generated provider); broader emitter coverage (straight-line lowering incl.
    `x:Static`/`x:Null`, deferred content/templates, Style/ResourceDictionary); and **S2 B3** (generator-lowered
    typed `CompiledBinding` descriptors + `x:DataType` build-time path diagnostics via `XamlDataTypeScope`).

Recorded P1 gaps: the `BindingOperations.TearDown` leg of `UIElement.TearDown()` **landed at P4** (the S2 sweep half:
`ValueStore.TearDown()` then `BindingOperations.TearDown(element)`, bottom-up — binding-matrix B108/B166); palette
theming + capability rewrite and the S7 surface merge into `UIApplication` (P5);
`TerminalSessionOptions.EmergencyRestoreBytes` Core seam for signal-path alt-screen restore (doc §10.7 — until it
lands, a signal-killed app restores cooked mode but may leave the shell on the alt screen).

## Cursorial.UI.Bars status (`Cursorial.UI.Bars`)

The command-surface suite (Actipro-*Bars*-style): a **Ribbon, a Toolbar, and menus are three surfaces over one shared
set of `ICommand`-driven bar controls** — build the controls once, bind the same commands to each surface. Design
guides at `docs/ui-layer-design/tokyo-night-bars-design-guide.html` (+ `…-terminal-toolbar.html`,
`…-terminal-ribbon.html`); the eventual showcase is a WYSIWYG Markdown editor (still pending — task #133). Everything
is whole-cell and dark/light-themed from one dictionary.

- **`Icon` (prerequisite, in `Cursorial.UI.Controls`)** — a capability-tiered icon (`Glyph` Nerd Font → `Image` inline
  protocol → `Text` emoji/Unicode floor), resolving the highest provided-and-supported tier; `{Icon …}` markup form.
- **`BarCommand`** — the define-once model (`ICommand` + display metadata: `Text`/`Icon`/`InputGestureText`/
  `IsCheckable`). Bar controls auto-fill their unset `Content`/`Icon`/gesture from the bound `BarCommand`
  (`BarCommandSync`), so one declaration drives a toolbar button, a ribbon toggle, and a menu row identically.
- **Shared bar controls** (`ButtonBase`-derived where it applies) — `BarButton`, `BarToggleButton`, `BarSplitButton`
  (primary zone + tinted `▾` zone) / `BarPopupButton` (whole-control opener) over the shared `BarDropDownButton`,
  `BarComboBox`, `BarGallery`, `BarSeparator`, `BarLabel` (access-key caption). Drop-openers carry a per-placement nav
  model (`DropDownPlacement`: Bottom = ComboBox model — open parks on the face, a 2nd Down enters; Left/Right = MenuItem
  submenu model — open AND enter; `FocusContentOnOpen` opts a Bottom opener into enter-on-open). Openers are retaining
  focus scopes so a non-retaining `Toolbar`'s auto-return can't yank focus out of an open dropdown.
- **`Toolbar`** (`ItemsControl`) — a single row with **discrete overflow**: `ToolbarOverflowPanel` (the items host)
  re-parents the trailing LIVE controls into a `»` chevron `Popup` band and back as the bar resizes (per-item
  `OverflowMode`); overflowed drop-openers flip to side placement in the vertical menu. `MiniToolbar` — the
  right-click floating strip.
- **`Ribbon`** (`ItemsControl` of `RibbonTab`) — tab strip + `RibbonGroup`s (large/small button sizes, `⋰` dialog
  launcher); **density collapse** (a too-narrow group demotes to a `[name ▾]` flyout), **contextual tabs** (purple
  tint, visibility-bound), **Backstage** (full-window File view or a compact File-anchored menu), **Quick Access
  Toolbar** (an embedded `Toolbar` + customize checklist + trailing collapse), and a **minimizable** band
  (double-click / pin, float-on-activate). **`RibbonControlGroup`** stacks related small/medium controls into the
  band's 2 rows beside `Large` heroes (contiguous min-width packer + `RowBreak` pins + `ItemSize` stamping; `Auto`
  stacks only when the band's AUTHORED height is 2 — density folds re-ink faces but never re-flow rows), and
  **`Ribbon.LayoutMode`** (`Classic`/`Simplified`/`Compact`) is the user-directed density axis: one labeled row (no
  group footers) or one icon-only row (a root-stamped `IsDensityCompact` pin the fold's clear-not-write-false
  un-compact respects), authored faces restored exactly on the way back. Vertical nav crosses the tab-strip ↔ body boundary (`Ribbon.OnKeyDown`:
  Down off a header enters the body; Up at body-top climbs to the strip; a collapsed opener is treated as body-top).
- **KeyTips** (`KeyTip`/`KeyTipExtensions`) — an Alt-overlay accelerator-badge layer with multi-level drill-in
  (parallel to `AccessKeyManager`, gated on the same negotiated capability), armed via `UIApplication.EnableKeyTips()`.
  **SuperTips** (`SuperTip`) — rich titled hover tooltips (title + shortcut + KeyTip hops + description + footer) over
  `ToolTipService`; a described `BarCommand` auto-provisions one.
- **Theming** — `CursorialBarsTheme` builds a code-first `ResourceDictionary` of every bar control theme keyed by
  control `Type`, reusing the **core `ThemeKeys` spine** (no separate `BarsThemeKeys` — the bars track the active
  palette + dark/light flips). `BarsThemeModule`'s `[ModuleInitializer]` registers it into the framework's
  **assembly theme-contribution tier** (`ThemeContributions`, design doc §11.3a) — a chain hop between
  `UIApplication.Theme` and `CursorialTheme.BuiltIn`, so the suite renders self-contained (no consumer merge) AND a
  bars control template's `{DynamicResource}` references resolve (the former per-control
  `Control.ThemeProperty.OverrideDefaultValue<T>` install delivered the theme `Style` but was not a chain node, so it
  could not carry brushes). Control themes resolve **exact-key**; a subclass (e.g. `GalleryRibbon : Ribbon`) opts into
  the base theme by overriding `Control.ControlThemeKey` to return the base type (WPF `DefaultStyleKey` parity).
- **`Cursorial.Gallery`** — the standalone XAML-first MVVM control-gallery app; `GalleryRibbon` self-populates the QAT
  from its view-model and is the live canary for the Bars surfaces (Bars/Ribbon pages). Tests in
  `Cursorial.UI.Bars.Tests/` (Toolbar overflow/focus, Ribbon density/minimize/QAT, KeyTips, SuperTips, drop-down focus
  return) + the `Cursorial.UI.Tests/Gallery` smoke tests.

Modules landed:

- **Input** (`Cursorial.Core/Input/`, namespace `Cursorial.Core.Input`) — see "Input module conventions" below.
- **Output** (`Cursorial.Core/Output/`, namespace `Cursorial.Core.Output`) — see "Output module conventions" below.
- **Text** (`Cursorial.Core/Text/`, namespace `Cursorial.Core.Text`) — grapheme-aware width computation and ANSI-aware
  text wrapping; see "Text utilities" below.
- **Terminal** (`Cursorial.Core/Terminal/`, namespace `Cursorial.Core.Terminal`) — `ITerminalNegotiator` is the single
  public entry point for capability detection and opt-in negotiation, returning a `TerminalCapabilities` aggregate.
  `VtTerminalNegotiator` is the VT/ANSI implementation; it owns the probe-and-respond handshake (XTVERSION + DA1
  sentinel pattern) using its own ephemeral classifier + interpreter, then applies opt-in enable sequences for the
  protocols the application requested (SGR mouse + button-event tracking + optional any-event motion, focus events,
  bracketed paste, Kitty keyboard with configurable flag set, Win32 input mode on Windows-family terminals,
  synchronized output on supporting families). The shared `VtInputMode` is updated to reflect what was actually
  enabled. `RestoreAsync` reverses every enable in LIFO order, is idempotent, and is invoked automatically on
  disposal. Kitty / Win32 / synchronized output opt-ins are gated on family identification so capability claims
  don't lie about features the terminal silently ignores. `IEnvironmentReader` abstracts environment access so tests
  can be deterministic.

`VtInputDevice` (`Cursorial.Core.Input.VtInputDevice`) is the concrete `IAsyncInputDevice` over an `IInputByteSource`.
It owns its own `VtSequenceClassifier` + `VtInputInterpreter`, runs a background pump that reads from the source's
`PipeReader`, and bridges the synchronous interpreter sink to the consumer via an unbounded `Channel<InputEvent>`.
The device owns the bare-ESC ambiguity timer (default 50 ms — the xterm convention, configurable via constructor)
and calls `classifier.Flush()` when the idle window elapses with a pending lone ESC. Single-shot per instance:
calling `ReadAllAsync` twice throws. Does NOT take ownership of the byte source — caller (typically `TerminalSession`)
is responsible for transport lifecycle.

`TerminalSession` (`Cursorial.Core.Terminal.TerminalSession`) is the orchestrated entry point with two factories:

- **BYO**: `TerminalSession.OpenAsync(source, sink, options?, ct)` — runs the negotiator over caller-supplied
  transports; disposal stops the input pump and runs negotiator restore but leaves the transports open.
- **Happy path**: `TerminalSession.OpenAsync(options?, ct)` — opens platform stdio transports via
  `StdioTransports.Open()` (POSIX `stty raw -echo` / Windows `SetConsoleMode` with VT input + output flags),
  applies negotiation, and returns a fully-wired session. Disposal restores prior terminal-mode state and closes
  the owned transports. Throws `InvalidOperationException` when standard I/O isn't a real terminal — typical in CI
  or under pipes; use the BYO overload there.

Both factories return a session exposing `Capabilities`, `Input` (`IAsyncInputDevice`), and `Output`
(`IOutputByteSink`). Disposal order: stop input pump → run negotiator restore (writes opt-in disable sequences) →
dispose owned transports (restore terminal mode + close streams). `TerminalSessionOptions` carries the
`NegotiationOptions` and the `EscapeAmbiguityTimeout` for the input device.

`Terminal/Stdio/` houses the platform-specific stdio code: `IStdioTransports` is the public abstraction,
`StdioTransports.Open()` the platform-detecting factory. POSIX uses the `stty` subprocess (one `-g` save + one
`raw -echo` apply at open, one `<saved-state>` restore at dispose). Windows uses `GetConsoleMode`/`SetConsoleMode`
P/Invoke (via `LibraryImport`) — clears `ENABLE_PROCESSED_INPUT`/`ENABLE_LINE_INPUT`/`ENABLE_ECHO_INPUT`, sets
`ENABLE_VIRTUAL_TERMINAL_INPUT` for stdin, sets `ENABLE_VIRTUAL_TERMINAL_PROCESSING` + `DISABLE_NEWLINE_AUTO_RETURN`
for stdout. `LibraryImport` requires `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the csproj.

**Two empirical gotchas baked into the implementation (documented in project memory):**

1. **Do not call `Console.OpenStandardInput`/`OpenStandardOutput`.** Both POSIX and Windows transports wrap fd 0 /
   fd 1 (or the equivalent `GetStdHandle` handles on Windows) as `FileStream` over a non-owning `SafeFileHandle`
   instead. .NET's `System.Console` subsystem manipulates termios/console-mode state on stream access (to ensure
   Ctrl+C generates SIGINT, etc.), silently reverting our raw mode.
2. **When invoking `stty` to apply mode changes, redirect nothing.** Even just `RedirectStandardError = true`
   prevents the change from taking effect even though stty exits 0. Capture calls (`stty -g`) can redirect stdout
   alone to read its output; apply calls must inherit all three streams.

`IStdioTransports.RestoreTerminalState()` is a synchronous, idempotent method (separate from `DisposeAsync`) that
restores just the terminal state — used by `TerminalSession`'s signal-handler safety net to ensure termios/console
mode is restored before the process exits, even if full async disposal is interrupted.

**Signal-handler safety net.** `TerminalSession.OpenAsync()` (the parameterless overload) registers
`PosixSignalRegistration` handlers for SIGINT / SIGTERM / SIGHUP / SIGQUIT plus an `AppDomain.ProcessExit` handler.
On any of these signals the session synchronously calls `RestoreTerminalState()` first (guaranteed terminal restore)
then attempts full `DisposeAsync` with a 2-second timeout, then `Environment.Exit(128 + signal)`. Handlers are
unregistered on normal disposal. BYO sessions (the source/sink overload) do NOT register handlers — those callers
are expected to manage their own signal-handling strategy.
- **Input parsing** (`Cursorial.Core/Input/Parsing/`, same `Cursorial.Core.Input.Parsing` namespace) —
  `VtSequenceClassifier` is a Williams-derived state machine that frames bytes into classified tokens dispatched to
  `IVtSequenceTokenSink`. Covers ground / ESC / CSI / OSC / DCS / SS3 (the SS3 state recognizes `ESC O <byte>` as a
  3-byte sequence and dispatches it through `OnEscDispatch` with intermediate `O`). Does NOT cover APC, SOS, PM, or
  8-bit C1 controls (deliberately out of scope for input). `VtInputMode` is the mutable mode bag (DECCKM,
  modifyOtherKeys level, mouse encoding, Kitty flags, etc.) the interpreter holds and the negotiator updates.
  `VtInputSequences` centralizes UTF-8 byte-string constants. `VtInputInterpreter` consumes classifier tokens and
  emits `InputEvent`s to an `IInputEventSink`. Decoder coverage so far: printable runs (one event per Rune, with
  cross-feed UTF-8 buffering), C0 controls (Tab, Enter, Backspace, NUL→Ctrl+Space, Ctrl+letter), bare ESC,
  focus events, bracketed-paste accumulation, CSI cursor keys + Home/End + special keys (Insert/Delete/PageUp/Down),
  function keys F1–F20 (xterm + vt220 codes), F1–F4 + cursor + Home/End via SS3, BackTab (`CSI Z`), xterm
  modifier-bearing variants (Shift / Alt / Ctrl / Super / Hyper / Meta / CapsLock / NumLock — full xterm + Kitty
  modifier-bit range), modifyOtherKeys level 2 (`CSI 27 ; mod ; codepoint ~`) and the CSI u shorthand
  (`CSI codepoint ; mod u`, handled as a strict subset of Kitty's `u`-final grammar), SGR mouse (DECSET 1006) —
  press / release / drag / motion / wheel and X1–X4 extended buttons, SGR-Pixels mouse (DECSET 1016, coords routed
  into `CellPosition.PixelX` / `PixelY` and divided by the cell size from `CSI 16 t` to populate Column/Row when
  available), and X10 mouse (`CSI M cb cx cy`, end-to-end via `VtInputDevice` mirroring `VtInputMode.MouseEncoding ==
  X10` onto the classifier's `X10MouseFramingEnabled` flag). The interpreter accumulates `MouseButtons` state across
  events so drag and motion carry an accurate held-button mask. Device responses: DA1 (`CSI ? … c`), DA2 (`CSI > …
  c`), DSR-CPR (`CSI row;col R`), CSI window-manipulation responses for window-size and cell-size in pixels (`CSI 4
  ; h ; w t` and `CSI 6 ; h ; w t`), OSC 4 / 10 / 11 / 12 color responses, DCS XTVERSION (`DCS > | … ST`), DA3
  (`DCS ! | hex-id ST`), DECRQSS (`DCS valid $ r data ST`), and XTGETTCAP (`DCS valid + r hex-name=hex-value ST`).
  Win32 Input Mode (DECSET 9001) — `CSI Vk;Sc;Uc;Kd;Cs;Rc_` wraps Windows console input records as escape sequences;
  decoded into `KeyEvent`s with VK→Key mapping, Unicode text payload, and CONTROL_KEY_STATE bits surfaced as
  modifiers. The Kitty keyboard protocol (`CSI key[:shifted:base][;mods[:event]][;text] u`) — full functional key
  mapping, up / down / repeat distinction via the event-type sub-parameter, text payload reporting, and codepoints
  in the Unicode private-use area mapped to the matching `Key` enum values. Alternate-key sub-parameters are parsed
  but not surfaced (`KeyEvent` doesn't carry shifted / base-layout keys yet). Unrecognized CSI/OSC/DCS sequences and
  ESC charset designators are reassembled and surfaced as `UnknownEvent` with the original wire bytes so consumers
  can log / forward / parse without us silently swallowing protocol surface.

`EventInputDevice` (`Cursorial.Core.Input.EventInputDevice`) is the push-style `IEventInputDevice` facade — wraps an
inner `IAsyncInputDevice`, runs a background pump that iterates it, and raises `Input` / `Error` / `Completed` events.
Single-shot per instance because the inner `IAsyncInputDevice` is single-shot. Takes ownership of the inner device on
disposal. By default events fire on the pump (thread-pool) thread; pass a `SynchronizationContext` to the constructor
(or use `EventInputDevice.CapturingCurrentContext(inner)`) to have raises `Post`ed onto it — e.g. a UI dispatcher —
which is order-preserving and never blocks the pump. The pull surface (`IAsyncInputDevice.ReadAllAsync`) needs none of
this: a consumer's `await foreach` resumes on its own captured context, so internal `ConfigureAwait(false)` (used
throughout the input layer) keeps our plumbing off the UI thread without denying the consumer theirs.

`MouseClickSynthesizer` (`Cursorial.Core.Input.MouseClickSynthesizer`) is an `IInputTransformer` that recognizes click
gestures. It counts rapid repeated presses on the same cell (single / double / triple …) using WPF-style timing — the
count is determined at the `ButtonDown` (same button + same cell within `MouseClickOptions.MultiClickThreshold`, default
500 ms; anything else resets to 1) and carried to the matching `ButtonUp` and synthesized `Click`. `ClickCountTarget`
selects which event surfaces the count on `MouseEvent.ClickCount` (default `ButtonDown`; the others stay at the baseline
1). It is purely synchronous — no timers, no channel, no `TimeProvider`; it reads each event's `Timestamp` (the
interpreter stamps every event from `TimeProvider.GetUtcNow()`), so it is deterministic and its gesture state is local
to each `TransformAsync` call (reusable instance). Optionally (`SynthesizeClickEvents`) it emits a `MouseEventKind.Click`
event after a same-cell release with no intervening drag. `ClickCountTarget.Click` requires `SynthesizeClickEvents` or
the constructor throws. Unlike `KeyReleaseSynthesizer` (a device decorator that fabricates events on a timer), click
recognition fits the lighter `IInputTransformer` shape; `TransformingInputDevice` is the reusable adapter that applies
any `IInputTransformer` over an inner device (capability flow via `TransformCapabilities`, single-shot, disposes inner),
with `device.Transform(transformer)` / `device.WithClickSynthesis(options?)` extension sugar.

Resize events: `PosixResizeMonitor` (in `Cursorial.Core.Terminal.Stdio`) registers a SIGWINCH handler and pushes a
`ResizeEvent` into the input device's stream on each signal (plus one at startup with the initial size, via
`stty size`). Wired into the happy-path `TerminalSession.OpenAsync()`. Windows console resizes are delivered by
`WindowsResizeMonitor` (console-API polling, with an XTWINOPS `CSI 18 t` wire-probe fallback for non-console stdout);
the OS-appropriate `IResizeMonitor` is chosen by a `ResizeMonitor.Create` factory wired into the happy-path session.

`Cursorial.Rendering` (the cell-buffer + diff frame renderer) is described in "Rendering conventions" below.
Higher-level concerns (widget tree, layout, focus, input routing) live in the `Cursorial.UI` framework (and its
companions `Cursorial.UI.Xaml` and `Cursorial.UI.Bars`) built on top of `Cursorial.Rendering`, which remains the
lowest layer with a TUI abstraction (everything above byte writing).

## Input module conventions (`Cursorial.Core.Input`)

The input API is designed to be usable both inside the future Cursorial framework and by existing apps that just want
better terminal input. Key shape:

- All public types live in the single namespace `Cursorial.Core.Input` regardless of folder location.
- A consumer picks a delivery surface per device instance: `IAsyncInputDevice` (pull, `IAsyncEnumerable<InputEvent>`)
  or `IEventInputDevice` (push, classic `EventHandler<>` events). A device may implement either or both. Both extend
  `IInputDevice`, which carries the `InputCapabilities` and `IAsyncDisposable` contract.
- `IInputByteSource` (a `PipeReader` wrapper) is the abstraction for parser-based devices. Devices not built on a
  byte stream (e.g. Win32 console input records) bypass it.
- Devices chain via decoration — a wrapper takes another `IInputDevice` and produces a new one whose
  `InputCapabilities` may differ. Decorators that want to expose their inner device implement
  `IInputDeviceDecorator`. `KeyReleaseSynthesizer` is the canonical example: it fabricates key-up / repeat events on a
  timer for terminals that don't natively report them, so it owns a background pump + channel.
- Stream stages that only rewrite the event sequence (filter, reorder, fabricate) without owning the source are
  `IInputTransformer`s — `IAsyncEnumerable<InputEvent>` → `IAsyncEnumerable<InputEvent>` plus a `TransformCapabilities`
  projection. `TransformingInputDevice` is the one reusable adapter that applies any transformer over an inner device
  (use the `device.Transform(...)` / `device.WithClickSynthesis(...)` extensions). Prefer this lighter shape over a
  full device decorator when the transform is synchronous and source-agnostic (e.g. `MouseClickSynthesizer`); reach for
  a device decorator only when you must fabricate events independently of input arrival (timers), as
  `KeyReleaseSynthesizer` does.
- Events are a sealed `record class` hierarchy rooted at `InputEvent`; consumers pattern-match on type.
  `InputEvent.Synthesized` flags fabricated events so consumers can distinguish them from device-reported truth.
- Capabilities are categorized records (`MouseCapabilities`, `KeyboardCapabilities`, `PointerCapabilities`,
  `ProtocolCapabilities`) aggregated under `InputCapabilities`. Each has a `None` static for defaults.
- The interface layer must stay free of framework-specific concepts so it remains usable standalone.

## Capability negotiation conventions (`Cursorial.Core.Terminal`)

`ITerminalNegotiator` is the orchestrator that turns a raw terminal connection (an
`IInputByteSource` + `IOutputByteSink` pair, or a Win32 console handle) into a known set of capabilities. It is
**both detector and negotiator** — by default it actively enables opt-in protocols (Kitty keyboard, bracketed paste,
SGR mouse, focus events, Win32 input mode, synchronized output) and records what it enabled so it can restore on
dispose. `NegotiationOptions.OptIns = OptInPolicy.Ignored` reduces it to a passive probe.

- Returned `TerminalCapabilities` reflects **realized** capabilities, not advertised ones — features the terminal
  claimed but did not honor are reported as unavailable. Consumers can branch on flags directly.
- Negotiation is **single-shot per instance**: re-negotiating requires a new instance. This keeps "what to restore to"
  unambiguous.
- Restore is best-effort and idempotent; failures (broken pipe, terminal closed) are swallowed. Disposal must run
  before process exit or the terminal will be left in a non-default state — register a signal handler.
- The Win32 implementation produces the same `TerminalCapabilities` shape via structured APIs (`GetConsoleMode`,
  parent-process inspection, etc.) — consumers see capabilities, not how they were detected.

## Output module conventions (`Cursorial.Core.Output`)

The output layer is split into three things: a byte sink, capability records that describe what the terminal can do,
and a family of pure byte-emitting writers that produce escape sequences for an `IBufferWriter<byte>`. Higher-level
composition (the cell buffer, diff frame renderer) lives in `Cursorial.Rendering` — see "Rendering conventions"
below. The intent is that a consumer who wants "print red text and move the cursor" should be able to use the
writers directly without pulling in the rendering layer.

- `IOutputByteSink` is a `PipeWriter` wrapper, parallel in shape to `IInputByteSource`. Consumers MUST NOT call
  `PipeWriter.Complete` directly; sink ownership of completion is enforced via `IAsyncDisposable.DisposeAsync`.
- Output capabilities are categorized records: `ColorCapabilities` (with the `ColorDepth` enum),
  `TextStylingCapabilities`, `TextSizingCapabilities` (the Kitty OSC 66 protocol's `Width` and `Scale` sub-features,
  family-gated to Kitty for now), `GraphicsCapabilities`, `CursorCapabilities`, `WindowCapabilities`,
  `OutputProtocolCapabilities`, aggregated under `OutputCapabilities`. Each has a `None` static for defaults.
- **Style primitives.** `Color` (readonly record struct discriminated as `Default` / `Palette(byte)` /
  `Rgb(r,g,b,alpha)` via `ColorKind`; alpha defaults to 255 from `FromRgb` and is settable via `FromRgba` or
  `WithAlpha`; meaningful only for the `Rgb` kind and only at composite time — terminal output is always opaque),
  `TextAttributes` (`[Flags]` — Bold, Faint, Italic, Underline, Blink, Inverse, Hidden, Strikethrough, Overline),
  `UnderlineStyle` (the *shape* — Single/Double/Curly/Dotted/Dashed; the *presence* is the `Underline` flag on
  `TextAttributes`). `Style` is a readonly record struct combining foreground, background, attributes, underline
  style, and underline color with fluent `With…` helpers; `default(Style)` is "no styling".
- **`SgrEncoder`** — pure `Style → SGR bytes`. Three operations: `WriteReset` (emit `CSI 0 m`), `WriteAbsolute`
  (emit `SGR 0` plus the parameters needed to express the style; used for full-redraw entry), `WriteDelta` (emit
  only the parameters that differ between two styles, including reset codes for attributes that turn off; the diff
  renderer uses this). Knows about the shared SGR 22 reset for both Bold and Faint, the SGR 4:n colon sub-parameter
  form for extended underline shapes, and the SGR 58/59 underline-color extension.
- **`StyleQuantizer`** — capability-aware `Style → Style` adapter. Holds an `OutputCapabilities` and adjusts a style
  to what the terminal can actually render: RGB → 256-color via xterm's 6×6×6 cube + grayscale ramp; palette > 15
  → 16 via approximate channel-on thresholds; drops attributes the terminal doesn't honor; collapses extended
  underline shapes to `Single`; drops colored underline when unsupported. Pure given its capabilities — no state
  beyond the constructor argument.
- **`CursorWriter`** — `WriteMoveTo` (CUP, 0-based row/col translated to 1-based on wire), `WriteColumnAbsolute`,
  `WriteRowAbsolute`, relative moves (`WriteMoveUp/Down/Left/Right`, zero/negative is a no-op),
  `WriteSavePosition` / `WriteRestorePosition` (DECSC / DECRC), `WriteHide` / `WriteShow` (DECRST/DECSET 25),
  `WriteShape(CursorShape)` (DECSCUSR with the seven xterm shapes including Default = "restore terminal default").
- **`ScreenWriter`** — ED variants (`WriteClearScreen`, `WriteClearScreenAfter/Before`,
  `WriteClearScreenAndScrollback`), EL variants (`WriteClearLine`, `WriteClearLineAfter/Before`),
  alternate-screen-buffer toggle via DECSET/DECRST 1049, scroll region via DECSTBM (0-based row coordinates on the
  API, 1-based on the wire), `WriteResetScrollRegion`.
- **`HyperlinkWriter`** — OSC 8: `WriteOpen(uri, optional id)`, `WriteClose`, `WriteHyperlink(uri, text, optional id)`
  one-shot convenience. URIs and ids are UTF-8 encoded.
- **`TextSizingWriter`** + **`TextSizing`** record struct — Kitty OSC 66. `TextSizing` carries the spec parameters
  (`s`/`w`/`n`/`d`/`v`/`h`) as a value type; default value is "normal text" and produces an empty metadata block on
  the wire. `Write` emits a single sequence (caller respects the 4096-byte payload cap); `WriteSplit` chunks on
  grapheme cluster boundaries when the payload exceeds the cap.
- **All writers take `IBufferWriter<byte>`** rather than `IOutputByteSink` directly. Both `PipeWriter` and
  `ArrayBufferWriter<T>` implement it, so the same encoder feeds both live terminal output (via the session's sink)
  and the diff renderer's scratch buffer (which it flushes as a single coordinated frame).
- **`VtWriterUtilities`** — internal helper for decimal-ASCII formatting, shared by the writers.
- **`VtOutputSequences`** — centralized byte-string constants for output-side protocols (OSC 8 prefix/close, OSC 66
  prefix/ST terminator, OSC 66 payload-length cap). Add new sequence constants here when introducing new writers.

## Text utilities (`Cursorial.Core.Text`)

Grapheme-aware text operations used by both the input and output sides. The contents are minimal but load-bearing:
without correct width accounting, any rendering layer above misaligns the moment a user types an emoji.

- **`GraphemeWidth`** — `CodepointWidth(int)`, `ClusterWidth(ReadOnlySpan<char>)`, `StringWidth(string)`.
  `CodepointWidth` returns 0 for combining marks / format controls / variation selectors, 2 for East Asian wide /
  fullwidth and the major emoji blocks, 1 for everything else. Wide-range detection is a hand-coded table covering
  Hangul, CJK Unified Ideographs (Plane 0–3), Compatibility Ideographs, Fullwidth Forms, and the common emoji
  blocks (Misc Symbols, Transport, Supplemental Symbols, etc.). Less-frequently-used codepoints fall through to
  width 1 — adequate for ~95% of real-world text; a full `EastAsianWidth.txt`-backed table can drop in later
  without breaking the API. `ClusterWidth` handles VS16 (U+FE0F, forces emoji presentation → bumps to 2),
  VS15 (U+FE0E, forces text presentation → pins to 1), and ZWJ (U+200D, zero-width continuation).
  `StringWidth` enumerates grapheme clusters via `System.Globalization.StringInfo`.
- **`AnsiTextWrap`** + **`WrapOptions`** — word-wrap that measures width via grapheme clusters and passes ANSI
  escape sequences through with zero column accounting. Recognizes CSI / OSC / DCS / SS3 / ESC + intermediates as
  zero-width pass-through tokens; word-break boundaries are ASCII whitespace; supports a `BreakLongWords` policy
  for words exceeding the column limit, configurable line separator, and trailing-whitespace trim. SGR state
  crosses wrap boundaries naturally (no SGR reset is injected) so multi-line styled output preserves color across
  the split.

## Rendering conventions (`Cursorial.Rendering`)

The cell-buffer + diff frame renderer layer. Sits on top of the byte-emitting writers in
`Cursorial.Core.Output`; assumes nothing about widgets, layout, or focus — those live in the `Cursorial.UI`
framework built on top of it. `Cursorial.Rendering` is the lowest layer above the byte writers with a TUI
abstraction, and higher-level frameworks build on it.

- **`Cell`** — readonly record struct carrying `Grapheme` (string? — null/empty means a blank cell rendered as a
  space), `CellKind` (`Single` / `WideLeft` / `WideContinuation`), and `Style`. `default(Cell)` is the canonical
  blank single-width cell. `Cell.WideContinuation` is the placeholder for the right half of a wide-left glyph;
  the renderer skips it during emission because the wide-glyph bytes emitted at the WideLeft position paint
  *both* cell columns (foreground and background) as a single terminal operation. The continuation's `Style` is
  mirrored from the WideLeft for diff-comparison hygiene but isn't separately emitted — terminals don't render
  different backgrounds on the two halves of a wide glyph.
- **`CellBuffer`** — 1D `Cell[]` indexed `row * Columns + column`. Operations: dimensions, `Resize` (reallocates,
  cursor state preserved), indexer (`buffer[r, c]` — raw read/write, bypasses both wide-cell handling and
  blending), `Set(row, col, grapheme, style)` (high-level: computes grapheme width, writes WideLeft +
  WideContinuation if needed, cleans up adjacent cells to maintain wide-cell consistency, applies the active
  blending mode), `Get`, `Clear` (resets all to blank — does NOT apply blending), `Fill` (applies blending).
  Cursor state (`CursorRow`, `CursorColumn`, `CursorVisible`, `CursorShape`) lives alongside the cell grid; the
  renderer emits cursor updates as a separate concern from cell content.
- **Blending mode stack on `CellBuffer`.** Each `Set` / `Fill` call composes the new style's colors against the
  existing cell's colors through `CurrentBlendingMode` — the top of an internal stack, or
  `BlendingModes.Default` (source-over) when the stack is empty. `PushBlendingMode` / `PopBlendingMode`
  manipulate the stack; pop on empty throws. Only the color fields (foreground, background, underline color)
  blend; non-color style (attributes, underline shape) takes the source's value. Blending and alpha-compositing
  engage only for RGB-on-RGB pairs — palette/default colors short-circuit to "return source" because
  round-tripping through RGB would be lossy and surprising. Built-in modes in `BlendingModes`: `SourceOver` /
  `Default`, `Multiply`, `Screen`, `Overlay`, `Darken`, `Lighten`, `Plus`. Custom modes implementing
  `IBlendingMode` plug in cleanly.
- **Alpha compositing.** Each color carries an `Alpha` byte (0 = transparent, 255 = opaque). The cell buffer's
  composite pipeline runs in two steps: the active `IBlendingMode` produces a *blended color* from the source and
  backdrop (treating both as opaque — modes don't see alpha), then the buffer linearly mixes that blend with the
  backdrop using the source's alpha: `result = blended·α + backdrop·(1-α)`. Stored cells always end up at
  alpha 255 — the terminal can't render translucent SGR colors, so alpha is consumed at composite time. With
  empty blend stack (`SourceOver`), this collapses to the classic linear alpha blend.
- **`FrameRenderer`** — the stateful diff renderer. One instance per output target. Holds the previous frame
  (front buffer), the SGR style currently active on the terminal, the cursor position the renderer believes the
  terminal is at, and the cursor visibility / shape last emitted. `Render(CellBuffer back, IBufferWriter<byte>)`
  emits one of two byte sequences: a full redraw (clear screen + SGR reset + every non-blank cell) when there's
  no prior frame, the dimensions changed, or `FrameRendererOptions.ForceFullRedraw` is on; otherwise a per-cell
  delta. The renderer is the single owner of SGR + cursor state across frames — interleaving raw output that
  mutates those will desync the next frame. `Reset()` forgets the front buffer and forces a full redraw on the
  next render.
- **Capability-aware quantization.** When constructed with an `OutputCapabilities` (the
  `FrameRenderer(OutputCapabilities)` overload), the renderer holds a `StyleQuantizer` and runs each cell's
  `Style` through it before diffing or emission. RGB cells are quantized to palette / 16-color indices when
  truecolor isn't available; extended underline shapes fall back to Single; unsupported attributes are dropped.
  The front-buffer snapshot stores the quantized form so subsequent frames compare apples-to-apples and a stable
  rendered frame produces an empty delta. The no-capability constructor preserves the original raw-style
  behavior — useful for tests and for consumers that quantize upstream of the cell buffer themselves.
- **Wide-cell emission.** A `CellKind.WideContinuation` cell is skipped in the diff loop. The wide-glyph
  emission at the WideLeft position is a single terminal operation that draws both columns; trying to write
  anything at the right-half column is undefined in most terminals (the cursor is past it, and moving back into
  the glyph corrupts it).
- **What was punted from v1.** (1) Scroll detection — **landed** (`FrameRenderer.TryDetectAndApplyScroll`):
  a frame that is the previous one scrolled up/down by K rows emits an SU/SD scroll rather than redrawing each
  row (bounded by `MaxScrollDetect`; disabled under ordered dither, whose phase is position-dependent).
  (2) Multi-row spans for OSC 66 sized text — kept as a separate `TextSizingWriter`
  primitive in `Cursorial.Core.Output` that bypasses the cell grid; rendering sized text alongside the cell
  buffer means drawing it at a fixed position with `TextSizingWriter.Write` rather than encoding it as cell
  contents. (3) Automatic dirty-region tracking — a TUI framework above us is the right place to decide *what*
  is dirty; the renderer only consumes `CellBuffer.DirtyRegions` when explicitly opted in (see
  `RestrictToDirtyRegions` below). (4) Consumer-set scroll regions for partial-screen rendering — the cell
  buffer assumes ownership of its full drawing area.
- **`FrameRendererOptions.ForceFullRedraw`** — debug / profiling knob that disables the renderer's diff
  optimization without changing the API. Treat every `Render` call as a full redraw.
- **`FrameRendererOptions.RestrictToDirtyRegions`** — opt-in (default `false`) for dirty-region-exclusive
  emission. **Off by default the renderer ignores `CellBuffer.DirtyRegions` entirely and always diffs the full
  buffer**, so a stray `MarkDirty` — including the internal one `CellBuffer.RemoveFragment` emits over a removed
  fragment's footprint — can't silently restrict a frame's repaint and drop unrelated changed cells. When opted
  in, only cells inside the union of the marked regions are eligible for emission, and the consumer owns the
  contract that it marked *every* cell it changed (cells still diff against the front buffer inside the region,
  so a marked-but-unchanged cell isn't re-emitted). Regions are cleared every frame regardless of the flag. The
  default exists because dirty marks arise as side effects (fragment removal), and a renderer that silently
  trusts them is a footgun; consumers wanting the partial-repaint optimization turn it on deliberately.

## Toolchain

- .NET SDK **10.0.100** is pinned in `global.json` with `rollForward: latestMinor` and `allowPrerelease: false`. Any newer
  10.x SDK works; pre-release SDKs and 11.x will be rejected. (The floor must be a full SDK version — `10.0.100`, not
  `10.0.0` — or a strict resolver rejects it: "a full SDK version is required" when `rollForward` is set.)
- Target framework: `net10.0`. `ImplicitUsings` and `Nullable` are both enabled.
- Embrace latest C# language featuers to the extent they make code easier to read and maintain.
- Prefer using [ReadOnly]Span<T> and Memory<T> for buffers and I/O.

## Common commands

Run from the repository root:

```bash
dotnet build              # build the whole solution
dotnet build -c Release   # release build
dotnet test               # run all xUnit tests across the solution's eight test projects (Core/Rendering/Drawing/Animation/UI/UI.Bars/UI.Xaml/UI.Xaml.Generator)
dotnet test --filter "FullyQualifiedName~VtSequenceClassifierTests"   # filter by class
dotnet test --filter "DisplayName~OscTerminatedByBel"                 # filter by single test
dotnet test Cursorial.Rendering.Tests                                 # run a single test project
```

## Parser / interpreter conventions

- **Classifier is purely a framing layer.** It frames bytes into classified tokens; it does not interpret meaning.
  Decoders (mouse, keyboard, focus, paste, device responses) live in the interpreter that consumes the sink callbacks.
  Narrow exception: the X10 mouse protocol (`CSI M cb cx cy`) is fundamentally framing-mode-dependent because the
  three follow bytes aren't otherwise distinguishable from printable text. The classifier exposes a single
  `X10MouseFramingEnabled` boolean — when set, an unadorned `CSI M` triggers a 3-byte slurp into the new
  `OnX10MouseDispatch` sink callback. The negotiator (or future mouse-mode wiring) is responsible for keeping this
  flag in sync with `VtInputMode.MouseEncoding`; the classifier does not read mode state itself.
- **ESC ambiguity timing belongs to the device, not the parser.** The classifier holds a lone ESC pending in its
  `Escape` state. The device above it is responsible for calling `Flush()` after the bare-ESC quiet period
  (xterm convention: 50 ms with no further input). This keeps the classifier deterministic and synchronously testable.
- **Mode state is shared mutable config.** `VtInputMode` is a mutable class shared between the interpreter (reads)
  and the negotiator (writes). When the negotiator pushes/pops an opt-in (Kitty keyboard, modifyOtherKeys, mouse
  protocol, …), it updates the corresponding property and the interpreter reads on the next event.
- **Use UTF-8 byte-string literals for sequence constants.** `"\x1b[<"u8` is a `ReadOnlySpan<byte>` matched against
  input with zero allocation. Centralize these in `VtInputSequences.cs`.
- **Buffer-lifetime contract still applies.** Sink callbacks receive `ReadOnlySpan<byte>` valid only for the call's
  duration. Implementations that retain data must copy it into event-owned memory.
