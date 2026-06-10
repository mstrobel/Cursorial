---

# Fork B Judgment: Styling Model (Requirements 1, 3, 8)

**Proposals evaluated:** `wpf-triggers`, `avalonia-selectors`, `hybrid`
**Criteria:** consumer ergonomics, API quality, XAML readability, debuggability, footgun density, consistency with existing Cursorial conventions, compatibility with the Fork A property system and the hard constraints of the stack below.

---

## Scoring Table

| Criterion | WPF Triggers | Avalonia Selectors | Hybrid |
|---|:---:|:---:|:---:|
| **Common-case ergonomics** (declare, style, bind) | 7 | 8 | 9 |
| **Debuggability / failure diagnostics** | 9 | 6 | 8 |
| **XAML readability** | 6 | 9 | 8 |
| **WPF/Avalonia veteran learning curve** | 9 | 8 | 8 |
| **Footgun density** | 6 | 7 | 8 |
| **Data-driven styling** | 8 | 6 | 9 |
| **Theming / reuse at scale** | 5 | 9 | 9 |
| **Fork A (property system) compatibility** | 6 | 8 | 9 |
| **Terminal-specific adaptation** | 7 | 8 | 9 |
| **Internal complexity / implementation risk** | 7 | 7 | 8 |
| **OVERALL** | **7.0** | **7.6** | **8.7** |

---

## Consumer Experience Assessment

Before scoring, I mentally wrote three usage scenarios: a simple button theme, a data-driven "save is dirty" indicator, and a capability-adaptive focus ring. Here is the Hybrid consumer experience for reference:

```csharp
// 1. Simple button theme (control-theme = keyed style, no separate type needed)
var buttonTheme = new Style
{
    Key = typeof(Button),
    Setters = {
        new(Button.BackgroundProperty, new ResourceReference("Surface")),
        new(Button.ForegroundProperty, new ResourceReference("Text.Primary")),
    },
    Children = {
        new Style("^:pointerover") { Setters = { new(Button.BackgroundProperty, new ResourceReference("Surface.Hover")) } },
        new Style("^:focus")       { Setters = { new(Button.TextAttributesProperty, TextAttributes.Bold) } },
        new Style("^:disabled")    { Setters = { new(Button.ForegroundProperty, new ResourceReference("Text.Disabled")),
                                                  new(Button.TextAttributesProperty, TextAttributes.Faint) } },
    },
};

// 2. Data-driven "dirty" indicator — no code-behind, no class toggling
new Style("Button#save")
{
    When = { new DataCondition(new Binding("IsDirty"), false) },
    Setters = { new(Button.IsEnabledProperty, false) },
};

// 3. Capability-adaptive focus ring (capability classes stamped by framework on root)
new Style(":root.caps-ansi16 Button:focus")
{
    Setters = { new(Button.TextAttributesProperty, TextAttributes.Inverse) },
};
```

This reads naturally. The `When` clause handles the data case without requiring class boilerplate or code-behind. The capability-class idiom for terminal adaptation is idiomatic and reuses the existing selector machinery. Each `Style` object tells you exactly what it does: one selector string, zero or more data conditions, a list of setters. No trigger taxonomy to learn.

---

## Findings

### Critical

**[C1] WPF Triggers — Priority slot proliferation creates a structural impedance mismatch with Fork A Hybrid**

**Location:** `wpf-triggers` §2.1, `ValuePriority` enum

The proposal defines eight priority slots, including separate `StyleSetter`/`StyleTrigger` and `TemplateSetter`/`TemplateTrigger` pairs. Fork A Hybrid's judgment (already decided) concluded that the property store should use a flat sorted array with one-winner-per-priority, arbitrated by a packed sort key. The WPF trigger model requires the store to distinguish `StyleSetter` from `StyleTrigger` as distinct priorities, which means triggers in the same style as setters win by slot, not by specificity. This is a direct contract conflict with the chosen Fork A design.

