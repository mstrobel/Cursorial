# Fork C Judgment — XAML Pipeline (Requirement 7)
**Lens: requirements coverage & architectural coherence**
Grounded in `/tmp/cursorial-ui-maps/design-doc.md`, `input.md`, `animation.md`, `drawing-core.md`, `rendering-session.md`, and spot-checks against repo source (`Cursorial.Core/Output/Color.cs` confirms `Cursorial.Output` namespace, `Color.FromPalette`, `Colors` — both P1 and P2 got the existing surface right; P3 mostly right).

---

## 1. Scores

| Criterion | P1 runtime-loader | P2 source-gen | P3 reuse-xaml |
|---|---|---|---|
| R7 coverage (pipeline completeness: parse, instantiate, extensions, deferred templates, namescopes, diagnostics, resources, processing stance) | **9** | 8 | **9** |
| Adjacent requirements service (R1/R2/R3/R6/R8/R9/R10 touchpoints) | 8 | **9** | 7 |
| Cross-fork composability (property-system ↔ styling ↔ XAML contracts) | **9** | 7 | 8 |
| Stack invariant & terminology consistency (design-doc.md) | **9** | 8 | 5 |
| Cross-platform / terminal soundness | 9 | **9** | 8 |
| Claim credibility & risk honesty (adversarial deductions) | 8 | 6 | **8** |
| **Total** | **52** | **47** | **45** |

Scoring notes on the three decisive rows:

- **Cross-fork composability.** P1 is the only proposal that fully answers all three interaction questions in the lens: trigger/DataTrigger value coercion (exposes the `XamlConverters` registry to Fork B for runtime coercion of `DataTrigger.Value`, where binding result types are unknowable at parse), template deferral + two-scope namescopes with *definition-site* lexical resource capture (the WPF-faithful rule), and value provenance (`Local` vs `TemplateLocal`) riding every set so template values sit correctly under document values in Fork A's ladder. P2 covers templates and Style/Trigger lowering well but has two composition holes (below). P3's set-interceptor + `IDeferredValue.AttachTo` is actually the *cleanest* single seam of the three.
- **Stack consistency.** P1 reads like it was written by the team that wrote the Drawing design doc: `BrushMarkup` grammar reuse, `Pens` presets, "weight is a glyph family, never thickness," cells-not-DIPs converters, hot-reload-as-the-designer, mechanism/orchestration split for R10 (correctly notes `Cursorial.Animation` types are immutable-ctor and that XAML-friendly mutable descriptions belong in the UI layer — exactly what `animation.md` prescribes), phased plan with punt list and oracle pinning. P3's vendoring of 30–45 kLOC of `#nullable disable` foreign code is the single largest culture violation in any proposal — this is a repo that hand-rolled PNG decoding rather than take a dependency; "we own the fork" does not make 40 kLOC of Mono-lineage code *understood* the way every other line of this codebase is.
- **Credibility.** P2 loses the most here; see flaws below — its central IDE-performance claim rests on machinery that doesn't work the way it's described.

---

## 2. Flaws, risks, unsupported claims

### Proposal 1 — runtime-loader

1. **The "same parser at build time" claim is unproven as written.** Roslyn analyzers/generators must target `netstandard2.0`. P1's loader lives in `Cursorial.UI` (net10.0, "embrace latest C#" per CLAUDE.md). Running *the literal same parser* inside `Cursorial.UI.Generators` requires the parser/node-model assembly to multi-target netstandard2.0 — constraining its language/BCL usage — and the proposal never addresses this. P2 explicitly solved it (`Cursorial.UI.Xaml.Frontend`, netstandard2.0). Solvable, but the no-drift story has an unstated structural cost.
2. **`{x:Static}` folded at parse time is a semantic deviation.** WPF evaluates `x:Static` per load. P1 folds it once into the cached `XamlDocument` and shares the box across every load and template build — an `x:Static` pointing at a static *property* whose value changes (theme accents, ambient config) silently goes stale. Needs a "fields fold, properties evaluate at instantiation" rule or a documented deviation; the proposal has neither.
3. **Sentinel-through-SetValue is the weakest seam in its contract.** `IUIPropertyTarget.SetValue` "must tolerate value == ResourceReference / IBindingExpression sentinels" — Fork A's property system must special-case Fork B/C marker types inside its core set path. P3's `IDeferredValue.AttachTo` and P2's `BindResource`/`Bind` keep the property system's value path clean. This is fixable without architectural change and should be fixed.
4. **R2 is partially satisfied, full stop.** No compiled bindings, no binding-path validation against `x:DataType`, reflection path-walking delegated to Fork B "with the generator package as the natural shared home later." For "powerful data binding," P1 ships the wiring but not the strongest part of the modern XAML binding experience. It names the seam where the fix lives but doesn't design it.
5. **Effort estimates are optimistic.** ~10.5 KLOC across X0–X5 for a parser + instantiator + extensions + templates + generator + AOT metadata provider; P3's counter-estimate for the same semantics (15–25 kLOC) is more believable. The phases are well-ordered, so the risk is schedule, not architecture.
6. **AOT/trimming is unsupported until X5.** Honestly flagged, but it means the TUI ecosystem's single-binary distribution norm is a degraded mode for the first several phases. (Note: AOT is *not* among the ten requirements — see verdict — but the gap is real.)
7. Smaller: events-in-templates banned (documented deviation, fine); `LineInfo` column clamped to 12 bits; the "tens of µs for ~600 objects" instantiation number ignores first-touch reflection warm-up, which dominates cold paths.

