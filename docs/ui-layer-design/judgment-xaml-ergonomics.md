---

# Fork C — XAML Pipeline: Judge's Verdict

## Summary

Three proposals for Cursorial.UI's XAML pipeline were evaluated: a custom runtime loader with a two-stage parse/instantiate split (`runtime-loader`), a Roslyn source generator that compiles XAML to C# at build time (`source-gen`), and a vendored fork of Portable.Xaml hidden behind Cursorial-owned seams (`reuse-xaml`). All three produced proposals that are substantively detailed and honest about their costs. The judgment is close at the top. `reuse-xaml` is eliminated cleanly; `runtime-loader` and `source-gen` are genuinely competing, with the winner determined by sequencing and risk profile rather than by the superiority of either end state.

---

## Scoring Table

| Criterion | runtime-loader | source-gen | reuse-xaml |
|---|---|---|---|
| Common-case ergonomics (declare, style, bind) | 8 | 8 | 7 |
| Debuggability (binding errors, style not applying, template issues) | 8 | 9 | 6 |
| XAML readability | 9 | 9 | 8 |
| WPF/Avalonia veteran learning curve | 9 | 8 | 8 |
| Footgun density | 7 | 8 | 5 |
| Framework/repo convention consistency | 9 | 8 | 5 |
| Implementation correctness & risk | 8 | 7 | 6 |
| Long-term maintainability | 8 | 7 | 4 |
| AOT / trimming story | 6 | 10 | 6 |
| Hot reload / dev loop | 10 | 8 | 9 |
| **Weighted total** | **82** | **82** | **64** |

The tie at the top is not coincidental. They are genuinely competing approaches with different tradeoff profiles. The tiebreaker is sequencing and staffing risk, argued below.

---

## Findings by Proposal

### Proposal 1: runtime-loader

#### Strengths

**The two-stage architecture (parse once / instantiate many) is exactly right for this project.** The immutable `XamlDocument` node graph with constant-folded literals is structurally aligned with the codebase's pervasive use of `readonly record struct` and cached immutable objects. Parse cost amortized to zero at instantiation time is the correct answer for a render-loop-adjacent framework where per-frame allocations accumulate at 50 fps.

**Diagnostic ownership is a genuine differentiator.** The proposal delivers `MainWindow.xaml(42,17): CUR1203: StaticResource 'AccentBrush' not found; searched: TemplateScope('ButtonChrome') → Window('MainWindow') → Application` — that is exactly the error a developer actually needs, and it requires owning the parser to produce it. Runtime loaders that surface errors through a third-party object writer emit messages like "Object writer failed to set member 'Background': value 'AccentBrush' not found" with no XAML position. The proposal gets this right.

**The deferred-template mechanism (node graph slices) is technically sound and cheap.** Sharing folded constants across template builds means a `ListBox` realizing 500 items does zero converter work and zero re-parsing. This is the right architecture.

**Hot reload is structurally free.** Because the runtime loader must exist anyway (for tools, dynamic markup, and — as `source-gen` itself concedes — the previewer), the proposal gets hot reload for no additional architectural cost. This matters more for terminal apps than the proposal's prose implies: the dev loop for a TUI is literally edit → observe in a terminal, and a sub-100ms round-trip via file watcher is the best possible experience.

**The `ITemplateContent` / `IDeferredValue` cross-fork seams are correctly shaped.** They are the same shapes `source-gen` independently converged on, which confirms they are natural to the problem domain, not artifacts of one advocate's perspective.

**Setter folding at parse time (§3.2, "Setters get special folding")** is a specific improvement over WPF: `<Setter Property="Background" Value="…"/>` resolves the property and folds the value at parse time, making property-not-found a parse error rather than a runtime exception. This is the right place to catch it.

#### Flaws

**Critical: The `ValueProvenance` enum in §5 is incomplete and inconsistent with Fork A Hybrid's established priority model.** The proposal lists `Local` and `TemplateLocal`, and adds "Fork A's full ladder" as a comment placeholder. Fork A Hybrid's one-winner-per-priority flat-table model uses `BaseValueSource` as a diagnostic artifact and maps multiple value-source levels into a packed priority key. The XAML loader needs to fit into that model specifically: `Local` must correspond to Fork A's `Local` priority, `TemplateLocal` must correspond to Fork A's `TemplatedParent` priority, and the loader must not invent its own priority numbering. This is a cross-fork coordination gap that will cause bugs if left to implementation-time discovery.