The proposal presents this as a "deliberate deviation" from WPF, but the deviation it makes is *toward* WPF's slot proliferation, not away from it. The Fork A verdict explicitly noted that "one-winner-per-priority shifts arbitration into the styling fork." This proposal does not do that — it puts arbitration into slot assignment, forcing a more complex store contract than Fork A Hybrid requires.

**Impact:** Either the Fork A store must grow a slot-pair structure it wasn't designed for, or the trigger retraction story breaks ("clearing `StyleTrigger` always falls through to `StyleSetter`" only holds if the store has separate physical locations for them). This is a pre-integration design conflict, not a detail to iron out later.

**Recommendation:** If WPF triggers wins, the `ValuePriority` enum must be collapsed to match Fork A Hybrid's slot model, and trigger precedence must be expressed via sub-priority within the `Style` slot. The proposal's author is aware of this at a surface level ("seal-once, sub-priority = flattened trigger index") but has not reconciled that the `StyleSetter`/`StyleTrigger` split as *separate enum values* is load-bearing in all their priority reasoning.

---

**[C2] Avalonia Selectors — The `DataTrigger` workaround is structurally incomplete for terminal-scale app patterns**

**Location:** `avalonia-selectors` §4, "DataTrigger parity mapping," and §2.3 `Classes.Bind`

The proposal's answer to `DataTrigger` is `Classes.Bind(name, binding)`. This is a real solution for simple cases, but it has a significant structural gap: the binding is attached to a *specific element instance*, meaning N buttons each need their own `Classes.Bind("urgent", binding)` call if you want N buttons to respond to the same view-model flag. In WPF and in Hybrid's `When`, you write *one* style with one `DataCondition` and every matching element responds. The Avalonia proposal's §8 rebuttal acknowledges this ("a binding plus a selector" vs "one `When` style"), then argues the class becomes a "named, reusable state" — but it only becomes reusable if *something* sets the class on each element, which is exactly the per-element boilerplate being avoided.

For a library-first project, this is not just ergonomics. A library consumer building a list of 100 rows where each row's appearance depends on a row-level view-model property cannot reasonably call `Classes.Bind` 100 times. The idiomatic answer — "bind `IsUrgent` into a view element, then select on it with `[IsUrgent=true]`" — is exactly `When`, written with more steps and a property selector that the proposal elsewhere argues against shipping because of invalidation complexity.

**Impact:** Medium for simple apps, high for list/collection-heavy apps (the exact use case a terminal file browser, log viewer, or data grid would encounter). The proposal is honest about this trade-off but undersells how frequently it arises in real applications.

---

### Major

**[M1] WPF Triggers — `EnterActions`/`ExitActions` with `BeginStoryboard` is a load-bearing dependency on animation orchestration that Phase 1 cannot deliver**

**Location:** `wpf-triggers` §2.3, §7 phasing table