### Proposal 2 — source-gen

1. **The two-tier incrementality claim is technically shaky at its core.** The pipeline sketch fingerprints references (Tier 1) and type skeletons (Tier 2), then says `RegisterSourceOutput` "re-acquires live symbols via a thread-local `Compilation` handle only inside this step." There is no sanctioned way to get the current `Compilation` inside an output step *without* making it a pipeline input — and if it's an input, the step re-runs on every keystroke (the exact trap the proposal claims to dodge). Additionally, `Collect()`ed `ImmutableArray<TypeSkeleton>` is not structurally equatable by default; without a custom comparer the combine invalidates per keystroke anyway. The intent (skeleton-keyed caching) is achievable, but the design as written does not deliver the headline "IDE typing latency is unaffected," and this is the proposal's *load-bearing* performance claim.
2. **Compiled bindings break against the dominant MVVM authoring pattern.** Generators can't see other generators' output — acknowledged as risk #3 — but underweighted: `CommunityToolkit.Mvvm`-style `[ObservableProperty]` viewmodels have their *bindable members generated*, so `x:DataType` path validation fails on exactly the codebases most likely to use it, and `CursorialXamlStrictAot` turns the fallback warning into a build break. The flagship feature degrades to reflection bindings for the most common viewmodel style.
3. **Template `StaticResource` scoping deviates from WPF.** `TemplateBuildContext.Resources` is "the lexical resource chain at the *instantiation site*." WPF resolves StaticResource in templates against the *definition* location. P1 captures definition-site scope; P2 silently changes the rule (when not build-traceable). Needs at minimum a documented-deviation entry; it will surprise WPF muscle memory, which this project deliberately courts.
4. **Cross-assembly resource-key tracing is unspecified.** `CXAML0201` ("key not found in any build-reachable dictionary") requires the generator to know keys inside `<ResourceInclude Type="theme:CursorialDarkTheme"/>` — compiled dictionary classes in *referenced assemblies* whose contents are opaque IL. No manifest mechanism is described. Suppressible warning, but the diagnostic as advertised can't be implemented as described.
5. **DataTrigger value coercion has no home.** Converters exist only as build-time folding; there is no runtime registry. If Fork B ships WPF `DataTrigger`s, comparing a markup string value against a runtime binding result needs runtime coercion machinery P2 never provides (P1 explicitly exposes `XamlConverters` for this).
6. **Runtime markup is gone from production.** Downloadable/user-editable theme files — a genuine TUI tradition (base16 etc.) — require opting into the dev interpreter with reflection and "runtime errors as your own informed tradeoff." Defensible, but it's a real R3-adjacent capability loss, not a free lunch.
7. **Two execution implementations of one semantics.** The drift containment (shared front end + conformance corpus) is real but the interpreter is explicitly a *subset* (no compiled bindings, no `x:TypeArguments`), so "identical widget trees" has permanent carve-outs — and the previewer therefore previews something slightly different from what ships.
8. **R6 folding covers literals only.** `Header="{Binding FileMenuLabel}"` can't be folded into `AccessText` at build; runtime underscore parsing is still needed for bound/localized headers, so "zero runtime string scanning" overclaims.
9. Smaller: "Widget" breaks the WPF/Avalonia naming-kinship convention the design doc instructs the UI layer to extend (P1 uses `Control`); 10–13 engineer-weeks for 12–18 kLOC of generator + front end + interpreter + previewer is optimistic by ~2x; "AOT is table stakes" is asserted, not derived from the requirements list.

### Proposal 3 — reuse-xaml

