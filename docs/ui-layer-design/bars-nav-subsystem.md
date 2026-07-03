# Keyboard-navigation subsystem — Cursorial.UI core focus + Cursorial.UI.Bars

**Status:** design locked (2026-06-30), pending implementation. Matrix-first per project cadence — the matrix
amendments in §7 land **before** any engine code. Authored from two converging design passes (three parallel
pillar investigations + the `wf_e43dd200-e93` workflow), reconciled with the user's locked decisions below.

This is a focused subsystem on top of the shipped P1–P9 UI engine. It does **three** things the user co-designed,
plus the foundation they share.

## 0. Scope

Three pillars + one shared foundation. **Out of scope** (user decision 2026-06-30): a dedicated "focus-the-bar"
shortcut and F6-style cycling *between* toolbars — Tab-Once-per-toolbar is good enough; revisit only if usability
proves lacking or a multi-toolbar surface (Ribbon QAT) needs it.

| Pillar | One line |
|---|---|
| **Foundation** | Entering *any* focus scope (Tab/Directional) lands on the scope's remembered focus, not its first element. |
| **P1 — ListBox** | ListBox becomes a focus scope so Tab-in lands on the selected/last item, not item 0 (no accidental reselect). |
| **P2 — chevron/popup** | The Toolbar overflow chevron is keyboard-drivable: Space/Down opens+enters, Up/Down move in/out, stays open while focus is on chevron OR popup. |
| **P3 — Menu** | Menus are returning focus scopes; Escape collapses the chain and returns focus to the outer scope's prior element. |

### Locked decisions (user, 2026-06-30)

1. **Foundation breadth = generalize (the full rework).** Entry-resolution applies to *all* focus scopes, not
   just `Once` containers. **This re-baselines the WPF-pinned rows N118–126** (plain-Tab-between-scopes), recorded
   as a deliberate `DEV` deviation in the input-matrix ND ledger.
2. **Prime ListBox scope-memory from `SelectedIndex`** so a never-keyboard-focused list still Tab-ins to its
   selected row (additive).