**Major: The markup-extension grammar's positional-argument mapping is underspecified.** The proposal says "positional arguments map to the extension's primary member (`Path` for Binding, `ResourceKey` for the resource extensions, ctor-arity match for custom extensions)" but does not specify how positional mapping is resolved for custom extensions with multiple parameters — by convention (first `init`-able property), by a mandatory `PositionalParametersAttribute`, or by constructor arity? WPF used constructor arity; Avalonia uses a constructor-mapping convention. This is a surface where custom extensions from third-party libraries will hit undocumented behavior.

**Major: The `[DeferredContent]` attribute as the template mechanism is a design smell.** Marking a property `[DeferredContent]` to change the loader's behavior is implicit and fragile — it works only if the property type is also `ITemplateContent`. The correct mechanism (which `reuse-xaml` gets right via `TemplateContent`-typed members) is to key deferral on the member's declared type: if a property is declared as `ITemplateContent`, the loader defers it. Using an attribute allows the type system to be bypassed (a `string` property with `[DeferredContent]` is nonsensical). The proposal should make deferral a type contract, not an attribute opt-in.

**Major: Event handler binding in `IsupportInitialize` context is not fully specified.** The proposal says events inside deferred content are CUR2301 at parse time in v1, but does not address whether event handlers can be wired to anything other than the `x:Class` root's type. In WPF, templates can wire events to any handler on the code-behind class through namescope gymnastics; the proposal bans this but does not provide an alternative guidance pattern (commands/`TemplateBinding` are mentioned but not defined). A WPF veteran will hit this immediately when trying to put a `Click` handler on a button inside an `ItemTemplate`.

**Minor: The `XamlHotReload.Enable(loader)` design (§3.7) uses weak references to live roots, which creates a non-obvious lifetime dependency.** If the consumer does not hold a strong reference to the root window (possible in app patterns where the `Application` class holds a reference but the stack frame does not), the hot-reload weak registry silently loses track of the window and reload silently fails. This should be documented explicitly.

**Minor: `XamlLoader.Shared` as a process-wide cached loader is a footgun for test isolation.** The proposal does not address how tests using `Shared` isolate cache state. A `XamlLoader.CreateIsolated()` factory or a per-test `XamlLoader(options)` is needed, and `Shared` should be documented as "not for test code."

**Minor: The `ConverterCulture` on `XamlLoaderOptions` defaults to `InvariantCulture`, which is correct, but the proposal does not address how localized value strings interact with constant folding.** A property that is context-free under `InvariantCulture` may need to be treated as context-dependent when the app overrides the culture. The `IsContextFree` flag on `ITypeConverter` should be defined relative to a specific culture, or the folding logic should be documented as always using `InvariantCulture` regardless of the options (both are defensible; the choice should be explicit).

---

### Proposal 2: source-gen

#### Strengths

**The AOT and trimming story is genuinely superior and structurally clean.** The generator emits references to every type it constructs, so the trimmer's analysis is complete by construction. No root annotations, no descriptor files, no `[DynamicallyAccessedMembers]` pyramids on public surfaces. For an app that wants to publish as a self-contained trimmed binary — the terminal ecosystem's distribution norm — this is the correct architecture.

**Build-time error detection for binding paths is the single biggest day-to-day quality win in the entire proposal set.** `{Binding Selected.Name}` failing with error `CXAML0301` at `MainView.xaml:23` during `dotnet build`, with the span pointing to exactly the right attribute value, is qualitatively better than the equivalent runtime `TargetInvocationException` with a stack trace that starts inside the binding engine. The proposal is right that this matches the project's culture of "oracle-pinned tables and compile-checked invariants."

**`x:TypeArguments` support is near-free for a compile-time system and painful for a runtime loader.** The proposal correctly identifies this as a concrete differentiator. An `ItemsView<FileItem>` is a common enough pattern that its absence is felt.

**The two-tier incremental fingerprint strategy (§3.2) is a thoughtful and correct solution to the classic source-generator keystroke-latency trap.** Tier 1 keyed on reference identity (assembly MVID list) and Tier 2 keyed on type declaration skeletons ensures that editing a method body in an unrelated class does not trigger XAML regeneration. The step-reason regression tests for this (`all-Cached on unrelated edit`) are the right thing to make a pinned invariant from day one.

**The shared front-end / conformance corpus design cleanly bounds the semantic drift risk** between the compiled and interpreted paths. Running every `.xaml` fixture through both the generator and the `Interactive` interpreter and asserting identical widget trees is the right oracle discipline.

