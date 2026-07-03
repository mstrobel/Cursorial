# Fork C Judgment — XAML Pipeline (Lens: implementation cost, performance, maintainability)

Judge basis: the five reference maps (`/tmp/cursorial-ui-maps/*.md`), repo conventions in `/Users/mike.strobel/Workspace/Cursorial/CLAUDE.md` and `docs/drawing-layer-design.md` (zero external production dependencies, oracle-pinning, phased living-design-doc builds, allocation discipline at 20–60 fps, hundreds-of-elements scale).

---

## 1. Scores

Criteria (1–10, higher is better):
- **A. Effort realism** — cost to reach a v1 at this repo's quality bar, honestly estimated
- **B. Runtime perf/alloc at terminal scale** — load, template stamping, restyling storms, steady state
- **C. Memory per element/document**
- **D. Invariant complexity** — how much an implementer must hold in their head to not break it
- **E. Subtle-bug risk** — priority interactions, retraction/leak classes, template namescopes, provenance
- **F. Degradation path** — virtualization, accessibility, hot reload, future XAML features
- **G. Trimming/AOT trajectory**

| Criterion | P1 runtime-loader | P2 source-gen | P3 reuse-xaml |
|---|---|---|---|
| A. Effort realism | **7** | 4 | 6 |
| B. Runtime perf/alloc | 8 | **10** | 4 |
| C. Memory | 8 | **10** | 5 |
| D. Invariant complexity | **6** | 4 | 4 |
| E. Subtle-bug risk | **6** | 5 | 4 |
| F. Degradation path | **8** | 6 | 6 |
| G. AOT trajectory | 5 | **10** | 5 |
| **Total** | **48** | 49 | 34 |

The raw totals put P1 and P2 within a point; the lens weighting does not. Criteria B/C/G are where P2 wins, and at terminal scale most of that margin is **paid for problems this project doesn't have** (parse cost of 1–50 KB documents, startup at hundreds of elements). Criteria A/D/E — the cost and maintainability axes this lens is explicitly about — all favor P1.

---

## 2. Adversarial findings per proposal

### Proposal 1 — custom runtime loader

**Flaws and unsupported claims:**

1. **"One semantic implementation; zero drift" is partially false for X4.** The build-time validation story claims the generator runs "the same parser." The *parser* yes — but the runtime resolver works over live `Type`/reflection (`Assembly.GetType`, `XamlType.ClrType`, activation thunks), and a Roslyn analyzer/generator cannot load the assembly being compiled. Build-time type/member resolution requires a **second, symbol-backed metadata provider** — exactly the dual-binder drift surface this proposal accuses P2 of. It's contained (resolution and member lookup, not instantiation semantics), but X4's ~1.5 KLOC estimate is light, and the proposal never names this. Same problem for parse-time folding of `{x:Static}` in the analyzer context.
2. **Shared folded boxes are an aliasing trap.** Folding is safe only if every context-free converter returns immutable values. A consumer-registered converter returning a mutable object gets silently shared across every template stamp and every document load. Needs an explicit immutability contract on `ITypeConverter.IsContextFree` (or an `x:Shared`-style opt-out), and a diagnostic. Not mentioned.
3. **Lexical-scope capture lifetime is hand-waved.** "Weak host references where Fork B's scope type allows" is not a design; a `DataTemplate` stored in an app-level dictionary capturing a window's resource chain pins the window's dictionaries for the app lifetime. The retention rule needs to be pinned in the cross-fork contract, not deferred.
4. **AOT is a promise, not a property.** Trimming is "unsupported-with-diagnostics" until X5, the last phase. For a TUI ecosystem where single-file trimmed publish is a normal deployment mode, the window of "flagship deployment mode is the unsupported mode" is the proposal's weakest position. Mitigant: the `IXamlTypeMetadataProvider` seam is genuinely designed in from day one, so the trajectory is credible — but the phasing buries it.
5. Smaller: parse-time member resolution requires Fork A's property registry populated *before* first parse (module-init ordering hazard, unstated); `XamlMember.SetClr` (`Action<object,object?>`) boxes struct values on every CLR-fallback set (load-time only, acceptable, unacknowledged); hot-reload claims ("structurally cheap") describe a file-watcher and a blunt rebuild — true but oversold as a differentiator since P3 gets the same thing and P2 ships a previewer.
6. **Perf numbers are estimates presented as facts** ("1–3 ms cold", "tens of µs"). Plausible, but per repo convention these should be Phase-X0 pinned benchmarks, not prose.

