# Fork B — oracle-pinned style matrix (selectors, specificity, arming, activation, diagnostics)

Status: **normative test specification**, authored 2026-06-11 *before any Fork B engine code exists* (design doc §14 P3; the repo's matrix-first discipline, mirroring `precedence-matrix.md`, `layout-matrix.md`, and `input-matrix.md`). Every numbered row below becomes exactly one xUnit `[Fact]`/`[Theory]` in `Cursorial.UI.Tests` (test authoring contract at the end). The styling engine is written *to* this matrix; a red row is an engine bug unless a PR amends this file first.

Canonical semantics sources, in precedence order: `docs/ui-layer-design.md` §3 (including the engine amendment ledger B1–B19) + §0 invariants + §13 resolutions + §14 P3 **over** `docs/ui-layer-design/proposal-styling-hybrid.md`. Places the proposal is superseded by the doc and this matrix pins the doc's side:

- ① **Selector lists (`,`) are supported** (doc §3.1: "each member compiles to its own rule") — the proposal's "no `,` lists" cut is superseded.
- ② **`IStyleValueSink.OnResourcesChanged` is deleted** (ledger B14) — resource delivery is S7's per-node registry; P3 builds no sweep entry point.
- ③ The element base type is **`UIElement`** (`Cursorial.UI`), not the proposal's `Element`; the SGR record is referred to as `CellStyle` in framework source (doc §1.3).
- ④ `Style.Enter`/`Style.Exit` edge-action collections exist with the B5 names; at P3 `IStyleEdgeAction` is a **declared seam** (inversion 3) — invocation edges are pinned here, the storyboard vocabulary is P8.
- ⑤ Capability classes stamp from the **negotiated** snapshot at P3 (inversion 6 — scaffolding; P5 re-points the color tier to `ActualThemeVariant.Tier`).

**Phase 3 scope boundary** (rows are written inside it): **no `When`/`DataCondition`** (needs `BindingOperations.Watch` — P4, inversion 2; the sort-key field reservation for `When` specificity is pinned now). **[P4 amendment, 2026-06-12]** `When`/`DataCondition` landed at P4 once `BindingOperations.Watch` existed: `Style.When` is a `WhenCollection` of `DataCondition`s, AND-composed into each `CompiledRule` (with `BasedOn` + nesting-parent conditions), each condition counting 1 classLike toward specificity (SD5, now realized — a `When`-guarded style beats its unguarded base). The styling engine arms one watch per condition when a rule structurally matches (parallel to ancestor-state requirements), gates `ComputeSatisfied` on every condition being met (unset ⇒ unmet), and reconciles through the same queued/fixpoint Phase-2 path on a watch delivery; watcher lifetime = armed rule lifetime (B16 — live across deactivation, disposed at disarm/detach). The normative `When` rows live in **`binding-matrix.md` §13 (B157–B168 + the B162a–B162h styling-integration rows)** — the binding engine owns the data half and the integration. The P3 style-matrix stays the frozen structural/pseudo oracle. **No resource- or binding-valued setters** (`ResourceReference` is S7/P5, `BindingBase` is S2/P4 — the types do not exist at P3; setter values are constants or `UIProperty.UnsetValue`), **no ControlTheme/Template/Theme channel content** (layers 0–2 land P5; their key ordering is pinned at the unit level now), **no template instantiation** (P5) — but the **template barrier** (invariant 5) is enforced and tested now via manually stamped `TemplatedParent`. `:pointerover` capability honesty (no motion ⇒ the bit never sets) is S3's P2-tested behavior, not re-tested here.

**[Amendment, 2026-06-16 — the `BindingPriority.Template` lane]** A new Fork A priority lane `BindingPriority.Template` (wire 150) sits *below the entire Style slot* and carries values a control/data template **authors on its parts** (literals, `{TemplateBinding}`s, `SetResourceReference`s); see `precedence-matrix.md` §20 / PD24. Consequence for styling: **every** Style-slot rule — including one armed at the `StyleLayer.Template` sort-key layer (a rule from a control template's own `Styles`) — now overrides a templated part's authored value, because Style (priority 100) beats Template (priority 150). **`StyleLayer.Template` (a sort-key layer *inside* the Style slot) and `BindingPriority.Template` (a lane *below* the Style slot) are different mechanisms that share a word** — the style matrix's layer ordering (ControlTheme < Template < Theme < App < Scoped < Explicit) is unchanged and entirely within Style priority. Within-lane provenance is reported by `ValueSource.Kind` (PD25): a Style-lane winner reports `StyleSetter` or `StyleWhen` (the `When`-guarded-rule distinction).

Stage mapping (the P3 implementation stages; rows for a later stage may stay unimplemented — not red — until that stage opens, but every row is binding from now):

| Stage | Sections | Delivers |
|---|---|---|
| **Y1 — grammar & keys** | §§1–2 | `Selector` model + `Parse`/`ToString` + the NO-fence + `Selectors` fluent builders; `StyleSortKey` construction, packing, specificity counting. |
| **Y2 — style object & seal** | §3 | `Style`/`Setter`/`Styles` collections, `BasedOn` flatten, `Children` nesting composition, seal-on-attach, seal-time validation/conversion errors. |
| **Y3 — matcher & store** | §§4–6 | `StyleIndex` + scope-chain candidate gathering, structural match, armed `ActivationFrame`s, the template barrier, one-frame-per-rule store integration, cookie batch retraction, the conformance-kit run. |
| **Y4 — state plumbing & dynamics** | §§7–9 | `IInteractionStateObserver` consumption, `PseudoClassMapping`, `ClassSet`/`PseudoClassSet` guards, fixpoint + loop diagnostics, class/name/`Styles` re-match dynamics, capability classes. |
| **Y5 — hooks, lifecycle, diagnostics, perf** | §§10–13 | `IStyleFrameHooks` wiring, attach/detach lifecycle + the edge-action seam, `StyleDiagnostics.Explain`/`MatchedRules` (the acceptance test), allocation contracts + the motion-storm re-assert. |

## 0. Conventions

### 0.1 Namespaces and placement

Styling types live in **`Cursorial.UI`** (doc §1.3 — `Style`, `Setter`, `Selector`, `Selectors`, `Styles`, `ClassSet`, `PseudoClassSet`, `PseudoClassMapping`, `StyleDiagnostics`, `IStyleEdgeAction`, `ISelectorTypeResolver`, `SelectorParseException`), source under `Cursorial.UI/Styling/`. Framework source uses `using CellStyle = Cursorial.Output.Style;` where both styles are needed. Tests live under `Cursorial.UI.Tests/StyleMatrix/`, namespace `Cursorial.Tests.UI.StyleMatrix`.

### 0.2 Fixture

| Symbol | Meaning |
|---|---|
| `host` | `UITestHost.Create()` — 80×24, `TestCapabilities.KittyTruecolor` unless stated; `app = host.Application`. Trees are attached via `host.ShowRoot(Root)`; rows follow mutations with `RunFrame()` unless asserting unit-level synchronous behavior. |
| `Widget` | `UIElement` subclass measuring rigid (`Widget(w×h)`); `FancyWidget : Widget`; `Card : UIElement` (unrelated branch). `Pane` = `StackPanel` (containers). Default tree: `Root` (StackPanel) → `paneA` (StackPanel, depth 1) → leaves `a`, `b` (Widgets, depth 2); `paneB` → `c` where stated. |
| `P`, `Q` | `StyledProperty<int>` on `Widget`, default `0`, `PropertyEffects.AffectsRender` (`Q` is the second property for batch rows) |
| `Pi` | `StyledProperty<int>` on `Widget`, default `0`, `inherits: true` |
| `Pd` | `StyledProperty<double>` on `Widget`, default `0.0` (conversion rows) |
| `Pcmp` | `StyledProperty<string?>` on `Widget`, metadata `Comparer = OrdinalIgnoreCase` |
| `Pro`/`Kro` | read-only `StyledProperty<int>` + its `UIPropertyKey<int>` on `Widget` |
| `Pb`, `Pq` | `StyledProperty<bool>` / `StyledProperty<int>` on `Widget` for `PseudoClassMapping` rows (mappings are process-global registrations — rows use per-row property instances via a registration helper so registrations stay idempotent across test classes) |
| `R(sel){P=v, …}` | a `Style` with `Selector = Selector.Parse(sel, resolver)` and the listed constant setters |
| `S_app` / `S(e)` / `e.Style` | the three P3 channels: `app.Styles` (App(3)), `e.Styles` (Scoped(4), scope owner `e`), explicit (Explicit(5)) |
| `arm(e)` | `StyleDiagnostics.MatchedRules(e)` — ordered strongest-first; entries carry (selector text, layer, key, `IsActive`) |
| `explain(e, P)` | `StyleDiagnostics.Explain(e, P)` rendered as lines (SD13) |
| `flip(e, Bit, on)` | the element's protected `SetInteractionState` via a test shim on `Widget`; `batch { … }` = a `BeginInteractionUpdate` scope around the enclosed flips |
| `pseudo(e, ":x", on)` | protected `PseudoClasses.Set` via the shim; `cls(e) ± "x"` = `Classes.Add`/`Remove` |
| `key(layer, n, c, t, d, o)` | the internal `StyleSortKey.Create(layer, names, classLike, types, scopeDepth, order)` factory (tests have `InternalsVisibleTo`) |
| `eff` / `src` / `notify` / `silent` / `L(v)` / `CV` / `SCV(v)` / `H` | exactly as `precedence-matrix.md` §0.2 (store observers subscribed on the element; `notify(old→new, Pr)` = one delivery carrying that priority) |
| `resolver` | an `ISelectorTypeResolver` mapping the fixture type names; grammar rows pass it explicitly except where testing the SD1 default |
| "0 B" | `GC.GetAllocatedBytesForCurrentThread()` delta of zero after warm-up, single-threaded (repo norm) |

### 0.3 Pseudo-class name table (binding)

| `InteractionState` bit | Pseudo-class |
|---|---|
| `PointerOver` | `:pointerover` |
| `Pressed` | `:pressed` |
| `Focused` | `:focus` |
| `FocusWithin` | `:focus-within` |
| `FocusVisible` | `:focus-visible` |
| `ActiveWindow` | `:active-window` |
| `AccessKeyCue` | `:access-keys` |
| `Disabled` | `:disabled` |
| `ModalAttention` | `:modal-attention` |

`:checked`/`:indeterminate`/`:selected` are control-registered names (P5+); at P3 they are ordinary custom pseudo-classes with no special handling.

### 0.4 Oracle tags

`AV` = Avalonia 11 behavior (primary styling oracle); `CSS` = the CSS specification semantics (Selectors L4 / Cascade — grammar and specificity oracle); `WPF` = WPF behavior; `PIN` = Cursorial pin with no direct parent analog (this matrix is the decision record); `DEV` = deliberate deviation from a parent system, always with rationale (inline or via the SD ledger).

### 0.5 Global rules restated (rows assert instances of these)

1. **The activation predicate**: a rule is active on an element iff (structural selector matches) ∧ (all required pseudo-classes set) — the `When` conjunct joins at P4. Structural matching is Phase 1 (rare); pseudo flips are Phase 2 (hot, element-local, allocation-free).
2. **One slot, one key**: all style values enter Fork A at `BindingPriority.Style`, one `ValueFrame` per active rule, ordered within-slot by the packed `StyleSortKey`; larger keys win; equal keys → later-armed wins (store M38).
3. **Layer beats specificity** (doc §3.4 — documented divergence from CSS/WPF/AV): `ControlTheme(0) < Template(1) < Theme(2) < App(3) < Scoped(4, deeper wins) < Explicit(5)`, packed above every specificity field.
4. **Retraction is store-owned** (invariant 4): deactivation removes the frame by cookie and the store promotes — nothing ever sets an old value back.
5. **Template barrier** (invariant 5): rules never match elements with `TemplatedParent != null` except through `/template/`; the engine skips such subjects before candidate scanning.
6. **Styling never touches `Scene`/`CellBuffer`** (invariant 2): restyle reaches pixels only through `PropertyEffects` routed by S1 (asserted via the §13 zone rows).

### 0.6 Pinned decisions made by this matrix (SD ledger)

Each goes beyond — but never against — the canonical doc text; deliberate and binding until amended.

- **SD1 — type tokens and the default resolver.** A type token is a simple (undotted) identifier — or, per SD25, a namespace-qualified `prefix|Local` — resolved through `ISelectorTypeResolver`. The default resolver (used when the argument is null) resolves simple names of element types known to the framework: types with `UIProperty` registrations in `UIPropertyRegistry` plus the exported `UIElement` types of `Cursorial.UI`/`Cursorial.UI.Controls`, exposed as `Selector.DefaultTypeResolver`. An unresolvable token is a parse error naming the token; an ambiguous simple name (two known types) is a parse error listing the candidate full names. Fork C supplies its own (namespace-aware) resolver at P6 — the parse API shape is shared.
- **SD2 — lexing and case.** Identifiers are `[A-Za-z_][A-Za-z0-9_-]*` (no leading digit or leading `-`). Whitespace (spaces/tabs) is insignificant around combinators and `,`; a whitespace run between compounds is the descendant combinator. **All matching is ordinal case-sensitive** — type tokens (CLR names), classes, names, and pseudo-classes. DEV from CSS's ASCII case-insensitive pseudo-class matching; rationale: one interned ordinal comparison everywhere, no folding tables on the hot path.
- **SD3 — parse errors.** `Selector.Parse` failures throw `SelectorParseException` (derives `FormatException`) carrying `Position` (zero-based char offset into the original text) and a message naming the offending token. The NO-fence constructs — sibling combinators (`+`, `~`), `:not(`, `:nth-*`, `:first-child`/`:last-child`, attribute selectors (`[`) — produce errors that name the construct and state it is **unsupported by design** (doc §3.10), pointing at the construct's position. `Parse(null)` throws `ArgumentNullException`.
- **SD4 — canonical `ToString` and the round-trip law.** `ToString` emits: compounds joined by `" > "` (child), `" "` (descendant), `" /template/ "` (template hop); within a compound: `^`, then the type (`Widget` or `:is(Widget)`), then simples **in declaration order** (`.class`, `#name`, `:pseudo` interleaved as written, not sorted); list members joined by `", "`. Laws: `Parse(s).ToString() == canonical(s)` and `Parse(x.ToString())` is structurally equal to `x`.
- **SD5 — sort-key construction.** The engine owns an internal factory `StyleSortKey.Create(StyleLayer layer, int names, int classLike, int types, int scopeDepth, int order)` packing `[layer:3][names:8][classLike:10][types:8][scopeDepth:8][order:27]` (most-significant first; doc §3.4). Each count **saturates** at its field maximum (255/1023/255/255/2²⁷−1) — never overflows into a neighbor. Specificity is counted over the **full flattened selector** (all compounds, both sides of `/template/`, nesting-composed parents): each `#name` → names; each `.class` and `:pseudo` → classLike (each `When` `DataCondition` will count 1 classLike at P4 — reserved now); each type or `:is(type)` → types. `order` is the rule's declaration index within its scope, assigned by a depth-first walk of the attached `Styles` collection (a style's own rule, then its `Children` rules in order, then the next style); selector-list members take consecutive indices left-to-right. Worked example (bit-exact): `Create(Scoped, names: 0, classLike: 2, types: 1, scopeDepth: 1, order: 0)` = `(4UL << 61) | (2UL << 43) | (1UL << 35) | (1UL << 27)` = `0x8000100808000000`.
- **SD6 — `scopeDepth`.** For Scoped(4) rules, `scopeDepth` = the scope owner's depth on the styling-parent chain (the shown root = 0), clamped at 255 — deeper scopes produce larger keys, so the nearer scope wins. All other layers carry `scopeDepth = 0`.
- **SD7 — the styling-parent chain.** Combinator traversal (child, descendant) and scope-chain gathering walk `LogicalParent ?? VisualParent` — the same fallback the tree already uses for value inheritance (`UIElement` doc). One parent notion for styling, pinned to the tree's.
- **SD8 — template-barrier exactness.** The barrier tests the **subject only**: an element with `TemplatedParent != null` is skipped before candidate scanning for every rule whose compiled chain contains no `/template/` combinator. Rules containing `/template/` are exempt and evaluate normally; the combinator, at its chain position, requires the current element's `TemplatedParent` to be non-null and to match the left compound, then continues the walk **from the templated parent** (each `/template/` crosses exactly one stamp edge). Combinators to the right of the hop walk styling parents inside the template subtree; compounds to the left of the templated control continue the ordinary walk. **Explicit `UIElement.Style` arms regardless of `TemplatedParent`** — it is element-addressed, not selector-matched; the barrier governs selector matching only.
- **SD9 — P3 setter-value vocabulary.** A setter value is a constant or `UIProperty.UnsetValue`. Constants are validated and converted **once at seal**: exact/assignable types pass through; `IConvertible` primitives convert via invariant-culture `Convert.ChangeType`; `null` is legal for reference/nullable property types; anything else is a seal error naming (style, rule index, property). An `UnsetValue` setter compiles to a **valueless entry** (Fork A ledger A8 — contributes nothing while active; the P4+ resource-pulse semantics reuse the same entry shape). `ResourceReference`/`BindingBase` setter values are P5/P4 vocabulary — the types do not exist at P3.
- **SD10 — read-only properties.** A setter targeting a read-only `UIProperty` is a seal error naming (style, rule index, property) — caught at seal rather than at `AddFrame` (PD14 made earlier and nameable).
- **SD11 — pseudo-class write guards.** `PseudoClassSet.Set` requires a `':'`-prefixed name (`ArgumentException` otherwise) and throws `InvalidOperationException` for any name in the §0.3 table (`InteractionState`-backed bits flow only through `SetInteractionState` — ledger B9; the DirectProperty-backing half of the B9 sanction is S8/P5 review territory). `ClassSet.Add`/`Remove`/`Replace` reject `':'`-prefixed names with `ArgumentException` — interaction classes are unreachable from app code. `ClassSet` entries are interned; `Add`/`Remove` return whether the set changed; no-change operations are restyle-free.
- **SD12 — activation timing.** Structural events (attach, `Classes`/`Name` mutation, `Style`/`Styles` assignment or mutation) re-match **synchronously at the mutation site**. Interaction-state deliveries (the observer's post-commit per-element notifications) apply **synchronously at batch commit**. Flips raised *during* application (re-entrancy via `PseudoClassMapping`) queue and drain to fixpoint before the outermost application returns — generation cap 16, then `InvalidOperationException` with the cycle trace; an A→B→A toggle within one drain trips the DEBUG style-loop diagnostic naming the rule pair. Flips raised during the frame's layout/render phases queue, surface via `HasPendingActivations`, and apply at the next `FlushPendingActivations` (B1). Styling engages when an element is attached to a shown root's tree; detached elements match nothing.
- **SD13 — the `Explain` line format (the §3.9 acceptance contract).** One line per contributor, strongest first; format: `<Property> = <value> <- <Layer>(<n>) "<selector>" names=<n> classLike=<n> types=<n> depth=<n> order=<n> key=0x<16-hex> -- <status>` where `<value>` is the entry value's invariant-culture `ToString()` (`(unset)` for valueless entries), `<selector>` is the flattened canonical selector text (`(explicit)` for selector-less explicit styles), the hex is the full packed key (16 digits, upper-case), and `<status>` is `winning` or `shadowed`. When a non-Style lane holds the effective value, the first line is `<Property> = <value> <- <Lane>` (`LocalValue`/`Animation`/`Inherited`/`Default`) and every style line is `shadowed`. Explain renders **active** contributors (winning + shadowed) plus the stronger-lane line; armed-inactive rules appear in `MatchedRules`, not `Explain`.
- **SD14 — capability classes at P3** (inversion 6 — negotiated snapshot, scaffolding for P5's effective-tier re-point). Stamped on the **shown root only**, as ordinary `ClassSet` entries, at visual-root attachment: exactly one of `caps-truecolor`/`caps-ansi256`/`caps-ansi16`/`caps-nocolor` from `Output.Color.Depth`; `caps-motion` iff `Input.Mouse.Motion`; `caps-kitty-keyboard` iff `Input.Protocol.KittyKeyboardProtocol`; `caps-unicode` always (`caps-ascii` is **reserved, never stamped at P3** — no negotiated glyph-capability source exists; recorded deferral, revisited with S7's glyph-resource tiers). `OnCapabilitiesChanged` records the snapshot only (B2); renegotiation re-stamps by replacing **only the `caps-*` subset** (app-added classes preserved), riding the ordinary class-change re-match path.
- **SD15 — P3 lifecycle surface.** §13.2/B11 pause-resume applies to S7 `ResourceSubscription`s (none exist at P3) and B16 watcher lifetime to `When` watchers (P4). At P3 the only per-element styling state is `ElementStyleState` + armed frames: deactivation edges retract frames; permanent detach retracts and **drops the state entirely**; reattach rebuilds from scratch. P4/P5 layer their pause/dispose edges onto these hooks without changing them.
- **SD16 — the edge-action seam at P3.** `IStyleEdgeAction { void OnActivated(UIElement scope); void OnRetracted(UIElement scope); }` with `scope` = the matched element. The engine invokes `Enter` actions on the inactive→active edge and `Exit` actions on the active→inactive edge (including detach-driven retraction), each in rule-document order, on **every** edge. P3 ships no built-in actions and no exception guard — S5 adds the `(igniter, scope)` instancing registry and the no-throw contract at P8 (B5); a P3 action that throws propagates (undefined-but-not-pinned; do not write tests against it).
- **SD17 — style placement rules.** A selector-less, key-less style added to a `Styles` collection is an attach-time `InvalidOperationException` naming the style (its legal homes are `UIElement.Style` and the keyed-theme channel at P5). `UIElement.Style` accepts selector-less or `^`-rooted styles only — any other selector is an assignment-time `InvalidOperationException`. `Style.Children` selectors must start with `^` (seal error naming style + rule index). `^` elsewhere (non-leftmost, or in a top-level `Styles` selector) is invalid — non-leftmost is a parse error; a `^`-rooted style added to a `Styles` collection is an attach-time error.
- **SD18 — arm-time truth.** `UnmetCount` is initialized from current state at arm: a rule whose requirements are already satisfied (bit already set, mapped property already true) activates within the same arm pass — one notification per affected property, no inactive flicker.
- **SD19 — `Styles` single ownership.** A `Styles` collection instance attaches to one owner at a time; attaching an already-attached instance throws `InvalidOperationException`. A sealed `Style` instance is immutable and freely shareable across collections — each scope compiles its own rule set (own order indices).
- **SD20 — unknown pseudo-classes.** Any identifier after `:` that is not in §0.3 parses as a custom pseudo-class and matches against the element's custom pseudo set — never a parse or seal error (control-registered vocabularies arrive at P5; the grammar is open by design).
- **SD21 — `Styles` mutation = coarse re-match with identity diff.** Mutating an attached `Styles` collection raises the internal `StylesInvalidated` hook (the hot-reload tier); the owning scope re-matches its subtree. Armed frames diff by **rule identity + sort key + scope owner** (same sealed `Style` ⇒ same `CompiledRule` instances): survivors keep their frames, cookies, and activation state — silent for their properties; only added/removed rules touch the store. **Amendment (correctness review finding 4):** "survivor" narrows to *key-stable* survivors. A `Styles` mutation that shifts a rule's declaration-order index (an insert/remove **before** existing styles) re-keys every later rule, so those rules retract + re-arm under the new key. Values still hand over silently (adds-before-removals ordering — S133 mechanics), but an active re-keyed rule fires its **Exit then Enter** edge actions despite "surviving" structurally, and at P4 its `When` watcher would churn. The common hot-reload case (append at end) preserves keys and is fully silent; mid-list replacement is the churning case. (At P4, re-examine whether to make the survivor diff order-index-stable.)
- **SD22 — the production observer slot.** P3 wires the styling engine as the default `UIApplication.InteractionStateObserver`. The slot remains assignable (the P2 contract): replacing it is a test-only act that disconnects interaction-driven styling; P2's existing sink-installing tests keep working unchanged.
- **SD23 — one-time DEBUG diagnostics.** Four lint surfaces, each emitted once per offending style/rule/scope, DEBUG builds only: ① a rule indexed into the universal bucket (no name/class/type discriminator); ② a rule with more than one ancestor-state compound; ③ the §3.8 hover-parity lint — a style whose flattened rule set contains a `:pointerover`-requiring rule for property set S with no keyboard-focus-parity rule covering any property in S, checked at seal (amended per correctness review finding 6b: "focus parity" is any of `:focus`/`:focus-within`/`:focus-visible` — any keyboard-visible affordance silences the lint, the lenient direction); ④ (amended per correctness review finding 8) an ancestor-state rule whose styling-parent chain exceeds the 64-element placement bitmap, so the engine falls back to a single greedy ancestor binding that may miss an alternative valid placement.
- **SD25 — namespace-qualified type tokens (`prefix|Local`).** A type token may be namespace-qualified with the CSS/Avalonia `|` separator (`:` is reserved for pseudo-classes), recognized **only** in type-token position — bare or inside `:is(...)`, never on a `.class`/`#name`/`:pseudo`; the `|` is otherwise a parse error, and a dangling `prefix|` (no local name) is a parse error at the pipe. The full `prefix|Local` token is passed to `ISelectorTypeResolver.Resolve` and preserved verbatim by `ToString` (round-trip-stable). Resolution is the resolver's responsibility: `Selector.DefaultTypeResolver` matches simple names only and rejects a qualified token (a parse error hinting that a namespace-aware resolver is required); the Fork C loader supplies `XamlSelectorTypeResolver`, which binds `prefix` against the document's **root** xmlns declarations — the top-level-only policy (a non-root xmlns is `CUR2004`, Fork C / xaml-matrix) makes the binding unambiguous — and resolves `Local` through the schema context to the exact CLR type (no simple-name ambiguity). The fluent `Selectors.OfType`/`Is` builders need no qualifier — they already carry the exact CLR type. DEV from CSS namespace selectors (`ns|E`), AV-aligned. Rows S23a–S23e; the loader end-to-end is xaml-matrix.
- **SD24 — structural re-entrancy fence.** SD12 covers re-entrant *flips* (queue + fixpoint). It does **not** cover a re-entrant *structural* mutation: a setter notification fired during an element's own arm pass that mutates that element's selector inputs (`Classes`/`Name`/`Style`/`Styles`) re-enters the engine while the element's armed-frames array is mid-rebuild. Pinned semantics: a structural re-match requested for an element already mid-re-match **defers** (it is **not** run nested against the not-yet-committed frames array — doing so diffs stale state, arms duplicate frames, and lets the outer pass clobber `state.Frames`, orphaning the nested frames in the store, a direct invariant-4 hole). Every structural-mutation entry point opens a structural pass; the outermost pass drains the deferred re-matches to a fixpoint (same generation cap 16, then `InvalidOperationException`) before draining the flip queue. Net effect: each affected element converges to one frame set matching the committed structure; no orphans, full retraction on detach.

---

## 1. Selector grammar — parse, round-trip, the fence (S1–S23)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S1 | — | `Selector.Parse("Widget", resolver)` | single compound, exact-type `Widget`; `ToString() == "Widget"` | AV |
| S2 | — | `Parse(":is(Widget)")` | is-type compound (assignable matching, §4); round-trips `":is(Widget)"` | AV+CSS |
| S3 | — | `Parse(".primary")` | class-only compound; round-trips | AV+CSS |
| S4 | — | `Parse("#save")` | name-only compound; round-trips | AV+CSS |
| S5 | — | `Parse(":focus")` | pseudo-only compound (universal subject — see S69); round-trips | AV+CSS |
| S6 | — | `Parse("Widget.a.b#save:focus:pointerover")` | one compound; simples preserved in declaration order; `ToString()` identical to input | CSS (SD4) |
| S7 | — | `Parse("Pane>Widget")`, `Parse("Pane  >  Widget")`, `Parse("Pane > Widget")` | all three structurally equal (child combinator); canonical `ToString() == "Pane > Widget"` | CSS (SD2/SD4) |
| S8 | — | `Parse("Pane \t  Widget")` | descendant combinator; whitespace run collapses; canonical `"Pane Widget"` | CSS (SD2/SD4) |
| S9 | — | `Parse("Widget /template/ #chrome")` and `Parse("Widget/template/#chrome")` | template combinator, surrounding whitespace optional; canonical `"Widget /template/ #chrome"` | AV (SD4) |
| S10 | — | `Parse("Widget, Card")` | two list members, each its own selector; canonical `"Widget, Card"` | CSS+AV |
| S11 | — | `Parse("Pane > .a, #b:focus")` | members with combinators parse independently; round-trips | CSS |
| S12 | — | `Parse("^:pointerover")` | nesting compound, leftmost; round-trips `"^:pointerover"`; placement legality is seal/attach-time (SD17), not parse-time | AV-adjacent PIN |
| S13 | — | `Parse("Widget ^")` | `SelectorParseException`, `Position` = the index of `^` | PIN (SD3) |
| S14 | — | `Parse(":frobnicate")` | parses — unknown pseudo-classes are custom names (SD20) | AV |
| S15 | malformed corpus | `Parse` each of: `""`, `"   "`, `"Widget >"`, `"> Widget"`, `"A > > B"`, `"Widget."`, `"#"`, `".2col"`, `":is(Widget"`, `"Widget,"`, `null` | one `[Theory]`: each throws `SelectorParseException` with the pinned `Position` (inline table; `null` → `ArgumentNullException`) | PIN (SD3) |
| S16 | fence corpus | `Parse` each of: `"A + B"`, `"A ~ B"`, `":not(.a)"`, `":nth-child(2)"`, `":first-child"`, `"Widget[IsDefault=true]"` | one `[Theory]`: `SelectorParseException` whose message names the construct and states it is unsupported **by design**, `Position` at the construct | PIN (SD3 — the NO-fence, doc §3.10) |
| S17 | resolver returning null | `Parse("Bogus", resolver)` | parse error naming the token `Bogus` + its position | PIN (SD1) |
| S18 | two fixture types named `Chip` registered in different namespaces | `Parse("Chip")` with the **default** resolver | parse error listing both candidate full names | PIN (SD1) |
| S19 | `Widget` has registered properties | `Parse("Widget")` with resolver `null` | resolves via the default resolver (registry-known simple names) | PIN (SD1) |
| S20 | — | `Parse(".Primary")` vs `Parse(".primary")`; `Parse(":FOCUS")` | distinct classes (ordinal); `:FOCUS` is a **custom** pseudo, not an alias of `:focus` | DEV from CSS (SD2 — ordinal everywhere; rationale: interned ordinal matching) |
| S21 | — | `Parse(".col-2")`, `Parse(".a_b")`, `Parse("#x9")` parse; `Parse(".-x")` errors | identifier charset per SD2 (`[Theory]`) | PIN (SD2) |
| S22 | round-trip corpus: ~25 selectors covering every construct (types, `:is`, classes, names, pseudos, all three combinators, `^`, lists, compounds-of-everything) | `Parse(s).ToString() == canonical(s)`; `Parse(x.ToString()) ≡ x` structurally | one `[Theory]` — the mandated §3.9-③ corpus, shared with Fork C's loader at P6 | PIN (SD4) |
| S23 | — | fluent builders: `Selectors.OfType<Widget>()`, `.Is<Card>()`, `.Class`, `.Name`, `.PseudoClass`, `.Child()`, `.Descendant()`, `.Template()`, `Selectors.Nesting()` composed into the S22 corpus shapes | each builder chain structurally equals its parsed equivalent; identical `ToString` (`[Theory]` over builder/string pairs) | AV (`Selectors` kinship) |
| S23a | namespace-aware resolver | `Parse("ui|Widget", resolver)` | exact-type compound resolving the local name in the prefix's namespace; `ToString` preserves `"ui|Widget"` | AV (`prefix\|Type` namespace form; SD25) |
| S23b | namespace-aware resolver | `Parse(":is(ui|Widget)", resolver)` | assignable-type compound; round-trips `":is(ui|Widget)"` | AV (SD25) |
| S23c | namespace-aware resolver | `Parse("ui|Pane > ui|Widget.primary:focus", resolver)` | the `\|` qualifier composes with simples + combinators (confined to the type token); round-trips | AV (SD25) |
| S23d | the **default** (non-namespace-aware) resolver | `Parse("ui|Widget")` | parse error naming the token + a hint that a qualified token needs a namespace-aware resolver (position 0) | PIN (SD25) |
| S23e | — | `Parse(".foo\|bar")` / `Parse("Widget\|")` / `Parse("ui\|")` | the `\|` is invalid outside a type token, and a dangling `\|` (no local name) is a parse error, each at its pinned position (`[Theory]`) | PIN (SD25) |

---

## 2. Specificity & the packed `StyleSortKey` (S24–S38)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S24 | — | `key(Scoped, 0, 2, 1, 1, 0)` | `Packed == 0x8000100808000000` — the SD5 worked example; pins the bit layout `[layer:3][names:8][classLike:10][types:8][scopeDepth:8][order:27]` exactly | PIN (SD5; doc §3.4 made bit-exact) |
| S25 | — | `key(Theme, 0,0,0,0,0)` vs `key(Template, 255, 1023, 255, 255, 2²⁷−1)` | the Theme key is **greater** — layer beats any saturated specificity | DEV from CSS/WPF/AV (doc §3.4 — the documented divergence) |
| S26 | — | adjacent-field dominance: names vs saturated classLike+types+depth+order; classLike vs saturated types+…; types vs depth+order; depth vs order | `[Theory]`: each higher field with count 1 beats full saturation of everything below it | CSS-shaped PIN (SD5) |
| S27 | — | `Create` with names=300, classLike=2000, types=300, scopeDepth=300, order=2²⁸ | each clamps to its field max; no carry into the neighboring field (`[Theory]` per field) | PIN (SD5 saturation) |
| S28 | rule `Widget.primary:pointerover` | compiled components (internal) | types=1, classLike=2 (class + pseudo), names=0 | CSS |
| S29 | rule `Pane.toolbar > Widget.primary` | components | types=2, classLike=2 — counted across **all** compounds | CSS |
| S30 | rules `#save` and `:is(Widget)` | components | names=1 / types=1 — `:is` carries its argument's specificity | CSS (`:is` specificity) |
| S31 | parent `Widget.primary`, child `^:pointerover` | flattened child-rule components | types=1, classLike=2 — nesting composes the parent's counts | CSS (nesting) |
| S32 | rule `Widget.primary /template/ #chrome` | components | types=1, classLike=1, names=1 — both sides of the hop count | PIN (SD5) |
| S33 | one style, selector list `#save, .primary` | per-member keys | member 1: names=1, classLike=0; member 2: classLike=1 — each member carries its own specificity (shared setters) | CSS |
| S34 | one scope: style A; style B with two `Children`; style C with a 2-member list | order indices (internal) | DFS declaration order: A=0, B=1, B-child₁=2, B-child₂=3, C-member₁=4, C-member₂=5 | PIN (SD5) |
| S35 | `.a{P=1}` declared before `.b{P=2}` in one scope; element has both classes | `eff` | `2` — equal specificity, later declaration order wins | CSS+AV |
| S36 | identical rule `.x{P=…}` in `S(paneA)` (owner depth 1, P=1) and `S(a)` (owner depth 2, P=2); `a` matches both | `eff` | `2` — deeper scope wins via `scopeDepth` | AV (scoped styles) (SD6) |
| S37 | same rule shape in `S_app{P=1}` (specificity-boosted: `Widget.x.y.z`) and `S(paneA){P=2}` (bare `.x`) | `eff` | `2` — Scoped(4) beats App(3) regardless of specificity | PIN (doc §3.5; rule 3) |
| S38 | `a.Style = {P=9}` (selector-less) + active scoped rule `Widget.a.b.c#save{P=1}` | `eff` | `9` — Explicit(5) beats Scoped at any specificity | WPF/AV (explicit style wins) |

---

## 3. Style object model & seal (S39–S58)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S39 | — | `new Style(Parse("Widget.a"))`, `new Style("Widget.a", resolver)`, `new Style { Selector = … }` | three construction forms equivalent; `Selector`/`BasedOn`/`Key` are init-only | PIN (doc §3.1) |
| S40 | — | `Seal()` twice; read `IsSealed` | idempotent; `IsSealed` true after the first call | AV+WPF |
| S41 | unsealed style | add to an **attached** `Styles` collection | auto-sealed and armed in the same operation (`IsSealed` true; matching elements styled immediately) | AV-adjacent PIN (doc §3.3 seal-on-attach) |
| S42 | sealed style | mutate `Setters` / `Children` / `Enter` / `Exit` | one `[Theory]`: each mutation throws `InvalidOperationException` | WPF (sealable) |
| S43 | base `{P=1, Q=2}`; derived `Widget` style `{P=3}` with `BasedOn = base` | seal + activate | flatten appends derived after base: `P=3` (later wins within the rule), `Q=2` | WPF (`BasedOn`) |
| S44 | chain `C.BasedOn=B`, `B.BasedOn=A` | seal C + activate | transitive flatten; nearest-derived wins per property | WPF |
| S45 | `A.BasedOn=B; B.BasedOn=A` | `Seal()` | `InvalidOperationException` naming both styles | PIN (doc §3.1 "cycle ⇒") |
| S46 | base selector `Widget` (never attached); derived `Widget:focus` `BasedOn` base | derived rule's key | counts **only the derived selector's** specificity (types=1, classLike=1) — `BasedOn` contributes setters, never specificity | PIN |
| S47 | `Widget.primary` with child `^:pointerover{P=5}`; classed widget | arm + `flip(a, PointerOver, on)` | child compiled to ONE rule (`Widget.primary:pointerover`); activates on the flip | AV (nesting AND-composition) |
| S48 | `Widget` → child `^.primary` → grandchild `^:focus` | composition | transitive: one rule `Widget.primary:focus` | CSS (nesting) |
| S49 | style with a `Children` entry whose selector is `"Card.x"` (no `^`) | `Seal()` | seal error naming (style, rule index) — children must be `^`-rooted | PIN (SD17) |
| S50 | `Widget` style with child `^ /template/ #chrome` | composition | flattens to `Widget /template/ #chrome` (matches per §5) | PIN (doc §3.3) |
| S51 | setter `(Pd, 5)` — int constant on a double property | seal + activate | converted **once at seal** (invariant culture); `eff == 5.0`; activation performs no conversion | PIN (SD9; WPF-adjacent) |
| S52 | setter `(P, "nope")` | `Seal()` | seal error whose message contains the style identity (`Key` ?? selector text), the rule index, and the property name — the §3.3 triple | PIN (doc §3.3) |
| S53 | setter `(P, null)` — non-nullable value type | `Seal()` | seal error naming the triple | PIN (SD9) |
| S54 | setter targeting read-only `Pro` | `Seal()` | seal error naming the triple | WPF (read-only DP spirit) (SD10) |
| S55 | active rule with setter `(P, UIProperty.UnsetValue)` | store state | valueless entry (A8): contributes nothing; `IsSet(P)` false; `src=Default` | AV (SD9) |
| S56 | selector-less, key-less style | add to a `Styles` collection | attach-time `InvalidOperationException` naming the style | PIN (SD17) |
| S57 | selector-less style `{P=9}` | assign as `a.Style` | legal: arms always-active at Explicit(5), zero specificity; `eff=9` | WPF (explicit style) |
| S58 | style with selector `Widget:focus` | assign as `a.Style` | `InvalidOperationException` — explicit styles are selector-less or `^`-rooted | PIN (SD17; doc §3.5) |

---

## 4. Indexing, scope chain, structural matching (S59–S79)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S59 | `R("Widget"){P=1}` in `S_app`; tree holds a `Widget`, a `FancyWidget`, a `Card` | arm sets | the `Widget` armed+active; the `FancyWidget` NOT (bare type = exact CLR type); the `Card` not | AV (bare type is concrete) |
| S60 | `R(":is(Widget)"){P=1}` | arm sets | `Widget` AND `FancyWidget` armed (assignable); `Card` not | AV |
| S61 | `R("Card.primary")`; `a` (a Widget) carries class `primary` | `arm(a)` | empty — type-keyed candidate exclusion: the rule is never evaluated against non-`Card` elements (the index's exact-type/`:is`-chain buckets) | AV (rule-hash; doc §3.3) |
| S62 | `R(".primary"){P=1}`; the class on a `Widget`, a `Card`, a `Pane` | arm sets | every element carrying the class matches, any type | AV+CSS |
| S63 | `R("#save")` | `a.Name = "save"` vs `"Save"` | matches `"save"` only — ordinal case-sensitive | AV (SD2) |
| S64 | `R("Widget.a#x:focus")` | `[Theory]`: all simples present / each one removed in turn | match only when **all** compound simples hold | CSS |
| S65 | `R("Pane > Widget")` | widget as immediate child vs grandchild of a Pane | child combinator = immediate styling parent only | CSS+AV |
| S66 | `R("Pane Widget")` | widget nested ≥2 levels under a Pane | descendant = any ancestor on the styling-parent chain | CSS+AV |
| S67 | leaf attached via `AddVisualChildOnly` (LogicalParent null) under `paneA` | `R("Pane Widget")` | still matches — traversal walks `LogicalParent ?? VisualParent` | PIN (SD7) |
| S68 | tree `Pane → Card₁ → Card₂ → Widget`; rule `"Pane > Card Widget"` | match | matches — Card₂'s parent fails the `Pane >` test but the walk continues to Card₁ (no greedy first-candidate failure) | CSS (backtracking correctness) |
| S69 | `R(":focus")` in `S_app` | arm sets + DEBUG | arms on every element in scope (universal bucket); one-time DEBUG diagnostic | PIN (SD23-①) |
| S70 | rule in `S(paneA)`; elements: `paneA` itself, descendant `a`, `paneB`'s child `c` | arm sets | scope = the owner **self-inclusive** + its subtree; `c` unmatched | AV (styles apply to host + descendants) |
| S71 | `R("Widget:pointerover"){P=5}`; `a` not hovered | `arm(a)`, then `flip(a, PointerOver, on)` | armed + `IsActive == false`, `src=Default`; flip ⇒ active, `eff=5` — the armed-vs-active split, no re-match on the flip | PIN (doc §3.3 Phase 1/2) |
| S72 | `a` already hovered (bit set) | rule arms (class added) | activates **within the arm pass** — `UnmetCount` initialized from current truth; one notify | PIN (SD18) |
| S73 | `R("Widget:focus:pointerover")` | flip each bit alone, then both | requires BOTH bits; activates only with both set | CSS |
| S74 | `R("Pane:focus-within Widget"){P=5}` | arm on widgets; `flip(paneA, FocusWithin, on)` | ancestor-state requirement: the ancestor flip activates the dependent descendants' frames (registered `AncestorDependency`, no subtree scan) | PIN (doc §3.3) |
| S75 | continued from S74 | `flip(paneA, FocusWithin, off)` | retracts; `notify(5→0, Default)` on `a` | PIN |
| S76 | rule with two ancestor-state compounds (`Pane:focus-within Pane:pointerover Widget`) | seal + arm + DEBUG | functional, but one-time DEBUG diagnostic flags >1 ancestor-state compound; each ancestor-state compound is checked **independently** (per-compound position sets, not a joint placement) — exact for one compound, an approximation for >1 (two compounds may share one ancestor; correctness review finding 3) | PIN (SD23-②) |
| S77 | subtree built but not attached | `arm(e)` pre-attach; then attach under the shown root | empty pre-attach; armed post-attach — styling engages on the attach walk | PIN (SD12) |
| S78 | attach a 3-level subtree in one operation | arm sets | every descendant armed by the single attach walk | PIN (doc §3.3 lifecycle) |
| S79 | `R("#a, .b"){P=7}`; element named `a` AND classed `b` | `arm` + notifications | both member-rules armed and active (own keys: names=1 vs classLike=1); `eff=7` with exactly **one** notify — the weaker member's activation is masked-silent (store M37) | CSS+AV |

---

## 5. Template barrier (S80–S88)

All rows stamp `TemplatedParent` manually via `SetTemplatedParent` while detached (the P1 seam) — template instantiation is P5.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S80 | `part` (Widget) stamped `TemplatedParent = card`, attached under `card`; `R("Widget"){P=1}` in `S_app` | `arm(part)` | empty; `src(P)=Default` — the barrier skips the subject before candidate scanning | PIN (invariant 5) |
| S81 | as S80 | `[Theory]` over rule shapes: type, `.class` (part classed), `#name` (part named), `:focus` universal | none arm on the part | PIN (invariant 5) |
| S82 | `R("Card /template/ #chrome"){P=5}`; part named `chrome`, `TemplatedParent = card` (a `Card`) | arm | armed + active; `eff=5` — `/template/` is the sanctioned crossing | AV (`/template/`) |
| S83 | as S82 but the part's `TemplatedParent` is a `Widget` | arm | no match — the left compound must match the templated parent | AV |
| S84 | free element named `chrome`, `TemplatedParent == null` | `R("Card /template/ #chrome")` | no match — the hop requires a non-null `TemplatedParent` | AV |
| S85 | nested stamping: `card` ← `pane` (`TemplatedParent=card`) ← `inner` (`TemplatedParent=pane`) | `R("Card /template/ Pane /template/ Widget")` vs `R("Card /template/ Widget")` | two-hop rule matches `inner`; the single-hop rule does NOT — exactly one stamp edge per `/template/` | PIN (SD8; doc §3.1 "exactly one") |
| S86 | template-side structure: `card` ← `pane` (stamped) ← `inner` (stamped `TemplatedParent=card`, visual child of `pane`) | `R("Card /template/ Pane > Widget")` | matches `inner` — combinators right of the hop walk styling parents inside the template subtree | PIN (SD8) |
| S87 | `card` sits under `paneA` | `R("Pane > Card /template/ #chrome")` | matches the part — compounds left of the templated control continue the ordinary walk | PIN (SD8) |
| S88 | part with `TemplatedParent != null` | `part.Style = {P=9}` (selector-less explicit) | arms and applies — explicit styles are element-addressed; the barrier governs selector matching only | PIN (SD8) |

---

## 6. Activation frames & store integration (S89–S107)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S89 | rule `{P=1, Q=2}` activates | store shape | ONE `ValueFrame` per rule at `BindingPriority.Style` carrying both entries (diagnostics enumeration shows one frame, two entries); one notify per property; `src=Style` for both | PIN (doc §3.6 "one ValueFrame") |
| S90 | fresh element, rule `{P=1}` activates | notification args | `notify(0→1, Style)` — old value, new value, Style priority | AV (M33) |
| S91 | `.a{P=1}` active + `.a:pointerover{P=2}` active (hovered) | `flip(a, PointerOver, off)` | `notify(2→1, Style)` — runner-up promotion in ONE notification; no Default flash, no intermediate value | invariant 4; AV (M39) |
| S92 | single active rule `{P=1}` | deactivate (flip off) | `notify(1→0, Default)` — promotion reports the new winning lane (PD10) | AV (M35) |
| S93 | observer recording every channel through an activate/deactivate cycle | inspect the sequence | no `LocalValue`-priority delivery ever appears — retraction is frame removal + store promotion, never a set-back | invariant 4 PIN |
| S94 | stronger rule active; weaker rule activates (its pseudo flips on) | notifications | silent for the masked property; `GetValue(P, Style)` still resolves the winner | AV (M37) |
| S95 | weaker rule active; stronger rule activates | notifications | `notify(weak→strong, Style)` | AV (M38 spirit) |
| S96 | same property from `S_app{P=1}`, `S(paneA){P=2}`, `a.Style{P=3}` — all active | peel strongest-first: clear `a.Style`, then remove the scoped rule | `eff` 3 → 2 → 1 — Explicit > Scoped > App end-to-end, one promotion notify per peel | PIN (doc §3.4/3.5) |
| S97 | active rule `{P=5}` | `L(9)` then `CV` | local masks (`eff=9`, `src=Local`); clear promotes the style value back: `notify(9→5, Style)` | AV+WPF (M45/M46) |
| S98 | ancestor `L(Pi=5)`; child rule `{Pi=9}` active | deactivate the rule | child: `eff` 9 → 5 with `notify(9→5, Inherited)` — Style sits above Inherited | AV (ladder) |
| S99 | `H.Set(50)` (animation) then a style rule `{P=5}` flips on/off | notifications | both edges silent-masked under Animation; `H.Dispose()` surfaces the style value | AV (Animation > Style, wholesale) |
| S100 | `SCV(7)` then style rule `{P=5}` activates | `eff`/`src` | `5`, `src=Style` — a producer change replaces the current-value overlay (A11 semantics) | AV |
| S101 | winner `{P=5}` + runner-up `{P=5}` (equal values) | deactivate the winner | silent (equal-value promotion); `src` stays `Style` | AV (PD9 within-slot) |
| S102 | rule with 2 setters active | deactivate | both properties retract in one cookie batch; one notify each; no ordering interleave with other elements | PIN (cookie batch retraction) |
| S103 | armed `Widget:pointerover{P=5}` | flip on/off 100× | values correct every cycle; the internal `ElementStyleState.Frames` array identity is stable across all flips — Phase 2 never re-runs Phase 1 | PIN (doc §3.3) |
| S104 | stronger active rule `{P=∅}` (UnsetValue setter) over weaker active `{P=5}` | `eff` | `5` — the valueless entry is skipped within the slot (M41 end-to-end) | AV (A8) |
| S105 | Fork B's frame implementation | subclass the `ValueFrameConformanceKit` with a `CreateFrame` factory producing the engine's style frames | the **entire kit passes** — activation/retraction promotion, `OnEntryChanged` in-place re-emit, within-slot ordering, entry-unset promotion, retraction-is-not-set-back | PIN (§3.9-② mandated; run before the engine wires to real selectors) |
| S106 | `a.Style = Style("^:pointerover"){P=5}` (`^`-rooted explicit) | arm + flip | arms inactive at Explicit(5); hover activates; `eff=5` | AV PIN (SD17) |
| S107 | ONE sealed `Style` instance added to both `S_app` and `S(paneA)` | arm on `a` (matches via both scopes) | legal (sealed styles are shareable, SD19); two armed rules with per-scope order indices; Scoped wins | PIN (SD19) |

---

## 7. Interaction-state & pseudo-class plumbing (S108–S125)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S108 | fresh P3 host | read `app.InteractionStateObserver` | the styling engine is pre-installed as the production observer; the slot remains assignable (replacement = test-only disconnect) | PIN (SD22; input-matrix ND11 fulfilled) |
| S109 | `R("Widget:pointerover"){P=5}`, `P` is `AffectsRender`; pointer off `a` | `move` onto `a` + `RunFrame()` | the rule activates and the rendered cells reflect the styled value in the **same frame** that drained the Move | invariant 1 (P3 exit precursor) |
| S110 | armed interested rule | `batch { flip(a, PointerOver, on); flip(a, PointerOver, off); }` | net-zero batch ⇒ no observer delivery ⇒ no styling work; `silent` on `P` | PIN (ND11 carried) |
| S111 | `R("Widget:focus:focus-visible"){P=5}` | one batch flips `Focused` + `FocusVisible` together | activates once — one delivery per element per batch, the combined delta applied in one pass; exactly one `notify` on `P` | PIN (doc §3.3 batching) |
| S112 | nine rules, one per §0.3 pseudo | `[Theory]` over all 9 `InteractionState` bits: flip each on/off | each bit arms/activates exactly its pinned pseudo-class rule | PIN (§0.3 binding table) |
| S113 | `paneA.IsEnabled = false`; `R("Widget:disabled"){P=5}` | effective-enabled recompute | descendants of `paneA` get `Disabled` pushed (S1 plumbing) ⇒ `:disabled` rules activate on them | PIN (B18; WPF-shaped) |
| S114 | `R("Widget:focus"){P=5}` + `R("Pane:focus-within Widget"){Q=5}` | `focus.SetFocus(a)`, then `SetFocus(c)` | both activate for `a`'s chain; the focus move retracts `a`'s and activates `c`'s — riding the focus transition's one batch | PIN |
| S115 | `PseudoClassMapping.Register<Widget>(Pb, ":on")`; `R("Widget:on"){P=5}` | set `Pb` true / false | pseudo flips via the property-observer bridge; rule activates/retracts | AV (pseudo-class mapping) |
| S116 | mapping registered for `FancyWidget` only | set `Pb` on a plain `Widget` | no flip — mappings apply to `TOwner`-assignable instances only | PIN |
| S117 | `Register<Widget,int>(Pq, v => v switch { 1 => ":one", 2 => ":two", _ => null }, [":one", ":two"])` | transition `Pq` 0→1→2→0 | each transition retires the old class and sets the new in one pass (1: `:one`; 2: `:two` only; 0: neither) | AV (`bool?` `:checked`/`:indeterminate` analog) |
| S118 | duplicate `Register` — same (`TOwner`, property) | second call | `InvalidOperationException` | PIN |
| S119 | `R("Widget:open"){P=5}` | `pseudo(a, ":open", true)`, repeat, then `false` | flips + restyles; the repeated `Set` returns `false` and is restyle-free | AV |
| S120 | — | `pseudo(a, ":pressed", true)` | `InvalidOperationException` — `InteractionState`-backed names flow only through `SetInteractionState` | PIN (B9; SD11) |
| S121 | — | `pseudo(a, "open", true)` (no colon) | `ArgumentException` | PIN (SD11) |
| S122 | — | `cls(a) + ":focus"` | `ArgumentException` — interaction classes unreachable from `Classes` | AV (SD11) |
| S123 | rule X (`Widget.go`) sets mapped `Pb=true`; rule Y (`Widget:on`) exists | `cls(a) + "go"` | X activates ⇒ `Pb` flips `:on` ⇒ Y activates — queued, drained to fixpoint **before the mutation call returns**; both active, values correct | PIN (SD12; doc §3.3 re-entrancy) |
| S124 | constructed oscillation: X's activation flips a bit that retracts X (A→B→A within one drain) | trigger | DEBUG style-loop diagnostic naming the rule pair; the drain still terminates | PIN (doc §3.3) |
| S125 | constructed 16-generation cascade | trigger | `InvalidOperationException` carrying the cycle trace (generation cap 16) | PIN (doc §3.3) |

---

## 8. Re-match dynamics: class, name, style mutations (S126–S139, S181)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S126 | `R(".primary"){P=5}` | `cls(a) + "primary"` | synchronous re-match at the mutation site: armed + active immediately (no frame pump needed); `eff=5` | AV (SD12) |
| S127 | continued | `cls(a) − "primary"` | retract + disarm; `notify(5→0, Default)`; `arm(a)` no longer lists the rule | AV |
| S128 | `a` holds armed rules | `cls(a) + "zzz"` (matches nothing) | rule-identity diff: existing frames survive (internal identity stable); zero notifications | PIN (doc §3.3 frame diff) |
| S129 | rule matched under both old and new class sets | `Classes.Replace(["x","y"])` swapping other classes | one restyle pass; the surviving rule's values never retract-then-reapply (no intermediate notify) | PIN (doc §3.2 `Replace`) |
| S130 | `R("#save"){P=5}` | set `a.Name = "save"` post-attach; later clear it | arms + activates on the rename; retracts on the clear — `Name` changes re-match | AV PIN |
| S131 | `R("Pane.fancy Widget"){P=5}` | `cls(paneA) + "fancy"` | the class is ancestor-interesting ⇒ bounded subtree re-match ⇒ the rule activates on `a` | PIN (doc §3.3 `AncestorInterestingClasses`) |
| S132 | as S131 | `cls(paneA) + "plain"` (not ancestor-interesting) | no subtree re-match — `a`'s arm state untouched (internal frame identity stable) | PIN |
| S133 | `a.Style = s1{P=1}` active | `a.Style = s2{P=2}` | old explicit frames retract, new arm: single promotion per property (`notify(1→2, Style)`, no Default flash) | WPF/AV |
| S134 | as S133 | `a.Style = null` | explicit frames retract; weaker layers promote | WPF |
| S135 | shown tree | `S_app.Add(newStyle)` post-attach | `StylesInvalidated` ⇒ scope-wide re-match; the new rule arms/activates across the tree | AV (hot-reload tier) (SD21) |
| S136 | shown tree | `S_app.Remove(style)` | its rules retract scope-wide; store promotes per element | AV (SD21) |
| S137 | element with armed rules from `S_app` | `S_app.Add` of an **unrelated** style | survivors keep frames + cookies (identity diff) — silent for their properties | PIN (SD21) |
| S138 | a `Styles` instance attached to `paneA` | attach the same instance to `paneB` | `InvalidOperationException` — single ownership | PIN (SD19) |
| S139 | shown tree | `paneA.Styles = new Styles { R(".x"){P=5} }` post-attach | subtree re-match; the rule arms at Scoped(4) with `paneA`'s depth | PIN (doc §3.2) |
| S181 | `R(".primary"){P=9}`, `R(".x"){Q=7}`; observer on `P` adds `cls(a)+"x"` on first change | `cls(a) + "primary"` | the re-entrant structural mutation defers and drains after the outer arm commits: both rules tracked + active (`Q=9`→`7`, `arm(a)` lists 2); `cls(a)−"x"` retracts `Q` to 0 with no orphan | PIN (SD24) |

---

## 9. Capability classes (S140–S146)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S140 | `KittyTruecolor` host | `ShowRoot(Root)` | `Root.Classes` ⊇ {`caps-truecolor`, `caps-motion`, `caps-kitty-keyboard`, `caps-unicode`}; exactly one color-tier class; **no** caps-* classes on any child element | PIN (SD14; inversion 6) |
| S141 | `Ansi16Legacy` host | `ShowRoot` | exactly {`caps-ansi16`, `caps-unicode`} — no motion, no kitty-keyboard, no other tier | PIN (SD14) |
| S142 | constructed snapshots, one per `ColorDepth` | `[Theory]` over NoColor/Ansi16/Ansi256/Truecolor | exactly one of `caps-nocolor`/`caps-ansi16`/`caps-ansi256`/`caps-truecolor` stamped | PIN (SD14) |
| S143 | `R(".caps-truecolor Widget"){P=5}` in `S_app` | run under Kitty host and under Ansi16 host | active under Kitty; not armed under Ansi16 — caps classes are ordinary matchable classes | PIN (doc §3.5/§3.7) |
| S144 | before `ShowRoot` | the startup `OnCapabilitiesChanged` call has run | stamps nothing — `Root.Classes` empty pre-attach (`OnCapabilitiesChanged` records only) | PIN (B2) |
| S145 | shown root carrying app class `brand`; `ScriptRenegotiatedCapabilities(Ansi16Legacy)` | `await app.RenegotiateAsync()` + frame | re-stamp replaces only the `caps-*` subset (`caps-truecolor`→`caps-ansi16`, motion/kitty classes dropped); `brand` preserved; tier-gated rules re-match within the renegotiation tick (the same frame's render reflects it) | PIN (B2/B4 P3 slice; SD14) |
| S146 | detach the shown root; `ShowRoot(newRoot)` | stamp | the new root stamped from the current snapshot | PIN (SD14) |

`caps-ascii` is reserved and never stamped at P3 (SD14 — no negotiated glyph-capability source exists; asserted inside S140/S141's exact-set checks).

---

## 10. Frame-loop hooks (S147–S152)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S147 | fresh P3 app | the frame loop's styling phase slot | non-null — the engine implements `IStyleFrameHooks` and occupies the Phase-3 slot (`FrameSeams` field wired) | PIN (B1) |
| S148 | idle engine | `HasPendingActivations` | `false`; O(1) read | PIN (B1) |
| S149 | a test element whose `MeasureOverride` sets a mapped property (flip raised during layout) | `RunFrame()`; inspect; `RunUntilIdle()` | the flip queues (`HasPendingActivations == true` after the frame); the loop schedules another frame; `RunUntilIdle` converges with the rule applied | PIN (B1; SD12) |
| S150 | hover rule + Move event | one `RunFrame()` | the input-driven flip is applied by Phase 3 of the **same** frame — styled values visible to that frame's layout and render | invariant 1 (B1) |
| S151 | spy `IStyleFrameHooks` wrapping the engine via the seam | `RunFrame()` | the loop calls `FlushPendingActivations` at Phase 3 **and** again after the animation tick (no-op driver at P3), every frame | PIN (B1) |
| S152 | pending activation, no other work | idle guard | the loop does not report idle while `HasPendingActivations` is true; idle returns after the flush | PIN (B1; `RunUntilIdle` convergence — §3.9-⑧) |

---

## 11. Lifecycle & the edge-action seam (S153–S163)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S153 | `R("Widget"){Width=20}` (layout-affecting setter) in `S_app` | `ShowRoot` + first `RunFrame` | arm happens **before the first measure** — the first arranged layout honors the styled width | PIN (B19) |
| S154 | active rules across a subtree | detach the subtree | every element's style frames retracted (store-owned promotion); `src=Default`; `arm()` empty on every detached element | invariant 4 (B19) |
| S155 | recording observer across a 3-level subtree detach | notification order | retractions arrive bottom-up (children before parents), one batch per element | PIN (B19) |
| S156 | detached subtree from S154 | reattach under the shown root | re-match rebuilds: rules re-arm, satisfied rules re-activate, values restored (fresh state per SD15) | PIN (SD15) |
| S157 | element with explicit `Style` active | detach + reattach | explicit frames retract on detach and re-arm on attach (the `Style` property value itself survives — it is element state, not engine state) | PIN |
| S158 | rule with two recording `Enter` actions + two `Exit` actions | activation edge | `OnActivated(element)` invoked on both, in rule-document order | PIN (B5; SD16) |
| S159 | continued | deactivation edge (pseudo off) | `OnRetracted(element)` in rule-document order | PIN (B5; SD16) |
| S160 | active rule with `Exit` actions | detach the element | `Exit` actions run as part of the detach retraction | PIN (SD16) |
| S161 | rule armed but never activated | detach | NO `Exit` action — edges fire only from the active state | PIN (SD16) |
| S162 | hover rule with `Enter`/`Exit` actions | three hover on/off cycles | actions fire on **every** edge (3× each), not once | PIN (SD16) |
| S163 | style with a `:pointerover` rule on `P` and no `:focus` rule covering `P` | `Seal()` (DEBUG) | one-time DEBUG hover-parity lint diagnostic; adding a `:focus` rule on `P` silences it | PIN (doc §3.8 lint; SD23-③) |

---

## 12. Diagnostics (S164–S172)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| S164 | `S(paneA)` (owner depth 1) sole style, sole rule `Widget.primary:pointerover{P=9}`, active on `a` | `explain(a, P)` first line | the EXACT literal: `P = 9 <- Scoped(4) "Widget.primary:pointerover" names=0 classLike=2 types=1 depth=1 order=0 key=0x8000100808000000 -- winning` — **the §3.9/§14 acceptance test**: every winning value's full sort-key derivation in one line | PIN (SD13) |
| S165 | + a weaker active rule on `P` | `explain(a, P)` | second line renders the shadowed setter with its own full derivation, `-- shadowed`; lines ordered strongest-first | PIN (SD13) |
| S166 | + `L(7)` | `explain(a, P)` | first line `P = 7 <- LocalValue`; every style line `-- shadowed` | PIN (SD13) |
| S167 | no style contribution (fresh / inherited-only) | `explain(a, P)` / `explain(leaf, Pi)` | single line reporting the effective lane (`<- Default` / `<- Inherited`) | PIN (SD13) |
| S168 | active rule with an `UnsetValue` setter | `explain` | the value renders `(unset)` | PIN (SD13) |
| S169 | `Children`-composed rule (S47's) | `explain` selector text | the flattened canonical selector (`"Widget.primary:pointerover"` — no `^`) | PIN (SD13) |
| S170 | selector-less explicit style | `explain` | selector renders `(explicit)` | PIN (SD13) |
| S171 | mix of armed-active and armed-inactive rules | `StyleDiagnostics.MatchedRules(a)` | entries strongest-first, each carrying (selector text, layer, key, `IsActive`); inactive armed rules present | PIN (doc §3.9) |
| S172 | structurally non-matching rules + barrier-skipped element | `MatchedRules` | absent for the non-matching; empty for the barrier-skipped part (except `/template/`/explicit entries) | PIN |

---

## 13. Performance & allocation (S173–S180)

Methodology per the repo norm (`GC.GetAllocatedBytesForCurrentThread()` deltas after warm-up, single-threaded). S177 is the probe-4 CI gate re-assert mandated by the §14 P3 row.

| # | Scenario | Expected |
|---|---|---|
| S173 | warmed pseudo flip on/off cycle, one armed 2-setter rule (interest-mask hit, full activate/retract through the store) | **0 B per flip** steady-state — the §3.9-④ contract. The P0 store-spike's known `ReevaluateFrameProperties` dedupe-list allocation must not surface on this path (pool it or bypass it; the spike notes it as trivially poolable) |
| S174 | warmed flip of a bit **no** armed rule wants (interest-mask miss) on an element with style state | 0 B, no frame scan (the one-AND early-out) |
| S175 | warmed flip on an element with **no** style state | 0 B, O(1) |
| S176 | two render-boundary zones; pseudo-flip restyle of an element in zone A (`AffectsRender` property) | zone A's `Scene.RasterVersion` bumps; zone B's `RasterVersion` **unchanged** — restyle re-rasters only the affected zone (the P3 exit criterion; invariants 2/3 routing) |
| S177 | **motion-storm re-assert** (probe 4): the P2 storm tree (~300 hover-reactive leaves) with a real `:pointerover` rule set armed on every leaf (2 setters each); 200-move sweep drained in one frame | ≤ 33 ms/frame (best-of-5, warm), **zero steady-state allocation** per Move including the restyles; `[Trait("Category","Benchmark")]`, allocation asserted every run, timing budget-asserted |
| S178 | 300-element tree attach under a 20-rule `S_app` | one-time costs pinned **bounded, not zero** (frames + state objects); completes inside the startup tier; timing recorded informationally in the design doc's §14 P3 row |
| S179 | warmed class add/remove cycle (re-match tier) | bounded-not-zero per re-match (the frames array rebuild); the flip path of *other* elements stays 0 B throughout |
| S180 | `FlushPendingActivations` with an empty queue | 0 B and trivially cheap ("cheap when empty" — B1), callable directly and idempotent |

---

## 14. Test authoring contract

Each numbered row becomes **exactly one** xUnit test in `Cursorial.UI.Tests/StyleMatrix/`, named after its row id with a behavior slug (`S091_RunnerUpPromotion_SingleNotify_NoDefaultFlash`), one file per section (`Section01_Grammar.cs` … `Section13_Perf.cs`), namespace `Cursorial.Tests.UI.StyleMatrix`. Rows whose Expected cell enumerates a family (S15, S16, S21, S22, S23, S26, S27, S42, S64, S81, S112, S142) become a single `[Theory]` with one case per family member, keeping the row↔test bijection at the row level. S105 (the conformance-kit run) is one kit subclass — its inherited facts collectively discharge the row. Rows are not merged, reordered, or "covered implicitly": a row without a matching test is a P3 exit-criterion failure (§14 P3: `Explain` acceptance + flip-without-unaffected-re-raster + motion-storm green). DEBUG-only rows (S69, S76, S124, S163) compile under `#if DEBUG` and assert the absence of the check in release where practical. Rows marked internal (S28–S34's component inspection, S103/S128/S132's frame-identity probes) use `InternalsVisibleTo` surfaces — pinned loosely: the *content* is the contract, member names are implementation freedom. When the engine cannot honor a row, the resolution is a PR that amends this file (and, where tagged `PIN`/`DEV`, the SD ledger) **before** the engine change lands — the matrix is the oracle, not the implementation.
