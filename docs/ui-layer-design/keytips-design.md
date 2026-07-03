# Cursorial Bars KeyTips (#145) — Final Implementation Design

<!-- Synthesized design (multi-agent workflow, 2026-07). The implementable spec for #145: an Alt-badge accelerator
overlay riding AccessKeyManager, with a multi-level prefix drill across the ribbon / toolbar / menu. Grounded to
file:line seams against the tree at design time. v1 scope in §14; flagged risks in §15. -->

## 0. Verdict and grounding

Base = **Angle A** (KeyTips is a MODE riding AccessKeyManager's Alt state machine, badges on a dedicated WindowManager surface). Grafted from Angle B: **(G1)** the interception seam is `InputDispatcher.PreProcessInput` — NOT an edit to `AccessKeyManager.OnPreStageKeyDown`; **(G2)** positioning via the existing `UIElement.TranslateToScreen`; **(G3)** a `KeyTipTargetKind` enum + per-entry drill/activate `Func`. Two grafts were validated against source and one is **rejected** (see §12): the trigger stays A's `CueActivated`/`CueDeactivated` off `ActivateCue`/`DeactivateCue`, not B's `EnterMenuMode`.

All file:line references below are verified against the current tree.

---

## 1. Architecture & layering

Three seam types in `Cursorial.UI` (all no-ops when no controller installed), the controller + all UX in `Cursorial.UI.Bars`:

- **`Cursorial.UI.Input.IKeyTipController`** (new interface, `Cursorial.UI`) — the nullable slot `AccessKeyManager` holds. No Bars dependency flows upward.
- **`Cursorial.UI.Bars.Input.KeyTipController : IKeyTipController`** — the FSM, level stack, badge-layer ownership, focus snapshot/restore.
- **`Cursorial.UI.Bars.KeyTip`** (static) — the attached properties + `KeyTipModel` derivation.
- Badge visuals in `Cursorial.UI.Bars.Input` (`KeyTipLayer`, `KeyTipBadge`).
- One WM seam (`ShowKeyTipOverlay`/`HideKeyTipOverlay`), one frame-loop hook (`CompletePendingKeyTipLayout`), one `AccessKeyManager` seam-bundle (slot + `CueActivated`/`CueDeactivated`), one public seam on `ContentControl` for access-text derivation.

Discovery: the controller is created lazily and installed via a Bars-side extension `UIApplication.EnableKeyTips()` (returns the controller; idempotent). Rationale over an auto-property: `UIApplication` must not reference Bars, and only a bars app wants KeyTips armed.

---

## 2. The interception seam (G1 — the risk-neutralizing change)

**Do NOT touch `AccessKeyManager.OnPreStageKeyDown`.** Instead subscribe `InputDispatcher.PreProcessInput` (`InputDispatcher.cs:95`; fired for KeyEvents at **line 436**, before `EventRouting.RaisePair` at **442** and before the access-key tail `_accessKeys.ProcessKeyDown` at **462**). Setting `args.Handled = true` there:
- suppresses the focused-element route (the `handled is false && target is not null` guard at **440**),
- suppresses `ProcessKeyDown` / navigation / `TextInput` synthesis (all gated on `!handled` at **458**),
- leaves the Alt pre-stage (`OnAltDown`/`OnAltUp` at **403–408**) intact — it runs *before* PreProcessInput, so Alt bracketing and the cue machine are unaffected.

The controller subscribes in `KeyTipController` construction:

```
app.InputDispatcher.PreProcessInput += OnPreProcessInput;
```

Handler contract (tight):
```
private void OnPreProcessInput(object? sender, InputEventArgs e)
{
    if (!IsActive || e is not KeyEventArgs k || k.Kind != KeyEventKind.Down)
        return;
    // Consume ONLY unmodified Character/Escape while active. Ctrl/Alt/Super/... chords fall through
    // so global gestures (Ctrl+S) still fire and Alt release still reaches AccessKeyManager.
    if (k.Key == Key.Escape && k.Modifiers == KeyModifiers.None)      { PopLevelOrExit(); e.Handled = true; }
    else if (IsPlainCharacter(k))                                      { TypeChar(k.Text.Span[0]); e.Handled = true; }
    // Backspace-to-untype is DEFERRED (v2).
}
```
`IsPlainCharacter` mirrors `RibbonTab.IsActivationKey`'s ND10 wire handling: `k is { Key: Key.Character, Text.Length: 1 }` AND `(k.Modifiers & TextInputChordMask) == None` (Shift allowed for shifted letters; Ctrl/Alt excluded).

Why this is safe: PreProcessInput is an existing public event purpose-built for pre-route inspection, already invoked in `ProcessKeyEvent`/`RaiseMousePair`/`RaiseTextInput`. Zero change to the proven Alt/dispatch hot path. This is Angle A's self-identified "one real engine blocker" fully dissolved.

---

## 3. AccessKeyManager coexistence — who owns Alt

`AccessKeyManager` stays **sole** owner of: the Alt bracket, the ND23 capability gate (`AccessKeyManager.cs:185`), F10/Alt-tap detection, sticky menu mode, terminal-focus-out clears, the flat registry, scope stack. KeyTips adds exactly these no-op-by-default seams to `AccessKeyManager`:

1. **`internal IKeyTipController? KeyTipController { get; set; }`** — the nullable slot.
2. **`internal event Action? CueActivated;`** and **`internal event Action? CueDeactivated;`** — fired from `ActivateCue()` (line 682) and `DeactivateCue()` (line 703). Fire `CueActivated` at the **end** of `ActivateCue` (after the batch commits) and `CueDeactivated` at the **end** of `DeactivateCue`. These are the arm/disarm triggers.

Trigger logic in the controller:
- `CueActivated` fires whenever the cue turns on — physical Alt down (`OnAltDown`), Alt-tap sticky (`OnAltUp`→`RaiseEnterMenuMode` also stamps cue), F10 (`ProcessKeyDown`), chord-flash self-correction. The controller **enters** on `CueActivated` **iff** `Mode == AccessKeyMode.AltHeld` AND at least one KeyTip surface is registered in the active root. In `AlwaysVisible` mode KeyTips stays OFF (badges need a real Alt bracket to arm/disarm; the inline underline is the fallback — ND23 parity).
- `CueDeactivated` fires on every cue-off path (`OnAltUp` chorded release, second Alt-tap, Esc-sticky-consume, `OnFocusChanged` Pointer, `OnTerminalFocusLost`, capability change). The controller **exits** on `CueDeactivated`. This single subscription subsumes ALL of Angle A's enumerated exit conditions (pointer focus, terminal focus-out, second tap, capability renegotiate) because they all route through `DeactivateCue` → `ClearCueForAltHeld`. **This is why the trigger is `CueActivated`/`CueDeactivated`, not `EnterMenuMode`** (which fires only on the Alt-*tap*, missing physical Alt-hold and missing all the exit paths).

**Window-activation-change exit:** subscribe `WindowManager.ActiveWindowChanged` (`WindowManager.cs:99`) → `Exit()` as a belt-and-suspenders (a window switch that doesn't route through cue-off).

**The double-cue problem (badges + underlines both showing).** v1 uses Angle B's **`SuspendCue`** as the simpler interim (the compose-both `:keytip-active` pseudo is v2 — see §12 risk):
- Add `internal void SuspendCue()` / `internal void ResumeCue()` to `AccessKeyManager`: a `_cueSuppressed` bool that makes `ActivateCue`/`StampCueRoot` no-op while set (and un-stamps any live cue roots on suspend). The controller calls `SuspendCue()` in `Enter()` **after** reading the cue state, and `ResumeCue()` in `Exit()`. Net: while KeyTips is active, no `:access-keys` underline anywhere; badges are the sole cue. When KeyTips exits, `ResumeCue` restores normal behavior (and if Alt is still physically held, re-stamps — but in practice exit coincides with cue-off).
  - Edge: `SuspendCue` must not fight the arm. Sequence in `Enter()`: read that cue is on → build level 0 → `SuspendCue()` (drops underlines) → show badges. Because `CueActivated` fires *after* `ActivateCue` already stamped, the suppress correctly tears the just-stamped underline down.

Registration coexistence: a bar control MAY carry both an access-key (its inline mnemonic, used in the AlwaysVisible fallback) AND a derived KeyTip badge — same letter, orthogonal mechanisms. Leaf activation reuses `IAccessKeyTarget.OnAccessKey` (see §7), so there is no duplicated activation logic.

---

## 4. KeyTip declaration model

`Cursorial.UI.Bars.KeyTip` (static, namespace `Cursorial.UI.Bars`):

```
public static readonly AttachedProperty<string?> KeyProperty =
    UIProperty.RegisterAttached<KeyTip, UIElement, string?>("Key");           // authored badge text, 1–2 chars
public static readonly AttachedProperty<bool> AutoAssignProperty =
    UIProperty.RegisterAttached<KeyTip, UIElement, bool>("AutoAssign", defaultValue: true);
// Get/Set for each.
```

**Derivation ladder** (`KeyTipModel.Resolve(UIElement) : string?`), applied at level-build time (lazy — parity with AccessKeyManager's activation-time scope resolution):
1. Explicit `KeyTip.Key` if set → use verbatim (uppercased).
2. Else if `AutoAssign == false` → the target has no badge (skipped from the level).
3. Else the control's access-key letter, if it has one. **Seam needed:** `ContentControl.GetAccessText()` is `protected virtual` (`ContentControl.cs:126`). Add:
   ```
   internal AccessText GetAccessTextInternal() => GetAccessText();   // ContentControl
   ```
   `KeyTipModel` calls it when the target is a `ContentControl` and `access.HasKey`, taking `access.Key`. For non-ContentControl targets (or when no mnemonic), fall to:
4. `BarCommand.Text`'s first mnemonic: if the target's `Command` is a `BarCommand` with `Text`, run `AccessText.Parse(text)` (public, `AccessText.cs:23`) and use `.Key` if present, else its first letter.
5. First non-whitespace letter of the derived display text (`AccessText.Text` / `Header` / `Content` stringified), uppercased.
6. Nothing derivable → skip (DEBUG diagnostic).

**Collision policy (v1, from graft G4 + Angle A's own deferral):** auto-assign is honored **only when the derived string is unique within the level**. On a collision among auto-assigned targets in one level: **first-in-document-order wins the letter; the colliders are dropped from the level with a `KeyTipDiagnostics.Warning`** (DEBUG). Authors disambiguate by setting explicit `KeyTip.Key`. Two-letter auto-assignment (WPF FN/FP resolver) is **v2**. Explicit multi-char keys ("FP", "FF") ARE supported in v1 (they just aren't auto-*generated*).

Optional per-control override: `IKeyTipProvider` (in `Cursorial.UI.Bars.Input`) with `string? ResolveKeyTip()` + `KeyTipAnchor PreferredAnchor` — checked first in `KeyTipModel.Resolve` before the ladder. Ships in v1 as the extension point; the badge anchor default is the target's trailing corner.

---

## 5. The FSM & level model

`KeyTipLevel` (value carrier, `Cursorial.UI.Bars.Input`):
```
sealed class KeyTipLevel {
    List<KeyTipEntry> Entries;      // resolved (target, keytip, kind, drillAction)
    UIElement ScopeRoot;            // the surface/element the level lives under (for TranslateToScreen)
    StringBuilder Typed;            // the matched-prefix buffer for THIS level
}
```
`KeyTipEntry`:
```
readonly struct KeyTipEntry(UIElement Target, string KeyTip, KeyTipTargetKind Kind, Action Drill);
enum KeyTipTargetKind { Activate, DrillTab, DrillGroup, DrillPopup }   // (G3)
```
Each entry carries a **`Drill` Func/Action closure** (G3) that encapsulates the reveal: set `Ribbon.SelectedIndex`, open a `BarDropDownButton` dropdown, open a `Toolbar` overflow, open a `MenuItem` submenu — the caller (the `IKeyTipHost` adapter, §6) builds the closure. This unifies ribbon/toolbar/menu behind one push/pop machine.

**States:** `Inactive → Active(List<KeyTipLevel> stack)`.

- **Enter()** (from the trigger, §3): snapshot `_restoreFocus = app.FocusManager.FocusedElement`; `SuspendCue()`; build LEVEL 0 from the registered `IKeyTipHost`s in the active root; `ShowKeyTipOverlay(_layer)`; place badges (§8). `IsActive = true`.
- **TypeChar(c)** (from §2): append folded `c` to the current level's `Typed`; filter entries by case-folded prefix:
  - **0 matches:** bonk — drop the appended char (revert), flash the layer (a brief `:keytip-bonk` on the layer, cleared by a `UITimer`) + optional bell via `QueueControlSequence`. Never leaks to TextInput (already consumed).
  - **exactly 1 entry whose keytip == `Typed` (complete):** COMMIT that entry. `Activate` → invoke leaf (§7) then `Exit()`. `DrillTab`/`DrillGroup`/`DrillPopup` → run `entry.Drill()`, then **push** the next level (built by the entry's host adapter) with `Typed` cleared. The next level's badges may need to be placed after a relayout → **park** the placement (§8, §9).
  - **≥1 entry still prefix-matching but none complete** (multi-char keytips): stay; set `MatchedPrefixLength = Typed.Length` on every still-viable badge (dims matched letters), hide non-matching badges. Composite-only.
- **PopLevelOrExit()** (Esc): if stack depth > 1, pop one level (undo its drill where reversible — collapse a floated band via the host adapter's `Retract` closure, close an opened popup), re-place the parent level's badges. If at level 0, `Exit()`.
- **Exit()** (from `CueDeactivated`, activation, `ActiveWindowChanged`, or Esc-at-top): `HideKeyTipOverlay()`; clear the stack; `ResumeCue()`; if exit was NOT via a leaf activation, restore focus via the ribbon's non-retaining scope (see §7); `IsActive = false`. Idempotent (guard on `IsActive`) — critical because `CueDeactivated` and a leaf-activation Exit can race.

`IKeyTipController` surface:
```
bool IsActive { get; }
void Enter();               // called by the controller's own trigger, exposed for tests
void Exit();
bool TryHandleKey(char c);  // test seam; production uses PreProcessInput
```

---

## 6. Multi-level drill & the `IKeyTipHost` abstraction

`IKeyTipHost` (in `Cursorial.UI.Bars.Input`) is implemented by small internal adapters, one per surface family. Each surface **registers** with the controller when it attaches (via a Bars-internal `KeyTipController.RegisterHost(IKeyTipHost)` called from `Ribbon`/`Toolbar`/`Menu` `OnAttachedToTree`, deregister on detach — mirroring `RibbonStripPanel.CollapseChanged` wiring lifetimes).

```
interface IKeyTipHost {
    UIElement SurfaceElement { get; }                  // for active-root membership test
    void BuildRootLevel(KeyTipLevelBuilder into);      // level 0 entries + drill closures
}
```

### Ribbon (full multi-level — the v1 centerpiece)

- **LEVEL 0** (`RibbonKeyTipHost.BuildRootLevel`): one `DrillTab` entry per realized `RibbonTab` header (source: `Ribbon.ItemContainerGenerator` containers; keytip from the tab's `Header` access-key), plus one `Activate` entry per QAT item (source: the active QAT toolbar's items via `Ribbon.ActiveQuickAccessToolbarForTests`→ promoted to a real internal accessor `ActiveQuickAccessHost`; digits per the guide), plus the ⋯▾ collapsed-QAT opener as `DrillPopup` when collapsed.
  - Tab drill closure: `() => { ribbon.SelectedIndex = i; if (ribbon.IsMinimized) ribbon.FloatBand(); }` — reuses the exact selection path (incl. File→Backstage: a File tab's entry is `Activate` whose closure `RaiseEvent(BackstageRequestedEvent)` — reusing `RibbonTab.OnAccessKey`). Then push LEVEL 1.
- **LEVEL 1** = the selected tab's `RibbonGroup`s + any direct-activate controls in the band. A group with hosted controls is a `DrillGroup`; its drill closure is a no-op reveal (the band is already shown) → push LEVEL 2. A collapsed group (`RibbonGroupDensity.Collapsed`) is a `DrillPopup` whose closure sets `_collapsedButton.IsDropDownOpen = true` and pushes a level over the flyout band.
- **LEVEL 2** = the group's hosted bar controls (`RibbonGroupPanel.Containers`) + the ⋰ launcher (`Activate`, closure `RaiseEvent(DialogLauncherRequestedEvent)`).
- **ACTIVATION of a dropdown control** (`BarSplitButton`/`BarDropDownButton`): the entry is `DrillPopup`, closure `button.IsDropDownOpen = true`; push a NEW level over the opened dropdown's items (parked until the popup surface exists — §9).

Because minimized/floated bands and collapsed groups realize over later frames (`ScheduleEnterFloatedBody`, `ScheduleCollapsedFocusRepair` precedents), the pushed level's **badge placement is parked** to `CompletePendingKeyTipLayout` with the same `_floatGeneration`-style guard (§9, Risk 5).

### Toolbar (single-level in v1; overflow-drill deferred)

`ToolbarKeyTipHost.BuildRootLevel` = one `Activate` entry per realized bar control (walk the `ItemsControl` containers). All flat, no tab step. **Overflow-drill (the ⋯/» popup) is DEFERRED to v2** — v1 badges only the visible row; overflowed items get no badge (documented). Rationale: the overflow popup needs the park-until-popup-surface leg, which v1 proves once on the ribbon dropdown path and can extend later.

### Menu bar (single-level top items in v1; submenu-drill deferred)

`MenuKeyTipHost.BuildRootLevel` = top-level `MenuItem` headers as `Activate` (each opens its submenu via the existing menu activation, then Exit) — matching the current access-key behavior. **Submenu KeyTip drill is DEFERRED to v2** (same park-until-popup-surface dependency as toolbar overflow).

The one `KeyTipLevel`/`KeyTipEntry` type serves ribbon tab→group→control, and (v2) toolbar→overflow→items and menu→submenu→items — one accelerator model.

---

## 7. Focus / input routing

- Keys reach the controller via PreProcessInput (§2), never the focused element while `IsActive`. Modified keys fall through (global gestures survive).
- Focus is **NOT moved during drill** — badges are the focus surrogate. `Enter()` snapshots `_restoreFocus = FocusManager.FocusedElement`. A tab drill sets `SelectedIndex` + floats the band but does not move keyboard focus.
- **Leaf activation** reuses `AccessKeyManager.InvokeAccessKey` semantics by calling the target's `IAccessKeyTarget.OnAccessKey(new AccessKeyEventArgs(key, isMultiMatch: false, target))` directly (single-match → invoke). For a focusable target the controller focuses it first (parity with `AccessKeyManager.ProcessKeyDown` line 514–517). Activation clears sticky state naturally because it triggers Exit which coincides with cue-off. A `BarToggleButton` toggles; a `BarSplitButton`/`BarDropDownButton` opens (handled as `DrillPopup`, not `Activate`, so it pushes a level).
- **Exit-without-activation focus restore:** call `app.FocusManager.RestoreRetainedFocus(ribbon)` (`FocusManager.cs:356`) — the Ribbon/Toolbar is a non-retaining scope (`Ribbon.cs:118–119`, `Toolbar.cs:71–74`), so this returns to the pre-entry document focus (the "Alt, poke, Esc, keep typing" model). A floated band re-collapses via `Ribbon`'s own `IsKeyboardFocusWithin` handler (`Ribbon.cs:686`) or the pop's `Retract` closure. If no surface was entered (e.g. the snapshot is still valid and attached), fall back to `FocusManager.SetFocus(_restoreFocus, FocusNavigationMethod.Restore)`.
- Mouse never routes to the KeyTip surface: it is `IsHitTestTransparent` (`TopLevelSurface.cs:98`). A real press exits via `CueDeactivated` (a pointer-driven focus change fires `OnFocusChanged(Pointer)` → `DeactivateCue`).

---

## 8. The badge overlay mechanism (exact)

**Surface, not adorner** (the engine has no adorner layer; the fit badge is the proven precedent). One dedicated KeyTip `TopLevelSurface` at the **top of the surface stack**, above windows AND popups.

**WM seam** (`WindowManager`, mirroring `_fitBadgeSurface`):
```
private TopLevelSurface? _keyTipSurface;
internal void ShowKeyTipOverlay(UIElement badgeRoot) {
    _keyTipSurface?.Detach();
    _keyTipSurface = new TopLevelSurface(badgeRoot, _scenePool, _capabilities, _guard)
                     { IsHitTestTransparent = true, Size = _viewport };
    RebuildSurfaceStack(); ResetCompositor();
}
internal void HideKeyTipOverlay() {
    if (_keyTipSurface is {} s) { s.Detach(); _keyTipSurface = null; RebuildSurfaceStack(); ResetCompositor(); }
}
internal void PlaceKeyTipBadges();   // re-reads each live target's screen rect (§9)
```
`RebuildSurfaceStack` (`WindowManager.cs:812`) appends `_keyTipSurface` **after** the fit badge — so KeyTips is the topmost surface (badges paint over an open dropdown popup — Risk 4).

**`badgeRoot` = `KeyTipLayer : Panel`** — a Canvas-idiom absolute-placement host. Each child is a **`KeyTipBadge : Control`**: an occluding amber `Border` wrapping an `AccessTextPresenter`-style text element. `KeyTipBadge` properties:
```
StyledProperty<string> KeyTipText;              // AffectsMeasure
StyledProperty<int> MatchedPrefixLength;        // AffectsRender — dims matched leading letters
```
The badge renders `MatchedPrefixLength` leading letters in a dimmed amber and the remainder full amber; on a complete/non-matching filter, the badge collapses (`Visibility.Collapsed`).

**Positioning (G2):** for each entry, `(sx, sy) = entry.Target.TranslateToScreen(anchorCol, anchorRow)` (`UIElement.cs:706` — live, allocation-free, never stale, and it already folds `SurfaceForElement().Left/Top`). Anchor = the target's trailing corner (guide: "1–2 cell overlay at the end of its command"), clamped into the viewport. `Canvas.Left/Top` set on each badge.

**Amber tint via the R2 DynamicResource palette spine:** two new `ThemeKeys` string keys — `KeyTipBrush` (`"Theme.KeyTipBrush"`, the amber fill) and `KeyTipMatchedBrush` (the dimmed matched-letter ink). `KeyTipBadge`'s theme (authored in `CursorialBarsTheme`) wires `Border.Background`/text foreground via `SetResourceReference` to these keys, so a dark/light theme flip re-skins badges live (matching the P6.1 fix #3 palette spine). Fallback default = `ThemeKeys.WarningBrush`-adjacent amber. `OnAccent` ink for the letters on the amber cell.

---

## 9. Frame-loop integration & position tracking

**New hook `CompletePendingKeyTipLayout()`** in `UIApplication.FrameLoop.cs`, sited in Phase 5 **after `OnLayoutCompleted()` (line 513) and `CompletePendingTransitionGoLive()` (line 518)**, before Phase 6 render — the exact mirror of `CompletePendingActivationFocus` (line 502) / `CompletePendingTransitionGoLive`:
```
// after CompletePendingTransitionGoLive();
_keyTipController?.CompletePendingLayout();   // re-anchor badges + build parked next-level badges
```
Exposed via a nullable `internal IKeyTipLayoutHook? _keyTipController` field on `UIApplication` set by `EnableKeyTips()` (the interface has one method `CompletePendingLayout()`), keeping Bars out of the core type.

`CompletePendingLayout()`:
1. If a level push is **parked** (a drill triggered a relayout — band float, group collapse, dropdown open), build its badges now that the subtree is arranged; guard with a `_levelGeneration` counter (bumped on every push/pop) so a stale parked build orphans itself (the `Ribbon._floatGeneration` pattern).
2. Re-read every live badge's screen rect via `TranslateToScreen` and update `Canvas.Left/Top` **only on change** (composite-only, no re-raster). Accepts a 1-frame lag for a target mid-reflow (Risk 2 — acceptable).

This runs after layout+`OnLayoutCompleted` so surface offsets (a window drag, a dropped popup's placement) are final for the frame.

---

## 10. Capability gate

**Reuse `AccessKeyMode` verbatim** (`AccessKeyManager.cs:185`, the ND23 formula `Color≠NoColor && ((DistinguishesKeyUpDown && ReportsRepeats) || Win32InputMode)`). The controller's arm predicate is `IsEnabled == (AccessKeys.Mode == AccessKeyMode.AltHeld)`. No new gate, no new capability plumbing. In `AlwaysVisible` mode the controller never enters on `CueActivated` — the inline access-key underline is the only affordance (ND23 parity; documented, manual-verification-only on a real Kitty session). Renegotiation flows through `AccessKeyManager.OnCapabilitiesChanged` → `DeactivateCue` → `CueDeactivated` → `Exit()`, so a gate-losing renegotiate tears KeyTips down cleanly.

---

## 11. New types / files & exact seams

**New files (`Cursorial.UI.Bars`):**
- `Input/IKeyTipHost.cs`, `Input/IKeyTipProvider.cs`, `Input/KeyTipAnchor.cs`
- `Input/KeyTipController.cs` (the FSM + `IKeyTipController` + `IKeyTipLayoutHook` impls)
- `Input/KeyTipLevel.cs`, `Input/KeyTipEntry.cs`, `Input/KeyTipTargetKind.cs`, `Input/KeyTipLevelBuilder.cs`
- `Input/KeyTipModel.cs`, `Input/KeyTipDiagnostics.cs`
- `Input/RibbonKeyTipHost.cs`, `Input/ToolbarKeyTipHost.cs`, `Input/MenuKeyTipHost.cs`
- `KeyTip.cs` (attached properties, static, `Cursorial.UI.Bars` namespace)
- `Input/KeyTipLayer.cs`, `Input/KeyTipBadge.cs`
- `KeyTipExtensions.cs` (`UIApplication.EnableKeyTips()` extension)
- Theme additions in `Themes/CursorialBarsTheme.cs` (KeyTipBadge control theme).

**New seams in `Cursorial.UI`:**
- `Input/IKeyTipController.cs` — new interface (§5 surface).
- `Input/IKeyTipLayoutHook.cs` — `void CompletePendingLayout()`.
- `AccessKeyManager.cs`: `+ internal IKeyTipController? KeyTipController`; `+ internal event Action? CueActivated` fired at end of `ActivateCue()` (line 682); `+ internal event Action? CueDeactivated` fired at end of `DeactivateCue()` (line 703); `+ internal void SuspendCue()/ResumeCue()` with a `_cueSuppressed` guard consulted in `ActivateCue`/`StampCueRoot`.
- `ContentControl.cs`: `+ internal AccessText GetAccessTextInternal() => GetAccessText();` (line ~126).
- `Windowing/WindowManager.cs`: `+ _keyTipSurface` field, `ShowKeyTipOverlay`/`HideKeyTipOverlay`/`PlaceKeyTipBadges`, append in `RebuildSurfaceStack` (line 831–832 region).
- `Hosting/UIApplication.FrameLoop.cs`: `+ CompletePendingKeyTipLayout` call after line 518; `Hosting/UIApplication.cs`: `+ internal IKeyTipLayoutHook? _keyTipController` + a `public WindowManager?` is already exposed (line 453).
- `Themes/ThemeKeys.cs`: `+ KeyTipBrush`, `+ KeyTipMatchedBrush` constants.
- `Cursorial.UI.Bars.csproj`: add `InternalsVisibleTo Cursorial.UI.Bars.Tests` already present.

---

## 12. Grafts adjudicated & rejected

- **Rejected:** Angle B's `EnterMenuMode` as the trigger. It fires only on the Alt-tap (`OnAltUp` chordless / F10), NOT on physical Alt-hold-then-type, and NOT on any exit path. Angle A's `CueActivated`/`CueDeactivated` off `ActivateCue`/`DeactivateCue` is the correctness keystone (covers hold-mode + all exits in one subscription). **Kept A.**
- **Rejected for v1, deferred to v2:** Angle A's compose-both cue (`:keytip-active` pseudo so badges show on bars + underlines on plain content simultaneously). Styling-specificity coordination is costly and unproven; v1 uses B's simpler `SuspendCue` mutual-exclusion (badges win the whole surface). Documented deferral.
- **Adopted:** G1 (PreProcessInput), G2 (TranslateToScreen), G3 (KeyTipTargetKind + drill Func), G4 (auto-assign-only-when-unique).

---

## 13. Headless test plan (`Cursorial.UI.Bars.Tests`, via `UITestHost`)

All headless; the `KittyTruecolor` preset gates `AltHeld`. Drive Alt via `SendBytes`/`SendKey` and letters via `SendKey`. Assert against the KeyTip surface's badge cells and controller state.

1. **Gate:** `KittyTruecolor` → `Mode==AltHeld`, Alt-down arms KeyTips (surface present, badges over tabs). `Ansi16Legacy`/`NoMotion` presets that fail the gate → `AlwaysVisible`, Alt-down shows underlines, NO KeyTip surface.
2. **Enter/Exit lifecycle:** Alt-down → `IsActive`, `_keyTipSurface != null`, `SuspendCue` dropped the underline (no `:access-keys` cue root). Alt-up chorded / Esc-at-top / second-Alt-tap / pointer press / terminal focus-out (`SendInput(FocusEvent{false})`) → `!IsActive`, surface gone, focus restored to snapshot.
3. **Ribbon L0→L1→L2 drill:** Alt, type a tab letter → `SelectedIndex` changed, band shown, L1 badges placed after one `StepFrame` (park proof). Type a group letter → L2 badges over the group's controls. Type a control letter → its `Click`/command fired, Exit, focus returned to document.
4. **File→Backstage:** Alt, type File's letter → `BackstageRequestedEvent` raised (reuses `RibbonTab.OnAccessKey`), Exit.
5. **Minimized ribbon drill:** `IsMinimized=true`, Alt, tab letter → band floats, L1 badges placed at the post-layout hook across the float generation; drilling completes; Esc collapses the float.
6. **Dropdown drill:** a `BarSplitButton` entry (DrillPopup) → `IsDropDownOpen==true`, a level pushed over the opened popup's items (parked-until-surface proof), type an item letter → item invoked.
7. **Matched-prefix (multi-char):** two controls "FP"/"FF" (explicit KeyTip.Key), type "F" → both badges show `MatchedPrefixLength==1`, others hidden; type "P" → FP invoked.
8. **Bonk:** type a non-matching letter → char reverted, no TextInput leaked to a focused `TextBox` (place a TextBox in the tree, assert its text unchanged), `:keytip-bonk` flashed.
9. **Toolbar single-level:** flat badges over visible controls; overflowed control has NO badge (documented v1 limit).
10. **Global-gesture survival:** while `IsActive`, `Ctrl+S` (both wire encodings) still fires a registered `KeyBinding` (not consumed by PreProcessInput).
11. **Auto-assign collision:** two auto-derived "H" controls in one level → first wins, second dropped + DEBUG warning; explicit `KeyTip.Key` resolves it.
12. **Theme flip:** dark→light while badges shown → `KeyTipBrush` re-resolves (badge cell color changes) with no crash (the R2 spine).
13. **Position tracking:** scroll/resize the surface under an armed level → `CompletePendingKeyTipLayout` re-anchors badges (assert new Canvas positions), composite-only (no re-raster of the target zone — `Scene.RasterVersion` unchanged).
14. **Renegotiate exit:** `RenegotiateAsync` to a gate-failing snapshot while active → `Exit()` via `CueDeactivated`, surface gone.

---

## 14. v1 vs deferred scope

**V1 (ship):** the `KeyTipController` FSM; `KeyTip.Key`/`AutoAssign` attached properties + `KeyTipModel` derivation ladder (explicit → access-key via `GetAccessTextInternal` → `BarCommand.Text` → first letter), auto-assign only-when-unique (first-wins + DEBUG diagnostic on collision, explicit multi-char keys honored); **full multi-level Ribbon drill** (tab→group→control, File→Backstage, drilling an opened bar-control dropdown, minimized/floated/collapsed realization via the parked layout hook); **single-level Toolbar** and **single-level Menu bar** (top items); the dedicated topmost WM KeyTip surface (`IsHitTestTransparent`) with amber `ThemeKeys.KeyTipBrush` tint via the R2 palette spine; matched-prefix highlight; Esc back-out + all exit conditions; capability gate reusing `AccessKeyMode`; `SuspendCue` mutual-exclusion; focus snapshot/restore via the ribbon's non-retaining scope; the PreProcessInput interception seam; headless tests per §13 row.

**Deferred (v2):** WPF FN/FP two-letter auto-assignment resolver; toolbar-overflow and menu-submenu KeyTip drill (the park-until-popup-surface leg — v1 proves parking once on the ribbon dropdown); the compose-both `:keytip-active` cue (badges on bars + underlines on plain content); Backspace-to-untype within a level; badge fade/animation; KeyTip badges on non-bars plain controls; a per-scope registry surviving re-templating without recompute; culture-aware letter selection beyond invariant folding; an AlwaysVisible-mode badge variant.

---

## 15. Remaining risk (flagged)

1. **`SuspendCue` correctness under re-entry** (Medium). `CueActivated` fires *after* `ActivateCue` stamps; `Enter()` then calls `SuspendCue` which must un-stamp already-stamped cue roots. If a scope push happens between (a menu opens during Alt-hold), the new stamp must also see `_cueSuppressed`. Mitigate: gate `StampCueRoot` on `_cueSuppressed` (not just `ActivateCue`), and have `SuspendCue` sweep `_cueRoots` clear. Covered by test 2. This is the only behavioral change to `AccessKeyManager` cue writes — must be mutation-verified.
2. **Parked-level generation races** (Medium). Drilling a minimized-ribbon tab floats a band realizing over ≥1 frames; a fast Esc-then-redrill must orphan the stale parked build. Mitigate: `_levelGeneration` guard on `CompletePendingLayout`, exactly the `Ribbon._floatGeneration` pattern. Risk is timing-dependent — needs the float/collapse tests (5, 6) plus an adversarial audit (project convention: green tests miss real bugs here).
3. **Dropdown-open level push depends on popup-surface existence** (Medium). The pushed level over an opened `BarSplitButton` dropdown can't `TranslateToScreen` its items until the popup surface exists (`SurfaceForElement` returns null pre-open). Mitigate: park the badge build to `CompletePendingLayout` and skip entries whose `TranslateToScreen` still resolves to no surface, retrying next frame (bounded). This is the one v1 leg that exercises the async-surface park; if it proves fragile, the whole dropdown-drill can degrade to v2 with the ribbon L0–L2 still shipping.
4. **QAT / density reflow badge staleness** (Low). A QAT collapse or density demotion mid-armed-level moves targets; badges lag one frame (accepted per Risk 2 in the proposal). Bounded by `CompletePendingKeyTipLayout` running every frame.
5. **Kitty last-column mouse / no-op** — irrelevant to KeyTips (keyboard-only), but the ND23 gate itself is only manually verifiable on a real Kitty terminal (per CLAUDE.md) — the AlwaysVisible fallback path is the sole headless-unverifiable behavior. Documented, not a code risk.

The riskiest single edit is the `SuspendCue`/cue-stamp guard (Risk 1) — the only touch to `AccessKeyManager`'s proven cue-write path. Everything else (badge surface, FSM, derivation, drill, the PreProcessInput subscription) is additive and isolated to `Cursorial.UI.Bars` plus no-op-by-default seams.