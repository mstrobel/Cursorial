# Default-theme gallery adoption — foundation spec (tranches 1 + 2)

**Status: RATIFIED (2026-06-14).** Sign-off decisions: Q1 terse naming = yes; Q2 light `--hover` = **nudge to
`#bfc0c6`**; Q3 resting-fill reversal = yes; Q4 §7 scope = confirmed (thumb hover/drag deferred);
**Q6 check/radio focus = in-box caret, NOT reverse-video** (refinement, 2026-06-14): a CheckBox/RadioButton
stretches to its (vertical) StackPanel's width, so a reverse-video focus fill spans the whole row like a
selection bar instead of cueing the box. Check/radio therefore keep the Task-B caret-in-box focus indicator
(code-driven off `:focus-visible` in `ToggleButton`); they still get the hover fill + disabled muted ink.
**Buttons/RepeatButton/ToggleButton keep reverse-video focus** (they are fill-blocks). This is a deliberate
deviation from the gallery's `.rev` checkbox focus.

**Q5 reverse-video mechanism = explicit brush-pairs** — at color tiers the `:focus`/`:pressed`/`:selected`/
`:disabled` rules set explicit `Background`/`Foreground` brush pairs (focus = `TextBrush`/`WindowBrush`,
pressed = `AccentBrush`/`OnAccentBrush`, selected = `SelectionBrush`, disabled =
`DisabledBackgroundBrush`/`DisabledForegroundBrush`) for pixel-exact gallery fidelity; NoColor visibility comes
from **`caps-nocolor`-gated rules** that layer `Inverse` (focus/selected/pressed), `Inverse+Bold`
(pressed/default + focused-list-item) and `Faint` (disabled) on top, since the brush values resolve to
`Colors.Default` under NoColor. This supersedes §2's "attribute budget *replaces* fills" framing — at color
tiers the fills are real; the attributes are the NoColor *layer*, not the primary mechanism. This is a
staging spec, not the design doc. It reconciles the
Tokyo-Night style gallery (`default-theme-gallery-final.html`, the visual oracle) against the engine and
pins the *foundation* work the roadmap ratified: tranche 1 (token spine + design-doc amendment) and
tranche 2 (reflavor the shipped controls + rewrite the coupled oracle rows). The forward catalog
(tranche 3) rides each control as it lands; this spec does not author it.

Produced by a draft + adversarial-critique design panel; §9 records the reconciliations the critique forced.

---

## 0. The two layers

The gallery is **(a)** an 18-role semantic token palette and **(b)** ~117 per-control keys that resolve to
those tokens. Our `(ThemeBase, ColorDepth)` `ThemeDictionaries` spine already runs this exact two-layer
indirection (control themes `SetResource` into `ThemeKeys`), so the spine absorbs it with **no structural
change**. The work is *content + state rules*, not new infrastructure.

---

## 1. Role-token palette (the spine)

**Naming: terse token-mirroring** — one `Theme.<Role>Brush` constant per role, matching the shipped
`ThemeKeys.cs` convention (`SurfaceBrush`, not `PageBackgroundBrush`). Only five legacy keys constrain us
(`SurfaceBrush`/`TextBrush`/`AccentBrush`/`ObscuredOverlayBrush`/`AccessKeyIndicatorBrush` + the 3 glyph
carriers); every other spine key is new and adopts this scheme.