The proposal phases `EventTrigger` and `BeginStoryboard`/`StopStoryboard` into S3, after animation orchestration lands. This is correct pacing. However, `EnterActions`/`ExitActions` are on the `TriggerBase` base class and are therefore part of every trigger type including `Trigger` and `MultiTrigger`, which are Phase S1. This means S1 ships a `TriggerBase` with `EnterActions`/`ExitActions` collections that do nothing until S3, which is a worse consumer experience than shipping them only in S3 (or having a clean phased API where Phase 1 triggers simply don't have those collections).

More concretely: the `TriggerActionCollection` type and the `TriggerAction`/`BeginStoryboard`/`StopStoryboard` types must be defined in S1 even if they're no-ops, or XAML consumers in Phase 1 who reference `EnterActions` get a compile error. The proposal's phasing table does not acknowledge this.

**Recommendation:** Either (a) put `EnterActions`/`ExitActions` on a separate `TriggerWithActions` intermediate class (not `TriggerBase`) so S1 triggers don't carry them; or (b) define `TriggerAction` as an abstract stub in S1 with `BeginStoryboard`/`StopStoryboard` arriving in S3 — then the collections exist but are functionally empty until S3 with clear documentation that they are no-ops until animation orchestration lands.

---

**[M2] Avalonia Selectors — The `ControlTheme` type and the `Style` type being different hierarchies creates a naming-scheme inconsistency with the rest of the stack**

**Location:** `avalonia-selectors` §2.2, `ControlTheme : Style`

The proposal defines `ControlTheme` as a subclass of `Style` with `TargetType` and `BasedOn`. Meanwhile, it also says "a WPF-style keyed Style + per-element `Style` property is deliberately absent." So `ControlTheme` is the keyed-style mechanism, and ordinary `Style` is the selector-based mechanism, and they share a class hierarchy. But Hybrid shows that a `ControlTheme` is just a selector-less `Style` registered in resources under a type key — which means the same `Style` class suffices for both and the `ControlTheme` subtype is purely ceremonial naming with no behavioral distinction except `BasedOn` and `TargetType`.

The naming confusion shows up in the proposal's own example: `app.Resources[typeof(Button)] = buttonTheme` — where `buttonTheme` is a `ControlTheme`. But a consumer who writes `app.Resources[typeof(Button)] = someStyle` where `someStyle` is a plain `Style` gets different behavior, for non-obvious reasons. The distinction between "this is a theme" and "this is a rule" should be structural (where you put it and what key you use) rather than type-based, or consumers will be confused about when to use which.

**Recommendation:** Either commit fully to `ControlTheme` as the mandatory theme bundle (in which case the type distinction has more weight and `Style` is purely rule-shaped), or drop `ControlTheme` and use `Style` throughout (Hybrid's approach). The Avalonia original has this inconsistency too, and the proposals that argue from Avalonia precedent inherit it uncritically.

---

**[M3] WPF Triggers — Selector-less theming "by type" is structurally equivalent to a degenerate selector, and shipping both mechanisms adds conceptual surface area with no net gain**

**Location:** `wpf-triggers` §2.5, implicit style lookup

The proposal ships both implicit-by-type style attachment (keyed by `DefaultStyleKey`) *and* triggers. But an implicit-by-type style is semantically identical to a `Button { }` type selector that applies globally. The proposal acknowledges in §8 ("steelman and rebuttal") that "an implicit style is just the degenerate selector `Button`," and correctly notes that selectors subsume it. WPF has both mechanisms because it evolved historically; a new framework has no reason to carry both. Shipping triggers *plus* implicit-by-type *plus* BasedOn inheritance creates three parallel ways to get style reuse, none of which subsumes the others cleanly.

At terminal scale this is a documentation and onboarding cost, not a performance cost. But documentation complexity matters — every "when should I use X vs Y" question costs developer time and produces StackOverflow-style confusion.

---

**[M4] Hybrid — The grammar subset's exclusion of `:not()` is presented as permanent but should be classified as a deferral**

**Location:** `hybrid` §2.3 selector grammar, §7 punt list

The proposal says `:not()` is "re-addable additively" and excludes it from the shipped grammar. This is defensible: `:not()` requires negative-dependency invalidation (if an element matches because it is *not* something, adding that something to it requires re-evaluation). The invalidation complexity argument is real.

However, the proposal presents this as an indefinite cut with a vague note about "re-addable," while the concrete use cases for `:not()` are frequent in real themes. `Button:not(:disabled)` expressing "the normal interactive state, excluding disabled" is idiomatic. Without it, authors write two rules (a base rule plus a `:disabled` override) to express what `:not(:disabled)` would handle in one. This is not fatal — it is exactly what the proposal suggests via redundant setters — but the proposal should be explicit that `:not()` is a Phase S5+ item with a known invalidation cost story, not a permanent exclusion.

---

**[M5] All proposals — The access-key requirement (Req 6) is underspecified for the degradation path**

**Location:** `wpf-triggers` §2.4, `avalonia-selectors` §3, `hybrid` §6 point 3

All three proposals handle access-key underlining as a styling concern triggered by Alt state. The input reference (§7 of `input.md`) is explicit: standalone Alt key-down/up events exist *only* under Kitty (`ReportEventTypes + ReportAllKeysAsEscapeCodes`) or Win32 input mode. On every other terminal, Alt is observable only as a modifier bit on other key events. The proposals' styled-property / pseudo-class / capability-class models handle the static "always show" case and the "toggle with Alt" case, but none addresses the middle ground: "highlight access keys on the first Alt-modified key event and hide on release" (the Office/browser fallback for terminals that lack standalone Alt events). This middle-ground behavior requires state that the input fork must maintain and expose to the styling system. None of the proposals defines that contract.

**Recommendation:** Whichever proposal wins, the access-key section must add: (1) the runtime test for which behavior applies (`Keyboard.ReportsRepeats || Protocol.Win32InputMode` for toggle-with-Alt; everything else for permanent-show or first-Alt-chord highlight); (2) how the input/focus fork communicates Alt-chord-highlight state to the styling system; (3) that `FocusEvent { HasFocus: false }` must clear the Alt-held state unconditionally.

---

### Minor

**[m1] WPF Triggers — `EffectiveValueReport.Contributor` typed as `object?` loses the benefit of the explicit trigger-object model**

The entire argument for WPF triggers over selectors is "triggers are inspectable objects." But the `StyleDiagnostics.GetValueSource` return type has `object? Contributor`, meaning a consumer must cast (to `Style`, `FrameworkTemplate`, `Storyboard`) to do anything useful with it. This pattern is less discoverable than a discriminated union or a set of well-typed properties. Consider a `sealed record StyleContributor` with a type discriminator that makes "which style, which trigger index, which storyboard" structurally typed rather than cast-to-object.

**[m2] Avalonia Selectors — `Style.Animations : StyleAnimationCollection` with no further definition is a contract deferral masquerading as a shipped feature**

The proposal mentions `Style.Animations` in §2.2 and says "Fork D executes." But `StyleAnimationCollection`'s type is never defined, its interaction with the priority system is not stated, and the "rising edge" / "falling edge" semantics are hand-wavy. Either define the contract in terms of `IAnimation<T>` (which is the actual animation layer) or exclude it from the proposal entirely and note it as a future graft from the animation fork.

**[m3] Hybrid — `StyleSortKey` as a packed `ulong` is correct but the bit layout should be explicitly pinned in the spec, not just described in prose**

The proposal describes the field layout in a comment (`[layer:3][names:8][classLike:10][types:8][scopeDepth:8][order:27]`). This is load-bearing: every specificity example in the proposal depends on it, the Fork A store implements against it, and getting it wrong produces silent ordering bugs. This should be a named constant table with a companion oracle test, consistent with the project's practice for any bit-packed format (cf. `VtSequenceClassifier`'s state-machine encoding, `StyleQuantizer`'s palette math).

**[m4] WPF Triggers — `sealed class Style` with mutable `Setters`/`Triggers` before seal and shared immutable state after creates a potential for consumer surprise**

The project's codebase strongly favors `readonly record struct` for value-like types and immutable objects. A mutable `Style` that can be shared after sealing is different enough from the rest of the API surface that consumers will be surprised by `InvalidOperationException` when they try to add a setter after attaching. WPF veterans will expect this, but Hybrid uses the `init` pattern on `Selector` and construction-time `SetterCollection` that is already sealed. Consider `readonly` collection properties (no post-construction add) with a factory method or builder for assembly, rather than a mutable-then-seal pattern.

---

## Adversarial Notes on Each Proposal's Self-Argument

### WPF Triggers

**Strongest claim:** "Triggers are debuggable objects. On a platform debugged over SSH with no devtools, an inspectable bit and an integer index is the difference between finding the problem and staring at a selector string."

**Verdict on the claim:** Partially valid. The `StyleDiagnostics` proposal is excellent and the bit-indexed report is genuinely more debuggable than a cascade. But the claim that *selectors require staring at a string* is only true if you don't build the equivalent diagnostic. Hybrid proposes `StyleDiagnostics.Explain` from day one, which produces frame/priority/active/value provenance just as specific. The debuggability advantage is real but is a function of the diagnostic tooling, not of whether the activation mechanism is trigger-objects vs. activator-nodes. Both can be equally transparent if the diagnostic is built.

**Unsupported claim:** "At terminal scale, per-style watch maps and per-element bits are simpler and strictly local." The proposal presents this as if selectors require a global index. They don't — the type-keyed candidate cache in both selector proposals is equally local after attach. The claim conflates "selector matching" with "CSS-style global O(rules × elements) rescanning," which neither selector proposal implements.

**Genuinely strong:** The §0 invariant (no styling mechanism ever writes a local value; all values carry a provenance tag) is precisely stated and correct. Every other proposal should adopt this invariant language.

**Footgun I would trip immediately as a consumer:** Writing a `DataTrigger` on a property that another trigger also sets, then being surprised by the sub-priority tie-breaking. The "later trigger in the collection wins" rule is simple but not self-evident in XAML where collection order is implied by document order, and the documentation does not flag this at the point of use. The `MultiTrigger` met-count evaluation is also a footgun: if you add a condition to a `MultiTrigger` but forget to add the matching property to the `Watch` map — a mapping that is invisible to the XAML author and computed at seal time from the condition list — you can get triggers that never fire.

### Avalonia Selectors

**Strongest claim:** "One matching engine, one retraction path; the type-keyed candidate cache means match time is bounded and effectively constant at terminal scale."

**Verdict on the claim:** True and well-argued. The two-phase model (structural match once, activators forever) is architecturally elegant and exactly right for the allocation discipline the project requires. This is the best per-frame performance story of the three proposals.

**Unsupported claim:** "Selector systems subsume WPF's implicit-style mechanism; we ship one matching engine instead of two attachment systems." This is true for *structural* styling. But the proposal's own `DataTrigger` parity table admits it: `DataTrigger` maps to "class binding" which requires per-element code or markup (`Classes.urgent="{Binding IsUrgent}"`). That is not "one mechanism" — it is the selector mechanism plus a separate per-element binding idiom that is only called "one mechanism" because the two wires cross in a different place. Compared to `When`'s one style with one condition, class binding is more steps.

**Genuinely strong:** The template barrier (elements with `TemplatedParent` are invisible to rules without `/template/`) is a critical correctness property. Hybrid adopts this. WPF triggers have nothing comparable — template internals are styled via `ControlTemplate.Triggers` and you can accidentally reach template parts from outside. The barrier is a real design improvement.

**Also genuinely strong:** Capability-classes-on-root (`:root.caps-ansi16 Button:focus { ... }`) is the best mechanism for capability-adaptive theming proposed by any of the three. It reuses the cascade naturally, requires no new mechanism, and is declarative. All proposals should adopt it.

**Footgun I would trip immediately:** A rule like `StackPanel Button { margin: ... }` stamped in `Application.Styles` that styles *all* buttons inside any `StackPanel`, including buttons inside a button's own template that happens to be inside a `StackPanel`. The template barrier prevents cross-template reach from externally-defined rules, but an internally-defined rule with a `/template/` combinator can still reach in. Without exhaustive documentation, template authors will write descendant rules that penetrate template boundaries unintentionally on complex control hierarchies.

### Hybrid

**Strongest claim:** "One predicate, one slot, one sort key, one retraction path."

**Verdict:** True, and it is the right design axis for this codebase. Every time I mentally traced through a styling scenario — hover, focus, data-driven disabled, capability adaptation — the Hybrid model handled it with fewer concepts and fewer "where does this go" decisions.

**Unsupported claim:** "Ancestor pseudo-class dependencies (`Pane:focus-within Button`) are supported but pay their own way: a flip on the ancestor walks only its dependency list — precise, no subtree scan." This is stated but not fully developed. The proposal needs to define what happens when an ancestor's `Styles` collection mutates after elements below it are attached and have registered ancestor dependencies. The Phase S0/S1 phasing does not include ancestor-dependency invalidation on collection mutation — it handles attach/detach but not runtime ancestor state changes during a `Styles.Replace`. This is a correctness gap that could produce stale renders on dynamic themes.

**Genuinely strong:** Counting each `DataCondition` as a class-equivalent in the sort key is the single best specificity insight in any of the three proposals. It means "when this binding is true" naturally beats "when no binding is specified" using the same comparison that `:pointerover` beats an unqualified type rule. No new mechanism, no extra slot, no documentation chapter on trigger precedence. This is clean design.

**Footgun I would trip:** Combining a `When` condition with a descendant combinator. If I write `StackPanel Button { When = [IsDirty=true] }`, the `When` bindings are evaluated against the *button's* `DataContext`, not the panel's. This is the correct behavior (the proposal states it: "evaluated against the target element's DataContext"), but XAML developers accustomed to scope-inherited DataContext in ancestor selectors will expect ancestor-scoped `When` conditions. The proposal should flag this explicitly in the XAML/consumer documentation.

---

## Strengths to Call Out Explicitly

**WPF Triggers:** The `§0 invariant` ("no styling mechanism ever writes a local value") is the cleanest statement of the retraction contract in any of the three proposals. Adopt this language wholesale, whichever proposal wins. The `StyleDiagnostics` design — first-class, defined with concrete types including `TriggerState.Description` — is excellent. The seal-time compilation model (one `CompiledStyle` shared by all elements, mutable-per-element state is bits-and-counts only) is correct and should carry over.

**Avalonia Selectors:** The two-phase model (match once at attach, activate via pre-built activator nodes) is architecturally correct for the allocation requirements. The template barrier is a real design improvement over WPF. The capability-classes idiom is the best proposed mechanism for terminal capability adaptation.

**Hybrid:** The `When` + selector unification, the `StyleSortKey` specificity model, and the explicit grammar subset (justified by invalidation geometry rather than performance micro-optimization) are all genuinely superior design choices. The phasing plan (S0–S5) is the most realistic of the three — each phase is a coherent shipped system, not a fragment that waits on a dependency.

---

## Ranked Verdict

1. **Hybrid** — 8.7. The right architecture for this codebase's stated priorities: one mechanism, allocation-free hot path, terminal-appropriate grammar subset, clean Fork A Hybrid compatibility.

2. **Avalonia Selectors** — 7.6. A strong proposal, particularly in its invalidation model and template barrier. Would win against WPF triggers. The `DataTrigger` gap and the `ControlTheme` naming inconsistency are the reasons it does not beat Hybrid.

3. **WPF Triggers** — 7.0. Usable, deeply familiar to WPF veterans, and has the best diagnostic story of any proposal. However: the priority-slot proliferation conflicts with the already-decided Fork A model, the theming/reuse story at scale is structurally weaker, and porting WPF's most litigated wart (trigger precedence confusion) onto a new platform without a selector grammar to simplify it is the wrong trade.

---

## Recommendation

**Hybrid wins.** Implement it.

### Graft list from losing proposals

**From WPF Triggers — steal these:**

1. The `§0 invariant` formulation verbatim. Use "no styling mechanism ever writes a local value; every injected value carries a (priority, sort-key) provenance tag; removal of the contributor restores the exact prior effective value with no residue" as the Hybrid design doc's named invariant.

2. `StyleDiagnostics.Explain(element, property)` must exist in **Phase S1**, not S5. The WPF proposal's `EffectiveValueReport` type (contributor, value, priority, resource key) should be the model. Re-type `Contributor` as a sealed discriminated record (`StyleContributor`), not `object?`.

3. The seal-time computation model. `CompiledRule` in Hybrid needs the same "one immutable shared representation, all per-element mutable state is in `ElementStyleState` only" discipline. WPF triggers articulates this most clearly.

4. The `ThemeVariant(ThemeBase, ColorDepth)` typed variant (light/dark × color tier) is better typed than Avalonia's string-keyed variant. Hybrid uses this same shape; keep it.

5. The `:pointerover` + `:focus` paired rule as a first-class **theme authoring lint**. WPF triggers correctly observes that on terminals without `MouseCapabilities.Motion`, hover never sets. Hybrid's §6 mentions a diagnostic warning for "hover-only affordance"; WPF triggers formalizes it. Ship this lint from S0.

**From Avalonia Selectors — steal these:**

1. **Capability-classes-on-root** (`:root.caps-truecolor`, `:root.caps-ansi16`, `:root.caps-ascii`, `:root.caps-mouse`, `:root.caps-kitty-keyboard`). This is strictly superior to both Hybrid's unspecified capability adaptation story and WPF triggers' `ThemeVariant.ColorDepth` approach. It is declarative, reuses the cascade, requires no new mechanism, and is stamped once at session open. Stamp these classes from `TerminalCapabilities` at window creation (and re-stamp on `RenegotiateAsync`).

2. **`Classes.Bind(name, binding)`** or an equivalent `DataContext`-aware class binding that does not require per-element imperative code. This rounds off Hybrid's `When` story for cases where multiple styles target the same class: the app can add `.urgent` to elements via a binding, and every `^.urgent`, `Button.urgent`, etc. rule lights up automatically. This is complementary to `When`, not a replacement — different ergonomic level.

3. **The template barrier formulation.** Hybrid has this implicitly via the `/template/` combinator requirement. Avalonia's proposal is more explicit about the enforcement point (before step 1 of match, if `TemplatedParent != null` and the selector lacks `/template/`, skip immediately). Make this an explicit invariant in the Hybrid design doc.

4. **`IStyleActivator.IsActive` as a public diagnostic property.** The Hybrid diagnostic model should expose per-frame active state, not just the final resolved value. This gives the dev overlay enough information to show "this rule is armed and currently active" vs "armed but waiting for condition."

### Things to not steal

- WPF's `EventTrigger` + `BeginStoryboard`/`StopStoryboard` as a styling concern. Animation ignition belongs to the animation orchestration fork. Hybrid correctly cedes this. The correct seam is: frame activation/deactivation edges are observable by the animation fork via a subscription, and it starts/stops storyboards there. Styling carries no storyboard references.

- Avalonia's property-value selectors (`[IsDefault=true]`). Hybrid's argument that these require value-change invalidation machinery equivalent to `When` but with worse ergonomics is correct. `When` is the right place for value-based conditions.

- Avalonia's `ControlTheme` as a distinct type from `Style`. One keyed-style mechanism is enough.

---

## Open Questions for the Author

1. **Fork A sink contract for multiple active frames per property.** Hybrid requires the property store to hold multiple sorted style entries per property (several active frames, each with setters for the same property) and expose the max as the effective value. Fork A Hybrid's judgment left open whether the store holds a single best-value per priority or a sorted list of all entries at the `Style` priority. This design choice must be settled before Phase S0 code starts — the retraction story ("remove this frame's entry; property promotes to next-highest") only works if the store keeps all entries, not just the winner.

2. **Class change invalidation on ancestor rules.** When a class is added to a container that has a descendant rule in some `Styles` collection (e.g., adding `.toolbar` to a panel when `StackPanel.toolbar > Button` exists), the proposal re-matches the panel's subtree. At what granularity? Does it re-match all elements in the subtree, or only elements that were previously not matched? If the former, is the re-match bounded by the panel's subtree root, or does it walk the full logical tree? The proposal mentions `AncestorInterestingClasses` as the set that triggers subtree re-match, but the scope of that re-match is not fully defined.

3. **`When` condition binding source.** When a `When`-guarded style targets `Button#save`, the `DataCondition` binding is evaluated against `#save`'s `DataContext`. This is stated. But what if the style targets a type pattern (`Button`) and is declared at `Application.Styles`? Each matched button's `DataContext` may be different — the same `DataCondition` binding connects to 200 different binding sources. Is there one `IConditionSubscription` per element? How does the watcher lifecycle compose with element attach/detach when the `DataContext` changes? This is the binding-intensive path and it deserves explicit treatment.

4. **`When` specificity and the "class column" accounting.** The proposal counts each `DataCondition` as one class-equivalent in the sort key. This means a style with three `DataCondition`s and no structural selector has higher specificity than a type rule (one type, zero classes). Is this the intended behavior? It means `DataCondition`-heavy styles can override local `Classes`-based rules unexpectedly. The justification ("`When`-guarded beats unguarded base") is correct for the simple case but may need a ceiling for pathological uses.