**What survives the adversarial pass:** the parse-once/instantiate-many split with templates as node-graph slices is the genuinely strong idea — parse-time validation of template bodies, deferred resource dictionaries recovered from the same mechanism, and stamping cost ≈ object allocation with zero re-conversion (folded constants). The SoA storage is intricate but testable in isolation, and every consumer-visible seam (`ITemplateContent`, metadata provider, `LoadComponent`) is producer-agnostic, so the compiled path is additive. The X0-heavy testing plan (fuzz + golden diagnostics + dual-provider suite runs) matches the house style.

### Proposal 2 — source generator

**Flaws and unsupported claims:**

1. **The incrementality design as written is unsound.** Stage B needs live `ISymbol`s; symbols come from a `Compilation`; a `Compilation` changes on every keystroke. The proposal's answer — "re-acquires live symbols via a thread-local `Compilation` handle only inside this step" — is exactly the pattern Roslyn's incremental-generator guidance forbids (stale compilations, IDE races, leaks). The two-tier fingerprint controls when outputs are *considered* changed, but it cannot conjure a fresh Compilation into a step whose inputs deliberately exclude it. The honest alternatives are (a) accept per-keystroke re-binding (~0.5 s background for 200 docs — tolerable but then the advertised "all-Cached on unrelated edit" CI invariant is unachievable), or (b) lower the entire bound model into equatable strings before the output step (much more Stage-B work than estimated). This is the proposal's central engineering claim and it doesn't hold as specified.
2. **Per-document invalidation is oversold.** Local type skeletons are `.Collect()`ed into one input; any type *shape* change anywhere invalidates the combined input. "Re-runs binding for the documents whose skeleton set changed" implies per-document dependency subsetting that is never designed.
3. **Build-time `StaticResource` resolution fights its own `ThemeVariant` design.** Theme dictionaries are selected at app start from negotiated `TerminalCapabilities` — i.e., assembled at *runtime*. Most theme keys therefore can't be build-time-traceable, falling to `XamlRuntime.FindResource` plus a suppressible `CXAML0201` warning on every one. The flagship diagnostic for resources becomes noise in precisely the theming scenario the same proposal promotes. Direct local references for lexically-resolved keys also bake out any runtime shadowing — a quiet semantic deviation from load-time resolution.
4. **It is the dual-implementation architecture it condemns.** The interpreter ships (previewer, conformance, plugins) and is "deliberately a subset" — no compiled bindings, no `x:TypeArguments`. So the previewer diverges from production behavior in exactly the binding-heavy screens people preview. The shared front end covers grammar, not object-construction semantics; the binder/lowering and the interpreter's reflection walk are two implementations of the semantic core, kept honest only by corpus discipline.
5. **Effort is optimistic by ~1.5–2×.** 10–13 weeks for: a symbol-backed binder (member tables, converters, content/collection shapes, attached properties), a diagnostics surface, compiled bindings including two-way/converters/StringFormat, templates, resources, theme variants, the props packaging, *plus* the interpreter, previewer tool, XSD tool, and a conformance corpus — at this repo's adversarial-review bar. The proposal's own risk #1 admits 12–18 KLOC of permanent generator code, the project's single largest UI-layer artifact, hosted in netstandard2.0 with Roslyn memory rules — the hardest debugging environment available.
6. **Cross-fork churn cost is unpriced.** Generated code lowers *directly into Fork A/B's object model at compile time* (the `FooProperty` field convention, `IBindableObject`, Style/Setter ctors). Forks A and B are being designed concurrently; every object-model rename breaks emission and invalidates snapshot tests. An interpreted loader absorbs that churn at one indirection layer; a generator pays it in full, repeatedly, during exactly the period when the object model is least stable.

**What survives:** the runtime artifact is unimpeachable — straight-line debuggable C#, `#line`-mapped breakpoints in markup, compiled typed bindings with build-checked paths (`CXAML0301` is the single best day-to-day DX item in the packet), strict-AOT mode that is clean *by construction*, build-time folding of access-key literals, and the capability-shaped `ThemeVariant`. The "worst-case failure is graceful" closing argument is also genuinely true: the object model and front end survive a generator retreat.

### Proposal 3 — vendored Portable.Xaml

**Flaws and unsupported claims:**