#### Flaws

**Critical: Generators cannot see other generators' output, and the proposal's mitigation is inadequate.** The proposal acknowledges this as "risk #3" and says the workaround is "move such types to a referenced project" or that viewmodel generators still work because "the type is user-declared." But this breaks down in a common real scenario: an `INotifyPropertyChanged` source generator (e.g. CommunityToolkit.Mvvm's `[ObservableProperty]`) generates the actual property name and type on the partial class. A compiled binding `{Binding FileName}` where `FileName` is a generated property will produce `CXAML0301` because the generator never sees `FileName`. The proposed mitigation (use reflection binding, suppress CXAML0302) defeats the primary value proposition — the entire point of `x:DataType` is to have binding paths checked at build time. This is a structural limitation of the Roslyn generator model that cannot be fixed without IL weaving.

**Critical: The `#line`-mapped generated code story breaks down at the property-element syntax level.** The proposal shows `#line N "Views/MainView.xaml"` before each statement. But a single XAML element often spans multiple lines (opening tag, nested property elements, closing tag) and generates a local variable declaration plus several `SetProp` calls. The generated code for one element will have multiple `#line` directives pointing at different attribute spans. A debugger breakpoint on `__e0.ItemContainerStyle = __style0;` shows `MainView.xaml:27` — which is the `ItemContainerStyle` attribute — not the element. This is the same experience Avalonia users have with `AvaloniaXaml`-compiled code: "the breakpoint is in the right file but the line is the attribute, not the element." It is acceptable but the proposal's claim that "breakpoints set in the .xaml file bind" is slightly optimistic about what the experience feels like in practice.

**Major: The generator complexity estimate is substantially understated.** The proposal estimates "~12–18k LOC across front end + binder + emit." The Avalonia XamlX compiler is approximately 30–40k LOC not including its XamlIl backend, and Avalonia's XAML compiler (built on XamlX) adds another ~20k LOC of Avalonia-specific binder. Both numbers are after years of hardening by a team with deep XAML semantics knowledge. A 12–18k LOC estimate for a new XAML compiler covering the same semantic surface — templates, ambient properties, namescopes, the markup-extension grammar with full nesting — is not credible. The proposal's own Phase 0 (front end only) is estimated at 1–1.5 weeks; the equivalent in `runtime-loader` (the same front-end work) is estimated at ~2.5 KLOC. The source-gen binder (Stage B) is where the complexity actually lives, and the proposal does not have a bottom-up LOC estimate for it.

**Major: The `DynamicResource` story in a compile-time system is fundamentally weaker than in a runtime-interpreted system.** The proposal handles `{DynamicResource}` by emitting `widget.BindResource(Prop, "K")` — a subscription through the `IResourceHost` chain at runtime. This is correct, but it means the `{DynamicResource}` key can only be validated at the `XamlRuntime.FindResource<T>` call site, which is exactly the runtime failure the rest of the proposal's error story tries to avoid. The proposal acknowledges this with "warning CXAML0201 (suppressible; dictionaries can legally be assembled at runtime)" but does not sufficiently stress that for any real theme system built on `DynamicResource` — which is the primary use case for dynamic theming — the compile-time validation story effectively disappears. The AOT/trimming win survives, but the "errors at build time" win does not for the 80% of style properties that use `DynamicResource`.

**Major: The `TemplateBinding` lowering in the generator is described but the scope constraint is not enforced in the described way.** The proposal says "`TemplateBinding` is lowered to `target.BindTemplate(TargetProp, SourceProp, __ctx.TemplatedParent)` — one-way, no path walk, property identities resolved at build (CXAML0304 if the template's `TargetType` lacks the property)." But `TemplateBinding` is only meaningful when inside a `ControlTemplate`, and the generator must trace the lexical XAML tree to determine whether the current position is inside a deferred `ControlTemplate` body. The proposal does not describe this lookup. A `{TemplateBinding Foreground}` appearing directly in a `DataTemplate` should either be an error or degrade gracefully; the proposal does not specify.

**Minor: The `Binding.Compiled<TSource, TValue>(static vm => vm.Files, path: "Files")` factory shape requires the lambda and the string path to be kept in sync manually.** In Avalonia compiled bindings, the path string is inferred from the lambda by the compiler (the lambda IS the path expression). Requiring the consumer to also supply a redundant `path:` string for the binding engine to use for change subscription is a consumer footgun: `static vm => vm.Files, path: "FileName"` silently binds to one property but subscribes change notifications for another.

**Minor: The `ThemeVariant` type is placed in `Cursorial.UI.Markup` (owned by Fork C) but is semantically a Fork B concern.** Resource variant selection is a styling/theming feature driven by the runtime terminal capabilities — it logically belongs in the styling/resource layer (Fork B), not in the XAML markup pipeline. Fork C should depend on a `ThemeVariant` from Fork B, not define it.

---

### Proposal 3: reuse-xaml

#### Strengths

**The proposal is admirably honest about System.Xaml's unavailability** (Windows Desktop runtime only, not `dotnet/runtime`) and about Portable.Xaml's dormancy (last release October 2020). Correctly eliminating System.Xaml from consideration outright saves everyone time.

**The node-stream architecture (XamlXmlReader → transform → XamlObjectWriter) is the correct structural model** and produces the cheapest hot-reload and preview story of any approach: file-watch re-parse, replay the node stream into a fresh writer. The proposal is right that this is "nearly free" once the engine exists.

**The set-interceptor seam (§3.4, XamlObjectWriterSettings.XamlSetValueHandler) is a clever and correct way to bridge the engine to Fork A's property system** without contaminating the engine with property-system types. The ordering — deferred values first, then UIProperty fast path, then events, then POCO fallback — is the right priority model.

**The `TemplateContentLoader` (§3.5) correctly uses the engine's `XamlDeferringLoader` contract** to capture a node list rather than parsed objects, preserving line info in the deferred content and delivering true WPF-grade template semantics. The proposal is right that this is the hardest feature in any XAML system, and the engine provides the correct mechanism for free.

#### Flaws

**Critical: Vendoring 30–45k LOC of an opaque, unmaintained C# library as the load-bearing core of the Cursorial UI layer is architecturally inadvisable and directly contradicts the project's zero-external-dependency stance.** The proposal argues this is "the honest version of the dependency" versus a NuGet binary, which is true in one sense — you can see the source and fix bugs. But the project has never taken on a dependency of this size and vintage for a core concern. The Drawing, Rendering, and Core layers have zero production dependencies (by design, per CLAUDE.md). Introducing a 40k LOC codebase as the UI layer's foundation — re-namespaced internally but owned by the framework — creates a maintenance burden that the proposal significantly underweights. The fork is not "frozen plumbing." XAML loading semantics interact with the property system, the resource system, and the template system in non-trivial ways. Every new feature in Forks A or B may require engine changes that involve understanding 40k LOC of Mono-lineage code.

**Critical: The Phase 0 kill criteria are necessary but the proposal does not commit to what "salvage path" means for the project timeline.** If the Phase 0 spike kills the Portable.Xaml approach — a genuine risk, per the proposal's own honest accounting — the team has spent 1–2 weeks and must pivot to one of the other proposals. The proposal says "keep XamlXmlReader + MEL parser + node model, hand-write a smaller object writer." But XamlXmlReader is the engine's code. If the engine is killed, the salvage path is essentially writing a runtime loader from scratch anyway, but with the added debt of having first committed to the Portable.Xaml architecture. The proposal does not price this correctly.

**Critical: The one-public-engine-type rule (`MarkupExtension`) is more porous than stated.** The proposal says "No engine type appears in their compile-time surface — A and B never reference Cursorial.UI.Xaml." But `MarkupExtension` *is* an engine type; it is re-namespaced into `Cursorial.UI.Xaml` but its implementation comes from the vendored engine. This means: (a) the semantics of `ProvideValue(IServiceProvider)` — the exact services available, the service interface types, the exception contract — are all defined by the engine; (b) third-party markup extensions must understand the engine's service provider model to function correctly; (c) if the engine is ever replaced with a compiled backend in the future, every third-party `MarkupExtension` subclass is an incompatible breaking change. The "one public type" claim understates the actual coupling.

**Major: The eager resource dictionary instantiation in v1 is a known regression from WPF behavior** and the proposal does not adequately address the user-visible consequences. WPF's BAML deferred dictionary means that a 300-entry theme file has near-zero startup cost; this proposal's "v1 instantiates entries eagerly" means that a theme file with 300 `SolidColorBrush`/`Style` entries instantiates all 300 at parse time. The proposal says this is fine because "terminal-scale theme dictionaries don't justify" the per-entry deferral, but a theme that includes `DataTemplate`s and `ControlTemplate`s — which is the normal use case for a styled application — pays the full template-instantiation cost up front even for templates that are never used. The `TemplateContent` node-list mechanism makes per-template deferral structurally available; the proposal should commit to deferring at least templates in v1.

**Major: The source generator in this proposal has the lowest error-detection coverage of all three.** The `Cursorial.UI.Xaml.Generators` in this proposal emits only three things: typed fields for `x:Name`, `InitializeComponent`, and the trim manifest. Build-time validation covers "malformed XML, unknown element names, duplicate x:Name, x:Class mismatch." Unknown member names, misspelled property values, binding paths, converter failures, and StaticResource misses are all runtime errors. The `runtime-loader` proposal's generator (Phase X4) delivers a superset of this because it runs the same parser as the runtime loader; `source-gen` delivers far more. The generator is the weakest part of this proposal.

**Minor: The `CursorialXaml.Parse<T>(string xaml, XamlLoadOptions? options)` method on the static loader facade is a footgun for production code.** Providing an in-memory string parse API on the same surface as `LoadEmbedded` makes it easy to accidentally load XAML strings at runtime (from network, from user input) in a context where the application expected only embedded markup. The `Parse` surface should be on a separate `CursorialXamlDevelopment` or `CursorialXamlTesting` type, or should carry a `[Conditional("DEBUG")]` attribute.

**Minor: Using the standard WPF `x:` namespace URI (`http://schemas.microsoft.com/winfx/2006/xaml`) is pragmatic for muscle memory** but creates a semantic hazard: tooling, documentation, and Stack Overflow results for this URI discuss WPF's `x:` semantics, some of which Cursorial does not support (e.g., `x:Uid`, `x:Shared`, `x:FactoryMethod`). The Cursorial-specific `https://cursorial.dev/xaml` namespace used in proposals 1 and 2 is a cleaner choice, even at the cost of WPF editor completion support.

---

## Ranked Verdict

1. **runtime-loader** — delivers the full semantic surface with the correct architecture, fits the project's zero-dependency stance, owns the error story from the right layer, and is the prerequisite for hot reload whether or not a generator is added later. Its flaws are fixable design gaps, not structural problems.

2. **source-gen** — the correct end state. Should be viewed as Phase X4/X5 of the `runtime-loader` roadmap, not a competing first move. The binding-path error story and AOT cleanliness are compelling and should be delivered; they should not be the *first* delivery.

3. **reuse-xaml** — eliminated primarily by the vendoring architecture, not by the node-stream design. The node-stream model itself is sound and should inform the runtime-loader's internal representation choices (the `runtime-loader`'s structure-of-arrays node graph is the right analogue).