| `ThemeKeys` constant | token | dark RGB | light RGB | ansi16 D/L | NoColor |
|---|---|---|---|---|---|
| `Theme.WindowBrush` | `--bg` | `#0d0f18` | `#e6e7ec` | 0 / 15 | Default (Inverse *target*) |
| `Theme.SurfaceBrush` | `--surface` | `#24283b` | `#cbccd1` | 0 / 7 | Default |
| `Theme.PanelBrush` | `--panel` | `#222639` | `#e9e9ed` | 0 / 7 | Default |
| `Theme.WellBrush` | `--well` | `#16161e` | `#f6f6f8` ⚠ | 0 / 15 | Underline |
| `Theme.SelectionBrush` | `--sel` | `#33467c` | `#a8aecb` | 8 / 8 | Inverse |
| `Theme.HoverBrush` | `--hover` | `#414868` | `#bfc0c6` (nudged) | 8 / 8 | Underline |
| `Theme.TextBrush` | `--text` | `#c0caf5` | `#343b58` | 15 / 0 | Default (Inverse *source*) |
| `Theme.TextDimBrush` | `--text-dim` | `#a9b1d6` | `#565a6e` | 7 / 8 | Default |
| `Theme.MutedBrush` | `--muted` | `#565f89` | `#9699a3` | 8 / 8 | Faint |
| `Theme.FaintBrush` | `--faint` | `#414868` | `#c4c5cc` | 8 / 7 | Faint |
| `Theme.DisabledBackgroundBrush` | `--disabled-bg` | `#1f2335` | `#dcdde2` | 0 / 7 | Faint (whole control) |
| `Theme.DisabledForegroundBrush` | `--muted` | `#565f89` | `#9699a3` | 8 / 8 | Faint |
| `Theme.AccentBrush` | `--accent` | `#7aa2f7` | `#34548a` | 12 / 4 | Inverse / Underline |
| `Theme.Accent2Brush` | `--accent-2` | `#7dcfff` | `#0f4b6e` | 14 / 6 | Underline (+Bold) |
| `Theme.OnAccentBrush` | `--on-accent` | `#0d0f18` | `#e9e9ed` | 15† / 15 | Default |
| `Theme.GreenBrush` | `--green` | `#9ece6a` | `#485e30` | 2 / 2 | Default+Bold (glyph) |
| `Theme.AmberBrush` | `--amber` | `#e0af68` | `#8f5e15` | 3 / 3 | Default+Bold (glyph) |
| `Theme.RedBrush` | `--red` | `#f7768e` | `#8c4351` | 9 / 1 | Bold (glyph) |
| `Theme.PurpleBrush` | `--purple` | `#bb9af7` | `#5a3e8e` | 13 / 5 | Default / Bold |

RGB values are **verbatim from the gallery** except the two ⚠ legibility nudges in §1.1. The ansi16 indices
are hand-picked for *role distinguishability under reverse-video* (the quantizer's nearest-match collapses
the surface family onto one index and kills the swaps): `--text`/`--bg` pinned to the palette extremes
(0↔15, a 21:1 focus swap), `--accent`/`--on-accent` to real blue (not gray) so accent-reverse reads as a
*colored* reverse, resting fills→0 vs interactive fills→8 (the dark 0/8 split re-creates the surface-vs-hover
lift), and status hues kept true (green 2, amber 3, red 9/1, purple 13/5, cyan 14/6). Served at
Truecolor/Ansi256 by **descent only** — RGB stays canonical, ansi16 is the hand-tuned floor.

**† `--on-accent` ansi16 dark = 15 (white), not 0 (black).** Black-on-bright-blue (idx 0 on idx 12) drops to
~2.44:1 on common pure-blue palettes (VGA/PuTTY/Windows console). White-on-bright-blue is ~5–8:1. This is the
*pressed/default* button text — a primary action — so it takes white at 16 colors. (At RGB, black-on-`#7aa2f7`
is ~6.5:1 and stays; the split is tier-local, which is exactly what hand-picked tiers are for.)

### 1.1 Legibility deviations from gallery RGB (⚠ — your call, §9-Q2)

- **`--hover` light `#cbccd1` → recommend `#bfc0c6`.** The gallery's light `--hover` is *byte-identical to
  `--surface`*, so a hovered control has **1.0:1 hover-fill contrast at every tier including truecolor** — the
  hover is invisible as a fill in light mode. This hits the shipped Button **now**. The nudge (a step toward
  `--sel`) gives a faint-but-nonzero lift. *Alternative:* keep verbatim and make light-mode hover lean on the
  Underline attribute / OSC 22 cursor at all tiers (state it, don't imply a fill that isn't there).
- **`--well` light `#f6f6f8` is *lighter* than `--surface` `#cbccd1`** (a "well" should recess = darker; cf.
  dark `--well #16161e` < surface). Only affects the **future** TextBox, so I'd **defer** this nudge to when
  the text family lands — flagging it only so it's on record.

### 1.2 Recorded, accepted sub-floor contrasts (no change)

