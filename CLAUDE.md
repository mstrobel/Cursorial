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

Eleven projects:

- `Cursorial.Core` — input parsing, capability negotiation, terminal session orchestration, byte-level output writers.
- `Cursorial.Rendering` — cell buffer + diff frame renderer that sits on top of `Cursorial.Core`'s output writers.
- `Cursorial.Drawing` — scene/brush/pen drawing layer (cached-raster scenes, compositor, gradients, charts); design
  doc at `docs/drawing-layer-design.md`.
- `Cursorial.Animation` — pure, time-free animation primitives (`IAnimation<T>`: elapsed → value; the consumer owns
  the clock).
- `Cursorial.UI` — the WPF/Avalonia-style UI framework layer (in progress; design doc at `docs/ui-layer-design.md`,
  phase plan in its §14). Phases 0 (property system) and 1 (element tree + layout + render zones + app spine) are
  complete; see "UI module status" below.
- `Cursorial.UI.Testing` — the headless test harness (`UITestHost` + `SyntheticTerminalHost` + capability presets);
  the integration substrate for every UI subsystem — no test needs a TTY.
- `Cursorial.Core.Tests`, `Cursorial.Rendering.Tests`, `Cursorial.Drawing.Tests`, `Cursorial.Animation.Tests`,
  `Cursorial.UI.Tests` — xUnit.
- `Cursorial.Demo` — interactive REPL for hands-on verification. `dotnet run --project Cursorial.Demo` opens a prompt
  with commands: `negotiate` (dump realized capabilities), `read` (stream input events to stdout), `raw` (dump
  stdin bytes verbatim with no parsing), `trace` (live raw bytes + decoded events side-by-side for protocol
  debugging), `sizing` (Kitty OSC 66 text-sizing demonstration), `probe` (XTVERSION + DA1 raw-response capture),
  drawing/animation showcases (`draw`, `animate`, `charts`, `brushtext`, `imagescene`, `imageclip`, `ui`),
  `uipanels` (Cursorial.UI panel-tree showcase on the real `UIApplication` frame loop — arrows slide a render
  boundary composite-only, `v` toggles a Visibility, `o` cycles an Opacity group),
  `rasterbench` (headless-capable scene-raster/compositor/diff benchmark — UI design-doc probe 1), `accesskeys`
  (live access-key gate probe: Alt down/up tracking, negotiated Kitty flags, the requirement-6 gate verdict — UI
  design-doc probe 3), `help`, `quit`. Each command opens its own raw-mode `TerminalSession` and restores cooked
  mode before the next prompt.

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
`UIObject` + `ValueStore` (effective/base split, priority frames at `BindingPriority` Animation > LocalValue > Style >
Default, store-owned retraction/promotion), `SetCurrentValue`, copied-value change carriers, typed/untyped observers
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
- **`Cursorial.UI.Testing`** — `UITestHost` (calling thread is the UI thread; manual `RunFrame`/`RunUntilIdle`/
  `AdvanceTime` stepping on a `FakeTimeProvider`; `SendKey/SendText/SendClick/SendResize/SendInput` direct injection;
  `SendBytes` through a real `VtInputDevice` on the fake clock; cell/row/byte assertions; teardown-byte capture),
  `UITestHostOptions`, `TestCapabilities` presets (`KittyTruecolor`/`Ansi16Legacy`/`NoMotion`/`NoMouseCursorShape`).

The doc §14 P1 exit criteria are proven end-to-end in `Cursorial.UI.Tests/Integration/Phase1EndToEndTests.cs`
(UITestHost through the full spine: static panel-tree cell+byte assertions, the AffectsComposite
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
`Cursorial.UI.Tests/Integration/Phase2EndToEndTests.cs` (end-to-end through `UITestHost`: Tab cycling +
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

Recorded P1 gaps: `BindingOperations.TearDown` leg of `UIElement.TearDown()` (P4); palette theming + capability
rewrite and the S7 surface merge into `UIApplication` (P5); `TerminalSessionOptions.EmergencyRestoreBytes` Core seam
for signal-path alt-screen restore (doc §10.7 — until it lands, a signal-killed app restores cooked mode but may
leave the shell on the alt screen).

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
`stty size`). Wired into the happy-path `TerminalSession.OpenAsync()`. Windows-side console buffer-size events are
not yet plumbed — TODO when needed.

`Cursorial.Rendering` (the cell-buffer + diff frame renderer) is described in "Rendering conventions" below.
Higher-level concerns (widget tree, layout, focus, input routing) are not started; if/when they land they live in
a separate `Cursorial.UI` library on top of `Cursorial.Rendering` rather than in `Cursorial.Rendering` itself,
which is meant to be the lowest layer with a TUI abstraction (everything above byte writing).

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
`Cursorial.Core.Output`; assumes nothing about widgets, layout, or focus — those are an explicit
follow-up library if/when they land. The intent is for `Cursorial.Rendering` to be the lowest layer above the
byte writers with a TUI abstraction, and for higher-level frameworks to build on it.

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

- .NET SDK **10.0.0** is pinned in `global.json` with `rollForward: latestMinor` and `allowPrerelease: false`. Any newer
  10.x SDK works; pre-release SDKs and 11.x will be rejected.
- Target framework: `net10.0`. `ImplicitUsings` and `Nullable` are both enabled.
- Embrace latest C# language featuers to the extent they make code easier to read and maintain.
- Prefer using [ReadOnly]Span<T> and Memory<T> for buffers and I/O.

## Common commands

Run from the repository root:

```bash
dotnet build              # build the whole solution
dotnet build -c Release   # release build
dotnet test               # run all tests (xUnit, across Cursorial.Core.Tests and Cursorial.Rendering.Tests)
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