---

## Recommendation: runtime-loader wins, with a structured migration path to source-gen

**Phase X0–X3 of the runtime-loader proposal should be implemented as designed**, with the following corrections:

**Mandatory corrections before X0:**

1. **Replace `[DeferredContent]` attribute with type-contract deferral.** A property declared as `ITemplateContent` (or an explicit registry entry in `XamlLoaderOptions.DeferredMemberTypes`) triggers deferral. Remove the attribute entirely. This aligns with the `reuse-xaml` proposal's correct instinct that deferral belongs in the type system, and eliminates the attribute footgun.

2. **Extend `ValueProvenance` to align with Fork A Hybrid's confirmed priority model.** At minimum: `Local`, `TemplateLocal`, `Style`, `StyleTrigger`, `Inherited`, `Default`. The values must map exactly to Fork A Hybrid's `BaseValueSource` enum values. Coordinate with Fork A authors before code is written.

3. **Define positional-argument resolution for custom markup extensions explicitly**, either by declaring a `PositionalParametersAttribute` on the extension's primary property or by adopting the Avalonia convention (a constructor whose parameter names match the property names is the positional source). This must be in the X0 spec, not discovered at X1.

**Recommended corrections before X1:**

4. **Restrict `{TemplateBinding}` to positions lexically inside a `ControlTemplate` body at parse time (CUR2303).** The parse-time check is cheap: track whether the current instantiation context is inside a deferred member of a `ControlTemplate` type.