1. **The vendoring is a culture and quality violation, not just a line-count cost.** 30–45 kLOC of `#nullable disable`, netstandard2.0-era, Mono-lineage code in a repo whose production code is zero-dependency, nullable-enabled, latest-C#, and *understood line-by-line* (the design doc's whole process — adversarial reviews, oracle pinning, resolved-decision records — presumes owned code). "Frozen plumbing" is the optimistic frame; the realistic frame is that the first deferral/ambient bug lands the team inside 40 kLOC of unfamiliar object-writer internals.
2. **The spike's failure mode converges on Proposal 1.** The stated salvage path — keep the reader + MEL parser, hand-write a smaller object writer against our property system — *is* the runtime-loader proposal, reached after spending the spike and the vendoring work. That makes P3 a bet that Mono-lineage deferral/ambient/`XamlSetValueHandler` behavior is sound; the kill criteria are honest, but the downside lands you where P1 starts.
3. **AOT story is the weakest of the three.** Trim-safety via a generator manifest rooting every markup-reachable type is workable but maximal (full reflection metadata for the whole markup closure); NativeAOT is "functional with a perf tax" contingent on spike findings about `Expression.Compile` interpreter fallback — flagged, but the engine's reflection-heavy writer sits awkwardly against the stack's allocation discipline either way (per-stamp template replay allocates a writer + context per instantiation; 50–150 µs/stamp vs. P1's allocations-plus-delegate-calls and P2's straight-line code).
4. **Diagnostics are partially engine-owned.** Line/column propagation is solid (`IXamlLineInfo` end to end), but error *messages* originate in the engine; "we wrap every error" with did-you-mean only on converter misses is weaker than P1/P2's fully-owned diagnostics — and a TUI's edit-run loop makes diagnostics a headline feature, as P1 correctly argues.
5. **Microsoft's schema URI** (`http://schemas.microsoft.com/winfx/2006/xaml`) hardwired as `xmlns:x` for a non-Microsoft framework is pragmatically argued (editor tooling) but is a permanent oddity Avalonia deliberately avoided; the engine hardwiring it is itself evidence of how little of the dialect Cursorial actually controls.
6. **v1 eager resource-dictionary instantiation** — deferred per-key entries are punted, so a 300-resource theme pays full instantiation at startup. Acceptable at terminal scale (their own argument), but P1 gets deferred entries for free from its slice mechanism and P2 from compiled delegates; P3 is alone in not having them in v1.
7. Smaller: "stripping `public` via a build step" over vendored sources is fragile tooling; `BorderPen="Heavy"` in the example brushes against the design doc's recorded *cut* of `BorderPen` (defensible at the UI-control level, but the doc's "no BorderPen; asymmetric borders are composed DrawLines" stance deserved a mention); "Avalonia shipped on Portable.Xaml for years" compresses a messier OmniXaml → Portable.Xaml → XamlX history. The citations and System.Xaml disqualification are accurate and the best-researched claims in the fork.

---

## 3. Ranked verdict

**1st — Proposal 1 (runtime-loader). 2nd — Proposal 2 (source-gen). 3rd — Proposal 3 (reuse-xaml).**

Rationale:

- **The requirements list decides the P1-vs-P2 question.** P2's strongest arguments — NativeAOT/trimming as "table stakes," zero startup parse — defend capabilities that appear nowhere in the ten requirements, while its costs land on things that do: a permanently dual-implementation semantics (generated lowering + dev interpreter) versus P1's single execution path; an IDE-performance architecture whose central mechanism is hand-waved; and compiled bindings that fail against generator-produced viewmodel members. At the stated terminal scale (hundreds of elements, 1–50 KB documents), the quantitative payoff of compilation is single-digit milliseconds — P1's §6.5 argument is correct and P2 concedes it ("the startup-cost argument is correct and I won't pretend otherwise").
- **P1 composes best with the other forks.** It is the only proposal that explicitly handles all three lens interactions: provenance-tagged sets for the Fork A priority ladder (`Local`/`TemplateLocal`), the runtime converter registry Fork B needs for DataTrigger coercion, selector strings with line info if Fork B goes Avalonia-style, `ITemplateContent`/`TemplateBuildContext` with definition-site lexical resource capture (the WPF-faithful template scoping rule, which P2 quietly changes), and `IDeferredValue` for lazy dictionary entries. Its contract asks the least convention-coupling of Fork A (P2's hard "static fields discoverable by symbol name" requirement is reasonable but binding).
- **P1's worst gaps are exactly where P2's best ideas graft.** P1 already ships a generator package (X4) running its parser for validation and typed fields, and already names that package as the future home of binding-path accessor generation (X5). P2's binder, compiled-binding descriptors, and strict-AOT mode are an *upgrade path inside P1's existing seams*, not a rival architecture. The reverse graft is the expensive direction: retrofitting a runtime loader and hot reload onto P2 means building P1 anyway (P2 ships an interpreter regardless — it just keeps two implementations forever).
- **P3 loses on culture and on convergence.** Its semantics-depth argument is the best-articulated risk analysis in the fork (the "spec long tail" is real, and its citations check out), but the project demonstrably does not need `x:Reference` fix-ups, `x:FactoryMethod`, or generic instantiation on day one — and P1's documented-punt discipline handles the tail the same way the repo handled Unicode width tables. Vendoring 40 kLOC of foreign engine into this particular repo is the proposal's own refuted steelman ("zero external dependencies is an established repo value") wearing a source-drop costume. And its spike-failure salvage path is literally Proposal 1.

---

## 4. Recommendation

**Adopt Proposal 1's architecture as the Fork C spine, with three structural amendments taken from the losers:**

1. **Make P1's parser front end a netstandard2.0 assembly from phase X0** (P2's `Frontend` move). This closes P1's unaddressed "same parser in the generator" gap and is nearly free if done first; it is expensive to retrofit.
2. **Replace P1's sentinel-through-SetValue with P3's `IDeferredValue.AttachTo` / P2's `BindResource` attachment shape.** `{Binding}`/`{DynamicResource}` results should attach via a dedicated seam, not flow through `IUIPropertyTarget.SetValue` as tolerated marker objects. Fork A's value path stays clean.
3. **Commit P2's compiled-binding descriptor contract (`Binding.Compiled<TSource,TValue>` with per-segment getters + segment names) as the X5 generator deliverable now, in the cross-fork contract,** even though the implementation is phased. Fork A's binding engine should be designed against the descriptor shape from day one so reflection bindings and compiled bindings are two producers of one consumer contract — with P2's `x:DataType` path-validation diagnostics and strict-mode escalation riding along. Document the generator-produced-member limitation honestly (P2's risk #3 applies to the graft too).

