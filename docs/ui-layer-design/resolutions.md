# Punch-list resolutions (FINAL — apply when condensing specs into the design doc)

The punch list (punchList.md) is **accepted as written** for every item, including each item's named winner. Where an item offered alternatives, the pick is below. Items not listed: apply the punch list's stated resolution verbatim. The completeness report's §3.B gaps get the recorded stances at the end.

## Picks for "choose one" items

- **1 (scene granularity):** S1's render-boundary zone engine is canonical. S4's `TopLevelSurface` wraps an S1 `RenderTree`; `ISurfaceRasterizer` whole-surface raster deleted. S8's `IElementLayer`/`RequestLayer` re-expressed as S1 boundary promotion; ProgressBar's indeterminate layer **persists once minted** (S1 sticky rule wins; no demotion valve in v1). De-risking probe P1 (raster benchmark) is a Phase-0 deliverable.
- **2 (ScrollContentPresenter):** S1 keeps the type; S8's banded scene policy `[anchor − K, anchor + viewport + K)` replaces S1's extent-sized scene + degraded fallback; S1's styled `ScrollOffset*` (`AffectsComposite`, storyboard-animatable, re-anchor check in metadata handler) wins; ScrollViewer's offsets are two-way mirrors. One named constant for the extent cap (`LayoutLimits.MaxScrollExtent`).
- **8 (animation frame protocol):** S5 implements S6's `IAnimationFrameDriver`. Clock carrier: **S6 computes `FrameTime` and passes it to `BeginFrame(in FrameTime)`** as the single time source; the scheduler freezes it for the frame. `TickNewlyStarted` implemented for storyboards ignited by the post-tick styling flush.
- **11 (capability fan-out):** S6 **explicitly enumerates** the four calls (styling, dispatcher, access-key manager, application) in startup + renegotiate sequences — no event-subscription indirection, so ordering is auditable.
- **21 (access-key gate):** pinned formula = `(Keyboard.DistinguishesKeyUpDown && Keyboard.ReportsRepeats) || Protocol.Win32InputMode`, evaluated against the **undecorated** negotiated snapshot; note in-doc that this is equivalent to the input map's conjunct form because Win32 input mode implies DistinguishesKeyUpDown.
- **38 (EventTrigger gap):** `ModalAttention` becomes a **transient pseudo-class pulse** (S4 sets, S3-style InteractionState plumbing, ~600 ms timed clear via S5's UITimer) so the existing style-edge-action path animates it. Routed-event `EventTrigger` recorded as deferred.
- **42 (Window.Content):** `Window : ContentControl` (hierarchy: `UIElement → Control → ContentControl → Window`); chrome's `PART_ContentHost` ContentPresenter auto-alias engages naturally.
- **46 (glyph tiers):** glyph resources live at color-tier keys (S7's proxy) + `caps-ascii`-class-selected style overrides for genuine mismatches.
- **50 (popup resource pulses):** nodes register under the registry of the root their **logical chain** tops out at (the host window). No surface→host fan map.
- **51 (CompositeClip):** **ADD** `CompositeClip (Rect?)` styled property to S1 (`AffectsComposite`, boundary-promoting) — keeps S5's reveal/wipe lane.
- **52 (Thickness):** v1 reuses `Margins` (unsigned); `Thickness*` animation types re-typed to `Margins` with clamping interpolation; signed-margin vocabulary stays deferred with S1's record.
- **37 (popups):** S4's `Popup` element is the contract; S4 **adds content-swap-without-close** on an open Popup (surface + scene retained ⇒ no layer-count change) for the menu-session case.

## Naming

`UI` fully capitalized in type names everywhere: `UIElement`, `UIObject`, `UIProperty`, `UIPropertyKey<T>`, `UIPropertyChangedEventArgs`, `UIElementCollection`, `UIApplication`, `UIDispatcher`, `UITestHost`, `UITimer`. Canonical subsystem map (punch item 57): S1 tree/layout, S2 binding, S3 input/focus, S4 windowing, S5 animation, S6 app model, S7 resources, S8 controls; engines: Fork A property system, Fork B styling, Fork C XAML loader.

## Recorded stances for completeness-report §3.B gaps (go in the doc's deferrals section)

- **Accessibility/automation:** no v1 story; recorded deferral. The caret-as-terminal-cursor design is the one v1 affordance (real terminal cursor positioning helps screen readers); an AutomationPeer-like seam is future work, additive.
- **Alternating-row styling:** deferred with a designed mechanism: the ItemsControl container generator stamps an `:alternate` pseudo-class (or `AlternationIndex` attached int) on generated containers — generator-owned state, element-local invalidation, does NOT reopen the `:nth-child` fence. Cheap early add after C1.
- **Collection views (sort/filter/current-item):** explicitly deferred, jointly owned by S2+S8 when it lands; v1 = bind pre-shaped collections.
- **`Window.Title` → OSC 2:** WIRED in v1 — main window's `Title` flows through S6's `QueueControlSequence` via `WindowWriter.WriteTitle` (title restored on shutdown per session teardown). One paragraph in S4 + S6 sections.
- **Grid `SharedSizeGroup`:** deferred, recorded (form layouts; additive to Grid).
- **Localization/`x:Uid`:** out of scope for v1, recorded one-liner.
