# S3 — oracle-pinned input/focus matrix (routing, focus, interaction state, commands, access keys)

Status: **normative test specification**, authored 2026-06-11 *before any S3 code exists* (design doc §14 P2; the repo's matrix-first discipline, mirroring `precedence-matrix.md` and `layout-matrix.md`). Every numbered row below becomes exactly one xUnit `[Fact]`/`[Theory]` in `Cursorial.UI.Tests` (test authoring contract at the end). The S3 implementation is written *to* this matrix; a red row is an implementation bug unless a PR amends this file first.

Canonical semantics sources, in precedence order: `docs/ui-layer-design.md` §7 (+ §0 invariants, §13 resolutions, §14 P2) **over** `docs/ui-layer-design/spec-input-focus.md`. Places the spec is superseded by the doc and this matrix pins the doc's side:

- ① **`InputDispatchResult` is 3-state** (`DispatchedHandled` / `DispatchedUnhandled` / `NotUIInput`, already declared in `Cursorial.UI/Hosting/FrameSeams.cs`) — the spec's 2-state `{ Dispatched, NotUiInput }` is superseded (doc §7.4, punch 15); S6's default gestures (Ctrl+C exit) key on `DispatchedUnhandled`.
- ② **Hit testing delegates to S1's `RenderTree.HitTest`** (doc §13.2 "one hit test"; §7.6). The spec's own `Descend` pseudo-code and its `ClipsToBounds = true` panel-default REQUIRES are superseded: S3 keeps no per-element bounds cache and imposes no clipping defaults — pruning comes from `RenderTree`'s boundary clips, and `ClipToBounds` stays S1's (default false).
- ③ **`KeyboardNavigationMode.Once` ships in v1** (doc §7.7, punch 24); the spec's three-mode enum is superseded.
- ④ **Access-key model**: `AccessText` is a `readonly record struct` with an **explicit** conversion, registration is control-side against the flat registry, and cue rendering targets `AccessTextPresenter` via the `ParsesAccessKeyLiterals` flag (doc §7.8/§13.2) — the spec's `ContentPresenter.RecognizesAccessKey` pipeline is superseded. P2 ships the **manager core only** (registry, modes, gate, scopes, cue bits); producers/presenters/underline UX are P9.
- ⑤ Casing is `NotUIInput` (doc §1.3), and the doc-only members `EditCommitRequested`, `HoverChanged`, `FindNext`, `IWindowFocusHooks`, and the F10 menu-mode entry are binding.

**Phase 2 scope boundary** (rows are written inside it): no styling engine (`InteractionState` raises seam notifications observable through a test sink — the P3 consumer seam is *designed* here, ND11), no windowing (single root surface; the window-topology/`FilterMouseEvent` consumption seam is declared but exercised at P7 — ND5), no access-key rendering, no bindings.

Stage mapping (doc §14 P2's ordered spine, sliced into the four implementation stages):

| Stage | Sections | Delivers |
|---|---|---|
| **I1 — routing core** | §§1–4 | `RoutedEvent` registry + handler store + route walker + pooled args; event vocabulary; `InputDispatcher.ProcessEvent` classification, key dispatch order, TextInput synthesis, terminal-focus cluster; S6 wiring (3-state result honored; capability fan-out). Exit chore: remove the P1 stopgaps — the `InternalsVisibleTo("Cursorial.Demo")` in `Cursorial.UI.csproj` and `UIPanelsDemo`'s internal `IInputDispatchTarget` consumption (the demo migrates to public routed events/`KeyBinding`s). |
| **I2 — mouse** | §§5–7 | Mouse dispatch delegated to `RenderTree.HitTest`; two-phase hover diff + `HoverChanged`; per-frame `UpdateHover`; capture; S6's click-synthesis pipeline default flipped to the §7.6 contract. |
| **I3 — focus & commands** | §§8–11 | `FocusManager` + scopes + Tab (incl. `Once`) + directional navigation; `InteractionState` plumbing + pressed-holder set + the P3 observer seam; `ICommand` coupling + `KeyGesture`/`KeyBinding`. |
| **I4 — access keys, timers, perf** | §§12–14 | `AccessKeyManager` core (gate, brackets, scopes, cycling); the early-S5 `FrameClock`/`UITimer` slice; allocation contracts + the probe-4 motion-storm CI gate. |

Rows for a later stage may stay unimplemented (not red) until that stage opens, but the rows themselves are binding from now.

## 0. Conventions

### 0.1 Fixture

| Symbol | Meaning |
|---|---|
| `host` | `UITestHost.Create()` — 80×24, `TestCapabilities.KittyTruecolor` unless stated. `app` = `host.Application`; `dispatcher`/`focus`/`ak` = the application's `InputDispatcher`/`FocusManager`/`AccessKeyManager` (public access lands at I1/I3/I4). |
| `Probe` | `UIElement` subclass recording every `On*` virtual invocation (with args snapshot: `Handled`, `Source`, position, key) into a process-global ordered `log`; instance handlers added in rows record into the same log as `A.h1`, `A.h2`… `Probe(w×h)` measures rigid. |
| `Btn` | `Probe` variant with `Focusable = true` (set as a local value — P2 has no metadata-overriding controls). |
| tree | Default: `Root` (Panel, fills viewport) → children `A`, `B`, `C` (Probes) laid out by a Canvas/StackPanel as stated. "at (c,r)" = the element's arranged window cell. `host.ShowRoot(Root)` before operations; the P1 single-root harness activates `Root` (`OnWindowActivated`) at show (N115). |
| `sink` | a test `IInteractionStateObserver` installed on the app — records `(element, oldState, newState)` notifications in order (ND11). `state(e)` reads the element's current `InteractionState` mask via the test-visible read surface. |
| injectors | `key(k, mods = None, text = null)` / `keyUp(…)` = `SendInput` of a `KeyEvent` Down/Up; `move(c,r)`, `down(c,r,btn=Left,clicks=1)`, `up(c,r,btn=Left)`, `drag(c,r,held)`, `wheel(c,r,dy)` = `MouseEvent`s; `focusEvt(b)` = `FocusEvent`; `paste(s)` = `PasteEvent`; `bytes(…)` = `SendBytes` through the real `VtInputDevice` (+ `DrainParsedInputAsync`). Each followed by `RunFrame()` unless the row asserts mid-drain order. Unit-level rows may call `dispatcher.ProcessEvent(e)` directly on the UI thread. |
| caps | `KittyCaps` = `TestCapabilities.KittyTruecolor` (key-up + repeats + motion); `LegacyCaps` = `Ansi16Legacy` (no key-up, no repeats, no Kitty); `NoMotionCaps`; `Win32Caps` = a constructed `TerminalCapabilities` with `Protocol.Win32InputMode = true`. Gate rows construct snapshots directly and call `OnCapabilitiesChanged`. |
| `clock` | `host.Time` (`FakeTimeProvider`); `AdvanceTime` + `RunFrame` step the frame clock. |

### 0.2 Notation

- `log == [X.OnPreviewKeyDown, Y.h1, …]` asserts exact invocation order; `∉ log` asserts absence.
- `result` = the `InputDispatchResult` returned for the injected event (host rows observe it via the dispatcher seam; unit rows directly). `H`/`U`/`N` = `DispatchedHandled`/`DispatchedUnhandled`/`NotUIInput`.
- `state(e) ⊇ Pressed` = bitmask contains the flag; `notify(e, old→new)` = one `sink` delivery.
- `focused == e` ≡ `focus.FocusedElement == e ∧ e.IsFocused ∧ state(e) ⊇ Focused`.
- "0 B" = `GC.GetAllocatedBytesForCurrentThread()` delta of zero after warm-up (ND25).

### 0.3 Oracle tags

`WPF` = WPF behavior (primary oracle); `AV` = Avalonia 11; `PIN` = Cursorial pin with no direct parent-framework analog (this matrix is the decision record); `DEV` = deliberate deviation from a parent framework, always with rationale (inline or via the ND ledger).

### 0.4 Pinned decisions made by this matrix (ND ledger)

Each goes beyond — but never against — the canonical doc text; deliberate and binding until amended.

- **ND1 — class stance is symmetric.** The `On*` virtual is the class-handler stage in *both* phases: at every tunnel node `OnPreview*` runs before that node's instance preview handlers; at every bubble node `On*` runs before instance handlers. Skipped once `Handled` (handledEventsToo instance handlers still run). Oracle: WPF (class handlers precede instance handlers per node).
- **ND2 — `Handled` is mutable and pair-scoped.** Preview/main share one pooled args. `Handled` in the tunnel ⇒ the remaining tunnel *and* the whole main phase invoke handledEventsToo subscribers only (virtuals skipped). A handledEventsToo handler may set `Handled = false`; downstream nodes then resume normal invocation. Oracle: WPF.
- **ND3 — route snapshot.** The route is built once per `RaiseEvent` (visual-parent walk target→surface root, logical hop per doc §7.5); tree mutation during dispatch does not alter the in-flight route — handlers of since-detached nodes still run. Oracle: WPF (route built before invocation).
- **ND4 — `MouseEventKind.Click` defense.** A `Click` event returns `DispatchedUnhandled` with **zero routing** and a DEBUG diagnostic (pipeline-contract violation; S6's mandated pipeline sets `SynthesizeClickEvents = false`). PIN.
- **ND5 — P2 topology.** One implicit surface: the application root. The window-topology consumption seam (`IWindowTopology`-shaped: `FilterMouseEvent`, plus the dispatcher's public `OnSurfacesChanged()` and `HitTest(CellPosition)` — N204/N207) is *declared* at P2 with the trivial single-root implementation; S4 substitutes the real one at P7 with no dispatcher rewrite. `IWindowFocusHooks` itself (and `HitTest`'s surface out-param) is declared at P7 with S4 — its signatures require S4's `Window`/`TopLevelSurface` types, which do not exist at P2 (amended at the P2 review). Mouse events with no shown root ⇒ `DispatchedUnhandled`, no throw. PIN.
- **ND6 — out-of-viewport positions** (legal after resize coalescing): hit-test as misses — no route, no hover chain, no throw; `LastPointerPosition` still updates. PIN (FrameSeams "clamp, never throw" given a concrete meaning).
- **ND7 — disabled gating.** Disabled (`!IsEffectivelyEnabled`) elements are **hit-opaque but event-transparent**: the dispatch/hover target is the deepest hit element's nearest effectively-*enabled* self-or-ancestor. Disabled elements never appear in a route or hover chain and never get `PointerOver`. Oracle: WPF (input raised on the topmost enabled element).
- **ND8 — `LastModality` starts as `Keyboard`**, so startup `Programmatic` focus sets `FocusVisible` before any input arrives (keyboard-first device; under-showing focus strands keyboard users — same rationale as the doc's `Restore` pin). PIN.
- **ND9 — KeyUp is route-only.** Pre-stage (Alt bracket) + tunnel/bubble; **no** unhandled tails ever run on Up (no access keys, no navigation, no TextInput). Framework code never activates on Up. Doc §7.5 made row-exact.
- **ND10 — the spacebar's identity is `(Key.Character, " ")`** on every protocol path (VT printable 0x20, Kitty codepoint 32, Win32 VK_SPACE→unicode 0x20); `Key.Space` is emitted only for NUL→Ctrl+Space. Any framework matching that means "spacebar" (KeyGesture `"Space"`, S8 activation at P5) must match `(Key.Character, Text == " ")` — the spec's `e.Key == Key.Space` sketch is superseded. PIN (verified against `VtInputInterpreter`).
- **ND11 — the P3 interaction seam.** `UIElement` implements `IInteractionStateSink` (doc §3.2): it stores the `InteractionState` bitmask, flips are equality-gated, and `BeginInteractionUpdate()` returns a `using`-scoped batch. One installable per-application observer (`IInteractionStateObserver` — P3's styling engine becomes the production instance; tests install `sink`) receives one `(element, oldState, newState)` notification per element per batch, delivered at scope disposal (immediately for unbatched flips), **after** state commit. Set-then-clear of the same bit inside one batch ⇒ no notification. Nested scopes flush at the outermost dispose. PIN (shapes Fork B's `BeginInteractionUpdate` contract).
- **ND12 — pressed-holder set** is identity-keyed on the dispatcher: entered on `SetInteractionState(Pressed, true)`, left on `(Pressed, false)`, detach, or a C8 clear (terminal focus-out). PIN (punch 54).
- **ND13 — `KeyGesture.Parse` grammar.** `'+'`-separated, tokens trimmed, case-insensitive. Modifiers: `Ctrl|Control`, `Shift`, `Alt`, `Super|Win|Cmd`, `Meta`, `Hyper`. Final token: single char ⇒ character gesture (`Key.Character`, `Character` = the token canonicalized upper-invariant — `Parse("ctrl+s")` and `Parse("Ctrl+S")` produce the same gesture and the same `ToString()`; matching is ordinal case-insensitive regardless — amended at the P2 review); multi-char ⇒ `Key` enum name or alias (`Esc`→Escape, `Return`→Enter, `Del`→Delete, `Ins`→Insert, `PgUp`/`PgDn`→PageUp/PageDown, `Up/Down/Left/Right`→arrows, `Space`→ the character gesture `" "` per ND10); unrecognized ⇒ `FormatException`. PIN (WPF-shaped).
- **ND14 — gesture matching**: `e.Modifiers` (lock-free, never `ExtendedModifiers`) must equal `Modifiers` **exactly**; named keys by `Key` equality; character gestures by ordinal case-insensitive `Character` vs `e.Text`. Oracle: WPF exact-modifier matching.
- **ND15 — `CanExecute == false` does not consume**: the `InputBindings` sweep continues to later bindings (and later route nodes). PIN (doc "first matching gesture whose command `CanExecute` executes" read strictly).
- **ND16 — `Once` semantics**: the container is a single tab stop. Entry (either direction) focuses the container's scope-memory element if valid, else its first tab-ordered eligible descendant, else the container itself when focusable; the next Tab/Shift+Tab exits past the entire container. PIN (WPF-shaped; memory clause feeds ListBox at P9).
- **ND17 — directional scoring** (doc §7.7 made exact): candidates filtered by direction on window-translated bounds (`TranslateToWindow`; e.g. Up ⇒ candidate bottom edge strictly above current top edge); score = facing-edge distance + 2 × orthogonal-range gap (gap 0 when cross-axis ranges overlap); lowest wins; ties → tab order. `Cycle` wraps to the farthest candidate on the opposite side; `Contained` stops; handled only when focus moved. PIN.
- **ND18 — multi-match cycling order**: eligible matches sorted by tab order within the resolved scope; each activation focuses the first match after the currently focused element (wrap-around); the cue stays up; nothing invokes. PIN (doc "cycle focus through matches" made deterministic). **Companion (single-match focus, amended post-P2):** a single (non-colliding) match **also** moves focus to the target when it is `Focusable` (method `AccessKey`, *before* invoking so the invoked action can redirect — last-wins), then invokes. The manager owns the focus move on both paths — parity with the multi-match cycle, the plain-element fallback, and WPF/Avalonia (Alt+mnemonic on a button focuses **and** clicks). The doc previously specified only "invokes" for the single match (silent on focus); the resulting "single-match never focuses" was an under-specification, not a deliberate choice. Non-focusable targets (e.g. `Label`, which forwards focus to its own `Target` inside `OnAccessKey`) are left untouched. Rows N184b/N184c.
- **ND19 — F10 ≡ Alt tap**: an unhandled `F10` (no modifiers) Down engages the sticky cue + raises `EnterMenuMode`, handled — in both modes (in `AlwaysVisible` the cue is already permanently on; sticky still governs Esc-consume/swallow). PIN (doc §7.8 "Alt tap or F10").
- **ND20 — `UITimer` coalescing**: a due timer fires during the frame's tick at the **frozen** `FrameTime`; a repeating timer fires at most once per frame regardless of how many intervals elapsed (the clock is frame-frozen). PIN.
- **ND21 — `UpdateHover` driver**: rows exercise it through `RunFrame()` (the loop calls it once per rendered frame, Phase 6, after layout and composite finalize — doc §10.5); a frame that renders nothing does not re-diff. PIN.
- **ND22 — `Source == OriginalSource`** always in v1 (template source adjustment deferred); both = the dispatch target. Doc §7.2 made row-exact.
- **ND23 — undecorated snapshot sourcing**: `OnCapabilitiesChanged` receives the **negotiated** session snapshot (`TerminalCapabilities`), never the decorated pipeline view — a decorator-claimed `DistinguishesKeyUpDown` (e.g. `KeyReleaseSynthesizer`) must not reach the access-key gate. S6 owns the sourcing; the gate rows assert the contract from both sides. PIN (doc §7.8/§13.2).
- **ND24 — FocusEvent{HasFocus:false} order** (doc §7.5 made row-exact): ① `AccessKeyManager.OnTerminalFocusLost` ② capture force-release (`LostMouseCapture`) ③ hover-chain clear (Leave + `PointerOver` off) ④ every pressed-holder cleared, and the dispatcher's held-button bookkeeping zeroed (a stale mask must not survive a focus loss — the matching Ups go to another window; N203, amended at the P2 review) ⑤ `EditCommitRequested(focused)` — only when `FocusedElement != null`, exactly once ⑥ `TerminalFocusChanged(false)`. Keyboard focus retained throughout. PIN.
- **ND25 — perf methodology** (P0 bench lesson): allocation rows assert `GC.GetAllocatedBytesForCurrentThread()` deltas after warming the **exact** measured delegate; timing rows are best-of-5 in-process repetitions after tiered-JIT settle; the probe-4 row carries `[Trait("Category","Benchmark")]` and its allocation contract is asserted on every suite run (timings informational, budget asserted best-of-N).
- **ND26 — the unmodified-activation window survives the stale-bracket clear for the triggering key** (added at I4, reconciling N174 with N182). The pre-stage's stale-Alt inference (1b) fires on *any* unmodified Down while a side bit is set — including the very key the user typed under visibly-up cues, since terminals report keys-while-Alt-held *with* the Alt bit. The dispatcher therefore samples `anyAltDown || sticky` **before** the pre-stage runs and passes it to the step-5 tail: the triggering key still enters the activation path (N182/N48) while the bracket closes for all subsequent events (N174). PIN.
- **ND27 — the in-flight handler sweep is stable** (added at the P2 review). Each node's instance-handler invocation iterates the registration list as of the moment that node's sweep began: a handler removing itself (or any other registration) affects later nodes and later raises, never the in-flight node sweep; a handler **added** during a raise is not invoked by that raise. Same rationale as ND3's route snapshot, applied one level down. Oracle: WPF (handler sets are stable for an in-flight raise).
- **ND28 — focus repair on in-place invalidation** (added at the P2 review). Disabling (effective-enabled flips false) or hiding (`Visibility` ≠ Visible) the focused element — or an ancestor containing focus — repairs focus exactly as detach does (nearest valid ancestor → scope root's first tab-ordered focusable → clear), with `method = Programmatic`, no `FocusVisible`, `LostFocus` raised at the old element. A disabled element must never keep receiving key routes. `Focusable` flips are NOT watched at P2 (no property hook; recorded deferral). Oracle: WPF (`KeyboardDevice` re-evaluates focus when the focused element becomes disabled or hidden).
- **ND29 — interaction-flush re-entrancy** (added at the P2 review, hardening ND11 for the P3 styling engine). An `IInteractionStateObserver` callback may open/dispose its own `BeginInteractionUpdate` batch or flip states mid-flush: elements already flushed are never re-notified by the in-flight flush; flips made during the flush deliver exactly once, after the original batch's entries; a nested `EndUpdate` reaching depth 0 during a flush never replays the in-flight list. PIN.
- **ND30 — subtree detach repairs focus once, outside the doomed subtree** (added at the P2 review). When focus lies inside a detaching subtree, the repair-candidate walk skips every element of that subtree — ancestors that are themselves detaching are never repair targets even though the bottom-up walk leaves them momentarily attached-looking. Exactly one transition; no `GotFocus` is ever raised at a detaching element. PIN (the bottom-up walk made event-exact).
- **ND31 — a nested focus transition wins the GotFocus tail** (added at the P2 review). When a `LostFocus` handler refocuses (the nested transition completes inside the handler), the outer transition skips its now-stale `GotFocus`: an element never observes `GotFocus` while `IsFocused == false`. N107's last-wins made event-exact. PIN.
- **ND32 — Alt auto-repeat never re-arms the tap** (added at the P2 review). A repeat (`IsRepeat`) Alt Down refreshes the side-bit/cue bookkeeping but does not touch the chordless flag: a chord followed by held-Alt repeats then Up stays chorded (no sticky cue, no `EnterMenuMode`); a genuine chordless tap with intervening repeats still taps. PIN (Kitty/Win32 repeat held modifiers; the chord-then-repeat sequence must not be misread as a tap).
- **ND33 — generalized scope entry** (added at the bars/nav rework, 2026-06-30; gate hardened after the foundation audit). Entry into **any** focus scope (`IsFocusScope`), not just `Once` containers, resolves to the scope's remembered focus (`GetFocusedElement(scope)`, validated + within) else its direction-appropriate eligible descendant — via a cross-scope redirect at the navigator's entry sites (`NextTabStop`/`FirstOrLastTabStop`/`NextDirectional`). The redirect fires **only on a genuine entry** — descending INTO a scope the current position is **not already inside**. Three gates: (1) same scope ⇒ no redirect (intra-scope traversal); (2) the target's scope is an **ancestor of** the current scope ⇒ no redirect — an outward / pass-through move toward an enclosing scope's own member is still *within* that scope, so it returns the raw element (the **trap-prevention gate**; without this an inner scope hard-traps Tab/arrow nav — audit finding, rows N218/N219); (3) `currentScope` for a non-stop marker (`from` of a Label/`FindNext` query) is the marker's **captured enclosing scope**, not null, so a Label inside a scope forwards to the next document-order stop, not the scope memory (N221). Entry is **direction-aware**: with no valid memory a forward crossing lands on the first eligible descendant, a backward crossing (Shift+Tab/Left/Up) on the **last** — continuing reverse document order (N220); the `Once` ladder is always first either direction (ND16). A host marked **both** `Once` and `IsFocusScope` resolves through the single `Once` ladder — the `IsFocusScope` mark exists only so `MoveFocusCore` records memory there; no double redirect. **DEV** — a deliberate divergence from WPF's `Once`-only entry-restore (the user's nav-logic rework). The concrete pillars (ListBox `Once`+scope; Toolbar `Once`; Menu return via `RestoreRetainedFocus`) are covered by the `Once` ladder + `IsFocusScope` marking; the generalization is purely **additive** (no existing pinned row changes — N118–N135 build no nested non-`Once` scope) and future-proofs non-`Once` scopes. Rows N214–N221.
- **ND34 — access-key proxy** (added at the bars/nav rework, 2026-06-30). Two attached properties on `AccessKeyManager` generalize `Label`-style access-key forwarding to any proxy↔target pair (a `Label`→its `Target`, an Expander header→its `ToggleButton`, a bar caption→its bar): `AccessKeyProxyFor` (on the proxy ⇒ its declared target) + the back-link `AccessKeyProxy` (on the target ⇒ its proxy). A proxy claims a target only when it is **unclaimed** (`GetValueSource(AccessKeyProxy).Kind == Default` — first-wins; a second proxy forwards itself via `IAccessKeyTarget.OnAccessKey`). Registration is released on the proxy's detach (no dangling link on a live target) and re-established on re-attach. On activation, `AccessKeyManager.ResolveEffectiveTarget` redirects to the declared target and resolves where focus lands via `FocusManager.ResolveFocusEntry` (the **same ND33 entry ladder** — a focusable target takes focus, a scope/container is entered at its remembered → first focusable descendant, an empty one forwards via `FindNext`); the resolved target is then focused **and** invoked (an input merely gains focus, an `IAccessKeyTarget` target also acts). **DEV** — generalizes the doc §12.7 `Label` forwarding into the manager so the nav system handles it without per-control special-casing. Rows N222–N226.

---

## 1. Routed events, handler store, route mechanics (I1) — N1–N18, N202

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N1 | — | `RoutedEvent<KeyEventArgs>.Register("X", Bubble, typeof(Probe))` ×2 distinct names | distinct instances; dense distinct `GlobalIndex`; `Name`/`Strategy`/`OwnerType`/`ArgsType` read back | AV+WPF |
| N2 | fresh `Probe` | no handlers added | element-side handler store not allocated (internal debug accessor); `AddHandler` then `RemoveHandler` returns to functional-empty | PIN (spec §3.3 lazy store) |
| N3 | chain `Root→A→B` (B target), handlers on all three for `KeyDownEvent` | `RaiseEvent` (caller-`new` args, Bubble) | bubble order B→A→Root: at each node `On*` virtual then instance handlers in registration order (ND1); `log == [B.OnKeyDown, B.h1, A.OnKeyDown, A.h1, Root.OnKeyDown, Root.h1]` | WPF |
| N4 | as N3, `PreviewKeyDownEvent` handlers | tunnel raise | order Root→A→B; per node `OnPreviewKeyDown` then instance handlers | WPF |
| N5 | as N3, Direct event (`MouseEnterEvent`) raised at B | raise | only B's virtual + handlers run; no walk | WPF |
| N6 | N3 with `A.h1` setting `Handled = true` | bubble raise | `Root.OnKeyDown` and `Root.h1` (normal) not invoked; a `Root.h2` added with `handledEventsToo: true` **is** invoked; route ran to completion | WPF |
| N7 | as N6 + `Root.h2(handledEventsToo)` sets `Handled = false` | bubble raise with a second root-level normal handler `Root.h3` registered after `h2` | `h3` invoked normally (un-handling resumes invocation downstream, ND2) | WPF |
| N8 | full key dispatch (host): `PreviewKeyDown` handler at Root sets `Handled = true` | `key(Enter)` | main phase runs handledEventsToo-only: `B.OnKeyDown ∉ log`; a `B.hKeyDown(handledEventsToo)` runs; `result == H` (ND2) | WPF |
| N9 | B focused, three-level chain | `key(Enter)`, inspect args at each node | same pooled args instance at every node and across the Preview/main pair; `Source == OriginalSource == B` everywhere (ND22) | WPF args-identity; PIN (ND22) |
| N10 | handler captures the pooled args | after dispatch completes (DEBUG) | touching the captured args throws (stale stamp); the wrapped `KeyEvent` device record remains valid/retainable | PIN (doc §7.2 pooling) |
| N11 | caller-`new RoutedEventArgs(evt, src)` passed to public `RaiseEvent` | after return | args remain valid (caller-owned, never pooled/stamped) | PIN |
| N12 | handler on A performs a nested `RaiseEvent` of the same args type at C | dispatch | legal; nested dispatch completes before the outer continues; the two dispatches use **distinct** pooled instances (free-list); outer args uncorrupted | PIN (doc §7.5) |
| N13 | control-author path | `RentEvent<ClickEventArgs>`-style rental passed to exactly one `RaiseEvent` | args returned to pool at completion (next rental reuses the instance — observable via test pool counter); DEBUG: raising the same rented args twice throws | PIN |
| N14 | handler at A throws | `key(Enter)` via `ProcessEvent` | exception propagates out of `ProcessEvent` (fail fast to S6's funnel); pooled args + any open interaction scope released along the unwind (next dispatch functions normally) | PIN (doc §7.5) |
| N15 | handler at A detaches B (the target) mid-bubble | dispatch | in-flight route unchanged: remaining nodes still invoked (ND3); no throw | WPF (ND3) |
| N16 | DEBUG | re-entrant `ProcessEvent` from inside a handler | debug assertion (programming error); nested `RaiseEvent` remains legal | PIN |
| N17 | element with `LogicalParent` set to a host element in another visual tree, acting as surface root | bubble raise from inside | route continues through the logical hop at the surface root (popup seam — the walker is P7-ready; exercised here with a synthetic detached-root pair) | PIN (doc §7.5; P7 pinned test "Esc closes the popup") |
| N18 | `AddHandler` same delegate twice | raise | invoked twice (registration list, not a set); one `RemoveHandler` removes one registration | WPF |
| N202 | three same-event handlers `h1, h2, h3` on B; `h1` removes itself **and** `h2`, and adds a new handler `h4` | bubble raise ×2 | first raise: `h1, h2, h3` all run (the node sweep is stable — ND27) and `h4` does **not**; second raise: exactly `h3, h4` | WPF (ND27) |

---

## 2. `ProcessEvent` classification & dispatch results (I1) — N19–N28

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N19 | any | `ProcessEvent` of `ResizeEvent` / `DeviceResponseEvent` / `UnknownEvent` / `PointerEvent` (one `[Theory]`) | `N` (NotUIInput); zero routing, zero state change — S6 routes them onward (device responses must reach their issuer) | PIN (doc §7.5) |
| N20 | B focused, no handler claims | `key(Enter)` | `result == U`; route ran (log non-empty) | PIN (punch 15) |
| N21 | B focused, handler sets `Handled` | `key(Enter)` | `result == H` | PIN |
| N22 | no focused element, no `ActiveRoot` (root never shown) | `key(Enter)` / `paste("x")` (Theory) | dropped: `U`, empty route, no throw — never routed to "topmost" | PIN (doc §7.5) |
| N23 | host with `ExitOnUnhandledCtrlC` | unhandled Ctrl+C `KeyEvent` (`Key.Character`,"c",Control) | S6 default gesture fires → shutdown requested | PIN (doc §10.7) |
| N24 | as N23 but a focused element handles the key | Ctrl+C | `H` suppresses the default gesture; no shutdown (a TextBox can claim copy) | PIN (doc §10.7) |
| N25 | mandated pipeline | `MouseEvent { Kind = Click, Synthesized = true }` | defensive no-op: `U`, zero routing, DEBUG diagnostic (ND4) | PIN |
| N26 | — | `move(5,5)` then `key(Enter)` | after move: `LastModality == Pointer`, `LastPointerPosition == (5,5)`; after key: `LastModality == Keyboard` (position retained) | PIN (doc §7.4) |
| N27 | fresh dispatcher | `LastPointerPosition` | `null` until the first real mouse event | PIN |
| N28 | terminal focus regained | `focusEvt(true)` | `TerminalFocusChanged(true)` raised; no element focus change, no other state touched | PIN |

---

## 3. Key dispatch order & TextInput synthesis (I1) — N29–N48

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N29 | B focused under `Root→A→B` | `key(Enter)` | full order: tunnel `PreviewKeyDown` Root→A→B, bubble `KeyDown` B→A→Root (virtual before handlers per node) | WPF |
| N30 | no focused element, `ActiveRoot == Root` | `key(Enter)` | target = `Root` (fallback); route is the single node | PIN (doc §7.5 step 2) |
| N31 | B focused | `key(Character, text:"a")` then `key(Character, text:"b")` | two distinct dispatches; `KeyEventArgs.Text` carries `"a"`/`"b"` — `(Key, Text)` identity observable at the handler | PIN (input-map gotcha) |
| N32 | B focused | `key(Character, text:"a")` with `IsRepeat = true, RepeatCount = 3` | args expose `IsRepeat == true`, `RepeatCount == 3` (pass-through; controls guard activation on it) | PIN |
| N33 | B focused | `keyUp(Enter)` | `PreviewKeyUp`/`KeyUp` route (tunnel+bubble); **no** tail processing of any kind (ND9) | PIN (doc §7.5) |
| N34 | B focused, unhandled | `key(Character, text:"f")` | after the KeyDown route: `PreviewTextInput`/`TextInput` tunnel+bubble at B with `Text == "f"`, `FromPaste == false` | WPF (KeyDown→TextInput) |
| N35 | as N34 but a KeyDown handler sets `Handled` | `key(Character, text:"f")` | no TextInput synthesized | WPF |
| N36 | B focused | `key(Character, mods: Shift, text:"F")` | TextInput `"F"` synthesized (Shift allowed) | WPF |
| N37 | B focused | `key(Character, mods: Control, text:"f")` / `mods: Alt` / `Super` / `Meta` / `Hyper` (Theory) | **no** TextInput (chord mask); the KeyDown route still ran | PIN (doc §7.5 step 7) |
| N38 | B focused | `key(Enter)` / `key(Tab)` unhandled | named keys never synthesize TextInput (`Key.Character` only; Enter/Tab text handling is explicit control code) | PIN |
| N39 | B focused | `bytes(" ")` (raw 0x20) | decodes as `(Key.Character, " ")` (ND10) and synthesizes TextInput `" "` — spacebar types a space end-to-end | PIN (ND10) |
| N40 | B focused | `key(Character, text:"")` (empty text) | no TextInput (Text non-empty required); KeyDown routed | PIN |
| N41 | B focused | `paste("hello\nworld")` | one `PreviewTextInput`/`TextInput` pair at B: `Text == "hello\nworld"`, `FromPaste == true`; **no** KeyDown route | PIN (doc §7.5; no OnPaste event exists) |
| N42 | no focus, `ActiveRoot == Root` | `paste("x")` | TextInput routed at `Root` (fallback parity with keys) | PIN |
| N43 | B focused, TextInput handler sets `Handled` | `key(Character, text:"f")` | `result == H` (the synthesis leg's Handled feeds the event's result) | PIN |
| N44 | B focused | `key(Character, text:"s", mods: Control)` with `ExtendedModifiers` also carrying `CapsLock` | dispatch + matching read `Modifiers` only — `KeyEventArgs.Modifiers == Control` exactly; lock bits visible only on `ExtendedModifiers` | PIN (input-map gotcha) |
| N45 | B focused | `key(LeftAlt)` (standalone Alt Down on Kitty) | pre-stage runs the bracket **and** the event still routes normally (`PreviewKeyDown`/`KeyDown` at B) | PIN (doc §7.5 step 1a) |
| N46 | Synthesized `KeyEvent { Key: LeftAlt, Synthesized: true }` | `ProcessEvent` | pre-stage skipped (no bracket state change — §12 asserts the bracket side); routing still occurs | PIN (doc §7.5 pre-stage) |
| N47 | B focused; B's `OnKeyDown` handles arrows (TextBox-style) | `key(DownArrow)` in a directional container | navigation tail never runs (focus unmoved) — handled in step 4 wins | PIN (doc §7.5 step 6) |
| N48 | B focused | unhandled `key(Character, text:"f")` where an access-key registration for `F` exists and is active | access-key tail (step 5) runs **before** navigation (step 6) and before TextInput (step 7): activation occurs, no TextInput | PIN (doc §7.5 order) |

---

## 4. Terminal focus events — the FocusEvent{false} cluster (I1, asserts I2/I3 state) — N49–N57, N203

Setup for the cluster: B focused (`Btn`), capture held by C, hover chain over A, pressed-holders {B, C} (B keyboard-pressed without capture), Alt held (AltHeld mode).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N49 | cluster | `focusEvt(false)` | full ND24 order asserted via the log: ak-clear → `C.OnLostMouseCapture` → `A.OnMouseLeave` → pressed clears → `EditCommitRequested(B)` → `TerminalFocusChanged(false)` | PIN (ND24) |
| N50 | cluster | after `focusEvt(false)` | **focus retained**: `focused == B` still; no `LostFocus` raised | PIN (doc §13.2) |
| N51 | cluster | after `focusEvt(false)` | pressed-holders empty: `state(B) ⊉ Pressed`, `state(C) ⊉ Pressed` — including the keyboard-held press that never took capture (C8) | PIN (punch 54) |
| N52 | cluster | after `focusEvt(false)` | capture released: `MouseCaptureTarget == null`; `C.OnLostMouseCapture` ran exactly once (Direct) | PIN |
| N53 | cluster | after `focusEvt(false)` | hover chain empty: `state(A) ⊉ PointerOver`, `MouseLeave` raised deepest-first over the old chain | PIN |
| N54 | no focused element | `focusEvt(false)` | `EditCommitRequested` **not** raised; everything else proceeds | PIN (ND24 ⑤) |
| N55 | cluster | `focusEvt(false)` then `focusEvt(true)` | `TerminalFocusChanged` raised `false` then `true`; focus state intact across the round trip; no Got/LostFocus either way | PIN |
| N56 | B focused, edit-commit subscriber recording | two consecutive `focusEvt(false)` | `EditCommitRequested` once per focus-out event (no dedupe across events; the second fires again with B still focused) | PIN |
| N57 | cluster | after `focusEvt(false)`, `key(Character, text:"f")` | keys still dispatch to the retained focused element (terminal focus is not element focus) | PIN |
| N203 | buttons held (`down` with no `up`) | `focusEvt(false)` | `dispatcher.ButtonsHeld == None` after the cluster — the bookkeeping cannot leak a stale held mask across a focus loss (the matching Up goes to another window); ND24 ④ | PIN (ND24) |

---

## 5. Mouse dispatch & hit-test delegation (I2) — N58–N71, N204

Tree: `Root` (Canvas 80×24) with `A` at (10,5,10×4), `B` at (30,5,10×4), `D` child of `A` at (12,6,4×2) unless stated.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N58 | — | `down(13,7)` | target = `D` — exactly `RenderTree.HitTest(13,7)`'s answer (asserted equal); route `PreviewMouseDown` Root→A→D tunnel, `MouseDown` D→A→Root bubble | PIN (§13.2 one-hit-test) + WPF route |
| N59 | `D.HitTestCore` overridden to count + reject | `down(13,7)` | the dispatcher performs **no second descent**: `HitTestCore` invocation count is exactly what one `RenderTree.HitTest` call produces; target falls to `A` | PIN (S3 re-derives nothing) |
| N60 | B with `ZIndex` above an overlapping sibling | `down` in the overlap | target = composite-order winner (same element `RenderTree.HitTest` returns) — z-order honored by delegation, not re-derived | PIN |
| N61 | — | `down(13,7)`, inspect args | `GetPosition(D)` = (1,1) element-local; `GetPosition(A)` = (3,2); `GetPosition(B)` negative columns (legal, no throw); `ScreenPosition`/terminal = (13,7) | PIN (doc §7.2) |
| N62 | — | `down(13,7, clicks:2)` then `up(13,7)` | `MouseDown` args `ClickCount == 2`; `MouseUp` args `ClickCount == 1` — counts arrive baked in (`ClickCountTarget.ButtonDown`), dispatcher adds no timing | PIN (doc §7.6) |
| N63 | A focused (keyboard) | `wheel(31,6, dy:120)` | `PreviewMouseWheel`/`MouseWheel` at **B** (hit element, not focus); `WheelDeltaY == 120` | WPF |
| N64 | — | `move(13,7)` | `PreviewMouseMove`/`MouseMove` routed at D (after the hover phases — §6 owns enter/leave order) | WPF |
| N65 | — | `down(0,0)` on empty root area | target = `Root` (surface hit-opaque: a point no descendant claims hits the root) | PIN (doc §7.6) |
| N66 | — | `down(200,50)` (out of viewport) | no route, no throw, `U`; `LastPointerPosition == (200,50)` (ND6) | PIN |
| N67 | `D.IsHitTestVisible = false` | `down(13,7)` | target = `A` (leaf-only gate per S1's `HitTest`; delegation reflects it) | AV+WPF |
| N68 | `D.Visibility = Hidden` | `down(13,7)` | target = `A` (invisible subtrees not hit) | WPF |
| N69 | `A.IsEnabled = false` (D inside) | `down(13,7)` | target = `Root` — nearest effectively-enabled ancestor (ND7); neither A nor D in the route; A still occludes nothing below it gets the event | WPF (ND7) |
| N70 | `down` then `up` same cell | both | `ButtonsHeld` reads `Left` between down and up (dispatcher bookkeeping mirrors device mask); `MouseUp` routed at the hit element (no capture in play) | PIN |
| N71 | root never shown (no surface) | `down(5,5)` | `U`, no throw (ND5) | PIN |
| N204 | the §5 tree | `dispatcher.HitTest(position)` at (13,7) / at (13,7) with `A.IsEnabled = false` / at (200,50) | the public position query (doc §7.4): returns exactly the dispatch target — `D`; `Root` under the ND7 disabled hop; `null` on an out-of-viewport / no-surface miss. Pure: no routing, no hover change, `LastPointerPosition` untouched. The surface out-param joins at P7 with `TopLevelSurface` (ND5) | PIN (doc §7.4 made P2-exact) |

---

## 6. Hover chain & `UpdateHover` (I2) — N72–N84, N205–N206

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N72 | fresh | `move(13,7)` (first ever) | chain Root→A→D built: `MouseEnter` outermost-first (`Root, A, D`); `PointerOver` set on all three; then `MouseMove` at D | WPF (enter order) |
| N73 | hover on D | `move(14,7)` (same leaf) | no Enter/Leave; `MouseMove` routed; zero interaction notifications (equality-gated) | WPF |
| N74 | hover on D (chain Root,A,D) | `move(31,6)` (into B) | common-ancestor pruning: `MouseLeave` deepest-first (`D, A`), `MouseEnter` (`B`); Root untouched — no Leave/Enter, no `PointerOver` flicker (no `sink` notification for Root) | WPF |
| N75 | as N74 | inspect `sink` + log interleave | **two-phase**: all `PointerOver` flips committed (one batch — one notification per changed element) *before* the first Leave/Enter raise; Leave handlers observe post-restyle state | PIN (doc §7.6) |
| N76 | `HoverChanged` subscriber | the N74 move | raised once after Enter/Leave with removed = [D,A], added = [B] (pooled snapshots; DEBUG: retaining past the raise throws) | PIN (doc §7.4) |
| N77 | hover on D | `move` off-viewport (ND6) | full chain leaves (deepest-first); chain empty; `PointerOver` cleared | PIN |
| N78 | hover on A; A's layout moves away from the (stationary) pointer cell via a `Width` change | `RunFrame()` (no mouse event) | `UpdateHover` re-diffs after layout: A leaves, new hit enters — hover correct under layout movement without motion | PIN (doc §7.4) |
| N79 | hover over scrolled content (`ScrollContentPresenter`), offset changes | `RunFrame()` | hover re-diffs to the element now under the pointer (composite slide tracked) | PIN |
| N80 | no mouse event ever observed | `RunFrame()` ×N | `UpdateHover` no-ops (`LastPointerPosition == null`); zero hit tests (counting probe) | PIN |
| N81 | hover on D | detach D mid-frame (handler or job) | chain truncated + deferred refresh: at the frame's `UpdateHover` the chain re-diffs against the live tree; no stale `PointerOver` on D | PIN (doc §7.10) |
| N82 | `MouseLeave` handler on A detaches B (in the added suffix) | the N74 move | snapshot iteration unaffected (no mutation-under-iteration); B's enter still raised from the snapshot; deferred refresh fixes state next `UpdateHover` | PIN (doc §7.6 two-phase) |
| N83 | buttons held | `drag(31,6, held:Left)` | drag updates the hover chain exactly like Move (enter/leave + `PointerOver`), then routes `MouseMove` | PIN (doc §7.6 "On Move/Drag") |
| N84 | A disabled, pointer over A's cells | `move` | A not in the hover chain; `Root` is (`PointerOver` on enabled ancestors only — ND7); no Enter raised at A | WPF (ND7) |
| N205 | hover on D (motion-capable snapshot) | `OnCapabilitiesChanged(NoMotionCaps)` (renegotiation drops Motion) | the live chain clears through the ordinary diff: `MouseLeave` deepest-first, `PointerOver` off everywhere, one `HoverChanged`; the hover driver is forgotten — subsequent frames/`UpdateHover` re-hover **nothing** until fresh motion arrives under a motion-capable snapshot (capability-honest in both directions; without the clear, `PointerOver` set under the old gate could never clear) | PIN |
| N206 | DEBUG; a `MouseEnter` handler calls `dispatcher.UpdateHover()` | the N72 move | throws `InvalidOperationException` (re-entrant hover diff is a programming error — phase 2 is iterating the pooled snapshots; mirrors N16); compiled out of release builds | PIN |

---

## 7. Mouse capture (I2) — N85–N97, N207

**P7 amendment (subtree capture pulled into v1).** The P2 deferral of subtree capture (doc §7.6 once read
"element-only") is reversed alongside S4's windowing: `CaptureMouse` takes a `CaptureMode { Element, SubTree }`
(default `Element`). `Element` is the unchanged N85 behavior; `SubTree` routes by hit while the pointer is inside
the holder's visual-then-logical subtree and redirects to the holder only outside it. Rows N95–N97.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N85 | attached visible A | `A.CaptureMouse()` | returns true; `MouseCaptureTarget == A`; subsequent `move`/`down`/`up` anywhere route to A (tunnel from root to A as usual), positions translated — `GetPosition(A)` may be negative | WPF |
| N86 | detached / `Collapsed` element | `CaptureMouse()` | returns false; no capture | PIN (doc §7.6 grant rules) |
| N87 | A holds capture | `A.ReleaseMouseCapture()` | Direct `LostMouseCapture` at A exactly once; `MouseCaptureTarget == null`; next mouse event routes by hit again | WPF |
| N88 | A holds capture | `B.ReleaseMouseCapture()` | no-op (only the holder releases); A still captured | WPF |
| N89 | A holds capture | `B.CaptureMouse()` | capture transfers: A gets `LostMouseCapture`, then `MouseCaptureTarget == B` | WPF |
| N90 | A holds capture | detach A | force-release: `LostMouseCapture` at A; `MouseCaptureTarget == null` | WPF |
| N91 | A holds capture, pointer over B | `move(31,6)` | events route to A **but** the hover chain/`PointerOver` still track B (hit testing runs under capture — `:pointerover` stays honest) | PIN (doc §7.6) |
| N92 | A holds capture, pointer over B | `wheel(31,6, dy:120)` | wheel targets **B** (the hit element) — the one mouse event capture does not redirect | PIN (doc §7.6) |
| N93 | A holds capture | `focusEvt(false)` | force-release + `LostMouseCapture` (N49/N52 cluster cross-check at unit level) | PIN |
| N94 | A holds capture | `up` completes; capture **not** auto-released | capture survives MouseUp (release is control logic, not framework policy — ButtonBase releases explicitly) | WPF |
| N95 | A holds `SubTree` capture, D is a child of A | `down(13,6)` over D | routes to **D** (the hit, since D ∈ A's subtree) — D and A both on the bubble route; the descendant stays interactive | WPF (`CaptureMode.SubTree`) |
| N96 | A holds `SubTree` capture, pointer over sibling B | `move`/`down(31,6)` over B | redirects to **A** (B ∉ A's subtree); hover/`PointerOver` still track B (hit honest) | WPF (`CaptureMode.SubTree`) |
| N97 | A holds `Element` capture | `A.CaptureMouse(SubTree)` | same-holder mode swap: returns true, `CaptureMode == SubTree`, **no** `LostMouseCapture`; the SubTree policy is immediately live | PIN (doc §7.6) |
| N207 | A holds capture | `dispatcher.OnSurfacesChanged()` with A attached + visible; then again after `A.Visibility = Collapsed` | first call: no-op, capture retained; second: force-release + Direct `LostMouseCapture` at A — the P2 semantics of the S4 seam (capture re-validation; surface-stack/modal validation joins at P7 — ND5); the hover refresh rides the per-frame `UpdateHover` | PIN (ND5, doc §7.4) |

---

## 8. Focus core — `Focus()`, physical singleton, scopes (I3) — N95–N117, N208–N210, N213

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N95 | plain `Probe` (default `Focusable == false`) | `Focus()` | false; nothing changes — `Focusable` default pinned false (controls opt in via `OverrideDefault` later) | AV (WPF defaults true on Control; DEV at the UIElement tier per doc §7.3) |
| N96 | attached, visible, enabled `Btn` | `B.Focus()` | true; `focused == B`; `IsFocused` mirror true; `IsKeyboardFocusWithin` true on B and every ancestor; `FocusVisible` set (ND8: startup modality Keyboard) | WPF |
| N97 | detached `Btn` | `Focus()` | false (validation: attached) | WPF |
| N98 | `Btn` with `IsEnabled = false` / inside a disabled ancestor (Theory) | `Focus()` | false (effective-enabled gate) | WPF |
| N99 | `Btn` `Collapsed` / `Hidden` / inside a `Hidden` ancestor (Theory) | `Focus()` | false (`IsEffectivelyVisible` gate) | WPF-shaped; PIN (Hidden included) |
| N100 | focusable parent P, non-focusable target | `target.Focus()` | false — **no ancestor fallback**; P untouched | PIN (doc §7.7) |
| N101 | A focused, then | `B.Focus()` | order: state commits (one batch: A clears `Focused/FocusVisible`, divergent ancestors swap `FocusWithin`, B sets) → `LostFocus` bubbles from A → `GotFocus` bubbles from B; both args carry `(OldFocus: A, NewFocus: B, Method)` | WPF (state-before-events) |
| N102 | A and B share ancestor `Root`; A focused | `B.Focus()`, observe `Root` | `Root.IsKeyboardFocusWithin` stays true with **zero** change notification (common-prefix diff; equality gate) | WPF+PIN |
| N103 | — | `SetValue(IsFocusedProperty, true)` / `IsKeyboardFocusWithinProperty` without the key | throws `InvalidOperationException` (internal `UIPropertyKey` is the write right — PD14 applied) | WPF |
| N104 | B focused via each `FocusNavigationMethod` (Theory) | inspect `FocusVisible` | set for Tab / Directional / AccessKey / Restore; set for Programmatic ∧ `LastModality == Keyboard`; **not** set for Pointer, nor Programmatic ∧ Pointer modality | PIN (doc §7.7; Restore-always recorded divergence from Chrome) |
| N105 | B focused by Pointer (`FocusVisible` off) | window-activation restore re-focuses B with `Restore` | `FocusVisible` **set** (Restore always sets) | DEV (Chrome heuristics; doc §7.7 rationale) |
| N106 | B already focused | `B.Focus(Tab)` | returns true; **no** Lost/GotFocus re-raise; `FocusVisible` updates per method; `ak.OnFocusChanged` still notified | WPF-shaped; PIN |
| N107 | `GotFocus` handler on B calls `C.Focus()` | `B.Focus()` | re-entrant last-wins: final `focused == C`; each transition's events exactly once; DEBUG depth cap 8 diagnostic on a pathological loop | PIN (doc §7.7) |
| N108 | A focused | `focus.ClearFocus()` | `FocusedElement == null`; A clears state + `LostFocus` raised; subsequent keys target `ActiveRoot` | WPF-shaped; PIN |
| N109 | B focused | detach B; nearest ancestor `A` is focusable | focus repairs to A (`method = Programmatic`, **no** `FocusVisible`) | PIN (doc §7.10) |
| N110 | B focused, no focusable ancestor, scope root has tab stops | detach B | focus → scope root's first tab-ordered focusable | PIN |
| N111 | B focused, nothing focusable remains | detach B | focus cleared (null); keys fall back to `ActiveRoot` | PIN |
| N112 | nested scope: `Root` (scope) → `Pane` (`IsFocusScope = true`) → `Btn` | `GetFocusScope(Btn)` / `GetFocusScope(Pane)` | `Pane` / `Pane` (nearest **self**-or-ancestor scope); `GetFocusScope(A)` outside Pane = `Root` | WPF (self-inclusive) |
| N113 | as N112, focus `Btn` | inspect scope memory | `FocusedElementProperty` recorded on `Pane` (nearest scope) **only** — `Root`'s memory untouched (survives the inner excursion) | WPF (doc §7.7) |
| N114 | scope memory on `Pane` points at `Btn` | detach `Btn` | memory cleared **eagerly** (no pinned subtree); restore later falls to first tab stop | PIN (doc §7.7 detach hygiene) |
| N115 | `host.ShowRoot(Root)` | startup | `OnWindowActivated(Root)` ran: `ActiveRoot == Root`; initial focus = scope memory (none) → first tab-ordered focusable, `method = Restore`; with zero focusables → no focus, keys still target Root | PIN (P1 single-root harness contract) |
| N116 | B focused, memory recorded | `OnWindowDeactivated(Root)` then `OnWindowActivated(Root)` | deactivate clears `ActiveRoot`; re-activate restores focus to B from memory with `Restore` (validated); if B was disabled meanwhile → first tab-ordered instead | WPF-shaped; PIN |
| N117 | `IsFocusScope` read on arbitrary elements | attached-property surface | default false; window root true (set by the harness/S4 convention); settable on any element | WPF |
| N208 | B focused | `B.IsEnabled = false` / `B.Visibility = Collapsed` / disable an ancestor containing focus (Theory) | in-place repair (ND28): focus repairs as on detach — nearest valid ancestor (else scope root's first tab stop, else clear); `LostFocus` raised at B, `method = Programmatic`, no `FocusVisible`; subsequent keys route to the repaired target, never to the disabled/hidden element | WPF (ND28) |
| N209 | focus on a deep leaf; its (focusable) parent and grandparent inside the same subtree; remove the subtree root | `RemoveVisualChild(subtreeRoot)` | exactly **one** transition (ND30): one `LostFocus` (from the old leaf) + one `GotFocus` at a target **outside** the removed subtree; the doomed focusable ancestors never receive focus or `GotFocus` | PIN (ND30) |
| N210 | A focused; A's `LostFocus` handler calls `C.Focus()` | `B.Focus()` | the nested transition wins (ND31): event order `A.LostFocus → B.LostFocus → C.GotFocus`; **no** `GotFocus` at B (B never observes `GotFocus` while `IsFocused == false`); final `focused == C` | PIN (ND31) |
| N213 | `host.ShowRoot(Root)` where every focusable sits behind a not-yet-realized boundary (`ScrollViewer.Content → Border → StackPanel → Button`s) — the first tab stop does not exist in the visual tree until the first layout applies templates / realizes content | `RunUntilIdle()` (≥1 layout pass) | activation is **parked** (ND33): `OnWindowActivated` found no tab stop pre-layout and recorded the root; the spine's post-layout retry (`CompletePendingActivationFocus`) re-runs the first-tab-stop search once the subtree is built and focuses it with `method = Restore`. Initial focus is on the first button on the first idle frame, not `none`. The retry is a no-op if focus landed or the app/user moved it meanwhile (never overrides), and is cleared on deactivation | PIN (ND33). In-section test: `Section08_FocusCore.N213_ParkedActivation_FirstTabStopBehindBoundary_FocusesAfterLayout` (hand-built shape). Loader-built parity + the no-override invariant are also proven end-to-end in `Cursorial.UI.Xaml.Tests/Integration/XamlFocusActivationTests.cs` |

---

## 9. Tab & directional navigation (I3) — N118–N135, N214–N221

Tree for Tab rows: `Root` (Cycle, default) → focusables `F1, F2, F3` in document order unless stated. Tab = unhandled `key(Tab)`; Shift+Tab = `key(Tab, mods: Shift)`.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N118 | F1 focused, default `TabIndex` | Tab | focus → F2 (document order when indexes tie at `int.MaxValue`); `result == H`; `method = Tab` (`FocusVisible` set) | WPF |
| N119 | indexes F1=2, F2=1, F3=2 | Tab from F2 | order F2(1) → F1(2) → F3(2) → wrap: `TabIndex` ascending, ties by document order (stable sort) | WPF |
| N120 | F3 focused (last) | Tab | wraps to F1 — root default `Cycle` (no OS to Tab out to) | PIN (doc §7.7 root trap) |
| N121 | F1 focused | Shift+Tab | wraps to F3 (previous with wrap) | WPF |
| N122 | no focused element | Tab / Shift+Tab (Theory) | starts at `ActiveRoot`'s first / last tab-ordered focusable | PIN (doc §7.7) |
| N123 | F2 with `IsTabStop = false` | Tab from F1 | skips to F3; `F2.Focus()` still works programmatically | WPF |
| N124 | F2 disabled / `Hidden` / `Collapsed` (Theory) | Tab from F1 | skipped (eligibility = `Focusable && IsTabStop && effectively enabled && effectively visible`) | WPF |
| N125 | container `Pane` (`TabNavigation = None`) holding F2 | Tab from F1 | F2 unreachable: F1 → F3 (None excludes descendants) | WPF |
| N126 | inner container with `Cycle` holding G1,G2; focus inside | Tab from G2 | wraps to G1 — nearest self-or-ancestor `Cycle` is the container (inner trap; nested scopes) | WPF |
| N127 | container `L` (`TabNavigation = Once`) holding G1,G2 between F1 and F3 | Tab from F1 | enters L at its first eligible (G1, no memory); next Tab → F3 (exits past the whole container) | WPF-shaped; PIN (ND16) |
| N128 | as N127 with scope memory on L pointing at G2 | Tab from F1 | enters at G2 (memory clause — the ListBox shape) | PIN (ND16) |
| N129 | as N127 | Shift+Tab from F3 | enters L once (memory/first per ND16); next Shift+Tab → F1 | PIN (ND16) |
| N130 | tree with zero eligible candidates besides current | Tab | focus unmoved; navigation marks handled **only when focus moved** ⇒ `result == U` | PIN (doc §7.5 step 6) |
| N131 | grid of buttons in a `DirectionalNavigation = Contained` container, focus center | `key(RightArrow)` | focus → nearest candidate to the right by ND17 scoring; `method = Directional` | WPF-shaped; PIN (ND17) |
| N132 | as N131, two right-side candidates: one nearer edge but orthogonally offset, one farther but row-aligned | RightArrow | scoring row: `facing + 2 × orthogonal-gap` picks the row-aligned candidate (gap 0 beats nearer-but-offset when `2×gap` exceeds the distance delta); tie → tab order | PIN (ND17) |
| N133 | focus at the right edge, `Contained` | RightArrow | no candidate → focus unmoved, `U` (arrow not stolen) | PIN |
| N134 | as N133 but `DirectionalNavigation = Cycle` | RightArrow | wraps to the **farthest** candidate on the opposite side (leftmost) | PIN (ND17) |
| N135 | default container (`DirectionalNavigation = None`) | arrows | directional tail never engages; arrows stay `U` (free for controls) | PIN (opt-in policy) |
| N214 | nested non-`Once` focus scope `S` (`IsFocusScope = true`, `TabNavigation` default) holding G1,G2 between F1 and F3, **no** memory | Tab from F1 | enters S at first eligible G1 (memory-absent fallback — identical result to pre-generalization document order) | DEV (ND33) |
| N215 | as N214 with scope memory on `S` pointing at G2 | Tab from F1 | enters at **G2** — the generalized memory clause: entry into ANY focus scope restores memory, not only `Once` | DEV (ND33) |
| N216 | `S` (`IsFocusScope`, non-`Once`) holding G1,G2,G3, scope memory → G3; focus G1 (inside S) | Tab from G1 | → G2 (next in document order), **not** G3 — an intra-scope move is not redirected to memory (trap-prevention gate) | DEV (ND33) |
| N217 | `S` marked **both** `Once` and `IsFocusScope`, memory → G2 | Tab from F1 | enters at G2 via the single `Once` ladder (≡ N128) — no double redirect; the `IsFocusScope` mark only lands memory on `S` | DEV (ND33) |
| N218 | nested scopes `A(IsFocusScope) → [B(IsFocusScope) → b1,b2], a2` + `tail` (all non-`Once`), focus b1 | Tab ×3 | b1 → b2 (intra-B) → **a2** (outward into A's own member — NOT trapped back to b1) → **tail** (outward past A). The outward-move trap-prevention gate (audit) | DEV (ND33) |
| N219 | as N218 directional (`Contained` root), focus b2 (g2) | RightArrow | → the right-facing member `a2` — an outward directional move into the enclosing scope's member is not snapped back inside B | DEV (ND33) |
| N220 | non-`Once` scope `S → G1,G2` between F1 and F3, **no** memory, focus F3 | Shift+Tab | enters S at **G2** (the LAST member — backward crossing continues reverse document order), then Shift+Tab → G1 → F1 | DEV (ND33) |
| N221 | non-stop marker `Lbl` (a Label) inside `S(IsFocusScope) → Lbl, G1, G2`, `S` memory → G2 | `FindNext(Lbl)` | → G1 (the next document-order stop in S) — a marker's enclosing scope makes the move intra-scope, NOT a redirect to S's memory (G2) | DEV (ND33) |

---

## 10. `InteractionState` plumbing, batching seam, pressed holders (I3) — N136–N147, N211

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N136 | `sink` installed | `SetInteractionState(Pressed, true)` on B (unbatched) | `state(B) ⊇ Pressed`; exactly one `notify(B, None→Pressed)` delivered immediately, **after** commit (handler reading `state(B)` sees the new mask) | PIN (ND11) |
| N137 | B has `Pressed` | `SetInteractionState(Pressed, true)` again | equality-gated: no notification, no pressed-holder churn | PIN |
| N138 | `using BeginInteractionUpdate()` scope | set `PointerOver` + `FocusWithin` on B inside | zero notifications inside the scope; one coalesced `notify(B, None→PointerOver|FocusWithin)` at dispose | PIN (ND11) |
| N139 | scope | set `Pressed` then clear `Pressed` inside one batch | **no** notification (net-zero flip) | PIN (ND11) |
| N140 | nested `BeginInteractionUpdate` scopes | flips in inner + outer | single flush at the outermost dispose | PIN (ND11) |
| N141 | batch touching A and B | dispose | one notification **per element**, in first-flip order | PIN |
| N142 | hover move N74 | inspect `sink` | hover writes ride one batch: per-element single notifications for the removed/added suffixes, none for the common prefix | PIN (doc §7.6 phase 1) |
| N143 | focus change N101 | inspect `sink` | focus writes ride one batch: old element `Focused|FocusVisible` off, new on, `FocusWithin` swaps along divergent chains only | PIN (doc §7.7) |
| N144 | B sets `Pressed` via the seam | inspect dispatcher | B in the pressed-holder set; `(Pressed, false)` removes; detach removes (ND12) | PIN (punch 54) |
| N145 | A and B pressed (B without capture) | `focusEvt(false)` | both cleared in one pass, each with one notification (C8 window-wide clear) | PIN |
| N146 | `Btn` whose `IsEnabledCore` flips false (command-style) | `InvalidateIsEnabledCore()` | S1 recomputes: `IsEffectivelyEnabled` false **and** `state ⊇ Disabled` with one seam notification — the `:disabled` producer path wired end-to-end | PIN (doc §13.2 effective-IsEnabled) |
| N147 | — | `IsPointerOver` property | reads the `PointerOver` bit (true under hover, false after leave); not a styled property | PIN (doc §7.3) |
| N211 | `sink` whose notification callback opens its own `BeginInteractionUpdate` scope and flips a **third** element (the P3 styling-engine shape) | a batch flipping A then B, disposed | flush re-entrancy (ND29): A and B each notified exactly once for the original batch; the callback's flip delivers exactly once, after the original entries; no element is replayed | PIN (ND29) |

---

## 11. Commands, `KeyGesture`, `KeyBinding` (I3) — N148–N165

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N148 | — | `KeyGesture.Parse("Ctrl+S")` | `(Key.Character, Control, Character: "S")` — single-char token ⇒ character gesture (ND13) | PIN (ND13) |
| N149 | — | `Parse("F5")` / `Parse("Alt+Enter")` / `Parse("Ctrl+Shift+P")` (Theory) | `(F5, None)` / `(Enter, Alt)` / `(Character, Control\|Shift, "P")` | PIN |
| N150 | — | `Parse("ctrl+s")` / `Parse("Control+S")` / `Parse("Win+K")`/`Parse("Cmd+K")` (Theory) | case-insensitive; aliases per ND13 (`Win`/`Cmd` → Super); `Character` canonicalized upper-invariant (`"ctrl+s"` ⇒ `"S"` — stable `ToString`, amended per ND13) | PIN |
| N151 | — | `Parse("Ctrl+")` / `Parse("Bogus+X")` / `Parse("")` (Theory) | `FormatException` | PIN |
| N152 | — | `new KeyGesture(Key.Character, Control)` without `Character` / `new KeyGesture(Key.F5, Character: "x")` | both throw `ArgumentException` (character gestures must carry `Character`; named must not) | PIN (doc §7.9) |
| N153 | gesture `"Ctrl+S"` | `bytes(0x13)` (legacy C0 DC3) → decoded `KeyEvent` | device shape `(Character, "s", Control)`; `Matches == true` — **legacy-C0 encoding row** | PIN (the dual-encoding pin) |
| N154 | gesture `"Ctrl+S"` | `bytes("\x1b[115;5u")` (Kitty CSI-u Ctrl+s) | same `(Character, "s", Control)` shape; `Matches == true` — **Kitty encoding row**; one gesture, both wires | PIN |
| N155 | gesture `"Alt+F"` | `bytes("\x1bf")` (ESC-prefix Alt+f) | `(Character, "f", Alt)`; matches — the legacy Alt observable | PIN |
| N156 | gesture `"Ctrl+S"` | `KeyEvent(Character, "s", Control, ExtendedModifiers: Control\|CapsLock\|NumLock)` | matches — lock bits never consulted (`Modifiers` only) | PIN (ND14) |
| N157 | gesture `"Ctrl+S"` | event `(Character, "S", Control\|Shift)` | **no** match (exact modifiers); `Parse("Ctrl+Shift+S")` matches it; case-insensitivity is on `Character` text, not on the Shift bit | WPF (ND14) |
| N158 | gesture `"Space"` | event `(Character, " ", None)` | matches (ND10/ND13 — `Space` token compiles to the character gesture) | PIN |
| N159 | B focused with `InputBindings = [Ctrl+S → cmd]` | `key(Character, "s", Control)` | sweep position: B's `OnKeyDown` virtual + instance handlers run first; then the binding executes `cmd`, `Handled = true`, `result == H` | PIN (doc §7.5 step 4) |
| N160 | two bindings same gesture on B | dispatch | first in collection order executes; second never consulted — **ordering is the priority mechanism** | PIN (doc §7.9) |
| N161 | binding gesture `Ctrl+S` with `CanExecute == false`, second binding same gesture `CanExecute == true` | dispatch | first skipped **without consuming** (ND15); second executes | PIN (ND15) |
| N162 | binding on `Root` (window-root default pattern) + same-gesture binding on focused B | dispatch | B's executes (bubble order: focused-element-wins); root binding fires only when inner leaves it unhandled | WPF (doc §7.9 IsDefault pattern) |
| N163 | KeyDown handler sets `Handled` before the binding node | dispatch | sweep skipped at that node (bindings are part of normal — non-handledEventsToo — processing) | PIN |
| N164 | command's `CanExecuteChanged` → control calls `InvalidateIsEnabledCore()` (the `Btn` test control) | toggle `CanExecute` false/true | `IsEffectivelyEnabled` flips with `Disabled` state both directions (N146 wiring exercised through the command seam — the ButtonBase pattern minus ButtonBase) | PIN (doc §7.9) |
| N165 | `KeyBinding` on a **disabled** element in the route | dispatch | binding does not execute (disabled elements don't process input; their route exclusion follows ND7 for mouse — for keys the element is in the focused chain only if focus repair failed; pin: the sweep skips bindings on effectively-disabled nodes) | WPF-shaped; PIN |

---

## 12. `AccessKeyManager` core — gate, brackets, registry, scopes, cycling (I4) — N166–N189, N212, N222–N226

AltHeld-mode rows use `KittyCaps`; legacy rows `LegacyCaps`. `cue` = `ak.IsCueActive` ∧ `state(activeScopeRoot) ⊇ AccessKeyCue`. Registrations use plain `Probe`s implementing `IAccessKeyTarget` (recording `OnAccessKey` args).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N166 | gate truth table (Theory): `(DistinguishesKeyUpDown, ReportsRepeats, Win32InputMode)` ∈ (T,T,F),(T,F,F),(F,F,F),(F,F,T),(T,T,T) | `OnCapabilitiesChanged` | Mode = AltHeld, AlwaysVisible, AlwaysVisible, AltHeld, AltHeld — the pinned formula `(up∧repeats) ∨ win32` | PIN (punch 21; probe-3 validated) |
| N167 | snapshot from a `KeyReleaseSynthesizer`-decorated pipeline (`DistinguishesKeyUpDown = true`, `ReportsRepeats = true` decorator-claimed) vs the **undecorated** negotiated snapshot (both false) | gate evaluation | gate reads the undecorated snapshot ⇒ AlwaysVisible; S6's fan-out passes session capabilities, never pipeline capabilities (asserted at the host level: `LegacyCaps` host stays AlwaysVisible even with the synthesizer hypothetically present) | PIN (ND23) |
| N168 | AltHeld mode, Alt held (cue on) | `OnCapabilitiesChanged` (renegotiation), same caps | unconditional clears: side bits, sticky, chord-flash latch, cue — no stale bracket survives the pump park | PIN (doc §7.8) |
| N169 | AltHeld host | renegotiate to `LegacyCaps` | mode flips to AlwaysVisible; cue set permanently; reverse flip clears the cue pending real Alt | PIN |
| N170 | AltHeld, `sink` installed | `key(LeftAlt)` | cue ON: `AccessKeyCue` set on the active scope root and window root (P2: both = `Root`) in **one** batch; `_sawStandaloneAlt` observable via the chord-flash rows | PIN |
| N171 | Alt held | `keyUp(LeftAlt)` with no intervening key | **Alt tap**: cue stays (sticky), `EnterMenuMode` raised once | PIN (doc §7.8) |
| N172 | Alt held | `key(Character, "f", Alt)` (chord) then `keyUp(LeftAlt)` | chorded: `_altWasChordless` false ⇒ cue OFF at Up, no sticky, no `EnterMenuMode` | PIN |
| N173 | both Alts held | release one | cue stays until both sides up | PIN |
| N174 | LeftAlt Down observed (bit set) | `key(Character, "x", None)` (no Alt bit) | stale-bracket inference: side bits cleared, cue off (unless sticky); the key then dispatches normally | PIN (doc §7.5 step 1b) |
| N175 | — | synthesized `KeyEvent(LeftAlt, Synthesized: true)` Down/Up | bracket state untouched (pre-stage skips synthesized) | PIN |
| N176 | Alt held + sticky | `focusEvt(false)` | side bits + sticky + cue all cleared unconditionally (Alt+Tab swallows the Up) | PIN |
| N177 | sticky cue active, B focused (B handles Escape in `OnKeyDown`) | `key(Escape)` | Esc **consumed** in the pre-stage: sticky + cue cleared, `result == H`, B never sees it (menu mode is modal to Esc) | PIN (doc §7.5 step 1c) |
| N178 | sticky cue active | second Alt tap | sticky + cue cleared (toggle) | PIN |
| N179 | sticky cue active | pointer-driven focus change (`Focus(Pointer)` via mouse down path) | sticky cleared (`OnFocusChanged(Pointer)` wire) | PIN |
| N180 | — | `Register('f', t1)`; `Register('F', t2)`; `Unregister` one; detach the other | case-folded registry (both under `F`); unregister removes one; detach backstop removes the other (`OnElementDetached`) | PIN (doc §7.8 flat registry) |
| N181 | `LegacyCaps` (AlwaysVisible), target `F` registered | `key(Character, "f", Alt)` unhandled | **legacy chord activation**: single match ⇒ `OnAccessKey(IsMultiMatch: false)` invoked, handled — works with no bracket ever observed | PIN (requirement 6 fallback) |
| N182 | AltHeld, Alt held, target `F` | `key(Character, "f", None)` (unmodified while held) | activates (Alt-held unmodified path) | PIN |
| N183 | focused TextBox-stand-in handles plain `f` in `OnKeyDown`; target `F` registered | `key(Character, "f", None)` vs `key(Character, "f", Alt)` | plain `f`: handled in step 4, access keys never consulted; Alt+F: reaches the manager (unhandled tail) — the coexistence row | PIN (doc §7.8) |
| N184 | targets `F`×2 (`t1` doc-order before `t2`), `t1` focused | Alt+F | **multi-match focuses, never invokes**: focus → `t2` via `OnAccessKey(IsMultiMatch: true)`, `method = AccessKey`; repeat → wraps to `t1`; cue stays | PIN (ND18) |
| N184b | single focusable target `F`, focus starts elsewhere | Alt+F | **single-match focuses then invokes**: focus → target (`method = AccessKey`, `FocusVisible` set) **and** `OnAccessKey(IsMultiMatch: false)` invokes | PIN (ND18 companion) |
| N184c | single **non-focusable** target `F`, focus starts elsewhere | Alt+F | invoked (`OnAccessKey(IsMultiMatch: false)`) but focus **stays put** — the manager's focus move is `Focusable`-gated (Label forwards focus itself) | PIN (ND18 companion) |
| N185 | target `F` disabled / detached / `Collapsed` (Theory) | Alt+F | excluded (`IsAccessKeyEligible`); 0 eligible ⇒ falls through per N186 | PIN |
| N186 | sticky cue active, no match for `x` | `key(Character, "x", None)` | swallowed: handled, cue stays, **no TextInput** (WPF bonk); without sticky: unhandled, TextInput proceeds | WPF (menu-mode swallow) |
| N187 | AltHeld mode but **no bracket ever observed** (family-matched terminal that never delivers Alt) | `key(Character, "f", Alt)` | **chord-flash self-correction**: cue flips ON (sticky) before processing, latch set; single match still activates (and clears per activation rules); a later real `key(LeftAlt)` clears the latch | PIN (doc §7.8) |
| N188 | scope stack: `PushScope(Pane)` with target `t1` inside Pane, `t2` outside (under Root) | Alt+F (both registered under `F`) | activation-time scope resolution: only `t1` matches (ancestor walk against the live stack); `PopScope(Pane)` ⇒ `t2` matches again — no registration-time scope capture | PIN (doc §7.8) |
| N189 | any mode | unhandled `key(F10)` | sticky cue + `EnterMenuMode`, handled (ND19); a focused element handling F10 in step 4 wins | PIN (ND19) |
| N212 | AltHeld; ⓐ Alt held, Alt+F chord (activates), then Alt **repeat** Down (`IsRepeat`), then Alt Up; ⓑ chordless Alt Down → repeat Down → Up | both sequences | ⓐ stays chorded: no sticky cue, no `EnterMenuMode` (a repeat never re-arms the chordless flag — ND32); ⓑ still a tap: sticky cue + `EnterMenuMode` once (repeats don't break a genuine tap) | PIN (ND32) |
| N222 | a `Label` with `Target = input` (focusable), both attached | inspect | bidirectional proxy registration: `GetAccessKeyProxy(input) == label` and `GetAccessKeyProxyFor(label) == input` | DEV (ND34) |
| N223 | two Labels both `Target = input` | inspect | first-wins: `GetAccessKeyProxy(input) == label1`; `label2` did not claim (`GetAccessKeyProxyFor(label2) == null`) — it forwards via `OnAccessKey` instead | DEV (ND34) |
| N224 | `label1` (Target = input) attached, then detached, then `label2` (Target = input) attached | sequence | detach **releases** the claim (`GetAccessKeyProxy(input)` null after detach); `label2` then claims it (`== label2`) — no dangling proxy on a live target | DEV (ND34) |
| N225 | `Label "_Name"` proxying `input` (a focusable field), label's mnemonic registered | Alt+N | activation redirects to the proxy target: focus → **input** (not the Label); the resolved target is focused and invoked (an input merely gains focus, a `ToggleButton` target would also toggle) | DEV (ND34) |
| N226 | `FocusManager.ResolveFocusEntry(x)` for: a focusable `x`; a non-`Once` focus-scope `S → G1,G2` with no memory; same `S` with memory → G2 | query | `x` (focusable) → `x`; `S` no memory → first focusable descendant G1; `S` memory → G2 — the shared ND33 entry ladder behind the proxy redirect | DEV (ND33/ND34) |

---

## 13. `FrameClock` / `UITimer` slice (I4) — N190–N195

The early-S5 slice (inversion 1): same types S5 A0 absorbs at P8. All rows on the fake clock.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N190 | `UITimer.Start(100ms, cb)` | `AdvanceTime(99ms)` + `RunFrame` / then `AdvanceTime(1ms)` + `RunFrame` | not fired / fired exactly once during the frame's tick at the **frozen** `FrameTime`; `IsRunning` false after (one-shot) | PIN (doc §9.8) |
| N191 | repeating `Start(100ms, 50ms, cb)` | advance 100ms, frame; advance 200ms, one frame | first due fires; the 200ms gap fires **once** that frame (coalesced, ND20), re-arming from the frozen clock | PIN (ND20) |
| N192 | running timer | `Stop()` / `Dispose()` twice / `Stop` then `Restart()` | stopped timer never fires; idempotent; `Restart` re-arms dueTime from the current frame clock and fires on schedule | PIN |
| N193 | pending timer, otherwise idle tree | `RunUntilIdle` behavior | the loop does **not** report idle while a timer is pending (`HasActiveAnimations` includes timers — the idle-guard participation row); after the one-shot fires, idle returns | PIN (doc §9.8/§10.5) |
| N194 | timer callback throws | due frame | exception surfaces through S6's guarded tick (funnel policy); scheduler state stays consistent — other timers unaffected, no re-fire of the thrower | PIN |
| N195 | callback starts another timer | due frame | newly started timer is armed from the same frozen frame clock; fires on its own schedule (no same-frame cascade) | PIN |

---

## 14. Performance & allocation contracts (I4; re-asserted P3/P5) — N196–N201

Methodology per ND25. Rows N196–N199 are `[Fact]`s with asserted allocation deltas; N200 is the probe-4 CI gate. N201's first-dispatch leg is asserted as a *bound* (≤ 64 KB), not a non-zero floor: the cold-process cost is real, but in-suite the xUnit worker thread's free-lists may already be warm from earlier tests — the binding contract is the bound plus the second dispatch's exact 0 (amended at I4).

| # | Scenario | Expected |
|---|---|---|
| N196 | warmed `move` along a row with an **unchanged** hover chain (the any-event-motion steady state) | **0 B per `ProcessEvent`**; no hit-test re-derivation beyond the single `RenderTree.HitTest` call |
| N197 | warmed `move` alternating between two sibling leaves (enter/leave + one interaction batch + `HoverChanged` per move) | **0 B per move** steady-state (pooled chains, pooled snapshots, pooled args) |
| N198 | warmed `key(Character, "x")` dispatch through a 10-deep route with one subscribed handler per node + TextInput synthesis | **0 B per event** steady-state (pooled args + route scratch; channel-side allocation excluded — measured from `ProcessEvent`) |
| N199 | warmed `RunFrame` with stationary pointer, clean tree (`UpdateHover` no-change re-diff) | **0 B** for the hover leg |
| N200 | **probe 4 — motion-storm CI gate**: UITestHost, ~300 elements (the probe-1-shaped dashboard tree), a 200-column pointer sweep injected as 200 Move events drained in one frame | ≤ 33 ms/frame (best-of-5, warm), **zero steady-state allocation** on the per-Move path; `[Trait("Category","Benchmark")]`, allocation asserted every suite run, timing informational + budget-asserted |
| N201 | one-time costs | pinned **bounded, not zero**: first dispatch allocates pooled args + route scratch + handler-store lazy arrays; the second identical dispatch allocates 0 (the warm-up contract N196–N199 rely on) |

---

## 15. Test authoring contract

Each numbered row becomes **exactly one** xUnit test in `Cursorial.UI.Tests/InputMatrix/`, named after its row id with a behavior slug (`N074_HoverDiff_CommonAncestorPruned`), one file per section (`Section01_RoutedEvents.cs` … `Section14_Perf.cs`). Rows whose Expected cell enumerates a family (N19, N37, N99, N104, N124, N149–N151, N166, N185) become a single `[Theory]` with one case per family member, keeping the row↔test bijection at the row level. Rows are not merged, reordered, or "covered implicitly": a row without a matching test is a P2 exit-criterion failure (§14 P2: motion-storm gate + focus-restore + modal-occlusion + gesture-encoding rows green — the modal-occlusion oracle rows themselves live at P7 with S4; their P2 precursors are N65/N69). DEBUG-only assertions (N10, N13's double-raise, N16, N25's diagnostic, N107's depth cap) compile under `#if DEBUG` and assert the absence of the check in release where practical. Encoding rows (N39, N153–N155) go through `SendBytes` + the real `VtInputDevice` on the fake clock — they are the wire-truth anchors; everything else may inject records directly. When the implementation cannot honor a row, the resolution is a PR that amends this file (and, where tagged `PIN`/`DEV`, the ND ledger) **before** the code change lands — the matrix is the oracle, not the implementation.
