# S2 — oracle-pinned data-binding matrix (paths, anchoring, modes/triggers, pipeline, lifecycle, watch, compiled)

Status: **normative test specification**, authored 2026-06-12 *before any S2 engine code exists* (design doc §14 P4; the repo's matrix-first discipline, mirroring `precedence-matrix.md`, `layout-matrix.md`, `input-matrix.md`, and `style-matrix.md`). Every numbered row below becomes exactly one xUnit `[Fact]`/`[Theory]` in `Cursorial.UI.Tests` (test authoring contract at the end, §16). The binding engine is written *to* this matrix; a red row is an engine bug unless a PR amends this file first.

Canonical semantics sources, in precedence order: `docs/ui-layer-design.md` §6 (incl. the 2026-06-11 source-ladder pin in §6.3) + §0 invariants + §13 resolutions + §14 P4 **over** `docs/ui-layer-design/spec-binding.md`. Places the spec is superseded by the doc and this matrix pins the doc's side:

- ① **`IBindingWatch` has NO `Pause`/`Resume`** (doc §6.2, ledger B16) — watchers stay live across styling *deactivation* edges (they are the re-activation predicate) and are disposed at disarm/element-detach. The spec §2.3/§3.9 `Pause()`/`Resume()` parking surface is **superseded**; pause semantics live only on S7's `ResourceSubscription`. Rows in §13 assert the no-pause contract.
- ② **The source change-notification ladder** (doc §6.3, pinned 2026-06-11): MVVM sources are plain CLR objects, so per CLR-property hop the ladder is (1) `INotifyPropertyChanged` → subscribe; (2) else a convention-matched `[PropertyName]Changed` CLR event → subscribe (the WPF `PropertyDescriptor.AddValueChanged` analog **without** the `TypeDescriptor` global-table leak); (3) else observed-on-parent-change only + a one-time `Info` diagnostic. INPC wins when both exist. The spec's §3.2 accessor table is augmented by this ladder, not replaced; §3 of this matrix is the ladder's test surface.
- ③ **`LostFocus` trigger** rides **S3's routed `LostFocusEvent`** (physical focus moving off) **plus** `InputDispatcher.EditCommitRequested` raised on terminal focus-out — terminal focus-out *retains* keyboard focus and raises **no** `LostFocusEvent` (doc §6.6 step 4, §6.11.6, §13; the spec's "S3 LostFocus" wording is the doc's two-source mechanism). The pulse + the routed event are distinct flush sources (§7 rows).
- ④ **DataContext** is the inherited `StyledProperty<object?>` on `UIElement` (doc §6.4), and **S2 owns `UIElement.FindName`** (doc §6.3) — `= NameScope.FindEnclosing(this)?.Find(name)`. `NameScope`/`FindEnclosing`/the guarded walk live in `Cursorial.UI` (the §6.3 attachment points), consumed but not produced here (Fork C / S8 populate scopes).
- ⑤ Casing is `NotDataBindable`/`BindsTwoWayByDefault` per the existing `PropertyEffects` flags (already declared); the `UnsetValue` sentinel is `UIProperty.UnsetValue`; the SGR record disambiguation `using CellStyle = Cursorial.Output.Style;` is needed only where binding to a style-typed property is exercised.

**Phase 4 scope fence** (rows are written inside it — pinned by the prompt and doc §6.14): **MultiBinding / PriorityBinding** are deferred (`BindingBase.CreateExpression` is the recorded seam — a MultiBinding is N watch-legs + an `IMultiValueConverter`); **`INotifyDataErrorInfo` validation** is deferred (seam reserved: `BindingStatus` + a future `IBindingValidationSink` at write-back + the `:data-error` pseudo-class); **`Binding.Delay`** is deferred (needs a clock — revisit against S5's `UITimer`); **typed `IValueConverter<TIn,TOut>`** / **weak-subscription backstop** / **multi-arg indexers & path casts** / **collection views** / **`RelativeSourceMode.FindVisualAncestor`** are deferred (§6.14). **Compiled bindings** are *designed-now-implemented-at-X4/P10*: the `CompiledBinding<TSource,TValue>` descriptor shape + `Binding.Compiled` runtime lambda analysis ship in B2 with the **reflective fallback as the v1 producer**; the X4 generator is a second producer with no engine change (B3, §15 rows are descriptor-shape + diagnostics only).

Stage mapping (doc §6.13's ordered phases, sliced into the four implementation stages; rows for a later stage may stay unimplemented — not red — until that stage opens, but every row is binding from now):

| Stage | Sections | Delivers |
|---|---|---|
| **B0 — spine** *(unblocks Fork B `When` + general element bring-up)* | §§1–9, §13 | descriptors (`BindingBase`/`AnchoredBinding`/`Binding`/`RelativeSource`); `BindingPath.Parse` (property steps, int/string indexers, attached segments); reflection expression + `AccessorCache`; the source-ladder wiring (INPC / `[Name]Changed` event / parent-change degradation) + INCC; `DataContextProperty` + inheritance hookup + **the DataContext-as-target parent-anchor special case**; all five modes + `BindsTwoWayByDefault` + read-only-leaf degradation; `PropertyChanged`/`Explicit` triggers; `Source`/`RelativeSource.Self`/`TemplatedParent`/**`FindAncestor`** (arm/attach + reparent re-resolution); the full §6.6 forward pipeline + the reverse lane; free-standing + **frame-hosted** `BindingEntry` production + eviction lifecycle; expression registry + **replace-and-dispose** + **teardown-sweep integration** (closes the P1 `BindingOperations.TearDown` gap) + the DEBUG leak tracker; **`BindingOperations.Watch`** incl. ancestor-source; diagnostics ring + sinks. |
| **B1 — tree-shaped sources & focus** | §10, §11.B1, §14 | `ElementName` + `NameScope.FindEnclosing` (guarded walk) + deferred resolution on attach; **`UIElement.FindName`**; the `LostFocus` trigger (routed `LostFocusEvent` + `EditCommitRequested`); `BindingDiagnostics.Explain`; namescope conformance (content-child-vs-part-names). |
| **B2 — templates & compiled runtime lane** *(with the template engine phase)* | §12, §15.B2 | `TemplateBinding` fast path + descriptor validation + the untyped→typed bridge; `TemplateInstance.Detach`-eviction conformance against the `ValueFrame` kit; `Binding.Compiled` expression-tree analysis + the typed `BindingEntry<TValue>` push lane + anchored-compiled cases. |
| **B3 — generator handshake** *(with X4)* | §15.B3 | generator-emitted `CompiledBinding` descriptors; `x:DataType` build-time path diagnostics. No engine change — second producer only; B3 rows are descriptor-shape + diagnostic only. |

---

## 0. Conventions

### 0.1 Fixture

| Symbol | Meaning |
|---|---|
| `host` | `UITestHost.Create()` — 80×24, `TestCapabilities.KittyTruecolor` unless stated. `app` = `host.Application`; `dispatcher`/`focus` = the application's `InputDispatcher`/`FocusManager`. Trees attach via `host.ShowRoot(Root)`; rows follow mutations with `RunFrame()` unless asserting unit-level synchronous behavior. Unit-level rows (path parse, descriptor validation, pipeline) need no host — they exercise the engine directly on the calling (UI) thread. |
| `Vm` | the canonical `INotifyPropertyChanged` viewmodel: `string? Name` (raises `PropertyChanged`), `int Age`, `bool IsDirty`, `Vm? Sub`, `Addr? Address`, `ObservableCollection<string> Tags`, `Dictionary<string,object?> Map`. `RaiseAll()` raises `PropertyChanged(null)`. `Addr` = `{ string? City (INPC) }`. |
| `PlainVm` | a CLR object with **no** `INotifyPropertyChanged`: `string? Title` + a convention-matched `event EventHandler? TitleChanged`; `int Count` + `event EventHandler<EventArgs>? CountChanged`; `string? Note` with **neither** (no INPC, no `[Name]Changed`) — the parent-change-degradation hop. |
| `W` / `Wt` | `Widget : UIElement` test target. `Text` = `StyledProperty<string?>` default `null`; `Num` = `StyledProperty<int>` default `0`; `Flag` = `StyledProperty<bool>` default `false`, metadata `BindsTwoWayByDefault`; `Off` = `StyledProperty<int>` `AffectsComposite` (the compiled-lane hot property); `RO`/`KRO` = read-only `StyledProperty<int>` + its `UIPropertyKey<int>`; `NDB` = `StyledProperty<int>` metadata `NotDataBindable`; `Cmp` = `StyledProperty<string?>` metadata `Comparer = OrdinalIgnoreCase`. `Dir` = `DirectProperty<Widget,int>` (getter/setter delegates). |
| tree | Default: `Root` (`StackPanel`, fills viewport) → `paneA` → leaves `a`, `b` (Widgets); `paneB` → `c` where stated. `DataContext` set on `Root` unless a row re-anchors a descendant. |
| `obs(e, P)` | a recording `IValueObserver<T>`/`IUntypedValueObserver` subscribed on `e`'s property `P` — records `(old→new, Priority)` deliveries in order (the precedence-matrix `notify`/`silent` surface). `eff(e,P)` = effective value; `src(e,P)` = `GetValueSource` lane. |
| `expr` | the `BindingExpressionBase` returned by `Install`/`SetBinding`. `expr.Status` reads `BindingStatus`; `expr.EffectiveMode` reads the resolved `BindingMode`. |
| `watch` | the `IBindingWatch` from `BindingOperations.Watch`; `watch.Value` reads the last-delivered value; `wlog` records the `onValueChanged(object?)` callbacks in order. |
| `trace` | a test `IBindingTraceSink` added via `BindingDiagnostics.AddSink` (and/or `TraceEmitted`); `errs` = `BindingDiagnostics.RecentEvents` filtered to the row's `BindingFailureKind`. `Level` defaults Error unless a row sets Verbose. |
| `culture` | `BindingDiagnostics`/pipeline culture defaults to `CultureInfo.CurrentCulture` (§6.11.3); culture rows set `CultureInfo.CurrentCulture` (or pass `ConverterCulture`) explicitly and restore in a `finally`. |
| injectors | `set(e, P, v)` = `SetValue`; `scv(e, P, v)` = `SetCurrentValue`; `clear(e, P)` = `ClearValue`; `lostFocus(e)` = raise S3's routed `LostFocusEvent` at `e` (physical move-off); `termOut()` = `focusEvt(false)` → `EditCommitRequested(focused)`; `keystroke(e, P, v)` = the control-author `SetCurrentValue` write (TextBox model). |

### 0.2 Notation

- `Unset` = `UIProperty.UnsetValue` (the sentinel; `expr` produces it via `SetUnset()` — never a default clobber). `eff(e,P) == Default` means the store promoted to the property default after a `SetUnset`.
- `wlog == [Unset, "x", Unset]` asserts exact callback order; `∉ trace` asserts no event of that kind was recorded.
- `vm.Name = "x"` is a source write that raises `PropertyChanged("Name")`; `vm.Sub = s2` swaps an intermediate (identity change → downstream rewire).
- `→S` denotes a write reaching the **source** (target→source / write-back); `→T` a value reaching the **target** (source→target).
- "0 B" = `GC.GetAllocatedBytesForCurrentThread()` delta of zero after warm-up, single-threaded (repo norm; the reflection lane's one boxed leaf is exempt per the row's note).

### 0.3 Oracle tags

`WPF` = WPF data-binding behavior (primary oracle); `AV` = Avalonia 11 binding behavior; `PIN` = Cursorial pin with no direct parent-framework analog (this matrix is the decision record); `DEV` = deliberate deviation from a parent framework, always with rationale (inline or via the BD ledger).

### 0.4 Pinned decisions made by this matrix (BD ledger)

Each goes beyond — but never against — the canonical doc text; deliberate and binding until amended.

- **BD1 — `Unset` is the only "no value" signal.** Every dead end in the forward pipeline (unresolved hop, converter exception, conversion failure, type-mismatch) resolves to `FallbackValue` **if specified**, else `entry.SetUnset()` — the engine never fabricates a default to "restore." The store promotes the next lane (invariant 4). A watch-only expression delivers `Unset` to its callback for the same dead ends (styling pins `Unset` = unmet). PIN (doc §6.6; invariant 4).
- **BD2 — DataContext-as-target anchors on the *logical parent's* DataContext.** A default-source binding whose **target property is `DataContextProperty`** does not anchor on the value it produces (that oscillates). It takes one `AddObserver(DataContextProperty)` on `LogicalParent`, re-anchored on `AttachedToLogicalTree`/`DetachedFromLogicalTree`; no logical parent yet ⇒ park `SourceMissing`, retry on attach. WPF/AV both special-case this identically. PIN (doc §6.4; spec §2.5).
- **BD3 — DataContext change is a full rebind, including `OneTime`.** A default-source expression rebinds (`WireFrom(0)`) on every DataContext change of its anchor, even in `OneTime` mode — WPF-consistent (OneTime re-evaluates per DataContext, it just does not subscribe path notifications). `Source` is fixed and never re-resolves. Oracle: WPF.
- **BD4 — strong subscriptions, contractual death edges.** No weak-event manager. The death edge per install path is the §6.5 table; the load-bearing premise is the **teardown sweep** (`ValueStore.TearDown()` then `BindingOperations.TearDown(element)`, bottom-up on permanent detach). "Strong handlers cannot leak" is a contract, enforced by the DEBUG leak tracker reporting undisposed expressions by path + install site. PIN (doc §6.5/§6.11.1).
- **BD5 — the source ladder is per-CLR-hop and INPC-wins.** (1) INPC → subscribe; (2) else convention `[PropertyName]Changed` (an `EventHandler`/`EventHandler<EventArgs>`-compatible CLR event named exactly `<Property>Changed`, discovered once per `(type, property)` and cached beside the accessor) → subscribe; (3) else parent-change-only re-read + one-time `Info`. Both present ⇒ INPC only (one subscription). A `UIObject` hop bypasses the ladder entirely (UIPropertyAccessor + `AddObserver`). Indexer hops over `INotifyCollectionChanged` subscribe `CollectionChanged` and honor the INPC `"Item[]"` convention. PIN (doc §6.3 source-ladder pin).
- **BD6 — replace-and-dispose at LocalValue; frames stack.** Installing a second LocalValue-lane binding for the same `(target, property)` disposes the first expression (entry disposed, subscriptions dropped) before the new install — one live LocalValue expression per pair, no zombie subscriptions. **Frame-hosted installs are exempt** (frames stack by design; each evicts on its own frame's retraction). PIN (doc §6.2; spec Open-Q3 resolved).
- **BD7 — `GetBindingExpression` returns the LocalValue lane only.** Frame-hosted, watch-only, and DirectProperty expressions return `null` from `GetBindingExpression`; `BindingDiagnostics.Explain` covers all lanes. PIN (doc §6.2; spec critique 23).
- **BD7a — install priority is scope-sensitive *(amended 2026-06-16, precedence-matrix §20/PD24)*.** A free-standing binding (`BindingOperations.Install` / `SetBinding`, and the `{TemplateBinding}`/`{Binding}` it compiles to) normally installs its target entry at `BindingPriority.LocalValue`, but installs at `BindingPriority.Template` when created **inside a template-instantiation scope** (`ControlTemplate.Instantiate` / `DataTemplate.Build`) — so a templated part's binding is overridable by a page/theme Style. The lane is captured into `BindingActivationContext.InstallPriority` at install time (the entry may materialize on a later attach, outside the scope) and used in `EnsureEntry`. Frame-hosted (Style) and watch-only installs ignore it. `Bind`/`BindUntyped` now accept `LocalValue` or `Template` (A6 amended). B101's free-standing install (outside any scope) stays `LocalValue` — unchanged. PIN (precedence-matrix M293/M295, A6).
- **BD8 — echo suppression is by-flag *and* by-value *and* by-priority.** Target→source write-back is skipped if (1) `IsPushingToTarget` (synchronous self-echo), (2) the new value equals `_lastPushedValue` per the property `Comparer` (the asynchronous-echo discriminator — covers **animation-handle disposal** resurfacing our pushed base at LocalValue), or (3) the change args' `BindingPriority == Animation` (animated values never round-trip; a mid-animation `SetCurrentValue` is `Animation`-priority and therefore also filtered — the A11×A12 joint). Suppression-by-value cannot lose a genuine edit (writing the round-tripped value is definitionally a no-op). PIN (doc §6.6 steps 1–3; precedence-matrix A11/A12, M120/M128/M132/M176).
  **Amendment (2026-07-05):** the by-value comparand is **cleared on every target→source write-back** (`ClearEchoComparand` — both lanes; the compiled typed lane clears its typed `_lastTyped`/`_hasLastTyped` pair too) and re-armed only by the next forward push. The comparand only ever describes **source-produced** values: after a write-back the source's newest value came *from* the target, so there is nothing to suppress until the source next produces. Without the clear, the "cannot lose a genuine edit" claim fails for **non-notifying** sources (ladder rung 3): no post-write re-read re-stamps the comparand, so it goes stale at the initial-transfer value and swallows the next target edit that *returns* to that value (the checkbox toggle-on/toggle-off case). For notifying sources the BD12 post-write re-read re-arms it in the same call, so B92's resurface suppression is unaffected. Rows B92b (reflection) / B92c (compiled typed).
- **BD9 — `SetValue` is a transient override; `ClearValue` is the kill.** Within one priority, last writer wins and a binding's push counts as a write — so `SetValue(LocalValue)` does **not** kill a local binding (it loses next produce). `ClearValue` removes the value *and* detaches local-priority bindings (the documented kill). Control-author contract: control-internal writes use `SetCurrentValue` (a LocalValue write would permanently shadow a frame-hosted Style binding; `SetCurrentValue` replaces the effective value in place). Both write APIs feed write-back; BD8 discriminates echoes, not APIs. PIN (doc §6.6; spec critique 3; precedence-matrix A12).
- **BD10 — `EffectiveMode` resolves at install, leaf-writability degrades at wiring.** `Mode == Default` → TwoWay iff `BindsTwoWayByDefault`, else OneWay (resolved once at install). A leaf proving read-only at wiring (no accessor setter / null `CompiledBinding.Setter`) degrades the expression to OneWay with a **one-time `Warning`**, re-evaluated on every rewire (intermediate identity, hence the leaf's declaring type, can change). PIN (doc §6.6; spec critique 14).
- **BD11 — `OneWayToSource` keeps the anchor observer, subscribes no path nodes, re-resolves the chain per write.** Its entry is installed but never produces (the store treats a never-set entry as contributing nothing — lifetime/discoverability only); the anchor observer is retained so DataContext changes re-target writes; each write re-reads hops 0..n−2 from the anchor (≤4 cheap reads) so a swapped intermediate never receives a write through a dead object. Initial activation pushes target→source. Oracle: WPF (init sync; per-write re-resolve PIN, spec critique 6).
- **BD12 — the reverse lane is fully specified.** `WriteToSource`: ConvertBack (`Unset`/exception ⇒ `ConvertBackFailed`, no write); `TargetNullValue` reverse mapping (target equals it ⇒ write `null`); `StringFormat` reverse parse **only** when the format is exactly `"{0}"` — any composite format (`"x: {0}"`) ⇒ `ConvertBackFailed`, no write (parsing a formatted prefix back is corruption); no-converter type gaps via the conversion ladder (assignable → `IConvertible`/enum → `XamlConverters.For(leafType)`); failure ⇒ `SourceUpdateFailed`, no write. A source INPC raised *during* the write coalesces into one post-write re-read (the WPF round-trip). PIN (doc §6.6; spec critique 7).
- **BD13 — anchors are mutually exclusive, validated at `CreateExpression`.** Setting more than one of `Source` / `ElementName` / `RelativeSource` throws `InvalidOperationException` naming the conflict. *(amended 2026-06-22)* A default-source (no anchor) binding on a non-`UIElement` target **anchors on the nearest `UIElement` up its inheritance chain** — the owner element set via `SetInheritanceParent` (an `InputBinding`/`KeyBinding` whose `Command="{Binding}"` resolves against its owning element's DataContext). The owner is assigned *after* the binding installs (the gesture is bound, then added to its element's `InputBindings`), so the install parks `SourceMissing` **silently** (no trace — like an unattached `UIElement` binding) and re-resolves when the inheritance parent arrives/changes (`UIObject.InheritanceParentChanged` → `BindingExpressionCore.OnTargetInheritanceParentChanged`); a chain that never reaches a `UIElement` stays parked (recoverable in principle). The walk lives in the expression, not `BindingOperations` (`anchor = target as UIElement`). PIN (doc §6.4; spec §2.1).
- **BD14 — `Watch` has no store entry and no `Pause`/`Resume`.** `BindingOperations.Watch(anchor, binding, onChanged)` builds the same expression with `_entry = null` and a callback sink; unresolved ⇒ `onChanged(Unset)`; anchor DataContext change ⇒ automatic rebind + re-deliver; delivery is synchronous on the UI thread (so a VM-driven `When` flip participates in the same frame). `IBindingWatch` exposes `Value` + `Dispose` only — **no `Pause`/`Resume`** (ledger B16): watchers stay live across deactivation, disposed at disarm/element-detach; the teardown sweep is the backstop. PIN (doc §6.2/§6.8, ledger B16).
- **BD15 — `TemplateBinding.CreateExpression` validation.** A `Mode` other than `Default`/`OneWay`, or a non-default `UpdateSourceTrigger`, **throws** `InvalidOperationException` naming the member. `Converter`/`FallbackValue`/`TargetNullValue`/`StringFormat` are honored but **forfeit the typed fast path** (route through the boxed pipeline). Two-way reach-in = `new Binding { RelativeSource = RelativeSource.TemplatedParent, Mode = TwoWay }`. PIN (doc §6.1; spec critique 15).
- **BD16 — the untyped→typed bridge is double dispatch on `UIProperty`.** `TemplateBinding`'s typed observer→entry pair comes from the internal virtuals `UIProperty.CreateEntry`/`CreateTemplateTransfer` overridden by `StyledProperty<T>` (`T` closed at registration) — no reflection, no `MakeGenericType`. The reflection-lane untyped push uses `BindingEntryBase.SetValue(object?)` + the untyped `AddObserver` (args carry `BindingPriority`). PIN (doc §6.7; S1 surface confirmed in `StyledProperty.cs`/`UIProperty.cs`).
- **BD17 — compiled lane: typed root check, whole-chain `Getter`, typed zero-box push.** `_anchor.Root is TSource s ? Getter(s) : SourceTypeMismatch+Unset`. `Steps` drive INPC rewiring but the value is one `Getter(s)` call (struct intermediates just work). When the target is `StyledProperty<TValue>` with no converter/StringFormat, push via `BindingEntry<TValue>.SetValue(v)` — zero boxing, zero steady-state allocation (the binding analog of `AnimatedValueHandle<T>`); otherwise fall through the boxed pipeline. The fast-path predicate is decided from the descriptor's construction-immutable converter/StringFormat (the target-property kind is fixed too), so the expression **commits to one entry kind — typed or boxed — for its lifetime** (chosen at first materialization; no mid-life swap). `Binding.Compiled` analyzes member + constant-index hops only — method calls/operators ⇒ `FormatException` naming the node; the **reflective fallback is the v1 producer**, the X4 generator is a second producer (no engine change). **B2 implementation note (P10):** the compiled lane shares the entire lifecycle (anchoring, the source-notification ladder, triggers, write-back, echo suppression, cross-thread coalescing, eviction/registry) with the reflection lane via a common `BindingExpressionCore` base — the two lanes differ only in value access (whole-chain `Getter` vs per-hop accessor) and push typing (B156 is the equivalence proof). **Amendment (2026-07-05):** "shares write-back" requires the typed path to wire the shared target observer at ITS entry-materialization site (`PushValue`, the `EnsureEntry` analog) — the boxed pipeline's `EnsureEntry` never runs on the typed path (`_entry` is already the typed entry), and before the fix typed TwoWay write-back was silently dead (B92c). PIN (doc §6.7; Fork C contract).
- **BD18 — reentrancy is flag-guarded and eviction-aware.** `Dispose()` is idempotent (`Disposing`/`Disposed` flags). An eviction-initiated dispose (`OnEvicted`) skips `entry.Dispose()` (the store is mid-eviction; `BindingEntryBase.Dispose` is itself idempotent and legal from `OnEvicted`). Every handler entry point (`OnSourcePropertyChanged`, target observer, collection-changed, LostFocus/EditCommit, dispatcher drain, watch callback) checks `Disposed` and returns; after any `entry.SetValue`/push returns, the expression re-checks `Disposed` before touching wiring state. PIN (doc §6.5; spec §3.11).
- **BD19 — the registry is release-build and triple-duty.** One inline list in the Fork-A-reserved opaque `UIObject.BindingHostState` slot (null when unused) tracks LocalValue installs (keyed by property — backs replace-and-dispose + `GetBindingExpression`), frame-hosted installs, DirectProperty-targeted expressions, and watches (keyed under the anchor). It backs replace-and-dispose, `GetBindingExpression`, `Explain`, and the teardown sweep. DEBUG augments it with install-site capture + a weak-target sweep on window close. PIN (doc §6.5/§6.10; `UIObject.BindingHostState` confirmed).
- **BD20 — cross-thread INPC coalesces into one pre-layout dispatch drain and wakes the loop.** Foreign-thread INPC sets a per-node dirty bitmask via `Interlocked.Or` and posts one drain via `IUIDispatcher.Post`; the drain rewires from the lowest set bit before layout (invariant 1); N changes between frames coalesce into one rewire+push. `Post` **MUST wake** the event-driven frame loop when no drain is pending. Same-thread INPC applies synchronously. PIN (doc §6.9; the `IUIDispatcher` seam is S6's, faked here).
- **BD21 — `FindName` is template-aware; the guard seals part names.** `UIElement.FindName(name)` = `NameScope.FindEnclosing(this)?.Find(name)`. The guarded walk consults a template scope at ancestor A only when `this.TemplatedParent == A`; a document content *child* of a templated control fails the guard and resolves document names, never part names. PIN (doc §6.3; spec §2.6).

---

## 1. Path parsing (`BindingPath.Parse`) — B1–B16 *(B0)*

`BindingPath.Parse(text, resolver?)`; grammar v1 per doc §6.3 / spec §2.4. Unit-level, no host.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B1 | — | `Parse("Name")` | one property step `Name`; `ToString() == "Name"`; round-trips | WPF |
| B2 | — | `Parse("Customer.Address.City")` | three property steps in order; `ToString()` round-trips the dotted chain | WPF |
| B3 | — | `Parse("Tags[0]")` | step `Tags` + int indexer `0`; `ToString() == "Tags[0]"` | WPF |
| B4 | — | `Parse("Map[key]")` and `Parse("Map['key']")` | both → step `Map` + string indexer `key` (bare and single-quoted equivalent); `ToString()` canonicalizes to one form (single-quoted), round-trips | WPF (bare/quoted equivalence) |
| B5 | resolver maps `Grid` | `Parse("(Grid.Row)", resolver)` | one attached/styled segment (owner `Grid`, member `Row`); `ToString() == "(Grid.Row)"` | WPF |
| B6 | resolver maps `Grid` | `Parse("Items[2].(Grid.Row)", resolver)` | step + int indexer + attached segment, in order; round-trips | WPF |
| B7 | — | `Parse("")` and `Parse(".")` | both ⇒ `BindingPath.Empty` (the source itself); `ToString()` of Empty is `""`; `ReferenceEquals(Parse(""), BindingPath.Empty)` need not hold but structural-equal must | WPF |
| B8 | — | `Parse(null)` | `ArgumentNullException` | PIN |
| B9 | — | `Parse("Name.")` (trailing dot) | `FormatException` with `Position` at the trailing-dot offset, message naming the empty step | PIN (SD3-shaped) |
| B10 | — | `Parse("Tags[")` (unterminated indexer) | `FormatException`, `Position` at `[`, message naming the unterminated indexer | PIN |
| B11 | — | `Parse("Tags[a,b]")` (multi-arg indexer) | `FormatException` naming multi-argument indexers as **unsupported by design** (doc §6.3 "out"), `Position` at the comma | DEV (recorded out) |
| B12 | — | `Parse("/Items")` (slash/current-item) | `FormatException` naming slash/`Path=/` current-item syntax unsupported (no collection views v1) | DEV (recorded out) |
| B13 | — | `Parse("(local:T)x")` (source cast) | `FormatException` naming source casts unsupported, `Position` at `(` | DEV (recorded out) |
| B14 | resolver = null; `Widget` known to the default resolver; `Foo` registered on two owner types | `Parse("(Widget.Num)")` then `Parse("(Foo.X)")` | the first resolves via the **default** `IPathTypeResolver` (Fork A `FindOwnersByShortName`); the second throws `FormatException` listing the candidate full names (ambiguous short name) | PIN (doc §6.3; `FindOwnersByShortName` confirmed) |
| B15 | resolver maps nothing for `Zzz` | `Parse("(Zzz.X)", resolver)` | `FormatException` naming the unresolvable type token `Zzz`, `Position` at the token | PIN |
| B16 | parsed path from B2 | parse the same descriptor twice (shared `Binding`) | `BindingPath` is parsed once per descriptor and cached on it (the second `CreateExpression` reuses the cached segments — internal probe) | PIN (spec §3.2 "parsed once per descriptor") |

---

## 2. Accessor resolution & the `UIObject` hop — B17–B24 *(B0)*

Per-node accessor resolution (doc §6.3 / spec §3.2), cached in the copy-on-write `AccessorCache`. The `UIObject` lane bypasses reflection/INPC entirely.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B17 | `Root.DataContext = w0` where `w0` is a `Widget`; bind `a.Text` ← `"Text"` (so the hop is a `UIObject`'s registered `UIProperty`) | `set(w0, Text, "hi")` | the hop resolves to a **`UIPropertyAccessor`** (registered `UIProperty` on a `UIObject` runtime type): no reflection, no INPC; observation via `AddObserver(Text)`; `a.Text == "hi"` | PIN (doc §6.3 rule 1) |
| B18 | as B17 | first resolve of `(typeof(Widget), "Text")` then a second binding to the same accessor | the accessor is cached in `AccessorCache`; the second resolve is a lock-free read of the same `IPropertyAccessor` (internal counter probe) | PIN (spec §3.2 COW cache) |
| B19 | `Root.DataContext = vm`; bind `a.Text` ← `"Name"` (CLR property) | inspect the resolved accessor | a CLR-property accessor: compiled delegate when `RuntimeFeature.IsDynamicCodeSupported`, else raw `PropertyInfo` (honest AOT fallback) | PIN (doc §6.3 rule 2) |
| B20 | `Root.DataContext = vm`; bind `a.Text` ← `"Tags[0]"` (`ObservableCollection`) | inspect | indexer node: `IList`/`IReadOnlyList<T>` int fast path; the node also subscribes `INotifyCollectionChanged` and honors the `"Item[]"` convention | PIN (doc §6.3 rule 3) |
| B21 | `Root.DataContext = vm`; bind `a.Text` ← `"Map[key]"` (`Dictionary<string,object?>`) | inspect | indexer node: `IDictionary`/general `Item[...]` reflection (no int fast path); resolves the value | WPF |
| B21a | `Dictionary<Status,T>` (enum-keyed) bind `"[Active]"` (bare) and `"[Status.Active]"` (qualified); also a plain class exposing only `Item[Status]` | resolve + get/set | non-integer indexer tokens coerce to an `Item[SomeEnum]` parameter: the token (after stripping a `Type.` prefix, case-insensitive) is `Enum.Parse`d and bound through the **typed** generic indexer, not the non-generic `IDictionary` string lookup; round-trips two-way. An unknown member stays unresolved (plain class) or degrades to the `IDictionary` string fallback (dictionary). More ergonomic than WPF's `[(local:Status)Active]` cast (the cast lane stays deferred). Tests `B024a`/`B024b` | DEV (doc §6.3 enum-index coercion) |
| B22 | mixed chain `"Sub.Name"` where `Sub` is a `Vm` (CLR) under `vm` (CLR) | resolve | each hop independently resolves through the ladder; the accessor cache keys on `(runtime-type, member)` per hop, not on the whole path | PIN |
| B23 | a chain hop whose instance is a `UIObject` exposing a CLR property of the **same name** as no registered `UIProperty` | resolve | falls through rule 1 (no registered `UIProperty` match) to rule 2 (CLR reflection) — registration match is by `(runtime type, name)` against `UIPropertyRegistry`, not "is a `UIObject`" | PIN |
| B24 | `RuntimeFeature.IsDynamicCodeSupported == false` (simulated) | resolve a CLR hop | the raw-`PropertyInfo` path is taken; values still read; one row documents that the compiled lane is the real AOT answer (not this fallback) | PIN (doc §6.3) |

---

## 3. Source change-notification ladder — B25–B36 *(B0)*

The 2026-06-11 pin (doc §6.3 ②). Targets are `UIProperty`/`DirectProperty` only; sources are plain CLR/MVVM.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B25 | `Root.DataContext = vm`; bind `a.Text` ← `"Name"`; `vm.Name = "a"` initial | `vm.Name = "b"` (raises INPC) | rung 1: INPC subscribed; `a.Text == "b"` after the source write; exactly one re-read | WPF |
| B26 | as B25, expression disposed | `vm.Name = "c"` | no delivery (the strong INPC handler was unsubscribed on dispose — the death edge); `vm` retains no handler (handler-count probe) | PIN (BD4) |
| B27 | DEBUG: as B25 but the target `Widget` is dropped without a teardown sweep | window-close weak-target sweep | the DEBUG leak tracker reports the undisposed expression by path (`Name`) + install site; release builds do not run the sweep | PIN (BD4/BD19) |
| B28 | `Root.DataContext = plain` (`PlainVm`); bind `a.Text` ← `"Title"` (no INPC; has `TitleChanged`) | `plain.Title = "x"; plain.TitleChanged?.Invoke(...)` | rung 2: the convention-matched `TitleChanged` CLR event is subscribed (the `PropertyDescriptor.AddValueChanged` analog, no `TypeDescriptor` leak); `a.Text == "x"` | WPF (analog), PIN (no-TypeDescriptor) |
| B29 | as B28, dispose the expression | `plain.TitleChanged?.Invoke(...)` after dispose | the `TitleChanged` handler is removed on dispose (subscriptions die with the expression, by contract — *not* a global table) | PIN (BD5) |
| B30 | `Root.DataContext = plain`; bind `a.Num` ← `"Count"` (`CountChanged : EventHandler<EventArgs>`) | raise `CountChanged` | rung 2 matches `EventHandler<EventArgs>`-compatible events too; re-read fires | PIN (BD5) |
| B31 | `Root.DataContext = plain`; bind `a.Text` ← `"Note"` (no INPC, no `[Name]Changed`) | initial activation | rung 3: one-time read; `a.Text` = the initial `Note`; a one-time `Info` diagnostic is recorded naming the un-observable hop | WPF (one-time read), PIN (Info) |
| B32 | as B31, the hop is **mid-chain**: `"Sub.Note"` where `Sub` is INPC | `vm.Sub` swapped (INPC on `Sub`) | the parent-change re-reads `Note` (rung 3 "observed-on-parent-change only"): a `Sub` swap re-reads the whole tail incl. `Note`; a bare `Note` mutation with no parent notify is **not** seen | WPF |
| B33 | source implements **both** INPC and a `NameChanged` event for `Name` | `vm.Name = "x"` (INPC) + the redundant `NameChanged` raise | INPC wins: exactly **one** subscription, one re-read per genuine change — the `[Name]Changed` event is not also subscribed | PIN (BD5 "INPC wins") |
| B34 | `Root.DataContext = vm`; bind `a.Text` ← `"Tags[0]"`; `Tags = ["x"]` | `vm.Tags[0] = "y"` (INCC `Replace`) | the indexer node's `INotifyCollectionChanged` subscription fires; `a.Text == "y"`; `"Item[]"` convention also honored if the collection raises INPC `"Item[]"` | WPF |
| B35 | as B34 | `vm.Tags.Insert(0, "z")` (INCC `Insert` shifting index 0) | re-read of index 0 ⇒ `a.Text == "z"` (the bound index re-reads on any structural change) | WPF |
| B36 | `Root.DataContext = vm`; chain `"Sub.Name"`; `vm.Sub = s1` (`Name="p"`) | `vm.Sub = s2` (`Name="q"`) — intermediate **identity change** | mid-chain node changes identity: the expression unsubscribes the old `s1.Name` handler, rewires from the changed hop, subscribes `s2.Name`, pushes `"q"`; a later `s1.Name = "stale"` does **not** deliver | WPF |

---

## 4. DataContext, the as-target special case, and Source — B37–B46 *(B0)*

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B37 | bind `a.Text` ← `"Name"` (default source); `a` is a descendant of `Root`; `Root.DataContext = vm` | inspect anchor + `set` | default anchor = `a`'s inherited `DataContext` (one `AddObserver(DataContextProperty)` on `a`); eager-notify inheritance delivers to the entry-less descendant; `a.Text == vm.Name` | WPF |
| B38 | as B37 | `Root.DataContext = vm2` (whole-subtree inherited change) | the observer fires → `WireFrom(0)` full rebind against `vm2`; `a.Text == vm2.Name` | WPF (BD3) |
| B39 | bind `a.Text` ← `""` (empty path / source itself); `Root.DataContext = "literal"` | activate | the target receives the source object itself (`a.Text == "literal"`); a `Vm` source ⇒ the `Vm` instance is pushed (type-convert/`ToString` per the pipeline if the target type differs) | WPF |
| B40 | **DataContext-as-target**: `a.DataContext` ← `new Binding("Sub")` (default source), `a` under `Root(DataContext = vm)`, `vm.Sub = s1` | activate; then `vm.Sub = s2` | BD2: anchor = **`a`'s logical parent's** DataContext (observer on the parent), NOT the produced value; `a.DataContext == s1`; after `vm.Sub = s2` ⇒ `a.DataContext == s2`; **no oscillation** (the produced value is never re-anchored on) | WPF/AV (PIN) |
| B41 | as B40 but `a` has **no logical parent yet** at install | install, then attach `a` under `Root` | parks `SourceMissing` (no trace); on `AttachedToLogicalTree` the parent anchor resolves and `a.DataContext` produces; reparent re-anchors | PIN (BD2) |
| B42 | bind `a.Text` ← `new Binding("Name") { Source = vm }` | `set(Root, DataContext, other)` | `Source` is fixed — never re-resolves; the DataContext change is ignored; `a.Text == vm.Name` | WPF (BD3) |
| B43 | bind `a.Text` ← `new Binding("Name") { Source = vm, ElementName = "x" }` | `Install` | `InvalidOperationException` naming the `Source`/`ElementName` conflict (anchors mutually exclusive) | PIN (BD13) |
| B44 | a non-`UIElement` `UIObject` target with **no inheritance parent**; default-source `new Binding("Name")` | `Install` | parks `SourceMissing` **silently** (no trace) — recoverable: re-resolves if an inheritance parent leading to a `UIElement` is later set *(amended 2026-06-22, BD13)* | PIN (BD13) |
| B44a | a `KeyBinding` whose `Command="{Binding Cmd}"` (`owner.DataContext = vm`, `vm.Cmd` set); install the binding, **then** `owner.InputBindings.Add(kb)` | install; add to collection | install parks `SourceMissing`; `SetInheritanceParent(owner)` re-anchors on `owner`, the DataContext path resolves, `kb.Command == vm.Cmd`; the **added-then-bound** order resolves at install; removing the gesture (`SetInheritanceParent(null)`) re-parks `SourceMissing` | PIN (BD13) |
| B45 | bind `a.Text` ← `"DataContext.Tags[0]"` (the §2.8 idiom) with `Source = b` where `b` is an element whose `DataContext = vm`, `Tags=["t"]` | activate | `DataContext` resolves as a path segment via the `UIPropertyAccessor` lane (it is a registered `UIProperty`), then `Tags[0]`; `a.Text == "t"` | WPF (doc §2.8) |
| B46 | whole-window DataContext swap across N bound descendants | `set(Root, DataContext, vm2)` inside `DeferNotifications` | each descendant rebinds; the row asserts coalesced (first-old/last-new per property) delivery — the bulk-swap cost note; without `DeferNotifications` the swap is N synchronous rebinds (also correct, just costlier) | PIN (doc §6.4 bulk-swap) |

---

## 5. RelativeSource anchoring — B47–B54 *(B0; FindAncestor pulled into the spine for Fork B)*

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B47 | bind `a.Text` ← `new Binding("Num") { RelativeSource = RelativeSource.Self }` (path against `a` itself) | `set(a, Num, 5)` | root = the target element itself; the `Num` hop resolves via UIPropertyAccessor; `a.Text` reflects `a.Num` (the self-source `When` requirement's data half) | WPF |
| B48 | bind a template part `p.Text` ← `new Binding("Num") { RelativeSource = RelativeSource.TemplatedParent }`; `p.TemplatedParent = ctrl` (manually stamped) | activate | root = `p.TemplatedParent` (`ctrl`); `p.Text == ctrl.Num` | WPF |
| B49 | as B48 but `p.TemplatedParent == null` (outside a template) | activate | `SourceMissing` + a trace; `Unset`/fallback | WPF/PIN |
| B50 | `FindAncestor<paneType>(level:1)`: bind `a.Text` ← path against the nearest ancestor of type `StackPanel` (`paneA`) | activate | walks `LogicalParent` upward counting assignable matches until level 1 → `paneA`; resolved at attach | WPF |
| B51 | `FindAncestor<StackPanel>(level:2)` from `a` (paneA depth 1, Root depth 0, both StackPanels) | activate | the **second** assignable match = `Root`; `level` counts matches not hops | WPF |
| B52 | as B50, then reparent `a` under `paneB` | `DetachedFromLogicalTree`/`AttachedToLogicalTree` | the ancestor is re-resolved on reparent (new nearest `StackPanel`); the binding re-targets | WPF (PIN re-resolve) |
| B53 | `FindAncestor<Card>(1)` where no `Card` ancestor exists | activate after attach | `AncestorNotFound` trace; `Unset`/fallback; re-tried on reattach | WPF/PIN |
| B54 | a template part with a `FindAncestor` reaching past the templated parent | activate | the walk crosses the template boundary the way the logical tree does (part → templated parent → beyond) | WPF (doc §6.4) |

---

## 6. The forward value pipeline — B55–B70 *(B0)*

Source → target (doc §6.6 / spec §3.5). `targetType` is the property type. Unit-level where no observation is needed.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B55 | bind `a.Num` ← `"Age"`, `vm.Age = 7` | activate | exact/assignable: `a.Num == 7`, no conversion | WPF |
| B56 | bind `a.Num` ← `"Name"` where `Name = "42"` (string→int, no converter) | activate | type-conversion ladder: `IConvertible`/`XamlConverters.For(int)` parses `"42"`; `a.Num == 42` | WPF |
| B57 | as B56 but `Name = "notanint"` | activate | conversion fails ⇒ `TypeMismatch` trace + `SetUnset()`; `a.Num == Default` (0); lower priorities resurface | PIN (BD1) |
| B58 | bind `a.Text` ← `"Age"` with a converter int→string (`v => $"#{v}"`), `Age = 3` | activate | `Convert(3, typeof(string), null, culture) == "#3"`; `a.Text == "#3"` | WPF |
| B59 | as B58 with `ConverterParameter = "x"` | activate | the parameter reaches `Convert(..., parameter: "x", ...)` | WPF |
| B60 | converter whose `Convert` throws | activate | `ConversionFailed` trace; `v = Unset` ⇒ `FallbackValue` if specified else `SetUnset` | WPF (error→fallback), PIN (trace) |
| B61 | converter returning `UIProperty.UnsetValue` from `Convert` | activate | treated as `Unset` ⇒ `FallbackValue` if specified else `SetUnset` (no exception) | WPF/PIN |
| B62 | bind `a.Text` ← `"Name"` with `StringFormat = "Editing: {0}"`, `Name = "Bob"` | activate | string/object target: `a.Text == "Editing: Bob"` (current-culture `string.Format`) | WPF |
| B63 | bind `a.Num` ← `"Name"` with `StringFormat = "x{0}"` (target is **int**, not string/object) | activate | `StringFormat` applies only to string/object targets ⇒ it is **skipped**; the value flows through type conversion instead | WPF |
| B64 | bind `a.Text` ← `"Name"`, `Name = null`, `TargetNullValue = "<none>"` | activate | null + `TargetNullValue` specified ⇒ `a.Text == "<none>"` (applied before `StringFormat`) | WPF |
| B65 | as B64 with both `TargetNullValue = "<none>"` and `StringFormat = "[{0}]"` | activate | `TargetNullValue` substitutes first, then `StringFormat` formats ⇒ `a.Text == "[<none>]"` | WPF |
| B66 | bind `a.Text` ← `"Sub.Name"` (mid-chain), `vm.Sub == null` | activate | broken path (unresolved hop) ⇒ leaf `Unset`; no `FallbackValue` specified ⇒ `SetUnset`; `a.Text == Default` (null) | WPF (BD1) |
| B67 | as B66 with `FallbackValue = "fb"` | activate | broken path ⇒ `FallbackValue`; `a.Text == "fb"`; later `vm.Sub = s1` rebinds and produces `s1.Name` (fallback withdrawn) | WPF |
| B68 | `FallbackValue` typed differently from the target (e.g. `FallbackValue = 5` for a string target) | activate on a broken path | the fallback runs through the same type conversion to the target type before push (a string target ⇒ `"5"`); conversion failure of the fallback ⇒ `SetUnset` + trace | PIN |
| B69 | no converter; bind a `Color`/`enum`-typed target ← a string source whose value is an enum name | activate | the conversion ladder: assignable → `IConvertible`/enum-parse fast path → `XamlConverters.For(targetType)`; the enum parses | WPF |
| B70 | bind `a.Off` ← `"Age"` (`AffectsComposite` property); `Age` changes each "frame" | repeated source writes | the boxed reflection-lane leaf is the only allocation; the row notes the compiled lane (B2) makes this 0 B; the store equality short-circuit absorbs no-op re-reads | PIN (doc §6.6/§6.11.7) |

---

## 7. Modes, triggers, and target → source write-back — B71–B92c *(B0; the LostFocus-routed leg is B1)*

Doc §6.6. `EffectiveMode` resolution, the three B0 triggers (PropertyChanged/Explicit + Default), the reverse lane, the SetCurrentValue-preserves-binding joint.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B71 | bind `a.Text` ← `"Name"` `{ Mode = OneWay }` | `vm.Name = "x"`; then `set(a, Text, "y")` | source→target only: `vm.Name` change ⇒ `a.Text == "x"`; a target write does **not** reach the source (`vm.Name` unchanged) | WPF |
| B72 | bind `a.Text` ← `"Name"` `{ Mode = TwoWay, UpdateSourceTrigger = PropertyChanged }` | `set(a, Text, "z")` | write-back now: `vm.Name == "z"`; a subsequent `vm.Name = "w"` flows back to `a.Text` | WPF |
| B73 | bind `a.Text` ← `"Name"` `{ Mode = OneTime }`; `Name = "a"` | activate, then `vm.Name = "b"` | one-time read at activation (`a.Text == "a"`); no path subscription ⇒ `vm.Name = "b"` is **not** seen | WPF |
| B74 | as B73 | `set(Root, DataContext, vm2)` (`vm2.Name = "c"`) | `OneTime` re-evaluates on DataContext change (BD3): `a.Text == "c"` | WPF |
| B75 | bind `vm.Name` ← `a.Text` `{ Mode = OneWayToSource }` (target `a.Text`, source `vm.Name`) | activate with `a.Text == "init"` | initial sync pushes target→source: `vm.Name == "init"`; the entry is installed but never produces into `a.Text` | WPF |
| B76 | as B75 | `set(a, Text, "edit")` | the target write reaches the source: `vm.Name == "edit"`; a `vm.Name = "x"` does **not** flow to `a.Text` (no path subscription) | WPF |
| B77 | OneWayToSource with chain `"Sub.Name"`; `vm.Sub = s1` | `vm.Sub = s2`; then `set(a, Text, "k")` | per-write re-resolve (BD11): the write re-reads the chain from the anchor and lands on `s2.Name`, never the stale `s1` | WPF/PIN |
| B78 | `Flag` has `BindsTwoWayByDefault`; bind `a.Flag` ← `"IsDirty"` `{ Mode = Default }` | inspect `expr.EffectiveMode`; write-back | `EffectiveMode == TwoWay` (resolved from metadata at install); a target write reaches the source | WPF (BD10) |
| B79 | `Text` has no `BindsTwoWayByDefault`; bind `a.Text` ← `"Name"` `{ Mode = Default }` | inspect `EffectiveMode` | `EffectiveMode == OneWay` | WPF (BD10) |
| B80 | `Mode = TwoWay`; bind `a.Num` ← `"ReadOnlyAge"` (source property with no setter) | wire | leaf read-only at wiring ⇒ degrade to OneWay + one-time `Warning` trace; source-direction writes are dropped | WPF (BD10) |
| B81 | as B80 but the leaf's declaring type changes on rewire to one with a setter | `vm.Sub` swap to a writable-leaf type | `EffectiveMode` re-evaluates per rewire — write-back re-enabled when the leaf becomes writable | PIN (BD10) |
| B82 | `Mode = TwoWay`, `Explicit` trigger; bind `a.Text` ← `"Name"` | `set(a, Text, "p")` (no flush) then `expr.UpdateSource()` | nothing reaches the source until `UpdateSource()`; then `vm.Name == "p"` | WPF |
| B83 | `Mode = TwoWay`, `PropertyChanged`; converter present (string↔int) | `set(a, Num, 9)` (target int → source string) | `ConvertBack(9, leafType, ...)` runs; the source receives the converted value | WPF |
| B84 | as B83, `ConvertBack` returns `Unset` (or throws) | target write | `ConvertBackFailed` trace; **no write** to the source | WPF/PIN (BD12) |
| B85 | `Mode = TwoWay`; `TargetNullValue = "<none>"`; target value set equal to `"<none>"` | target write | `TargetNullValue` reverse mapping: the source receives `null` | WPF/PIN (BD12) |
| B86 | `Mode = TwoWay`; `StringFormat = "{0}"` exactly (no converter); bind `a.Text` ← `"Age"` (int leaf) | `set(a, Text, "12")` | reverse parse applies (format is exactly `"{0}"`): `"12"` parses to int via `XamlConverters` ⇒ `vm.Age == 12` | WPF/PIN (BD12) |
| B87 | as B86 but `StringFormat = "Age: {0}"` (composite) | `set(a, Text, "Age: 12")` | composite format ⇒ `ConvertBackFailed` trace, **no write** (parsing a prefix back is corruption) | PIN (BD12) |
| B88 | `Mode = TwoWay`, no converter, type gap (`a.Text` string → `vm.Age` int) | `set(a, Text, "5")` | no-converter conversion ladder: `"5"` → int → `vm.Age == 5`; a non-numeric value ⇒ `SourceUpdateFailed`, no write | WPF (BD12) |
| B89 | `Mode = TwoWay`; the source raises INPC **during** the write (VM clamps `Age` 5→3) | `set(a, Num, 5)` where the VM normalizes to 3 | one coalesced post-write re-read runs (guarded by `IsWritingToSource`): `a.Num` converges to 3 — the WPF round-trip kept; no infinite loop | WPF (BD12) |
| B90 | **SetCurrentValue preserves the two-way binding** (the A12 joint): `Mode = TwoWay` bind `a.Text` ← `"Name"` | `keystroke(a, Text, "typed")` (i.e. `scv(a, Text, "typed")`) | `SetCurrentValue` is a non-echo genuine write ⇒ write-back: `vm.Name == "typed"`; the binding is **not** killed (in-place effective replacement, no LocalValue planted); a later `vm.Name = "ext"` still flows to `a.Text` | PIN (BD9; precedence-matrix A12/M132) |
| B91 | `Mode = TwoWay` bind `a.Text` ← `"Name"` | `set(a, Text, "lv")` (a plain `SetValue` at LocalValue) | last-writer-wins: `vm.Name == "lv"` (the push counts as a write); the binding survives (transient override) — a later produce wins; **`clear(a, Text)` kills** it (the documented kill) | PIN (BD9; A12/M132) |
| B92 | `Mode = TwoWay` bind `a.Text` ← `"Name"`; the round-tripped value resurfaces (e.g. animation handle on `a.Text` disposes, the pushed base resurfaces) | dispose the animation handle | echo suppression by-value (BD8 step 2): the resurfaced value equals `_lastPushedValue` ⇒ **no** write-back; the source is untouched | PIN (BD8; precedence-matrix M176/M128) |
| B92b | `Mode = TwoWay` bind `a.Text` ← a **non-notifying** source leaf (ladder rung 3 — one-time read, no post-write re-read) | `set(a, Text, "edited")` then `set(a, Text, "orig")` (back to the initially pushed value) | **both** writes reach the source: the echo comparand is **cleared on every write-back** (BD8 amendment) and re-armed only by a forward push, so returning to the initial value is a genuine edit, never a stale-comparand "echo" (the checkbox toggle-on/off case) | PIN (BD8 amendment 2026-07-05) |
| B92c | B92b through the **compiled typed lane** (`CompiledBinding<PlainHolder, string?>`, zero-box push — the comparand is the TYPED `_lastTyped`/`_hasLastTyped` pair, not the base boxed field) | as B92b | as B92b: `ClearEchoComparand` clears the typed comparand too (the override mirrors `ProduceUnsetOrFallback`'s dual clear). This row also pins the typed path **wiring its target observer at entry materialization** (the `EnsureEntry` analog) — before the 2026-07-05 fix the typed lane never subscribed one, so typed TwoWay write-back was dead entirely | PIN (BD8 amendment; BD17/B156 equivalence) |

---

## 8. Cross-thread, frame coherence, and animated-value filtering — B93–B100 *(B0)*

Doc §6.9 / §6.6 step 3. `IUIDispatcher` is faked (`UITestHost`'s dispatcher / a test fake with the loop-wake hook).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B93 | bind `a.Text` ← `"Name"`; a same-thread `vm.Name = "x"` during frame N's input drain | `RunFrame()` | applied synchronously before layout; frame N's layout/render see `a.Text == "x"` (invariant 1, no machinery) | PIN (BD20) |
| B94 | bind `a.Text` ← `"Name"`; raise `vm.Name = "y"` on a **foreign** thread | next `RunFrame()` | the per-node dirty bitmask is set via `Interlocked.Or` and one drain is posted via `IUIDispatcher.Post`; the drain rewires from the lowest set bit **before** layout; `a.Text == "y"` that frame | PIN (BD20) |
| B95 | as B94, N foreign-thread changes between frames (`Name`, `Age`, …) | one `RunFrame()` | coalesced into **one** rewire+push pass (bitmask OR); each affected node re-read once | PIN (BD20) |
| B96 | event-driven loop with no pending drain; a background `vm.Name` change | (no input arrives) | `Post` **wakes** the frame loop (schedules a drain) — the change does not sit until unrelated input; the row asserts a frame ran solely due to the post | PIN (BD20, the `Invalidate()` pattern) |
| B97 | DEBUG: call `BindingOperations.Install` from a non-UI thread | install | `VerifyAccess` debug-assert (invariant 6); release behavior is a documented "UI-thread-only" contract | PIN (doc §6.9) |
| B98 | `Mode = TwoWay` bind `a.Off` ← `"AgeAsInt"`; an animation drives `a.Off` (Animation priority) | per animation frame | BD8 step 3: change args carry `BindingPriority == Animation` ⇒ write-back **skipped**; the source is never spammed at frame rate | PIN (doc §6.6/§6.11.4; precedence-matrix A11) |
| B99 | as B98, a **mid-animation** `scv(a, Off, 11)` while the animation holds the property | the SetCurrentValue write | the args priority is `Animation` (the replaced lane is the animation, A11) ⇒ also filtered from write-back; consistent with "animated values never round-trip" | PIN (BD8 step 3; M128/M132) |
| B100 | `PauseIOAsync`/`RenegotiateAsync` window open; a `vm.Name` change | apply | bindings touch only the store (invariant 2) — no special handling, the change applies normally | PIN (doc §6.9) |

---

## 9. Producer lifecycle, registry, eviction & the teardown sweep — B101–B120 *(B0)*

Doc §6.5. The expression registry (`UIObject.BindingHostState`), replace-and-dispose, frame-hosted vs free-standing entries, the teardown sweep that closes the P1 gap, the DEBUG leak tracker.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B101 | `BindingOperations.Install(a, Text, new Binding("Name"))` | inspect | a free-standing `BindingEntry<string?>` at `BindingPriority.LocalValue`; the expression is registered in `a.BindingHostState`, keyed by `Text`; `src(a, Text) == LocalValue` while producing | PIN (BD19; `Bind` is LocalValue-only) |
| B102 | as B101, then `Install(a, Text, new Binding("Age"))` (second LocalValue binding, same property) | second install | replace-and-dispose (BD6): the first expression is disposed (entry disposed, INPC unsubscribed) **before** the new install; `GetBindingExpression(a, Text)` returns the second; the first's source handler is gone | PIN (BD6) |
| B103 | a binding-valued style setter armed on `a` via `Install(a, Text, binding, frame)` (the styling Y-stage path) | inspect | a **frame-hosted** `BindingEntry` living in `frame` at `BindingPriority.Style`, within-slot ordered by the frame's `StyleSortKey`; `GetBindingExpression(a, Text)` returns **null** (frame-hosted, not LocalValue — BD7) | PIN (BD7/BD19) |
| B104 | two frame-hosted bindings for the same property (two armed rules) | install both | exempt from replace-and-dispose (frames stack): both expressions live; the stronger key wins in the store; each evicts on its own frame's retraction | PIN (BD6) |
| B105 | B103's frame-hosted binding | retract the style frame (cookie removal) | the frame-hosted entry is evicted → `OnEvicted` → expression `Dispose` → INPC unsubscribed; the store promotes the next lane (invariant 4) | PIN (BD4; doc §6.5) |
| B106 | B101's local binding | `clear(a, Text)` | `ClearValue` evicts the local-priority entry → expression disposed → unsubscribed (the documented kill, BD9); registry slot for `Text` cleared | PIN (BD9) |
| B106a | B101's local binding | `BindingOperations.ClearBinding(a, Text)` | the WPF-shaped surface (the `System.Windows.Data` analog): disposes the tracked LocalValue expression (subscriptions dropped) **and** runs `ClearValue` (BD9 kill); `GetBindingExpression(a, Text) == null`; `BindingOperations.SetBinding(a, Text, b)` is the symmetric install alias. Frame-hosted/watch expressions are untouched. | DEV (WPF parity; BD9) |
| B107 | B101's local binding | `set(a, Text, "lv")` (plain SetValue) | the binding is **not** evicted (BD9 transient override) — it survives and re-wins on the next produce; the registry still holds it | PIN (BD9) |
| B108 | a bound subtree `Root→paneA→a` with INPC subscriptions to `vm` | `Root.TearDown()` (permanent detach sweep) | bottom-up per element: `ValueStore.TearDown()` (evicts every entry, firing `OnEvicted`) **then** `BindingOperations.TearDown(element)` (remaining registry-tracked expressions); **every** expression disposed, every INPC handler removed from `vm`; this is the P1-gap close | PIN (BD4; closes `UIElement.TearDown` S2 leg) |
| B108b | an element with a `KeyBinding` in `InputBindings` whose `Command="{Binding}"` resolved against the owner DataContext | `owner.TearDown()` | `TearDown` sweeps `InputBindings` (neither visual nor logical children) via a dedicated leg, calling `BindingOperations.TearDown(binding)` on each — the command expression is disposed, the owner-side DataContext observer + the `InheritanceParentChanged` subscription released | PIN (BD13; `UIElement.TearDown` InputBindings leg) |
| B109 | as B108 | inspect notification order across the subtree | retractions/eviction arrive **bottom-up** (children before parents), mirroring the store's S155 ordering | PIN (doc §5.1) |
| B110 | a `DirectProperty<Widget,int>` (`Dir`) target on a **tree-attached** element, bound | `Root.TearDown()` | the DirectProperty expression has **no store entry** — it is disposed via `BindingOperations.TearDown` (the registry leg); getter/setter delegates + observer unhooked | PIN (BD19; doc §6.5) |
| B111 | a `DirectProperty` target on a **non-element** `UIObject` (no tree lifecycle), bound | drop the object without disposing | caller-owned (documented loudly): no sweep runs; the DEBUG leak tracker flags this case **specially** (no tree lifecycle exists) | PIN (doc §6.5 table) |
| B112 | binding to a `NotDataBindable` property (`NDB`) | `Install` | `ArgumentException` (metadata `NotDataBindable` — confirmed `PropertyEffects` flag) | PIN (doc §6.2; spec §2.3) |
| B113 | binding to a read-only `UIProperty` (`RO`, no public setter; `KRO` key) in OneWay | `Install` | **DEV-corrected (2026-06-12):** rejected at install with `InvalidOperationException` (PD14). The producer mouth (`Bind`/`BindInFrame`/`BeginAnimation`) enforces PD14 — read-only properties are writable *only* through their `UIPropertyKey`, never through a binding producer (precedence-matrix **M209**, shipped/green, supersedes the prior "legal through the entry mouth" premise; a binding entry IS a `Bind` producer and cannot bypass the key gate). The S1 layer is the oracle for entry-mouth admission. | DEV (matrix M209; corrects the prior B113 premise) |
| B114 | DEBUG leak tracker on | install with site capture, then window close without sweeping one expression | the weak-target sweep on window close reports the undisposed expression: path + install site + target description; the strong-no-leak-**only-if-sweep-runs** invariant is the row's subject | PIN (BD4/BD19) |
| B115 | `GetBindingExpression(a, Text)` for a property with no binding | call | returns `null` | PIN (BD7) |
| B116 | reentrancy: a push triggers a `When` flip → cookie retraction → eviction of **the pushing expression's own** frame, mid-stack | drive the push | `Dispose(fromEviction:true)` skips `entry.Dispose()` (store mid-eviction); `Dispose` idempotent; after the push returns the expression re-checks `Disposed` and unwinds without touching `_nodes`/`_tokens`; no double-dispose, no NRE | PIN (BD18) |
| B117 | a source INPC arrives on a **disposed** expression | raise after dispose | the handler entry point checks `Disposed` and returns; no work, no throw | PIN (BD18) |
| B118 | `Dispose()` called twice on an expression | call ×2 | idempotent (gated by `Disposing`/`Disposed`); second call is a no-op | PIN (BD18) |
| B119 | a free-standing entry's `BindingHostState` slot starts `null` (no bindings) | first install | the slot allocates the inline list lazily on first install; an element that never binds carries a `null` slot (one pointer, no `ConditionalWeakTable`) | PIN (BD19; `BindingHostState` confirmed) |
| B120 | nested teardown: `Root.TearDown()` where a child's `OnEvicted` (a style watcher backstop) disposes a sibling-anchored watch | sweep | the sweep is robust to re-entrant disposal during eviction; each expression disposed exactly once; the registry never re-enters an already-swept element | PIN (BD18/BD19) |

---

## 10. ElementName, NameScope, and FindName — B121–B130 *(B1)*

Doc §6.3. `ElementName` resolves through `NameScope.FindEnclosing`; `UIElement.FindName` is S2-owned. Scope producers (Fork C / S8 / DataTemplate) are stubbed in tests with manual `SetNameScope`/`TemplateNameScopeProperty` stamping.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B121 | document scope on `Root` registering `"editor" → b`; bind `a.Text` ← `new Binding("Num") { ElementName = "editor" }`; `a`,`b` attached | activate | `FindEnclosing(a)?.Find("editor") == b`; the path resolves against `b`; `a.Text == b.Num` | WPF |
| B122 | as B121 but `a` not yet attached at install | install, then attach | parks `SourceMissing` (no trace yet); resolves on `AttachedToLogicalTree`; produces after attach | WPF/PIN |
| B123 | as B121 but the name `"editor"` is never registered, after attach | activate + attach | `NameNotFound` trace (only **after** attach — a forward reference during build does not trace early) | WPF/PIN |
| B124 | `a` is a **template part** of `ctrl` (`a.TemplatedParent == ctrl`); `ctrl` carries a `TemplateNameScopeProperty` registering `"partName"`; document scope on the window registers a different `"partName"` | `FindEnclosing(a)` | the guard: `a.TemplatedParent == ctrl` ⇒ the **template** scope is consulted first → the part name; template names are seen by parts | PIN (BD21; doc §6.3 guard) |
| B125 | a **document content child** `d` of `ctrl` (`d.LogicalParent == ctrl` but `d.TemplatedParent != ctrl`); both scopes from B124 present | `FindEnclosing(d)` | the guard **fails** at `ctrl` (`d.TemplatedParent != ctrl`) ⇒ the walk continues to the **document** scope → the document name; part names are invisible to document content (the pinned conformance test) | PIN (BD21; doc §6.3 conformance) |
| B126 | `DataTemplate` realization: a fresh scope attached to the item root's **document** slot (`SetNameScope`) registering item-instance names | `FindEnclosing(itemChild)` | item-instance names are subtree-visible and shadow outer names (no reach-in / no barrier — the DataTemplate slot is the document slot on the item root) | PIN (doc §6.3) |
| B127 | `UIElement.FindName("toast")` where `toast` is document-registered under the window | `window.FindName("toast")` | returns the element (`= FindEnclosing(window)?.Find("toast")`); S5 storyboard targeting + app code consume this | PIN (BD21; doc §6.3 "S2 owns FindName") |
| B128 | `FindName("nope")` (unregistered) | call | returns `null` (lookup miss, not a throw) | PIN |
| B129 | inside a template instance, `ElementName` binding to another part | activate | `FindEnclosing` returns the template namescope (guard-matched) → finds the sibling part; document names invisible | PIN (doc §6.4) |
| B130 | `ElementName` source resolved, then the **anchor** reparents out of the resolving scope | reparent `a` out of the named scope | the expression re-resolves on the **anchor's** detach/attach events (WPF — `ElementName` follows the anchor's tree life, not the named element's): leaving the resolving scope ⇒ re-resolution finds the name unreachable ⇒ `NameNotFound`, the old source subscription is dropped, no stale push. (Clarified 2026-06-12: detaching the named element alone does not sever the binding — only anchor tree events re-resolve.) | PIN/WPF |

---

## 11. The LostFocus trigger (routed event + edit-commit pulse) — B131–B138 *(B1)*

Doc §6.6 step 4 / §6.11.6. Two distinct flush sources; terminal focus-out retains keyboard focus.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B131 | `Mode = TwoWay`, `UpdateSourceTrigger = LostFocus`; bind `a.Text` ← `"Name"`; `a` focused | `set(a, Text, "edit")` (no focus change) | `SourceDirty` set; the source is **not** yet written (`vm.Name` unchanged) | WPF |
| B132 | continue B131 | `lostFocus(a)` (S3 routed `LostFocusEvent` — physical focus moves off `a`) | the pending edit flushes: `vm.Name == "edit"` | WPF |
| B133 | continue B131 (still dirty, `a` still focused) | `termOut()` (terminal focus-out: `FocusEvent{false}` → `EditCommitRequested(a)`, focus retained) | the pulse flushes the pending edit: `vm.Name == "edit"`; keyboard focus is **retained** on `a` (no `LostFocusEvent` raised) | PIN/DEV (doc §6.11.6, §13) |
| B134 | `LostFocus` trigger; cross-scope physical move (focus to a menu/toolbar, a separate scope) | the move raises `LostFocusEvent` on `a` | flushes — physical-focus move flushes where WPF's logical-focus trigger would not (recorded divergence §6.11.6) | DEV (doc §6.11.6) |
| B135 | `LostFocus` trigger | activation | **DEV-corrected (2026-06-12):** the routed `LostFocusEvent` is a **framework** event, always available (focus is framework-tracked in Cursorial, not a terminal capability), so the spec's "routed event unavailable ⇒ fall back to `PropertyChanged` + `Warning`" path is **unreachable** — the `LostFocus` trigger always rides the routed event + the edit-commit pulse. The row asserts the trigger does NOT flush on the keystroke (which a `PropertyChanged` fallback would) and DOES flush on the routed event. | DEV (corrects the spec's capability premise — no S3 capability gate on a framework routed event) |
| B136 | `LostFocus` trigger; the edit was flushed; focus leaves again with no new edit | `lostFocus(a)` | no write (nothing dirty); the source is not re-written | WPF |
| B137 | `LostFocus` trigger; `a` focused with a dirty edit; `a` is **detached** (permanent) before focus leaves | `Root.TearDown()` | the teardown sweep disposes the expression; the LostFocus subscription is unhooked; the dirty edit is **not** flushed by a later spurious event | PIN (BD4/BD18) |
| B138 | `Explicit` trigger contrasted with `LostFocus` on the same setup | `lostFocus(a)` on the Explicit binding | the Explicit binding does **not** flush on LostFocus — only `UpdateSource()` flushes it (the trigger taxonomy is exact) | WPF |

---

## 12. TemplateBinding & the compiled lane — B139–B156 *(B2)*

Doc §6.7. `TemplateBinding` fast path + validation + the untyped→typed bridge; `Binding.Compiled` runtime lane (reflective fallback is v1, descriptor shape pinned). Template-content eviction conformance against the `ValueFrame` kit.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B139 | a part `p` with `p.TemplatedParent = ctrl`; frame-hosted `Install(p, Text, new TemplateBinding(ctrl-typed Text-equivalent property), frame)` | `set(ctrl, Num, 5)` (the source property) | one-way fast path: observer on the templated parent → typed entry push via the `CreateTemplateTransfer` bridge (no path parse, no DataContext dependency); `p.Text` reflects `ctrl`'s property | WPF (BD16) |
| B140 | `new TemplateBinding(prop) { Mode = TwoWay }` | `CreateExpression` | `InvalidOperationException` naming `Mode` (only Default/OneWay allowed) | PIN (BD15) |
| B141 | `new TemplateBinding(prop) { UpdateSourceTrigger = Explicit }` | `CreateExpression` | `InvalidOperationException` naming `UpdateSourceTrigger` (non-default forbidden) | PIN (BD15) |
| B142 | `new TemplateBinding(prop) { Converter = c }` | activate | honored, but **forfeits** the typed fast path — routes through the boxed pipeline (the value still converts) | PIN (BD15) |
| B143 | the untyped→typed bridge: `TemplateBinding` over a `StyledProperty<int>` target | activate | `UIProperty.CreateTemplateTransfer` (overridden by `StyledProperty<int>`) wires `AddObserver<int>` → `BindingEntry<int>` with `T` closed at registration — no reflection, no `MakeGenericType` (internal probe asserts the typed entry type) | PIN (BD16; `StyledProperty.CreateTemplateTransfer` confirmed) |
| B144 | a template instance with a `TemplateBinding` expression (frame-hosted in the instance's frames) | `TemplateInstance.Detach()` | the instance's frames are removed → frame-hosted entries evicted → **every** expression created inside template content dies (the barrier-teardown guarantee feeding the leak tracker); no expression survives `Detach` | PIN (doc §6.5; spec PROVIDES) |
| B145 | two-way reach-in via `new Binding { RelativeSource = RelativeSource.TemplatedParent, Mode = TwoWay }` | target write | the reflection-lane TemplatedParent binding writes back to the templated parent's property (the documented two-way reach-in path; `TemplateBinding` itself stays one-way) | WPF (BD15) |
| B146 | `Binding.Compiled(static (Vm m) => m.IsDirty)`; bind `a.Flag` ← that; `Root.DataContext = vm` | `vm.IsDirty` raises INPC | compiled lane: `_anchor.Root is Vm s ? Getter(s) : …`; `Steps` drive the INPC rewiring; the value is one whole-chain `Getter(s)` call; `a.Flag == vm.IsDirty` | PIN (BD17) |
| B147 | as B146, the target is `StyledProperty<bool>` (`Flag`), no converter/StringFormat | per source change | push via `BindingEntry<bool>.SetValue(v)` — **0 B** steady state (zero boxing); the row asserts the allocation delta after warm-up (the compiled-lane perf claim, the binding analog of `AnimatedValueHandle<T>`) | PIN (BD17; doc §6.11.7) |
| B148 | `Binding.Compiled((Vm m) => m.Sub.Name)` (member chain) | `vm.Sub` swap | the `Steps` per-hop object getters drive subscription rewiring; the typed whole-chain `Getter` reads the value; a swapped intermediate rewires downstream | PIN (BD17) |
| B149 | `Binding.Compiled((Vm m) => m.Tags[0])` (constant-index hop) | `Tags[0]` INCC `Replace` | the indexer step carries `MemberName == "Item[]"` and subscribes `INotifyCollectionChanged`; the value re-reads via `Getter` | PIN (BD17; doc §6.7 indexer hops) |
| B150 | `Binding.Compiled((Vm m) => m.Age + 1)` (operator in the lambda) | `Binding.Compiled(...)` analysis | `FormatException` naming the offending node (`+` operator) — member + constant-index hops only | PIN (BD17) |
| B151 | `Binding.Compiled((Vm m) => m.GetName())` (method call) | analysis | `FormatException` naming the method-call node | PIN (BD17) |
| B152 | a compiled binding with `Setter == null` (read-only leaf) used `TwoWay` | wire | degrades to OneWay + one-time `Warning` (BD10 applies to the typed lane via the null `CompiledBinding.Setter`) | PIN (BD10/BD17) |
| B153 | a compiled binding whose anchor resolves to a **non-`TSource`** object (ElementName/FindAncestor mismatch) | activate | `SourceTypeMismatch` trace + `Unset` (styling: unmet) — the typed root check covers anchor/type mismatch | PIN (BD17) |
| B154 | a compiled binding **anchored** via `RelativeSource.Self` (full `AnchoredBinding` surface on `CompiledBinding<,>`) | activate | the compiled lane shares anchoring with the reflection lane — Self/TemplatedParent/ElementName/FindAncestor all available; `a.Flag` reflects the self-source typed read | PIN (BD17; doc §6.7) |
| B155 | a struct-typed intermediate hop in a compiled chain (`vm => vm.SomeStruct.Field`) | activate | the whole-chain `Getter` copies the struct; no subscription is attempted on the non-INPC struct hop; the value reads correctly | PIN (BD17) |
| B156 | a compiled binding installed at LocalValue + the reflective-fallback equivalence: the same path via `Binding.Compiled` and `new Binding("IsDirty")` | both activate against `vm` | both produce the identical target value through the same engine contract (descriptor shape consumed identically); the compiled lane only changes value access + push typing — the lifecycle/anchoring/triggers are shared | PIN (BD17; Fork C "second producer") |

---

## 13. Watch-only surface (the Fork B `When`/`DataCondition` seam) — B157–B168 *(B0)*

Doc §6.2/§6.8, ledger B16. `BindingOperations.Watch` → `IBindingWatch` (NO `Pause`/`Resume`). This closes the styling engine's deliberate Y-stage hole (`When`/`DataCondition` absent pending `Watch`).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B157 | `Watch(a, new Binding("IsDirty"), onChanged)`; `Root.DataContext = vm`, `vm.IsDirty = false` | `vm.IsDirty = true` | no store entry created; `watch.Value` follows the source; `wlog == [false, true]`; delivery is synchronous on the UI thread | PIN (BD14) |
| B158 | `Watch(a, new Binding("Sub.Name"), onChanged)` with `vm.Sub == null` | activate | unresolved path ⇒ `onChanged(Unset)`; `watch.Value == Unset` (styling pins this as **unmet**) | PIN (BD14; doc §6.8) |
| B159 | `Watch` self-source: `Watch(a, new Binding("Num") { RelativeSource = RelativeSource.Self }, onChanged)` | `set(a, Num, 4)` | the self-source `When` data half: `watch.Value == 4`; ships in B0 (the numbered Fork B requirement) | PIN (doc §6.8 "self-source in B0") |
| B160 | `Watch` ancestor-source: `Watch(a, new Binding("Num") { RelativeSource = RelativeSource.Ancestor<StackPanel>() }, onChanged)` | `set(paneA, Num, 9)` | the ancestor-source `When` data half: `watch.Value == 9`; ships in B0 (the numbered requirement, whole at spine) | PIN (doc §6.8 "ancestor-source in B0") |
| B161 | `Watch(a, new Binding("Name"), onChanged)`; `Root.DataContext = vm` | `set(Root, DataContext, vm2)` | DataContext change ⇒ the watch automatically rebinds and re-delivers `vm2.Name`; `wlog` shows the re-delivery | PIN (BD14; doc §6.8) |
| B162 | a `When`-driven Style rule armed via `Watch` on `a` (the styling integration): rule active iff `watch.Value == true` | `vm.IsDirty` false→true→false | end-to-end: `false` ⇒ rule inactive (unmet), `true` ⇒ rule **activates** (the frame arms via the styling engine on the callback), `false` ⇒ deactivates; the property the rule sets flips accordingly across frames | PIN (doc §6.8; the deliberate-hole close) |
| B163 | a `Watch` whose styling rule **deactivates** (the rule drops out of contention while still structurally matched) | deactivation edge | the watcher stays **live** (it is the re-activation predicate — ledger B16); it is NOT paused; `IBindingWatch` exposes no `Pause`/`Resume` | PIN/DEV (BD14, ledger B16 over spec §3.9) |
| B164 | a `Watch` on `a`; `a` is **detached** (the rule disarms) | detach | the watcher is **disposed** at disarm/detach (watcher lifetime = armed lifetime); subscriptions unhooked; reattach rebuilds arming from scratch | PIN (BD14; doc §6.8) |
| B165 | `watch.Dispose()` directly | dispose | idempotent; subscriptions dropped; further source changes do not deliver | PIN (BD14/BD18) |
| B166 | a `Watch` whose styling engine never disarms (leak path) | `Root.TearDown()` | the teardown sweep is the backstop: registry-tracked watches anchored on the element are disposed by `BindingOperations.TearDown` | PIN (BD14/BD19) |
| B167 | `Watch` callback delivery during a frame (a VM-driven flip mid-input-drain) | `RunFrame()` | the flip participates in the **same** frame (synchronous UI-thread delivery) — a live `When` condition reaches layout that frame (invariant 1) | PIN (BD14/BD20) |
| B168 | `Watch` with a converter + value comparison (`DataCondition Value=False` style) | source flips | the watch delivers the converted value; the styling-side equality compare (`== Value`) is the styling engine's job — the watch delivers raw/converted, not a boolean verdict | PIN (doc §6.8 — engine is the data half) |

### 13a. `Style.When`/`DataCondition` end-to-end through the styling engine — B162a–B162h *(P4; the deliberate-hole close, real integration)*

Doc §3.1/§3.3/§6.8. `Style.When` is a `WhenCollection` of `DataCondition`s; the engine arms one `BindingOperations.Watch` per condition when a rule structurally matches, gates activation on every condition being met (unset ⇒ unmet, §3.3), reconciles on each delivery through the queued/fixpoint Phase-2 path, and disposes watches at disarm/detach (B16). Setup uses the `StyleMatrixFixture` tree (`tree.App.Styles.Add(R(sel){…})`) with a `DataCondition` added to the style's `When`; the element's `DataContext` is a `Vm`. Tests live in `Cursorial.UI.Tests/StyleMatrix/Section14_When.cs`, namespace `Cursorial.Tests.UI.StyleMatrix` (the styling-engine integration is the styling oracle's concern; the binding engine owns the data half).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B162a | `R("Widget"){P=5}` with `When { new DataCondition(new Binding("IsDirty"), true) }`; `a.DataContext = vm{IsDirty=false}` | show / arm | the rule is armed but **inactive** (the condition is unmet — `false ≠ true`); `a.P == 0` (default); the rule appears in `MatchedRules` (armed) but is not active | PIN (doc §3.3 — When unmet) |
| B162b | continue B162a | `vm.IsDirty = true` | the watch delivers `true` ⇒ the condition is met ⇒ the rule **activates synchronously** (no frame pump); `a.P == 5`; one notify at `StyleTrigger` (a `When`-guarded rule is conditional — precedence-matrix PD26, 2026-07-12) | PIN (doc §3.3/§6.8 — live When flip) |
| B162c | continue B162b (active) | `vm.IsDirty = false` | the condition becomes unmet ⇒ the rule **deactivates**; `a.P` promotes back to `0` (store-owned retraction, invariant 4); one notify | PIN (doc §3.3) |
| B162d | a structurally non-matching element (`b` with no class) under the same `R(".primary"){…}+When` | arm | no watch is armed (the rule never structurally matches `b`) — `When` watchers connect only at structural match (doc §3.3 "watchers connect at arm time") | PIN (doc §3.3) |
| B162e | `R("Widget"){P=5}` with `When { DataCondition(Binding("Sub.City"), "NYC") }`; `a.DataContext = vm{Sub=null}` | arm, then `vm.Sub = addr{City="NYC"}` | unresolved path ⇒ unmet ⇒ inactive; after the chain resolves to `"NYC"` the watch re-delivers and the rule activates; `a.P == 5` | PIN (doc §3.3/§6.8) |
| B162f | a `When`-guarded rule (`Widget` + 1 condition, `P=5`) vs an **unguarded** rule (`Widget`, `P=9`) at the same layer/order region | both match, condition met | the guarded rule wins — each `DataCondition` counts 1 classLike (SD5), so the guarded rule's sort key is higher; `a.P == 5` while met, `9` when unmet | PIN (doc §3.4; SD5 realized) |
| B162g | two conditions on one style (`IsDirty == true` **and** `Age` predicate `> 5`) | flip each independently | the conjunction holds iff **both** are met (there is no `Or`); the rule activates only when both hold and deactivates when either fails | PIN (doc §3.1 — conjunction) |
| B162h | an active `When`-guarded rule on `a`; `a` is **detached** (`OnElementDetached`) | detach | the frame retracts (store promotes) **and** every `When` watch is disposed (no lingering INPC subscription); a post-detach `vm` change does not re-activate or throw; DEBUG leak tracker reports no leak | PIN (doc §3.3/§6.8; B16) |

---

## 14. Diagnostics (`BindingDiagnostics`) — B169–B178 *(ring/sinks B0; `Explain` B1)*

Doc §6.10. Never write to the terminal. Ring policy: Warning/Error always constructed + ring-recorded; Verbose gated.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B169 | a broken path that traces `PathError`/`SourceMissing`; `Level == Error` (default) | activate | the event is constructed and recorded to the 256-entry ring (`RecentEvents`) regardless of `Level` (Warning/Error always recorded — failure paths only); `TraceEmitted` + sinks receive it (severity ≥ Level) | PIN (doc §6.10 ring policy) |
| B170 | a happy-path binding; `Level == Error` | activate + steady-state changes | **no** diagnostic events constructed on the happy path (0 alloc for diagnostics); `RecentEvents` unchanged | PIN (doc §6.10) |
| B171 | `Level == Verbose` or `binding.Trace == true` | activate a happy binding | Verbose events are constructed (gated on `Level`/`Trace`) and enter the ring; with `Level == Error` they are not | PIN (doc §6.10) |
| B172 | `BindingDiagnostics.AddSink(testSink)` + `TraceEmitted += ...` | a Warning event | both the sink and the event handler receive the `BindingTraceEvent` (level, `BindingFailureKind`, path, target description like `Widget#a.Text`, message, `Environment.TickCount64`) | PIN (doc §6.10) |
| B173 | the ring overflows (>256 events) | record 300 events | overwrite-oldest; `RecentEvents` holds the most recent 256; `ErrorCount` counts all errors | PIN (doc §6.10) |
| B174 | `CURSORIAL_BINDING_TRACE=<path>` env set | a Warning event | an env-gated file sink writes the event (mirroring `CURSORIAL_TRACE_OUTPUT`); never the terminal | PIN (doc §6.10; repo convention) |
| B175 | `Explain(a, Text)` with one active LocalValue binding | call | one line reporting the LocalValue lane: status (`Active`), the resolved source chain, last produced value, last failure (none) | PIN (doc §6.10; SD13-shaped) |
| B176 | `Explain(a, Text)` covering all lanes: a frame-hosted binding shadowed by a LocalValue binding | call | lines for **every** expression across lanes (LocalValue / frame-hosted / watch-only / DirectProperty), strongest-first; the winning lane first — `Explain` covers all lanes while `GetBindingExpression` covers LocalValue only | PIN (BD7; doc §6.10) |
| B177 | `Explain(a, Text)` for a path-error binding | call | the line shows `Status == PathError`/`SourceMissing`, the last failure kind, and the resolved-so-far chain | PIN (doc §6.10) |
| B178 | `BindingDiagnostics.DumpTo(writer)` | call | a post-session dump (called **after** session disposal in the canonical teardown order — the row asserts it does not write to the terminal mid-session); content = the ring + active explanations | PIN (doc §6.10) |

---

## 15. Compiled-binding descriptor shape & generator handshake — B179–B186 *(B2 descriptor; B3 generator)*

Doc §6.1/§6.7. The descriptor is defined now and consumed by the engine; the generator is a second producer (B3, no engine change). v1 binds reflectively.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| B179 | `new CompiledBinding<Vm,bool>(getter, setter, steps, "IsDirty")` by hand | inspect | the descriptor exposes `Getter`/`Setter`/`Steps`/`PathText`; `Setter == null` ⇒ one-way only; it inherits the full `AnchoredBinding` surface (Source/ElementName/RelativeSource) | PIN (doc §6.1) |
| B180 | `CompiledPathStep("Item[]", getStep)` | inspect | a constant-index hop's `MemberName` is `"Item[]"` (the INPC convention); `GetStep` applies the captured index; the object-typed step getter is used only for subscription rewiring (the typed `Getter` does value reads) | PIN (doc §6.1; spec §2.1) |
| B181 | `Binding.Compiled(static (Vm m) => m.IsDirty)` cached in a `static readonly` field | use across two elements | the descriptor is construction-immutable + instance-shareable: one `CompiledBinding` serves both; per-target state lives in expressions; each `Binding.Compiled` **call** re-analyzes the tree (so cache the result) | PIN (doc §6.1) |
| B182 | a `CompiledBinding` with a `Converter` and a `StyledProperty<TValue>` target | activate | the typed zero-box push is forfeited (converter present) → the boxed pipeline (B2 fast-path condition is "no converter/StringFormat") | PIN (BD17) |
| B183 | the X4 generator emits the `CompiledBinding` ctor directly (simulated hand-written equivalent) | activate | identical runtime behavior to `Binding.Compiled` — same type, second producer, **no engine change**; the row asserts behavioral equivalence to the reflective fallback (B156) | PIN (doc §6.1/§6.13 B3) |
| B184 | `x:DataType` build-time path diagnostics (descriptor-shape level): a `PathText` referencing a non-existent member under the declared type | generator analysis (B3) | a build-time path diagnostic names the offending member + type — the descriptor carries `PathText` for exactly this; v1 (reflective) produces a runtime `PathError` trace instead | PIN (doc §6.1/§6.13 B3) |
| B185 | the reflective fallback **is** the v1 producer | bind any compiled-shaped path before X4 lands | `Binding.Compiled` works at runtime via `expr.Compile()` (interpreter on AOT-without-codegen); the generator is purely additive | PIN (BD17; the prompt's "reflective fallback in v1") |
| B186 | the `BindingActivationContext` carries the host frame for template-content compiled installs | install a compiled binding in template content | the install routes to `Install(..., hostFrame)` (frame-hosted), participating in the instance's frames and dying on `Detach` — the compiled lane honors the same install seams as reflection | PIN (doc §6.2/§6.7) |
| B188 | a MULTI-HOP `x:DataType` `{Binding}` (e.g. `Inner.Caption`) under full-lowering | generator analysis (B3/P1D) | COMPILES to a typed `CompiledBinding<TSource,TLeaf>` with a **null-safe whole-chain getter** (`?.` after each reference-typed hop), one `CompiledPathStep` per hop (INPC/INCC rewiring), and a **null-guarded reverse setter** emitted only when the leaf is writable AND its owner is a reference type (a value-typed owner ⇒ OneWay/null, B152). **Pinned null semantics:** a null INTERMEDIATE yields `default(leaf)` (never an NRE — safer than `Binding.Compiled`'s `path.Compile()`), which matches the reflective lane for the non-null reads B156 pins and differs only on a null intermediate (`default(leaf)` here vs reflective `UnsetValue` — the documented, opt-in-via-full-lowering difference; indexer/method hops stay reflective). **REALIZED (X5/B3 — P1D; P1-REVIEW hardened):** `TryEmitCompiledBinding` walks the dotted path against the x:DataType (per-hop `FindMember`), threading owners/types. A hop accessed ON a `Nullable<T>` (path through `.Value`/`.HasValue`) BAILS to the reflective lane (the `is T? __t` step pattern wouldn't parse + `.Value` would NRE); a hop name that is a C# reserved keyword (a VB/F# VM's `event`/`class`/…) is `@`-escaped at every member-access position. Tests: `LoweringEmitterTests.Lowered_Binding_MultiHop_CompilesAndBindsLive` + `…IndexerPath_FallsBackToReflectiveBinding`; `LoweringGeneratorTests.LoweringOptIn_MultiHop_NullIntermediateSafe_AndTwoWayWriteBack`. | PIN (doc §6.7/§6.13 B3) |
| B187 | under full-lowering, an `x:DataType`-scoped `{Binding}` that the compiled lane DECLINES (multi-hop/indexer/method path, explicit Source/ElementName/RelativeSource, Converter/StringFormat/FallbackValue, unrecognized Mode, or a static/not-found/not-readable leaf) but the reflective lane still emits a working binding for | generator analysis (B3) | a **CURG2002 Info** diagnostic at the binding's `.xaml` position names WHY it stayed reflective (the binding works — Info, not Warning); it does NOT fire for a successfully-compiled single-hop binding, for a whole-DataContext (`.`) binding, for a non-`x:DataType` binding, for a Converter binding the reflective lane also drops (that is the CURG3001 gap), or for a path-walk failure (a NOT-FOUND member is the path validator's CURG2001; a static / write-only member wouldn't resolve through an instance DataContext, so no false "it works" — P1-REVIEW Fix E). **REALIZED (X5/B3 — P1A):** `TryEmitCompiledBinding` threads the bail reason; `EmitReflectiveBinding` reports whether it emitted; `EmitBinding` raises `Context.Info` → `CURG2002`. Test: `LoweringGeneratorTests.LoweringOptIn_ReflectiveFallback_EmitsCurg2002Info`. | PIN (doc §6.13 B3) |

---

## 16. Test authoring contract

Each numbered row above becomes **exactly one** xUnit test in `Cursorial.UI.Tests`, named after its row id with a behavior slug (`B090_SetCurrentValue_PreservesTwoWayBinding_WritesBack`), one file per section under `Cursorial.UI.Tests/BindingMatrix/` (`Section01_PathParsing.cs` … `Section16` is the contract, so `Section01`…`Section15`), namespace `Cursorial.Tests.UI.BindingMatrix`. Rows whose Expected cell enumerates a family (e.g. B4 bare/quoted, B11–B13 the recorded-out parse errors, B30 the two event shapes, B43 the anchor conflicts) become a single `[Theory]` with one case per family member, keeping the row↔test bijection at the row level. The fixture types (`Vm`, `PlainVm`, `Widget` with its `StyledProperty`/`DirectProperty` registrations) are registered once via a shared harness class — **dense property ids are process-global, so registrations must be idempotent across test classes** (the layout/style-matrix harness pattern). Host-level rows use `UITestHost` (`RunFrame`/`SendInput`/`ShowRoot`); unit-level rows (path parse, descriptor validation, pipeline math) call the engine directly on the calling UI thread. Cross-thread rows (B94–B96) use a real background thread + the host's dispatcher (or a test `IUIDispatcher` fake with the loop-wake hook). Allocation rows (B70's note, B147, the §6.11.7 0-B claims) follow the repo norm: `GC.GetAllocatedBytesForCurrentThread()` deltas after warm-up, single-threaded `[Fact]`s, not BenchmarkDotNet; the reflection lane's one boxed leaf is exempt where the row says so. DEBUG-only rows (B27, B97, B111, B114) compile their assertion under `#if DEBUG` and assert the absence of the check/throw in Release where practical. Rows marked internal (B16/B18 cache probes, B143 typed-entry-type probe, B103/B104 frame-hosted-entry probes) use `InternalsVisibleTo` surfaces — pinned loosely: the *content* is the contract, member names are implementation freedom.

Rows are not merged, reordered, or "covered implicitly by" other rows: a row without a matching test is a P4 exit-criterion failure (§14 P4: the binding-pipeline oracle matrix green — fallback/null/format permutations, the DataContext-self case, echo suppression incl. animation-handle disposal — plus the `Watch`/`When` close and the teardown-sweep P1-gap close). Rows are staged per the §0 stage map: §§1–9 + §13 (B0) must be green at B0 exit; §10/§11/§14-`Explain` (B1) at B1; §12 + §15-B2 (B2) with the template engine; §15-B3 (B3) with X4. Later-stage rows may be absent (not red) before their stage opens, but every row is binding from now. When the engine cannot honor a row, the resolution is a PR that amends this file (and, where the row carries a `PIN`/`DEV` tag, the BD ledger) **before** the engine change lands — the matrix is the oracle, not the implementation. Oracle tags document provenance and do not alter test behavior.

---

## 17. Departing views — the detach-time reverse-lane quiesce (B190–B199)

**The pinned rule (BD22): a departing view must not write to its source.** The detach walk quiesces every
expression's reverse lane (target → source write-back AND pending-flush marking, pending LostFocus/Explicit
edits DISCARDED — cancel semantics) at `OnDetachedFromTreeCore`, BEFORE any inheritance severance can
cascade — so the DataContext-loss chain (items sources clear → selections clear) never round-trips into a
view-model as a phantom edit (the curio chooser / dialog-picker selection-loss bug; WPF's most notorious
unfixed defect). Re-attach re-arms at `OnAttachedToTreeCore` (a rescued/re-hosted view binds two-way
again). Pre-first-attach behavior is unchanged: the quiesce is set only by the detach walk, never as an
initial state — bindings on never-attached elements write back as before. The mechanism is
`BindingExpressionCore.QuiesceReverse/ResumeReverse`, fanned per-element by `BindingRegistry` (a null
`BindingHostState` probe — free for unbound elements).

Companion (app-model, doc §10.7 amendment): app dispose DETACHES the mounted root's surface first, then
tears it down (1b — the same order as window close), and sweeps every still-alive formerly-mounted root
(1c — a weak list recorded at `RootElement` swap, each root fenced in its own try/catch so one throwing
sweep can't abandon the rest): past dispose the dispatcher dies and every element of the app is
permanently unusable, so an un-torn swapped-out root is by definition a leak. A teardown-FIRST order was
tried and audited out: `ValueStore.TearDown` evicts style frames the `StyleEngine` still tracks, so the
subsequent detach walk throws PD21 at its first styled element and silently aborts the whole severance —
the quiesce (which makes detach-first harmless) supersedes the reorder. Swapping stays REVERSIBLE
(nothing is torn at swap; A→B→A is legal) and the guide's advice stands — tear a root down eagerly when
swapping it out for good. Window close keeps its load-bearing detach-first order (off-display before
`Closed`; the content-rescue escape hatch), riding the same quiesce.

The adversarial audit hardened the quiesce with five follow-ups (each mutation-verified): the
**same-root re-anchor skip** extends from FindAncestor to ElementName (a mid-detach ElementName
re-anchor resolves the SAME root through the still-intact logical tree — a redundant re-wire whose OWTS
activation pass was a phantom write); the **OWTS activation write gates on the quiesce and is DEFERRED,
not dropped** — a re-host's re-anchor can fire at parenting time, BEFORE the attach walk's
`ResumeReverse` (empirically confirmed), so the resume replays the swallowed activation write exactly
once; a binding **installed on a departed element is born quiesced** (`UIElement.IsDepartedFromTree`,
set detach-after-attach, cleared on re-attach — a fresh install must not resurrect write-back the
departure quiesced; pre-first-attach installs are unchanged); **`UIElementCollection.Clear` batch-
quiesces every child subtree before the first disown** (the cross-sibling gap: an earlier-severed
sibling's cascade must not phantom-write through a later, still-unquiesced one); and the **StyleEngine
tolerates torn elements at detach** (the documented public pattern `element.TearDown()` then
`parent.Children.Remove(element)` — frame retraction skips when the store already evicted them, PD21).

Design note (pinned, cancel semantics): pending LostFocus/Explicit edits are DISCARDED on every close
path — including accept-path closes (default-button Enter, command-driven `Close()`) that previously
auto-committed via the detach-repair focus loss. The working idiom for an accept path is an explicit
`expression.UpdateSource()` (or a focus move) BEFORE `Close()`; a first-class commit affordance (e.g. a
`CommitPendingEdits` sweep) is a possible future amendment, not current contract.

| Row | Scenario | Expected |
|---|---|---|
| B190 | App dispose with a live chooser (ItemsSource + TwoWay SelectedItem) | The VM's selection SURVIVES dispose; the teardown sweep releases the VM's INPC subscriptions (count 0) |
| B191 | Window close with the same chooser as content | The selection survives the close's detach cascade; the terminal sweep releases the VM |
| B192 | Detach → target write → re-attach → target write | The detached write never reaches the source; the re-attached write flows (quiesce/re-arm pair) |
| B193 | Explicit-trigger TwoWay: pending edit at detach; `UpdateSource()` after detach | The pending edit is discarded; the explicit flush no-ops — never a phantom edit |
| B194 | Root swapped out un-torn; app dispose | The former root still pins its VM until dispose (swap is reversible); the 1c backstop sweeps it — subscriptions 0, no phantom writes |
| B195 | ElementName-anchored OWTS; source moved on; root swapped out | The mid-detach same-root re-anchor never re-fires the activation write — the source keeps its value (covered in depth: the same-root skip AND the gated-deferred write each suffice) |
| B196 | OWTS view detached from host A, re-hosted under host B | No write while departed; the re-host activation write lands in B's VM exactly once (the resume replays a pre-resume-swallowed write); A's VM untouched |
| B197 | Fresh TwoWay + OWTS installs on a DEPARTED element; then re-attach | Both born quiesced (no write-back resurrection, no immediate OWTS activation); re-attach re-arms and replays the deferred activation write |
| B198 | `Children.Clear()` with a dependent chooser BELOW its provider sibling | The batch pre-pass quiesces every subtree before the first disown — the provider's severance never round-trips through the chooser into the VM |
| B199 | Public pattern: `element.TearDown()` then `parent.Children.Remove(element)` on styled content | No throw; the detach walk runs to completion (torn-element style-frame retraction is tolerated) |

Tests: `Cursorial.UI.Tests/BindingMatrix/Section17_DepartingViews.cs` (one test per row, the §16 contract).