5. **Add `XamlLoader.CreateIsolated()` as a named factory for test/tooling contexts** that bypasses the process-wide caches. Document `XamlLoader.Shared` as "shared process-wide; not for test code."

**Phase X4 (the generator) should be explicitly planned as the migration point to source-gen semantics.** The design principle: Phase X4's generator produces `InitializeComponent`, typed `x:Name` fields, and build-time diagnostics by running the same X0 parser as an incremental Roslyn analyzer. The two-tier incremental fingerprint strategy from `source-gen` §3.2 should be adopted for the Phase X4 generator implementation. Phase X5 (AOT generator and compiled binding paths) should adopt the `source-gen` strict-mode / compiled-binding contract, using the same `Binding.Compiled<TSource, TValue>(static vm => vm.Files, path: "Files")` shape — but the `path:` redundancy in that shape should be removed; the lambda should be the sole path source, with the string derived from it (matching Avalonia's compiled binding ergonomics).

---

## Graft List

These specific ideas from the losing proposals should be incorporated into the winning runtime-loader implementation:

**From source-gen:**
- Two-tier incremental fingerprint for Phase X4's generator (§3.2 — avoid keystroke-latency regressions from day one).
- `ThemeVariant` capability-axis design — but own it in Fork B (not Fork C), with capability keys drawn from `ColorDepth` and `DefaultBackground` luminance. The `DynamicResource` re-evaluation on renegotiation is the right integration point.
- Strict-AOT mode flag (`CursorialXamlStrictAot`, auto-set from `PublishAot`) for Phase X5, promoting unresolved reflection-bindings to errors.
- `EmitCompilerGeneratedFiles` documentation pattern for the generator — the equivalent in the runtime-loader world is a `XamlDocument.DumpNodeGraph()` debug surface to make the stage-1 output inspectable.
- `AccessText` as a first-class type populated at load time from underscore-tagged strings, so the access-key underline index never requires runtime string scanning. The runtime-loader should apply this conversion in the converter for any property declared as `AccessText` or `object` on a `Header`-like member.