Sequencing stays P1's X0–X5, with X0 absorbing P3's Phase-0 discipline (kill-criteria spike on the parser/markup-extension grammar, fuzzing, WPF escape-case oracle table) and a Windows-only CI leg pinning the conformance corpus against real System.Xaml *as an oracle* for intentionally-matching semantics (P3's best process idea, usable without the engine).

This lands one semantic implementation, hot reload as the terminal's designer, full cross-fork contracts available to Forks A/B immediately (X0/X1 have no fork dependency), and a non-speculative road to build-time validation, compiled bindings, and trim/AOT cleanliness through seams that exist from day one.

---

## 5. Graft list

**From Proposal 2 (source-gen):**
- `Cursorial.UI.Xaml.Frontend`-style netstandard2.0 parser assembly shared by loader and generator (adopted above).
- Compiled-binding descriptors with per-segment getters and `x:DataType` path diagnostics; `CursorialXamlStrictAot` mode auto-set by `PublishAot` (adopted above).
- **`ThemeVariant` keyed on negotiated `ColorDepth` + background-luminance dark/light, re-resolved on `RenegotiateAsync` and pulsing `ResourceDictionary.Changed`** — the single best terminal-native idea in the fork; belongs in the Fork B/C resource contract regardless of pipeline.
- Build-time folding of access-key literals into an `AccessText(text, key, underscoreIndex)` data model (generator path folds; runtime loader parses at load — one data model, two producers). Pairs with input.md §7's capability gate.
- `#line`-mapped generated code + `EmitCompilerGeneratedFiles` inspection discipline, for whenever the X4+ compiled producer emits construction code.
- Incrementality step-reason regression tests ("all-Cached on unrelated edit") as a pinned CI invariant for the generator package — P2's best process idea, needed precisely because the mechanism is fragile.
- `x:TypeArguments` support (P1 punts it; the generator path makes it cheap, and the runtime path can follow).
- The XSD-emitting `cursorial xaml-schema` tool for editor completion — cheap, high-leverage DX.

**From Proposal 3 (reuse-xaml):**
- `IDeferredValue.AttachTo(target, property)` as the expression-attachment seam (adopted above).
- Phase-0 spike with explicit written kill criteria, applied to P1's own parser/instantiator assumptions (e.g., the 1–3 ms parse and template-build cost claims get benchmarked before X1).
- Windows-only CI oracle leg diffing conformance fixtures against genuine System.Xaml for the intentionally-WPF-compatible subset — converts P1's "Deviations from WPF" doc section from folklore into measurement.
- The `.cxaml` extension consideration (keeps the VS WPF designer from claiming files); decide once, early.
- `GlyphSet`/ASCII chrome degradation as a `DynamicResource` theme resource rather than per-element properties — consistent with Drawing's capability-blind stance and worth putting in the Fork B theme contract.
- The explicit dependency-direction guarantee: XAML assembly is optional, Forks A/B never reference it, lower layers need zero changes — P1 implies this; P3 states it as a contract clause. State it.