- **Disabled `--muted` on `--disabled-bg` = 2.1:1 light / 2.51:1 dark** (below 3:1 even at RGB). Disabled text
  is WCAG-exempt; this is a deliberate "de-emphasized" choice. Recorded, not fixed.
- **Dark `--faint` == `--hover` (`#414868`, gallery source collision):** a hovered scrollbar thumb does not
  lift from a faint track by fill at any tier; the thumb stays distinguishable by its solid glyph, and drag
  uses accent (12). Accepted (and moot until thumb hover/drag is wired — see §7).

---

## 2. NoColor attribute model

Budget = `{Inverse, Underline, Bold, Faint}` + the glyph layer + `Colors.Default`. Allocation by *state*:

- **Reverse-video → `Inverse`:** pick-focus, text-selection, selected pick-item.
- **Focused pick-item → `Inverse + Bold` (unconditional).** *(critique fix)* Plain `Inverse` is reserved for
  *selection*; the focused row is always `Inverse+Bold` — so in a list showing a selected row *and* a
  separately-focused row simultaneously (the gallery ListBox), the two don't collapse: selected=Inverse,
  focused=Inverse+Bold, focused+selected=Inverse+Bold, hover=Underline — all distinct.
- **Pressed/default (accent-reverse) → `Inverse + Bold`** (distinct from plain focus by the Bold delta).
- **Soft highlight → `Underline`:** text-field focus (+caret), hover, hyperlinks (hover-link adds Bold).
- **De-emphasis → `Faint`:** disabled (whole control), placeholder, inactive track / empty progress segment.
- **Status/emphasis → `Bold` + glyph:** success/warning/error/paused/indeterminate are distinguished by
  **glyph** (`✓ ⚠ ✗ ‖ ▪`), lifted with Bold; danger item, emphasis label, pressed-slider thumb → Bold.

**Knowingly lost in NoColor** (none load-bearing for operability, all recorded): normal-vs-visited link
(both Underline); secondary text de-emphasis (`--text-dim` → Default, Faint reserved for disabled);
hover-vs-text-focus (both Underline — different modality, never the same cell). **NoColor × caps-ascii
caveat** *(critique fix)*: the status/file glyphs are load-bearing in NoColor; confirm caps-ascii and NoColor
are independent axes and the `✓/⚠/✗` distinctions survive (or add a Bold backstop) in the ascii∩nocolor corner.

---

## 3. Retirements (default look only; survive as opt-in chrome)

- **`Theme.FocusPen` — retire as the focus mechanism.** Focus is reverse-video / well+caret, not a ring.
  The `^:focus { BorderPen = FocusPen }` child rules in `ButtonTheme`/`ToggleButtonTheme` become
  reverse-video `Foreground`/`Background` setters. Keep a deprecated alias for one release.
- **`Theme.BorderPen` — demote** from a universal resting key to opt-in chrome. The spine stops wiring
  `BorderPenProperty` on every control; a control's extent is its fill. Survives for `Border`/GroupBox/
  Expander/Window chrome and apps that re-add a bordered look.
- **`DefaultPen` (`Pens.Double` `^:default` weight bump) — retire.** With no resting border there's nothing
  to thicken; `IsDefault` is re-expressed cell-faithfully as the `▸ … ◂` gutter brackets (+ optionally an
  accent fill) — TBD when authored.

---

## 4. Resting-fill reversal (opinionated default-look change — §9-Q3)

Today the spine leaves `Control.Background` **unset** (the WPF transparent default). The cell-faithful model
defines a control's extent by a **solid fill**, so the BuiltIn themes gain a resting `Background` setter:

- **Button/RepeatButton/ToggleButton → `Theme.SurfaceBrush`.**
- **CheckBox/RadioButton → stay transparent** (the gallery's normal toggle fill *is* `--bg`, the page
  itself; only their glyph/label ink is themed).
- **ScrollBar Track → `Theme.PanelBrush`, thumb → `Theme.FaintBrush`** (new fill wiring — see §7).
- **Label/TextBlock/Border → stay transparent** (content primitives must not paint a block behind text).

Armed at `StyleLayer.ControlTheme` (below `LocalValue`), so an explicit consumer `Background` still wins;
opt back out with `Background="{x:Null}"`. **Risk:** a control on a non-matching custom background now paints
an opaque `--surface` rectangle where it used to be see-through — a visible behavior change. Under NoColor,
`SurfaceBrush → Colors.Default`, so monochrome keeps the old transparent look automatically.