**From reuse-xaml:**
- The explicit Phase 0 kill criteria pattern — adopt the same discipline for the runtime-loader's X0 parser fuzz pass: define upfront what failure modes would require design revision, rather than discovering them at X2.
- The `TemplateContent.Instantiate(in TemplateInstantiationContext context)` shape (rather than `ITemplateContent.Build(in TemplateBuildContext context)` from the `runtime-loader` proposal) — the naming `Instantiate` is more discoverable and correctly implies "produce a new instance" vs `Build` which implies "under construction." This is a naming suggestion, not a structural change.
- The explicit `IDeferredValue.AttachTo(IUiObject target, UIProperty property)` seam for `{Binding}` and `{DynamicResource}` result objects. The `runtime-loader` proposal has the right concept (`BindingOperations.Apply` + `ResourceReference`) but the `AttachTo` interface is more composable — any Fork A/B type can implement it, and the loader's set interceptor stays simple.

---

## Open Questions for the Author

1. **Cross-fork timing on Fork A's `UIProperty` registry.** The `runtime-loader` proposal's X2 phase requires Fork A's property registry to be stub-able. Fork A Hybrid was selected with a static-field convention (`BackgroundProperty`, `ForegroundProperty`, etc.). Can Phase X0 and X1 of the XAML loader be implemented against a stub `IUIPropertyRegistry` that returns `null` for all lookups (CLR fallback only), allowing the loader phases to land before Fork A's property system is complete?

2. **What is the `ContentProperty` convention for `Window`?** The proposal uses `<DockPanel>` as the content of `<Window>` in its example (§2.7). Does `Window` have a `[ContentProperty("Content")]` attribute naming a `Content` property (WPF style), or a `Children`/`Child` collection (Avalonia style)? The choice here is Fork B's, but the loader's content-property resolution must agree with it. Given Fork B Hybrid's verdict, a `Content` property is the expected shape.

3. **Should the loader use `cursorial://assembly/path` URIs for embedded resources, or the `embedded://` scheme used in `reuse-xaml`?** The `cursorial://` URI is more on-brand but it requires implementing a custom `IXamlResourceProvider` that understands the scheme. `embedded://` follows the convention of Avalonia's `avares://` and is already named in the proposals. Either is fine; the choice should be made once and embedded in the X1 `.targets` file so it is never re-litigated.

4. **Is there a constraint on the `x:Class` naming convention from the build system?** The proposal embeds XAML files with `LogicalName="$(RootNamespace)/%(RelativeDir)%(Filename).xaml"`. The `LoadComponent` method must map from the runtime type back to this logical name. The mapping convention (type namespace + class name → embedded resource path) must be documented and tested, because any mismatch produces a silent failure (no embedded resource found) rather than a clear error. How should mismatches be reported — as a `XamlParseException` at `LoadComponent` time, or as a compile-time diagnostic from the X4 generator?