3. **Menu Escape = whole-chain collapse** (the user's `OnPopupClosed: EscapeKey→CloseMenuChain` design), with the
   **access-key menu-mode two-Esc model preserved**: in menu mode the *first* Esc only clears the cue (focus + any
   open submenu stay), the *second* Esc (cue down) collapses the chain + returns focus. Going up *one* level is the
   Left-arrow, not Esc.
4. **No cue-up chain-collapse path** (follows from #3). `AccessKeyManager`'s Esc-consume is **untouched** (N177
   preserved); the menu owns only the cue-*down* Esc. No `IMainMenu.OnExitMenuMode` is needed.
5. **No-color focus cue reach** — see §5 (one open sub-decision: `:is(ButtonBase)` over-reach vs. the CheckBox/
   RadioButton exclusion).

---

## 1. The keystone — generalized scope-entry resolution

Both passes verified the framework already writes the data this needs; the navigator just never reads it for the
non-`Once` case.

- **Memory is already recorded.** `FocusManager.MoveFocusCore` (~`FocusManager.cs:615`) does
  `GetFocusScope(target).SetValue(FocusedElementProperty, target)` on *every* focus change — to the **nearest**
  scope. `OnWindowActivated` (~`:361`) already restores root-scope memory the same way.
- **The `Once` ladder already resolves it.** `ResolveOnceEntry` (`FocusNavigator.cs:194`) is the proven
  `GetFocusedElement(scope) → first-eligible → self` chain (with the `MaxOnceEntryDepth` recursion cap). The
  Toolbar's correct return is produced by it (Toolbar is `Once`); `IsFocusScope` only makes the bar *own* its
  memory and serve as the `FindReturningScope` barrier.

### The change (decision ①=B)

Generalize that ladder to every focus scope at the navigator's single entry chokepoint:

1. **`Stop.EntryScope`** — add `UIElement? EntryScope` to the `Stop` struct (`FocusNavigator.cs:12`). Stamp it
   during `CollectInto` (~`:248`) by threading a `currentScope` parameter down the recursion: on descending into a
   child, `var childScope = FocusManager.GetIsFocusScope(child) ? child : currentScope;`. The tab container is the
   implicit scope root (`currentScope: null` at the top).
2. **`ResolveScopeEntry(scope, forward, depth)`** — refactor `ResolveOnceEntry` into this shared, direction-aware
   ladder (same validate → `IsWithin` → first-tab-stop → self chain). `ResolveOnceEntry` becomes a thin
   `forward:true` caller — **behavior-preserving for `Once`** (N127/N128/N129 stay green, unmutated).
3. **Cross-scope redirect at the two return sites** — `NextTabStop` (~`:84`), `FirstOrLastTabStop` (~`:112`), and
   `NextDirectional` (~`:394`) route through a new `ResolveEntryAcrossScopes(target, currentScope, forward)`:
   - if `target.IsOnceContainer` → `ResolveOnceEntry(target.Element)` (the one ladder; never twice);
   - else if `target.EntryScope is {} ts && !ReferenceEquals(ts, currentScope)` and `ts` has valid memory within
     it → `ResolveScopeEntry(ts, forward)`;
   - else → `target.Element` (raw).
4. **Trap-prevention gate** — when the target's `EntryScope == currentScope` (an **intra-scope** Tab/arrow), return
   the raw element. You must be able to move *through* a scope's members without snapping back to memory.
5. **No double-redirect** — a host that is `Once` **and** `IsFocusScope` (the ListBox items panel, P1) resolves
   through the single `Once` ladder; its `IsFocusScope` mark exists only so memory lands there.

**Files:** `Cursorial.UI/Input/FocusNavigator.cs` (all changes). `FocusManager.cs` — no code change expected
(`GetFocusScope`/`GetFocusedElement`/`IsValidFocusTarget`/`GetIsFocusScope` are the reused reads).

**Pinned-row impact:** N118–126 (plain Tab between sibling scopes) move from "first element" to "remembered focus."
Amended matrix-first (§7), tagged `DEV` with rationale in the ND ledger. N127–129 (Once) and N113/N114
(scope-memory recording / detach-clear) are untouched by construction.

---

## 2. Pillar 1 — ListBox lands on the selected item

- **Mark the items panel a focus scope** (not the ListBox root — write/read-target alignment: `GetFocusScope(item)`
  and `GetFocusedElement(panel)` must be the same element). In `ControlThemes.cs:~215`
  (`VirtualizingItemsPanelTemplate`), beside the existing `KeyboardNavigation.TabNavigation = Once`, add
  `FocusManager.IsFocusScope = true`. Then item focus records on the panel — exactly where the `Once` ladder reads.
- **Prime memory from selection** (decision ②) — in `SelectingItemsControl` (`PushSelectedProperties` ~`:191` /
  `OnModelSelectionChanged` ~`:176`): when no item currently holds focus, set the items-host scope-memory to the
  container for `SelectedIndex`. Without this, a never-focused list with a selection still Tab-ins to item 0.
- **Result:** Tab-in lands on the selected/last item via a plain `Focus()` (the `Once` ladder), which does **not**
  run selection-follows-focus — so Tab-in no longer mutates the selection. Arrow-driven select-follows-focus inside
  the list is unchanged.

**Files:** `Cursorial.UI/Themes/ControlThemes.cs` (+ XAML overlay twin if present),
`Cursorial.UI/Controls/SelectingItemsControl.cs`.

---

## 3. Pillar 2 — chevron ⇄ overflow popup (Toolbar-local; no engine change)

The chevron is **already** arrow-reachable as the right-end ring member once `IsTabStop=true` (it's a visual
descendant in the shared Grid; the right-edge geometry scores cleanly under `DirectionalNavigation=Cycle`). New
logic lives entirely in `Toolbar.OnKeyDown` + the bars theme:

- **Open + enter** — chevron-focused + `Space`/`DownArrow` (match the modifier-free `Key.Character " "` form, per
  ND10; exclude Ctrl+Space): `SetCurrentValue(IsOverflowOpenProperty, true)` then focus the first focusable child of
  `PART_OverflowHost` with `Focus(Directional)`. The popup surface attaches **synchronously** on `IsOpen=true`
  (input dispatches with topology unlocked — `WindowManager._topologyLocked` is set only during S6 surface
  iteration), exactly as `MenuItem.OpenSubmenuWithFocus` relies on. **Fallback:** if the open is deferred
  (`DeferIfLocked`), park a one-shot and complete it from `Popup.Opened` (the `CompletePendingActivationFocus`
  idiom, locally scoped — not via `FocusManager._pendingActivationRoot`).
- **Intra-popup nav** — `KeyboardNavigation.DirectionalNavigation = Cycle` on `PART_OverflowHost` (theme), so
  Up/Down between overflow items is free directional scoring on the popup surface.
- **Boundary out (Up from first item → chevron)** — explicit in `Toolbar.OnKeyDown`. Directional scoring **cannot**
  cross the chevron-surface ↔ popup-surface boundary; but the overflow items remain **logical** children of the
  Toolbar, so their key events bubble back through the logical route to `Toolbar.OnKeyDown`. When the focused
  element is the first focusable child of `_overflowHost` and key is `UpArrow`, move focus to `_chevron`.
- **Stay-open-while-focus-on-either, close-when-leaves-both** — a focus-changed handler (the Toolbar is on the
  logical route of both the chevron and the popup band): if `IsOverflowOpen` and **neither**
  `_chevron.IsKeyboardFocusWithin` **nor** `_overflowHost.IsKeyboardFocusWithin`, close via `SetCurrentValue`. The
  retaining-scope barrier stays correct: when focus has already left both, the popup no longer holds keyboard focus,
  so `Popup.CloseCore`'s `Child.IsKeyboardFocusWithin` guard means **no focus yank** on close. Light-dismiss +
  Escape (closes one level, returns to chevron) are unchanged and funnel through the same guarded `IsOverflowOpen`.
- **Focus cue** — chevron color look flips `:focus` → `:focus-visible` (keyboard-only) + a no-color cue (§5).

**Files:** `Cursorial.UI.Bars/Toolbar.cs`, `Cursorial.UI.Bars/Themes/CursorialBarsTheme.cs`. Subscribe/unsubscribe
the focus handler in the existing `WireOverflow`/`OnTemplateDetaching`/`OnDetachedFromTree` hooks (mirror the
`Click`/`Closed` unhook to avoid leaks).

---

## 4. Pillar 3 — Menu as a returning focus scope

- **Make `Menu` mirror the Toolbar** — static ctor: `IsFocusScope = true` + `RetainsFocus = false`. Then
  `FindReturningScope` resolves the `Menu` as the non-retaining scope, and `RestoreRetainedFocus` returns to
  `GetFocusedElement(outer scope)` — the element focused before menu entry. Because `Menu` is now a focus scope,
  focus moves onto menu items record memory on the `Menu`, never clobbering the outer (window-root) scope's memory.
- **Escape ownership (decision ③ + ④):**
  - **Cue UP (menu mode):** `AccessKeyManager.OnPreStageKeyDown` (~`AccessKeyManager.cs:315`) is **unchanged** —
    the first Esc clears the sticky cue, is consumed (`DispatchedHandled`), and the focused element never sees it
    (N177). Focus + any open submenu **stay**.
  - **Cue DOWN:** the menu owns Esc. `Menu.OnKeyDown`: `case Key.Escape when IsKeyboardFocusWithin: CloseMenuChain()`
    (the Toolbar's unhandled-Escape pattern — `Popup.OnKeyDown` consumes Esc on its own surface first, so the Menu
    only sees an unhandled Esc once focus is on a bar header). `MenuItem.OnPopupClosed`: reason-aware —
    `if (e.Reason is EscapeKey or LightDismiss) CloseMenuChain(); else SetSubmenuOpen(false)`. `CloseMenuChain()`
    collapses the whole chain, then `RestoreRetainedFocus(anchor, Restore)` returns to the outer scope (the general
    machinery, not ad-hoc). Re-entrancy is self-terminating (the inner `SetSubmenuOpen(false)` round-trips through
    `OnPopupClosed` with reason `Programmatic` → the `else` branch, idempotent).
  - **Rationale (user):** terminal menus aren't deep; going up one level is the **Left-arrow**, so Esc = "leave
    entirely." This is the deliberate change to C6.27 (was: Esc closes one level). Re-baselined in §7.
- **`TopLevelMenu` + hover-focus gate** — add an internal `MenuItem.TopLevelMenu` that walks the logical-parent
  chain to the **terminal** `Menu`/`ContextMenu` (crosses submenu `Popup`s correctly). `MenuItem.OnMouseEnter`'s
  trailing `Focus()` becomes `if (TopLevelMenu is { IsKeyboardFocusWithin: true }) Focus();` — hover takes focus
  only when the user is already keyboard-driving the chain (highlight via `RefreshHighlight()` is unchanged, so
  pure-mouse hover still highlights). Note: the bare `Focus()` resolves to `Programmatic`, which (correctly) does
  **not** clear the sticky cue.
- **`ContextMenu`** — the existing **W4** popup-restore (`Popup.CloseCore` ~`:194`) already returns focus to the
  right-clicked trigger; let it own the context return (`RestoreRetainedFocus` no-ops there — no non-retaining
  scope on the surface-rooted chain). **Recommended:** also mark `ContextMenu` `IsFocusScope=true` so its item
  focus never clobbers the outer window-root memory. Do **not** set `RetainsFocus=false` on `ContextMenu`.

**Files:** `Cursorial.UI/Controls/Menu.cs`, `MenuItem.cs`, `ContextMenu.cs`. `AccessKeyManager.cs` —
**unchanged**. `FocusManager.cs` — no change (`RestoreRetainedFocus` reused).

---

## 5. No-color focus cue (decision ⑤ — one open sub-decision)

UX-review finding #4 (`:focus`→`:focus-visible`) + finding #3 (bar buttons get no state feedback on `caps-nocolor`).
The chevron's color look flips to `:focus-visible` (§3). For the no-color reverse-video cue
(`CapsNoColorInteractiveInverse`, `CursorialThemeStyles.cs:75`):

- The chevron is a plain `Button` → **already** matched by `.caps-nocolor Button:focus`.
- `BarButton` (`: ButtonBase`) and `BarToggleButton` (`: ToggleButton`) are **missed** — the rule uses exact-type
  `Button`/`RepeatButton`/`ToggleButton` selectors (finding #3: type tokens are exact-type).

The user proposed broadening to `:is(ButtonBase)`. The engine supports it (`CapsNoColorDisabledFaint` already uses
`.caps-nocolor :is(ButtonBase):disabled`). **Catch:** `:is(ButtonBase)` also matches CheckBox/RadioButton, which
this rule **deliberately excludes** (in-box caret focus, not a row-spanning reverse fill), and `:not(...)` is fenced
off by design — so the carve-out can't live in one selector.

**Options (pick one):**
- **(A)** Broaden the core rule to `:is(ButtonBase):focus`/`:pressed` — bar buttons covered, but CheckBox/
  RadioButton now reverse-fill on focus/press (loses the documented exclusion).
- **(B, recommended)** Keep the core rule's exact families (preserving the CB/RB exclusion); add a **bars-local**
  rule in `CursorialBarsTheme` for `BarButton`/`BarToggleButton` (`:focus-visible`). The chevron is already covered
  as a `Button`. The bars assembly can name its own types; the core theme can't.

Recommendation **(B)** — honors "bar buttons get the cue for free" without regressing CheckBox/RadioButton.
(Whether to also flip the *core* button rule `:focus`→`:focus-visible` for all controls is a separate, broader
theme decision NOT bundled here.)

---

## 6. Cross-pillar coherence

- **Two independent passes converged** on this architecture (three pillar agents + the workflow), incl. the pins:
  mark the *items host* not the ListBox root; the chevron↔popup hop is explicit not scored (cross-surface); keep
  the `AccessKeyManager` Esc-consume rather than relaxing the dispatcher (N177).
- **Foundation × all three pillars:** Toolbar + ListBox panel are `Once` → resolve via the single `Once` ladder
  (no double-redirect). `Menu` is entered by Alt/F10/click (not Tab), so the generalized *Tab-entry* redirect
  doesn't apply to menu entry; the menu *return* uses `RestoreRetainedFocus` (the same mechanism the Toolbar uses).
- **Chevron retaining-scope barrier × entering the popup:** the chevron stays a retaining focus scope
  (`FindReturningScope` barrier), so opening/entering the popup never trips the toolbar's auto-return; popup entry
  is an explicit `Toolbar.OnKeyDown` move, not a scored navigation.

---

## 7. Matrix amendments (land first — amendment-before-code)

- **`input-matrix.md`** — re-baseline **N118–126** (plain Tab between sibling scopes → remembered focus); add an
  ND ledger row recording the `DEV` deviation from WPF with rationale (the user's "weakness in the nav logic"
  rework). Add new `Once`-scope-memory + generalized-entry rows (entry uses memory over first; memory cleared on
  detach falls back to first; intra-scope move keeps outer memory — the trap-prevention gate). N127–129, N113/N114
  unchanged.
- **`control-matrix-p9.md`** — ListBox rows: Tab-in lands on selected (no reselect); never-entered list → item 0;
  after-arrow remembers cursor; Multiple-mode lands on lead; Tab-out exits the whole list; panel is a focus scope.
  Menu rows: Menu is a returning scope; cue-up first-Esc clears cue (focus stays); cue-down Esc collapses chain +
  returns to outer scope; **C6.27 re-baselined** to whole-chain-collapse-on-Esc + a menu-mode two-Esc variant;
  `TopLevelMenu` resolution across the submenu Popup boundary; hover-focus gate; ContextMenu W4 return + no-op
  RestoreRetainedFocus.
- **Bars tests** (`Cursorial.UI.Bars.Tests/ToolbarFocusTests.cs`) — chevron ring membership; Space/Down opens+enters;
  Up returns to chevron, stays open; focus-leaves-both closes (no yank); Esc-in-popup → chevron (barrier intact);
  `:focus-visible` keyboard-only; no-color Inverse cue; existing `RetainingFocusScopeChild_IsExemptFromReturn` +
  `ArrowKey_NavigatesWithinBar` stay green.

---

## 8. Implementation sequencing

1. **Foundation** (`FocusNavigator`) + its matrix re-baseline — the keystone; everything else builds on it.
2. **P1 ListBox** (panel scope + selection priming) — smallest, proves the foundation.
3. **P2 chevron/popup** — Toolbar-local, no engine risk.
4. **P3 Menu** — the most cross-cutting (focus scope + Esc ownership + hover gate); do last so the foundation +
   `RestoreRetainedFocus` interplay is already proven.

Each pillar: matrix rows first → implement → headless tests → **adversarial audit (Workflow)** before commit →
commit my files only (no trailer) → push origin + softserve.

### Risks
- The N118–126 re-baseline is the highest-risk change (touches the shared navigator + WPF-pinned rows). Land the
  foundation + its matrix amendment in isolation, full-suite green, before the pillars.
- Auto-close on un-overflow while focus is in the popup (P2): focus must land on a valid element as items
  re-parent back to the row (live instances move bands, not detach — verify).
- C6.27 behavior change (P3): re-baseline the row deliberately; don't let it read as a regression.