---

## 5. Key taxonomy

**Convention:** `Theme.<Control><Slot><State>` — e.g. `Theme.ButtonBackgroundFocus`,
`Theme.ScrollThumbNormal`. Base/shared keys keep the `<Role>Brush` form (§1). The **full ~117-key set is the
gallery's `KEYS` table** (its "copy as XAML" output is the authoring template); the spine role tokens are the
*resolution targets*, authored as the per-`(base,tier)` brush values, not as separate alias entries. Control
themes `SetResource` against the role tokens directly for the common case; per-control keys are introduced
where a control needs to diverge, and land with their controls (tranche 3).

The **shipped-control authoring subset** (what tranche 2 actually wires) is §7.

---

## 6. Tranche 1 — design-doc amendment

Lands **before** the oracle rewrites so the matrices stay the oracle of record. Seven passages + one new
subsection + the deviation rewrite:

1. **§11.8** — demote `BorderPen`/`FocusPen` from the spine to opt-in chrome; re-point the spine at the
   fill/foreground/glyph role tokens; **re-target the R2 re-skin proof from border ink to a fill/foreground
   token.** ⚠ **Correction the critique forced:** R2 is **landed**, not pending (C99 proves a live re-skin
   today). The amendment frames the work as *"add the gallery palette + reverse-video state rules on top of
   the already-wired R2 spine"* — the current spine wires only Foreground + resting BorderPen against a
   generic dark/light palette (`#1E1E1E`/`#66D9EF`); adoption repopulates `AddTierPalette` with the 18 tokens
   and adds the per-control fill/focus state setters.
2. **§11.2** — swap the illustrative `<Pen x:Key="Theme.BorderPen">` ThemeDictionary entry for fill/foreground
   brush entries (`Theme.AccentBrush` + `Theme.WellBrush`).
3. **§12.4** — rewrite the nullity-escalation hot-path example: reverse-video focus is a pure paint-only
   `AffectsRender` flip with no geometry (not an instance of nullity-escalation at all); keep
   `BorderPen`/`Border.Title` as the conditional-geometry exemplar. Add a clause noting the migration from
   today's `BorderPen→FocusPen` Heavy escalation.
4. **§12.7** — rewrite the "default-theme vocabulary" sentence to fill-bounded / no-borders / no-rings; focus
   = reverse-video (pick) / well+caret (text); buttons/items/tabs are 1-row, content at row 0; keep
   `DrawTitledBox`/`DrawBox` + the demoted pens as opt-in GroupBox/Expander/Window primitives. Add
   self-contained focus-look pins to the `ButtonBase` + text-family rows.
5. **Doc line ~473** *(critique fix — was missed)* — the styling-engine `:focus → Bold/Inverse/underline,
   :pressed → Pens.Heavy` line: rewrite to `:focus → reverse-video (pick) / well+caret (text),
   :pressed → reverse-video in accent`.
6. **§12.9** — deviation rewrite: reverse-video / well+caret focus; no rings; no border-weight escalation;
   folds in the pinned **text-input blinking i-beam caret** (`CursorShape.BlinkingBar`) deviation. (Retain
   "pen weight = glyph family" under the opt-in-chrome clause rather than deleting it.)
7. **New §11.8a** — normative "cell-faithful theme conventions" subsection: the conventions, the role-token
   table (mapped to `ThemeKeys`), and the per-control key-taxonomy reference, all citing the gallery as the
   source artifact.

---

## 7. Tranche 2 — shipped-control reflavor (honest scope)

⚠ *Not pure resource flips* — most of this is **new state rules + small control plumbing**, not just value
changes. Honest per-control breakdown:

| Control | Tranche-2 work | Notes |
|---|---|---|
| **Button / RepeatButton** | resting `Background`→Surface; `^:pointerover`→Hover; `^:focus`→reverse pair; `^:pressed`→accent pair; `^:disabled`→disabled pair; replace `^:focus` BorderPen rule; re-express `^:default` | all states are live pseudo-classes; `:pressed` reverse rule is **net-new** (no pressed bg/fg rule today) |
| **ToggleButton / CheckBox / Radio** | glyph state-brush (checked→Green, indeterminate→Amber); `^:focus` reverse or keep code-driven caret; `^:disabled`→Muted/Faint | **`ToggleGlyph.Render` needs a state-driven glyph brush** (reads only `Owner.Foreground` today) — small control change |
| **ScrollBar** | Track fill→Panel; thumb resting fill→Faint; arrow ink→Muted, `:pointerover`→Accent | **Track has no fill mechanism today** (draws a BorderPen rail) and `ThumbBrush` exists but is unwired — new control wiring |
| **Label / TextBlock** | foreground role tokens (Text / Accent emphasis / Muted disabled) | pure resource; emphasis is a caller foreground choice |
| **Border** | panel-bearing surfaces → Panel fill | the GroupBox path (`Border.Title`) keeps its opt-in pen |

**Deferred out of tranche 2** *(critique fix — these are NOT skinnable now):* **scrollbar thumb hover/drag**
needs a `:dragging` pseudo-class/`InteractionState` bit (none exists) **and** a thumb hit-region or distinct
`Thumb` element (the thumb is just `█` cells the single `Track` paints, so `:pointerover` lands on the whole
track). Ship resting thumb/track fill now; thumb hover/drag rides a small future state-plumbing task.

---

## 8. Oracle-row rewrites (tranche 2, ~14 rows)

- **`Section05_VariantFlipReSkin` C113–C119 (all 7)** — near-total rewrite. `BorderColor()`/`IsBoxDrawing()`
  helpers and the focus-border-accent proof (C119) have no analog. Re-skin proof moves to a fill/foreground
  token; focus proof asserts reverse-video, not an accent border.
- **`Phase5EndToEndTests`** — `ThemedTree` (`GetRowText(1)` "content on the inner row" + `rows: 3`) and the
  button-click `TranslateToWindow(2,1)` both assume a 3-row bordered button → 1-row fill-bounded.
- **`CaretFocusIndicatorTests.CheckBox_PointerFocus`** — `SendClick(1,3)` assumes the 3-row button offsets the
  CheckBox to row 3 → row 1.
- **`Section04` C95/C98/C99** + **`Section09` C168–C172** — rescope: `BorderPen`/`FocusPen` survive as opt-in
  (the Border-primitive tests stand with a scope note; C95's "both pens resolve" relaxes).

The styling/binding engines and the **style-matrix are untouched** (they pin selectors/frames on synthetic
properties — zero bordered assertions).

---

## 9. Open decisions for sign-off

- **Q1 — naming:** terse token-mirroring (`Theme.WindowBrush`/`PanelBrush`/`SelectionBrush`/`OnAccentBrush`/…).
  *Recommend: yes* (matches shipped `ThemeKeys.cs`).
- **Q2 — gallery RGB verbatim vs the light `--hover` nudge** (`#cbccd1`→`#bfc0c6`). The gallery value makes
  light-mode hover invisible as a fill at *every* tier. *Recommend: nudge* (defer the `--well` nudge to TextBox).
- **Q3 — resting-fill reversal:** controls gain a `--surface` resting fill (opt-out via `{x:Null}`).
  *Recommend: yes* (it's the gallery's core "fill bounds the control" premise; flagged as an opinionated
  default-look change).
- **Q4 — scope confirmation:** tranche 2 includes the small control changes in §7 (ToggleGlyph state brush,
  ScrollBar track/thumb fill), with thumb hover/drag deferred. *Confirm acceptable.*

### Reconciliations the critique forced (already applied above)

1. Doc R2-status corrected (landed, not pending) — §6.1. 2. One naming scheme across all drafts — §1/§5.
3. Focused-pick = `Inverse+Bold` unconditional (NoColor list collapse) — §2. 4. Dark `--on-accent` ansi16 →
white for pressed — §1†. 5. Light `--hover`==`--surface` flagged as an all-tier defect — §1.1. 6. Scrollbar
thumb hover/drag de-scoped (no `:dragging`/thumb element) — §7. 7. Missed doc line ~473 added — §6.5.
8. Disabled sub-floor + `--surface-border` dangling token recorded/dropped — §1.2/§3.