1. **30–45 KLOC of foreign, `#nullable disable`, Mono-lineage reflection code is the worst possible fit for this repo.** The codebase hand-rolls PNG decoding to avoid dependencies and runs adversarial reviews over its own 2-KLOC modules. Nobody on the team knows this engine; every deferral/ambient/fixup bug is a spelunking expedition through code written to a 2010 design with different allocation values. "Frozen plumbing" is false the moment you touch it — and the proposal already commits to touching it (strip `public`, replace `Expression.Compile` paths, thread provenance through). Owning a fork is not cheaper than writing the ~7 KLOC you actually need; it's renting 40 KLOC to use 7.
2. **The `[ThreadStatic]` template-replay ambient is a bug factory.** Provenance (`ValueSource.TemplatedParent`) is smuggled around the engine via thread-static scope because the engine has no native channel for it. Nested template instantiation, exceptions unwinding past the scope, and any future async touchpoint all corrupt it silently — and corrupted provenance is precisely the "priority interaction" bug class this lens flags.
3. **Forward-reference fix-ups break the proposal's own seams.** The engine's fixup machinery assigns values *after* `EndObject` — potentially after the template-replay ambient has popped and after `ISupportInitialize` batching closed. The set-interceptor will see late assignments with the wrong provenance context. Unaddressed.
4. **Worst performance of the three, by its own numbers.** 1–5 µs/node replay, 50–150 µs per template stamp, 10–30 ms for 200 controls — versus P1's folded array walk and P2's delegate invoke. No constant folding: every `Margin="2,0"` re-parses and re-boxes *per stamp*. Restyling storms and virtualized scrolling (the lens's named scenarios) hit exactly this path; the mitigation ("compiled factory cache on Nth instantiation") is explicitly not in v1. Eager dictionary instantiation in v1 compounds it at startup.
5. **"Trim-safe by construction" is asserted, not demonstrated.** DAM annotations on `RegisterType<T>` root public members, but the engine internally reflects through paths (`LookupAllMembers`, `MakeGenericType`, converter dispatch, possible `Expression.Compile`) that a 40-KLOC audit must clear. The Phase-0 spike gates some of this — good — but the headline claim outruns the spike.
6. Credit where due: the System.Xaml non-viability evidence is sourced and correct; the package-dormancy disqualification is the honest version of the dependency argument; the Phase-0 spike with kill criteria and the Windows System.Xaml oracle CI leg are the best *process* artifacts in the packet; and its "spec long tail" warning (deferral, ambient, namescope duality, MEL escaping, fixups) is the most useful critique of P1 — it is the checklist P1's "Deviations from WPF" doc must answer item by item.

---

## 3. Ranked verdict

**1. Proposal 1 — runtime loader.**
At this project's scale, the runtime loader buys ~90% of P2's developer experience (build-time diagnostics via the X4 generator, typed fields, line-accurate errors everywhere including runtime) and ~95% of its performance (zero per-frame cost, folded-constant template stamping) for roughly half the mandatory engineering and a fraction of the invariant load. Its semantics live in a library, not in generated code baked into consumer binaries — which matters enormously while Forks A and B are still moving. Its deferred-content design (node-graph slices) is the strongest single mechanism proposed by anyone: parse-time-checked templates, free deferred dictionaries, stamp cost ≈ allocation. Its weaknesses — the unacknowledged symbol-backed second binder in X4, the AOT window, folded-box aliasing — are all repairable by grafts and re-phasing, not by re-architecture.

**2. Proposal 2 — source generator.**
The right end state and the wrong first move, as P1's steelman argues and P2 never actually rebuts at terminal scale. Its decisive wins (AOT-by-construction, compiled bindings, runtime cost identical to handwritten C#) are real, but its central incrementality claim is unsound as specified, its effort estimate is optimistic, its resource-resolution story contradicts its own theming design, and it ships the dual implementation it condemns. Crucially, its own closing argument concedes the ordering: "if the generator proves too costly... the system degrades to [a runtime loader] with a better object model." Build that object model and the checked pipeline's *seams* first; build the compiler when profiling demands it.

**3. Proposal 3 — vendored Portable.Xaml.**
The best-researched proposal and the worst recommendation. It optimizes time-to-spec-fidelity, which is not the binding constraint, while taking the maximum position on the constraint that *is* binding for this lens: long-term maintainability of code mass the team doesn't own, can't fully audit for trimming, and must patch through fragile seams (`[ThreadStatic]` provenance, fixup timing). It is also last on performance in the scenarios the lens names. Its process artifacts deserve to outlive its architecture.

---

## 4. RECOMMENDATION

**Build Proposal 1's loader as the spine, with Proposal 2's binding/AOT contracts designed in from day one and its metadata-generation phase pulled forward.** Concretely:

1. **Adopt P1 X0–X3 unchanged** (node model, parser, instantiator, extensions, deferred slices, namescopes) — with the §2 fixes: a pinned immutability contract for `IsContextFree` converters, a specified retention rule for captured lexical scopes (dictionaries only, weak hosts, documented in the cross-fork contract), and Phase-X0 pinned benchmarks instead of prose numbers.
2. **Re-phase X5's metadata-provider generation into X4.** The generator package's first release must ship build-time validation *and* the generated `IXamlTypeMetadataProvider` + strict-mode flag (P2's `CursorialXamlStrictAot`, auto-set by `PublishAot`). This shrinks the "trimming unsupported" window from five phases to one and makes the AOT trajectory a deliverable, not a promise.
3. **Name the dual binder and contain it.** X4 requires a symbol-backed resolution backend; say so, budget it (+1–1.5 KLOC), and keep it honest with P2/P3's conformance-corpus discipline: every fixture runs through the runtime loader and the analyzer binder, asserting identical diagnostics.
4. **Adopt P2's compiled-binding descriptor contract** (`Binding.Compiled<TSource,TValue>` + per-segment getters, `x:DataType`, the missing-path-member diagnostic) as the Fork A/C contract *now*, even though v1 bindings run reflectively. The X4 generator is the designated future emitter; the contract costs nothing today and prevents a redesign later.
5. **Do not vendor Portable.Xaml**, but adopt P3's process: a Phase-X0 spike with kill criteria for the node-graph design, and a Windows-only CI leg running the escape/whitespace/ambient-resolution corpus against genuine System.Xaml as the behavioral oracle for every documented deviation.

This composition keeps one semantic implementation in the library, gives consumers diagnostics at build and line-accurate errors at runtime, hits zero per-frame cost and allocation-cheap template stamping immediately, and leaves the compiled path (templates as generated `ITemplateContent`, compiled bindings, full AOT) as the additive Phase-6-style endgame all three proposals independently converge on.

---

## 5. GRAFT LIST

**From Proposal 2 (source-gen):**
- `Binding.Compiled<TSource,TValue>` descriptor shape + per-segment getter emission + `x:DataType` — the Fork A binding contract and the future generator target (graft #4 above).
- `CursorialXamlStrictAot` / `PublishAot`-triggered strict mode semantics for the metadata-provider generator.
- **Access-key folding at parse time**: fold `Header="_File"` into an `AccessText("File", 'F', 0)`-equivalent during P1's stage-1 constant folding (P1 currently defers underscore parsing to controls at runtime; folding is free in the loader and eliminates per-render string scanning for req. 6).
- **`ThemeVariant` keyed on negotiated `ColorDepth` + background-luminance dark/light** — the capability-shaped theme axis is the best terminal-native idea in the packet; propose it to Fork B regardless of pipeline.
- Incrementality **step-reason regression tests** ("all-Cached on unrelated edit") as pinned CI invariants for whatever the X4 generator becomes — P2's most transferable engineering discipline.
- `#line`-mapping and deterministic-local emission conventions, shelved for the eventual compiled producer.
- `x:TypeArguments` as a recognized-but-punted directive with a reserved diagnostic, so the grammar doesn't have to change when the compiled path makes it cheap.

**From Proposal 3 (reuse-xaml):**
- The **Phase-0 spike with explicit kill criteria** pattern, applied to P1's X0 (line-info fidelity, slice replay, ME grammar oracle cases).
- The **Windows System.Xaml oracle CI leg**: diff P1's documented deviations (whitespace, `{}` escaping, ambient `Setter.Property` resolution, StaticResource-in-template scoping) against the genuine article, converting the "Deviations from WPF" doc from assertions into measurements.
- The **spec long-tail checklist** (deferral, ambient, namescope duality, forward references, MEL nesting/escaping, line-info propagation) as the table of contents for P1's deviation/punt register — each item gets a resolved-decision entry, per house style.
- The `.cxaml`/`.axaml`-style **file-extension rationale** (keep the VS WPF designer from claiming files) — adopt the extension decision consciously either way.
- The salvage-path framing ("keep reader + grammar, rewrite the writer") as the documented fallback if P1's instantiator hits an unforeseen wall.

**From Proposal 1 itself (flagged so they survive into the design doc):** setter property/value folding against ambient `TargetType` at parse time (errors WPF only finds at runtime); deferred resource-dictionary entries recovered from the template slice mechanism; did-you-mean Levenshtein diagnostics (P3 independently proposed the same — convergent evidence it's cheap and high-value).