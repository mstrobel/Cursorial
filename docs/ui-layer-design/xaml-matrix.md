# Fork C — oracle-pinned XAML runtime-loader matrix (parse/node-graph, instantiation, markup extensions, deferred content + resources, diagnostics)

Status: **normative test specification**, authored 2026-06-13 *before any `Cursorial.UI.Xaml` loader code exists* (design doc §14 P6; the repo's matrix-first discipline, mirroring `precedence-matrix.md`, `layout-matrix.md`, `input-matrix.md`, `style-matrix.md`, and `binding-matrix.md`). Every numbered row below becomes exactly one xUnit `[Fact]`/`[Theory]` in `Cursorial.UI.Xaml.Tests` (test authoring contract at the end, §16). The loader is written *to* this matrix; a red row is a loader bug unless a PR amends this file first.

Canonical semantics sources, in precedence order: `docs/ui-layer-design.md` §4 (incl. the engine amendment ledger C-1…C-22 in §4.12) + §0 invariants + §13 resolved decisions + §14 P6 **over** `docs/ui-layer-design/proposal-xaml-runtime-loader.md` and `docs/ui-layer-design/decisions.md` Fork C. Places the proposal is superseded by the doc and this matrix pins the doc's side:

- ① **Deferral is type-contract-driven** (doc §4.1, ledger superseding proposal §2.6/§3.5): a member typed `ITemplateContent` defers; there is **no `[DeferredContent]` attribute** (the proposal's `DeferredContentAttribute` is cut). `ControlTemplate.Content`, `DataTemplate.Content` defer; `Storyboard`/`TransitionCollection` do **not** (C-12 — no `ITemplateContent`-typed member). Note the existing `DataTemplate.Content` is `ITemplateContent?` and `ControlTemplate.Content` is `ITemplateContent?` — both satisfy the type-contract rule today.
- ② **Markup-extension results attach via the `IDeferredValue.AttachTo` seam — never sentinel objects through `SetValue`** (doc §4.4, §4.11 rejected, ledger C-1/C-5). Value stores only ever see values. `DynamicResource` on a direct property → `SetResourceReference` (producer at `BindingPriority.LocalValue`); on a `Setter.Value` → a `ResourceReference` carrier. `StaticResource` resolves **eagerly** against the ambient stack. `Binding`/`TemplateBinding` attach **live** through `BindingOperations`.
- ③ **The default Cursorial xmlns (`https://cursorial.dev/ui`) covers `Cursorial.UI` + `Cursorial.UI.Controls` + `Cursorial.UI.Data`** (prompt; doc §4.1 default-map). `xmlns:x="https://cursorial.dev/xaml"` is the intrinsics namespace (`x:Class`/`x:Name`/`x:Key`/`x:Type`/`x:Null`/`x:Static`). `Cursorial.UI.Media` brush builders map into the default xmlns (C-7).
- ④ **The parser frontend is a separate `netstandard2.0` assembly `Cursorial.UI.Xaml.Frontend`** (doc §4.1; prompt) — the node model + `XmlReader` parse + diagnostics + markup-extension grammar, shared with the future X4/X5 generator. `Cursorial.UI.Xaml` (net10.0) references it and owns instantiation. Rows tagged *(frontend)* exercise the netstandard2.0 surface with **no** `Cursorial.UI` reference at parse time; rows tagged *(loader)* run the full instantiation.
- ⑤ **`AccessText` folding is metadata-flag-driven for object-typed slots** (doc §4.7, ledger C-19): a string literal folds to `AccessText` iff the resolved per-type metadata of the instance's runtime type carries Fork A's `ParsesAccessKeyLiterals` — exactly `ButtonBase.Content`, `MenuItem.Header`, `TabItem.Header`, `Label.Content` (only `ButtonBase`/`ScrollViewer`/`Label` exist at P6 — `MenuItem`/`TabItem` are deferred controls, so their rows are pinned-but-deferred). `TextBlock.Text` and unflagged slots never fold. For `AccessText`-typed properties the fold is type-driven (no flag needed). The runtime `ContentControl.GetAccessText()` (P5) is the third identical producer.
- ⑥ **`x:Key` converts through the target collection's `DictionaryKeyType`** (doc §4.3, ledger C-8): a `ResourceDictionary` keeps literal string keys; a `ThemeDictionaryCollection` item's key goes through `ThemeVariantKey.Parse`. `XamlType.DictionaryKeyType` drives the choice.
- ⑦ **`X4`/`X5` are out of scope** (prompt P6 scope fence): no generator (build-time validation / typed `x:Name` fields / `InitializeComponent` / generated `IXamlTypeMetadataProvider` — P10), no compiled bindings at scale, no hot reload, no `PreloadAsync`. The compiled-binding descriptor (`CompiledBinding<TSource,TValue>`, `Binding.Compiled`) already exists (P4 / binding-matrix §15) and is *consumed*, not built. Rows that bind generator behavior are tagged **X4 (deferred)** and are descriptor-/contract-shape only, recorded now, not implemented at P6.

**Phase 6 scope fence** (X0–X3 only — the runtime loader). Inside it:

- **X0** — the node model (`XamlDocument` flat struct arrays), the `XmlReader` parse, xmlns→CLR resolution, member resolution, the markup-extension grammar, constant folding, whitespace handling, diagnostics with **line + column** on every node. No instantiation. *(frontend assembly.)*
- **X1** — the instantiator: type activation, the converter ladder (terminal converters reusing/extending `StyleSetterConverter`'s ladder), content/collection/attached-property syntax, `x:Name` → the document namescope, `Load`/`LoadComponent`, the value-vs-markup-extension disambiguation. *(loader assembly.)*
- **X2** — markup extensions end-to-end (`{Binding}`/`{StaticResource}`/`{DynamicResource}`/`{TemplateBinding}`/`{x:Static}`/`{x:Null}`/`{x:Type}` + custom), resource dictionaries incl. `MergedDictionaries` + separate-file theme dicts + deferred (lazy) entries, the `AttachTo`/`SetResourceReference`/`BindingOperations` attach routing. *(loader.)*
- **X3** — deferred content: a property typed `ITemplateContent` captures a node-graph slice instantiated per-target at expansion; the template namescope (fresh per `Build`, sealed from the document scope); lexical resource-scope capture for templates; access-key literal folding. *(loader.)*

Deferred beyond P6 (recorded, doc §4.13): events inside deferred content (CUR2301 — pinned, parse-time rejected now); `x:TypeArguments`/`x:Shared="False"`/attached events/`x:FieldModifier`; localization/`x:Uid`; East-Asian-aware whitespace; per-instance designer metadata; trimmed/AOT publish (`[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` honest annotations now, generated provider at X5). **Post-P9 (implemented since): `x:Reference` (the markup-extension form, §Section17), the X4/X5 generator deliverables, and `x:Array` + element-valued built-in primitives (§15c, XD27/XD28).** *(amended 2026-06-22)* A binding's named-element anchor — `{Binding ElementName=x}` and `{Binding Source={x:Reference x}}` — resolves to the concrete element through the shared forward-reference deferral (`ResolveName`/`DeferNameResolution`): backward refs resolve at build, forward refs at end-of-(sub)tree, against the **active** name scope — the document scope, or, inside a `DataTemplate`/`ControlTemplate` instance, that instance's own `TemplateBuildContext.NameScope` (so it works in either part order, in templates as at the document level — §Section17). The resolved element becomes the binding's `Source` (binding anchor props are init-only), avoiding the runtime `ElementName` lookup racing the post-build namescope attach. *(amended 2026-06-24)* `Classes="accent primary"` (static style-class assignment, Avalonia parity) — the read-only `ClassSet` (`UIElement.Classes`, no setter) takes a space-separated split → `Classes.Add` per name, in both the runtime loader (`ApplyTextMember`) and the lowering generator (`EmitScalarAssign` → `el.Classes.Add(...)`), at the document level and inside templates (§Section17 / `LoweringEmitterTests`). Dynamic `Classes="{Binding …}"` is deferred. The bare directive-attribute forms `x:Array="…"`/`x:Reference="…"` remain `CUR1203` (X29 — not valid XAML).

Stage mapping (rows for a later stage may stay unimplemented — not red — until that stage opens, but every row is binding from now):

| Stage | Sections | Delivers |
|---|---|---|
| **X0 — frontend: parse → node graph + diagnostics** | §§1–5, §14 (X0 rows) | `Cursorial.UI.Xaml.Frontend` (netstandard2.0): `XamlDocument`/`ObjectRecord`/`MemberRecord`/`ExtensionRecord` flat arrays; `XmlReader` parse (`DtdProcessing=Prohibit`); xmlns→CLR resolution (`XmlnsDefinitionAttribute`, `using:`/`clr-namespace:`, did-you-mean); member classification (directive/attached/event/member) + resolution order; the markup-extension recursive-descent grammar + `{}` escape; constant folding (`IsContextFree`); whitespace; `XamlDiagnostic`/`XamlParseException` with line+column; the parse fence (unsupported constructs throw with position). |
| **X1 — loader: instantiation + converters** | §§6–9, §14 (X1) | `Cursorial.UI.Xaml` (net10.0): `XamlLoader.Load`/`Load<T>`/`LoadComponent`; `XamlType`/`XamlMember`/`ReflectionXamlMetadata`; the converter ladder (geometry/color/`GridLength`/enum/bool/int/double/`Pen`/`IBrush`/`TextAttributes`/`KeyGesture`); `ContentProperty`; implicit-collection fill (`Panel.Children`/`Styles`/`ResourceDictionary`/grid definitions); attached-property syntax; `x:Name` + document namescope + `FindName`; value-vs-extension `{}` disambiguation. |
| **X2 — markup extensions + resources** | §§10–12, §14 (X2) | the built-in extensions end-to-end + custom-extension `ProvideValue`; `IDeferredValue.AttachTo`; `StaticResource` eager / `DynamicResource` (`SetResourceReference` / `ResourceReference`) / `Binding` (`BindingOperations.Apply`) / `TemplateBinding` attach; `ResourceDictionary` loading + `MergedDictionaries` + theme dicts (`Source=` via `ResourceDictionaryLoader.LoadCallback`) + deferred (`SetDeferred`/`IDeferredResourceEntry`) entries + retry-safety; `x:Key`/`DictionaryKeyType`; the module-init `LoadCallback` registration (C-9). |
| **X3 — deferred content + folding** | §§13, §14 (X3) | type-contract deferral (`ITemplateContent`-typed members → node-graph slice); `ITemplateContent` over a slice; per-`Build` template namescope; lexical resource-scope capture; `TemplateBinding` build-time enable + template-body restriction; access-key literal folding (type-driven + metadata-flag). |

---

## 0. Conventions

### 0.1 Assemblies, namespaces, placement

The loader is two new assemblies plus a test project, all added to `Cursorial.sln`:

| Assembly | TFM | Holds | References |
|---|---|---|---|
| `Cursorial.UI.Xaml.Frontend` | `netstandard2.0` | node model (`XamlDocument` + flat records), `XmlReader` parser, markup-extension grammar, `XamlDiagnostic`/`XamlDiagnosticSeverity`/`XamlParseException`, the `IXamlTypeMetadataProvider`/`XamlType`/`XamlMember`/`ITypeConverter`/`XamlValueContext` abstractions, `XmlnsDefinitionAttribute`/`ContentPropertyAttribute`/`XamlMetadataProviderAttribute` | none (no `Cursorial.UI`) |
| `Cursorial.UI.Xaml` | `net10.0` | `XamlLoader`/`XamlLoaderOptions`/`XamlLoadContext`, `ReflectionXamlMetadata`, `XamlConverters` registry + terminal converters, `MarkupExtension` runtime, `XamlTemplateContent`/`XamlDeferredResourceEntry`, the module initializer wiring `ResourceDictionary.LoadCallback` (C-9) | `Cursorial.UI.Xaml.Frontend`, `Cursorial.UI`, `Cursorial.Drawing`, `Cursorial.Rendering`, `Cursorial.Core` |
| `Cursorial.UI.Xaml.Tests` | `net10.0` (xUnit) | the rows below | both above + `Cursorial.UI.Testing` |

Namespace for the public surface is **`Cursorial.UI.Xaml`** (the doc §4.2 sketch; the proposal's `Cursorial.UI.Markup` is superseded by the doc's `Cursorial.UI.Xaml`). `XamlType`/`XamlMember`/diagnostics live in `Cursorial.UI.Xaml` too (re-exported from the frontend assembly under the same namespace). `using CellStyle = Cursorial.Output.Style;` is needed only where a row exercises a style-typed property. Tests live under `Cursorial.UI.Xaml.Tests/XamlMatrix/`, namespace `Cursorial.Tests.UI.Xaml.XamlMatrix`, one file per section (`Section01_Parsing.cs` … `Section15_Generator.cs`).

### 0.2 Fixture

| Symbol | Meaning |
|---|---|
| `loader` | `new XamlLoader()` (default options: `ReflectionXamlMetadata.Instance`, `DiagnosticMode.ThrowOnFirstError`, `FoldConstants = true`, invariant culture) unless a row sets options. `loader.Parse(xml)` = stage 1 (pure, thread-safe, no host); `loader.Load(doc, ctx?)` / `loader.Load<T>(...)` = stage 2 (UI thread). `XamlLoader.Shared` is the process-wide default-options instance. |
| `host` | `UITestHost.Create()` — 80×24, `TestCapabilities.KittyTruecolor` unless stated; `app = host.Application`. Stage-2 rows needing tree attachment / resource chains / theme variant use the host (instantiation is UI-thread). Pure parse rows (§§1–5, grammar) need **no** host and may run on the calling thread. |
| `Vm` | the binding fixture viewmodel from `binding-matrix.md` §0.1 (`string? Name` (INPC), `int Age`, `bool IsDirty`, `Vm? Sub`, `ObservableCollection<string> Results`). Reused so `{Binding}` rows share one oracle with S2. |
| `doc` | the `XamlDocument` from `loader.Parse(...)`; `doc.Diagnostics` is the collected list (in `CollectAll`), `doc.RootType`/`doc.RootClassName` the resolved root + `x:Class`. Internal node-graph shape is asserted through an `InternalsVisibleTo("Cursorial.UI.Xaml.Tests")` probe surface — **the content is the contract; record/field member names are implementation freedom** (the binding-matrix §16 stance). |
| `diag(code)` | the first `XamlDiagnostic` with `Code == code`; `diag.Line`/`diag.Column` are 1-based (the `IXmlLineInfo` convention). `throws(code)` = `XamlParseException` whose first diagnostic is `code`, carrying that line/column. |
| `D` xmlns | the document preamble `xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml"` is assumed on every fragment unless a row alters it; rows show only the body when the preamble is boilerplate. |
| `RootInstance` | for `LoadComponent` rows: a code-behind instance whose runtime type matches `doc.RootType`; `ctx = new XamlLoadContext { RootInstance = inst }`. |

### 0.3 Notation

- `<Button Content="Hi"/>` etc. are the literal XAML under test; multi-line bodies are fenced. `‹…›` elides boilerplate. `⟦x⟧` marks a folded constant in the node graph; `⟨ext⟩` a parsed `ExtensionRecord`; `⟪slice⟫` a deferred node-graph slice.
- `Unset` = `UIProperty.UnsetValue`. `Local` / `TemplateLocal` denote the `BindingPriority`/provenance a member set carries (doc §4.4: document sets `LocalValue`; template builds stamp template-local provenance).
- "line N, col C" asserts the **exact** 1-based position of the offending token (the diagnostic contract); a row that only requires *a* position writes "line/col present".
- "0 B" = `GC.GetAllocatedBytesForCurrentThread()` delta of zero after warm-up, single-threaded (repo norm) — used only for the per-frame / template-rebuild claims; parse/instantiate are explicitly *not* per-frame, so allocation rows are scoped (folded-constant sharing, slice reuse).

### 0.4 Oracle tags

`WPF` = WPF XAML behavior (primary oracle). `System.Xaml` = a row pinned against **real System.Xaml**, flagged for the **Windows-only CI oracle leg** (doc §4.10): escape/quoting, whitespace collapse, ambient `StaticResource` resolution order. `AV` = Avalonia 11 XAML behavior (cited where it diverges from WPF and we follow it — e.g. `using:` xmlns). `PIN` = a Cursorial decision with no direct parent-framework analog (this matrix is the decision record). `DEV` = a deliberate deviation from a parent framework, always with rationale (inline or via the XD ledger).

### 0.5 Pinned decisions made by this matrix (XD ledger)

Each goes beyond — but never against — the canonical doc text; deliberate and binding until amended.

- **XD1 — diagnostics are 1-based line *and* column, on every record, at every stage.** `XamlDiagnostic.Line`/`Column` come from the `XmlReader`'s `IXmlLineInfo` for X0/X1 errors and from the current `ObjectRecord`/`MemberRecord`'s packed `LineInfo` for X2/X3 (instantiation) errors. A diagnostic with `Line == 0`/`Column == 0` is a loader bug. The `CUR1xxx` (parse) / `CUR2xxx` (resolve) / `CUR3xxx` (instantiate) banding is fixed (doc §4.2). PIN (doc §4.2/§4.10).
- **XD2 — the parse fence throws *with position*, never silently degrades.** Every construct on the deferred list (§"Phase 6 scope fence") — `x:TypeArguments`, the bare directive-attribute forms `x:Array="…"`/`x:Reference="…"`, attached events, `x:FieldModifier`, `x:Shared`, `x:Uid`, DTDs/external entities, events inside deferred content — produces a specific `CUR1xxx`/`CUR2xxx` diagnostic naming the unsupported construct and its line/col. No construct is silently dropped. (The `<x:Array>` element form + `{x:Reference}` markup-extension form are now implemented — §15c / §Section17.) PIN/DEV (doc §4.13; recorded outs).
- **XD3 — `FoldConstants` folds iff the member's converter `IsContextFree`.** A context-free converter (Thickness/Margins, Color/hex, GridLength, enum, bool/int/double, TimeSpan, TextAttributes) runs once at parse, the boxed result stored in `Constants` and shared by every `Load`/template `Build`. A context-dependent value (needs services / relative URI / target-type knowledge unavailable at parse) stays `Text` and converts in stage 2 with the cached converter reference. `FoldConstants = false` defers *all* folding to stage 2 (the profiling knob) without changing results. PIN (doc §4.3/§4.6; proposal §3.2 step 5).
- **XD4 — member resolution order is registered `UIProperty` first, then CLR, then `CUR2102`.** Per element/attribute: (1) the Fork A registry (`UIPropertyRegistry.Find(ownerType, name)` — reflection-free, base-walking; attached via `FindOwnersByShortName`/the attached registry); (2) a CLR property via the metadata provider; (3) `CUR2102` with the type's available member list. A `UIProperty` match assigns through `SetValue(prop, value, provenance)`; a CLR-only match through the cached setter delegate. PIN (doc §4.3; proposal §3.2 step 3; `UIPropertyRegistry` confirmed). **Setter `Property` addendum (attached-setter Phase 1):** a *dotted* Setter `Property` name (`Grid.Row`, `Control.Foreground`, `TextElement.TextAttributes`) resolves the **owner** xmlns-aware (the lexical `TargetType` is **ignored** — WPF parity), and therefore needs **no enclosing `TargetType`** at all — `CUR2110` ("no resolvable target type") fires **only** for an *undotted* Setter that lacks a `TargetType` (an undotted name resolves against the lexical Style `TargetType`, the only case the `TargetType` is the owner). This lets a multi-type theme rule (e.g. `.caps-nocolor Button:focus, …:pressed { TextElement.TextAttributes: Inverse }`) carry no `TargetType`. **Phase 2 (4C) landed:** a `prefix:`-qualified dotted owner (`my:Owner.Member`) now resolves too — its namespace is captured at the attribute (via `_reader.LookupNamespace`, while the xmlns scope is live) and stashed in the `Property` member's `ItemCount` slot, which end-of-object `ResolveSetter` reads back to resolve the owner in the prefix's namespace (the `CUR2111` deferral is retired; the prefix can map outside the Cursorial.UI namespaces). Rows X64a–X64e / X66b–X66d. **The sibling prefix gaps landed (#22):** one shared `ResolveQualifiedType(maybeQualified, …)` primitive binds the prefix from the live reader scope for a custom markup-extension name (`{my:Foo}`), an `{x:Type my:Foo}` argument, and a Style `TargetType="my:X"`; the loader's runtime `Selector` synthesis from `TargetType` strips the prefix before `Selector.Parse` (whose grammar reads `:` as the pseudo-class separator). Rows X24b/X64e. The in-selector-string namespace form (`Selector="t|Foo"`) is the separate XD26 mechanism.
- **XD5 — type resolution is xmlns-stack + short-name registry, ambiguity is a diagnostic.** A URI xmlns maps via `XmlnsDefinitionAttribute` to one-or-more CLR namespaces; a local name resolves to a CLR type within the mapped set (the default Cursorial map covers `Cursorial.UI`/`Cursorial.UI.Controls`/`Cursorial.UI.Data`). Two types of the same local name across mapped namespaces is `CUR2001` (ambiguous) listing the candidates; no type is `CUR2002` with a Levenshtein did-you-mean. `using:Ns` and `clr-namespace:Ns;assembly=Asm` both resolve directly (AV + WPF muscle memory). PIN (doc §4.3; proposal §3.2 step 1).
- **XD6 — deferral is the member's *static* `ValueType`, computed once.** `XamlMember.IsDeferredContent` is `ValueType == typeof(ITemplateContent)` (the derived rule, doc §4.2 / §4.1 ①). It is independent of the value's runtime type and of any attribute. A non-`ITemplateContent` member never defers even if its assigned object happens to be a template-shaped object (C-12 — `Storyboard`/`TransitionCollection` instantiate eagerly). PIN/DEV (doc §4.1 ①; ledger C-12; cuts the proposal's `[DeferredContent]`).
- **XD7 — markup-extension results attach through the deferred-value seam, never a sentinel `SetValue`.** `StaticResource` resolves eagerly (an ordinary value reaches `SetValue`). `DynamicResource` on a direct property calls `ResourceExtensions.SetResourceReference(element, prop, key)` (a `BindingPriority.LocalValue` producer); on a `Setter.Value` it constructs a `ResourceReference(key)` carrier. `Binding`/`TemplateBinding` route through `BindingOperations.Apply`/the `TemplateBinding` expression. A `ResourceReference` is **never** passed through `UIObject.SetValue` as a value. PIN (doc §4.4; ledger C-1/C-5; §4.11 rejected).
- **XD7a — a `{StaticResource}`/`{DynamicResource}` KEY may itself be a markup extension.** The common form is a literal string key (`{StaticResource AccentBrush}` — X44/X57, unchanged: the `*Resource` `ExtensionRecord` has `PayloadIsParsedExtension == false` and `Payload`→`Strings`). When the key is itself an extension (`{DynamicResource {x:Static ThemeKeys.SurfaceBrush}}` — the WPF `{StaticResource {x:Static SystemColors.…Key}}` idiom), the frontend cannot resolve it (no static resolver in netstandard2.0), so it records `PayloadIsParsedExtension == true` with the **inner** key node in `ParsedExtensions`; the net10.0 loader resolves the key at instantiate via the shared `ResolveNestedExtension` (`{x:Static}`/`{StaticResource}`/`{x:Null}` — reusing the X121/X122 producers, three-identical-producers). Both readers (direct-property attach **and** the `Setter.Value` `ResourceReference` carrier) route through one `ResolveResourceKey`; the resource system is object-keyed, so the resolved value is used as the key directly. A null-resolving key is `CUR2103`-class with position (never a silent empty-string key, never a bare `ArgumentNullException`). `ThemeKeys` lives in `Cursorial.UI.Themes`, added to the default xmlns map so `{x:Static ThemeKeys.X}` resolves unprefixed (the colliding `Cursorial.UI.Themes` glyph carrier was renamed `GlyphSetCarrier` to keep the simple name `GlyphSet` unambiguous against `Drawing.Media.GlyphSet`). **Out of scope:** a resource-dictionary entry's *own* `x:Key="{x:Static …}"` (a different code path, `TryGetKey` reading `Strings` — XD10) is unaffected. DEV (matches WPF; X44/X57 preserved verbatim; rows X44a/X57a/X113a/X116a/X117a/X114a).
- **XD8 — the loader is a value source at `LocalValue`, never a precedence authority.** A document-level member set carries `BindingPriority.LocalValue`; a template `Build` stamps template-local provenance, which Fork A's store integrates so a document-local value overrides a template-shipped one. The loader retracts nothing — restoration is store-owned (invariant 4). Setter-value ordering inside a `Style` is Fork B's `StyleSortKey`, untouched here. PIN (doc §4.4; invariant 4).
- **XD9 — `StaticResource` is eager + forward-reference-free; `DynamicResource` is late.** `StaticResource` walks the lexical `IResourceScope` stack innermost-first then `XamlLoadContext.AmbientResources` (default `ResourceScopes.ForApplication()`), at instantiation time; a forward reference within a dictionary (a key defined later) is `CUR2103` with **both** positions. `DynamicResource` never resolves at load — it only constructs the reference; resolution + re-resolution is S7's. System.Xaml pins the ambient-walk order. System.Xaml / PIN (doc §4.4; C-4).
- **XD10 — `x:Key` converts through the collection's `DictionaryKeyType`.** A plain `ResourceDictionary` keeps the literal string key (`DictionaryKeyType == typeof(object)`/`string`). A `ThemeDictionaryCollection` item's `x:Key` runs through `ThemeVariantKey.Parse` (`DictionaryKeyType == typeof(ThemeVariantKey)`), so `x:Key="Dark+Ansi16"` → `ThemeVariantKey(Dark, Ansi16)`; an unparseable key is `CUR2401`-class with position. An implicit key (`Style.TargetType`-equivalent selector / `DataTemplate.DataType` → `DataTemplateKey`) is read from the attribute without instantiation. PIN (doc §4.3; ledger C-8; `ThemeVariantKey.Parse`/`DataTemplateKey` confirmed).
- **XD11 — access-key folding has one data model and a type-vs-flag fork.** Folding produces `AccessText.Parse(text)` exactly (`"_File"` → `AccessText("File",'F',0)`; `"__"` → literal `_`; non-letter/digit mnemonic → literal underscore, no key — never throws). It engages for an `AccessText`-typed member by type, and for an object-typed member **iff** the resolved per-type metadata of the assigned-to instance's runtime type carries `ParsesAccessKeyLiterals` (exactly `ButtonBase.Content`, `Label.Content`; `MenuItem.Header`/`TabItem.Header` pinned-deferred). `TextBlock.Text` never folds. The loader fold === `AccessText.Parse` === the runtime `ContentControl.GetAccessText()` (three identical producers). PIN/DEV (doc §4.7; ledger C-18/C-19; `AccessText.Parse`/`ParsesAccessKeyLiterals` confirmed).
  - **Object-typed-slot fold timing (amended 2026-06-13, P6 review correctness P2-1).** For an `AccessText`-typed member the loader folds **at load** (in `Assign`, by type). For an **object-typed** flagged slot (`ButtonBase.Content`, `Label.Content`) the loader **keeps the raw string** — the fold is performed on demand by the runtime `ContentControl.GetAccessText()` (the third, genuine producer), **not** materialized into the `Content` value at load. This is deliberate: folding at load would store an `AccessText` instance that `GetAccessText()` would then have to special-case (re-deriving / unwrapping), creating exactly the drift this rule exists to prevent. The three-identical-producers invariant is preserved by fold-**equivalence** (`AccessText.Parse((string)Content) == GetAccessText() == the generator fold`), which is what X165/X167/X168 assert (they compare the parse of the stored string to the expected `AccessText`, not `Content is AccessText`). The X4 generator must match this timing — emit the raw string for object-typed slots, fold only `AccessText`-typed slots.
- **XD12 — integer-cell geometry; a fractional component is `CUR2401`.** `Thickness`/`Margins`/`GridLength`(`Cell`)/`Rect`/`Size` parse as **integer cells**; a fractional component (`"0.5"`) is `CUR2401` with a "cells are atomic" message + position. `Margins` components may be **negative** (`"0,-1,0,0"` is legal — P2.6/LD19). `Rect`/`Size` validate ≥ 0 at parse (the ushort `Rect` ctor throws on negatives → `CUR2401`). `GridLength` accepts `Auto`/`*`/`2*`/`12`. PIN/DEV (doc §4.8.1; LD19; `GridLength`/`Margins` confirmed).
- **XD13 — the color/brush/pen mini-language is terminal-first.** `Color` accepts `#RGB`/`#RRGGBB`(+`AA`), named **ANSI palette** colors (`"Red"` → `Colors.*` palette entries, *not* web RGB), `"Palette(123)"`, `"Default"`, `"Transparent"`. `IBrush` reuses `BrushMarkup`'s grammar (`"linear:#f92672,#66d9ef"`, `"radial:…"`, `"conic:…"`); a plain color yields the cached `Brushes.*` singleton when one exists (allocation discipline). `Pen` text → `Pens` presets (`"Heavy"`, `"Double Rounded"`, `"Dashed #888"`). No font converters — `TextAttributes` flags (`"Bold,Italic"`) instead. DEV (doc §4.8.2–4; `BrushMarkup`/`StyleSetterConverter` color+brush ladder confirmed; extended for XAML).
- **XD14 — events bind to the root instance only; events inside deferred content are `CUR2301`.** A `Click="OnRun"` attribute binds `OnRun` on the `XamlLoadContext.RootInstance`'s type via the metadata provider (`Delegate.CreateDelegate`). An event attribute on an element inside a deferred (template) slice is `CUR2301` at parse with position (templates use commands/`TemplateBinding`). PIN/DEV (doc §4.3; ledger §4.13; proposal §3.3).
- **XD15 — the namescope attachment points are S2-owned.** Document roots: `NameScope.SetNameScope(root, scope)` (a `NameScopeDictionary`); template `x:Name` registers **only** in the per-`Build` `TemplateBuildContext.NameScope`, and the template-scope carrier is the **templated parent** (`TemplateNameScopeProperty`, set by `ApplyTemplate` — the template root is NOT the carrier). `FindName(name)` === `NameScope.FindEnclosing(this)?.Find(name)`. A document content child of a templated control resolves document names, never template part names (the guarded walk). `x:Name` inside a resource dictionary (no namescope) is `CUR2304`. PIN (doc §4.5; ledger C-16; `NameScope`/`FindEnclosing` confirmed).
- **XD16 — runtime loading uses reflection only inside `ReflectionXamlMetadata`, honestly annotated.** Activation / CLR setters / events / `x:Static` reflection lives only in the default metadata provider, cached per type, and the provider is `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`. `UIProperty` members never reflect (the registry is the lookup). Two providers run the whole suite (the dual-provider drift gate, §14): `ReflectionXamlMetadata` and a hand-built provider, asserting identical results. The X5 generated provider is the supported trimmed mode (deferred). PIN (doc §4.10; proposal §3.8). **P1B amendment — `x:Static` under the generated provider:** the loader's fold-finalize no longer hard-casts to `ReflectionXamlMetadata` for `x:Static`; it consumes the optional `IXamlStaticResolver` seam (a separate interface — netstandard2.0 has no default interface methods). `ReflectionXamlMetadata` implements it reflectively; `MetadataProviderEmitter` bakes a `TryResolveStatic` switch over the document set's `{x:Static}` references (`global::FullType.Member`, default UI xmlns — the prefix-aware widening is P1C), collected by the shared `ClosedTypeSet.CollectStatics`. So `{x:Static}` works under the generated/AOT provider, not just reflection (one of the two static-`ReflectionXamlMetadata` references in the loader is also dropped). Dual-run test: `DualRunDriftTests.GeneratedProvider_ResolvesXStatic_AsReflection`. **P1-REVIEW drift hardening:** `ResolveStaticExpr` resolves only members declared DIRECTLY on the type (no base-walk) — matching the reflection side's `GetField/GetProperty(Public|Static)` without `FlattenHierarchy`, so an inherited static (`Button.OpacityProperty`) is NOT baked (it would resolve under the generated provider but miss under reflection); and `CollectStaticPaths` matches the markup-extension grammar (stops at `,`, strips a surrounding `'…'` quote pair, resolves `\` escapes) so a quoted/escaped/comma-followed `x:Static` arg baked-vs-resolved consistently. **P1C deferral — xmlns-prefixed `{x:Static prefix:Type.Member}`:** all four resolution paths (reflection `ReflectionXamlMetadata.TryResolveStatic`, the generated provider's baked switch, full-lowering `ResolveStaticPath`, the dual-run collection) resolve the type part through the DEFAULT UI xmlns only — a prefixed `{x:Static vm:MyKeys.Foo}` is uniformly unresolved across all of them (consistent, no drift). Widening it is coordinated: the frontend folds `x:Static` to a raw-string `XamlStaticReference` (prefix kept; unlike `{x:Type}` it does not pre-resolve), and the loader's `TryResolveStatic(string)` carries no xmlns context — so prefix-awareness needs the resolved type threaded from parse through all four paths together. Deferred (low value — framework statics are default-xmlns; an app uses a code-behind field or a resource); the common default-xmlns form works everywhere (P1B).
- **XD17 — `LoadComponent` populates an existing instance via the `x:Class` ⇒ embedded-resource convention.** `XamlLoader.LoadComponent(component)` resolves the document for `component.GetType()` by the `x:Class` convention (`cursorial://<assembly>/<type-path>.xaml` over embedded resources through `IXamlResourceProvider`), then loads with `RootInstance = component` (the root `ObjectRecord` populates the instance instead of activating). `Load`/`Load<T>` activate a fresh root. The static one-arg `LoadComponent(object)` (closed during consolidation, doc §4.12) and `LoadComponent(object, Uri)` overloads exist. PIN (doc §4.2/§4.12; proposal §2.1/§2.7).
- **XD18 — a throwing deferred `Realize` resets the slot to `Deferred` and is retried.** A `ResourceDictionary.SetDeferred(key, IDeferredResourceEntry)` slot realizes once-on-success on the UI thread; a `Realize` that throws leaves the slot `Deferred` (consuming no slice state) so the next lookup retries. The lexical scope passed to `Realize` is the definition-site chain (`ResourceScopes.ForDictionary(definingDict, enclosingChain)`). PIN (doc §4.5; ledger C-2; `ResourceDictionary.Realize` reset-on-throw confirmed).
- **XD19 — whitespace follows the documented (non-East-Asian) WPF-faithful rule.** Element text is trimmed at both ends; an interior newline+indent run collapses to a single space; `xml:space="preserve"` is honored verbatim. Significant vs collapsible boundaries are pinned against System.Xaml (the Windows-only leg). The simpler model is deliberate (doc §4.13 East-Asian deferred). System.Xaml / DEV (doc §4.3; proposal §3.2 step 6).
- **XD20 — `XamlDocument` is immutable, thread-safe to parse + share, and re-walked per `Load`/`Build`.** Parse is pure and may run off-thread; a parsed document is shared across `Load`s and template `Build`s with no re-parse. Folded constants are shared boxes (immutable value-type boxes / immutable objects) — a template double-build produces **distinct element instances** but **shares the same folded constant references** and uses **separate namescopes**. Instantiation + `Build` run on the single UI thread (the loader never touches `TerminalSession`/scenes/buffers — invariants 2/6/7). PIN (doc §4.3/§4.8.7; proposal §3.1/§3.9).
- **XD26 — namespace-aware selectors, top-level-only xmlns, and exact-type `TargetType`.** Three coupled decisions (#23, style-matrix SD25):
  - **Top-level-only xmlns (`CUR2004`).** An `xmlns`/`xmlns:prefix` declaration is allowed **only on the root element**; on any other element it is `CUR2004` with position (Avalonia parity — keeps the prefix→namespace binding unambiguous). The root's declarations are captured into `XamlDocument.Namespaces` (prefix→namespace URI, the default xmlns under `""`). Rows X13b/X13c.
  - **In-selector namespace form (`Selector="t|Foo"`).** A Style's `Selector` is **not** folded at the frontend (`SelectorConverter.IsContextFree == false`) and is built at activation with `XamlSelectorTypeResolver` over `XamlDocument.Namespaces` + the metadata provider: a `prefix|Local` token (style-matrix SD25) resolves `Local` in `prefix`'s namespace to the exact CLR type; a bare token delegates to `Selector.DefaultTypeResolver`. This is the `:is(t|Base)` case for a base type outside the default xmlns. An explicit `Selector` **wins** over a co-present `TargetType` (the latter is then only the frontend Setter-resolution hint). Rows X139a/X139b.
  - **Exact-type `TargetType`.** `TargetType="Button"` / `TargetType="my:Foo"` now builds an **exact-type** selector (`Selectors.OfType(resolvedType)`) by resolving the (optionally `prefix:`-qualified) name through the document table + metadata. A `prefix:`-qualified name **must** bind a declared (root) xmlns: an unbound prefix is `CUR2003` and an unresolvable prefixed type is `CUR2002`, both positioned (parity with the `prefix|Type` selector form — never a silent strip-to-default-namespace). An **unprefixed** name resolves in the document's default xmlns and falls back to a simple-name `Selector.Parse` (errors wrapped as a positioned XAML diagnostic, not a raw `SelectorParseException`) for a name the metadata can't resolve but the default selector resolver knows. Matching semantics are unchanged (`element.GetType() == type`); only ambiguity/precision improves. Row X139. PIN/AV/DEV (doc §3.1, §4.3; `XamlSelectorTypeResolver` confirmed).

---

## 1. XML → node graph: element & property-element syntax — X1–X12 *(X0, frontend)*

`loader.Parse(xml)` → `doc`; node-graph shape via the internal probe (XD20). Pure, no host. Per doc §4.3 (stage 1) / proposal §3.1–3.2.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X1 | `<Button/>` | parse | one root `ObjectRecord`, `TypeId` → `Button` (`Cursorial.UI.Controls.Button`); `IsRoot` flag; `MemberCount == 0`; `SubtreeLength == 1`; `doc.RootType == typeof(Button)` | WPF |
| X2 | `<Button Content="Hi"/>` | parse | root + one `MemberRecord` for `Content`, `Kind == Text` (object-typed slot, context-dependent) or `Folded` per the converter; value `"Hi"`; `RootClassName == null` | WPF |
| X3 | `<Border><Button/></Border>` | parse | `Border` root, `SubtreeLength == 2`; the child `Button` is the value of `Border`'s **content property** (`Child`), depth-first contiguous after the root | WPF |
| X4 | property-element `<Button><Button.Content>Hi</Button.Content></Button>` | parse | the `<Button.Content>` element re-enters the member path: one `Content` member, value `"Hi"`; identical node shape to the attribute form (X2) modulo whitespace | WPF |
| X5 | both forms together: `<Button Content="A"><Button.Content>B</Button.Content></Button>` | parse | `CUR1101` "property `Content` set both as attribute and property-element", line/col at the property-element open | WPF |
| X6 | `<StackPanel><Button/><Border/></StackPanel>` | parse | `StackPanel`'s content property is the implicit collection `Children` (XD-content); two child object records appended to `Children`; `SubtreeLength == 3` | WPF |
| X7 | property-element for a collection: `<StackPanel><StackPanel.Children><Button/></StackPanel.Children></StackPanel>` | parse | explicit collection property-element = same `Children` fill as X6 | WPF |
| X8 | nested property-element across two owners `<Grid><Grid.RowDefinitions><RowDefinition/></Grid.RowDefinitions></Grid>` | parse | `RowDefinitions` member, `Kind == Items`, one `RowDefinition` item | WPF |
| X9 | self-closing with attributes `<Button Content="X" Width="10"/>` | parse | two member records in attribute order; `Width` folds to `⟦10⟧` (int, context-free), `Content` stays `Text` | WPF |
| X10 | the line-info contract: a 3-line document, the `Button` opening on line 2 col 3 | parse | the `Button` `ObjectRecord`'s packed `LineInfo` decodes to line 2, col 3 (1-based); every record carries a non-zero position (XD1) | PIN (XD1) |
| X11 | `<Button></Button>` (empty element body, no whitespace-only text) | parse | identical to `<Button/>`; no spurious content member | WPF |
| X12 | a deep tree (5 levels) | parse | `SubtreeLength` of each record = the count of `ObjectRecord`s in its subtree incl. self (the O(1) slice invariant — XD20); the root's `SubtreeLength == total object count` | PIN (proposal §3.1) |

---

## 2. xmlns → CLR resolution, intrinsics, directives — X13–X30 *(X0, frontend)*

xmlns-stack resolution (XD5), `xmlns:x` intrinsics, the directive set (XD4/XD15/XD10). Per doc §4.3.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X13 | default xmlns `https://cursorial.dev/ui`; `<Button/>` | resolve | `Button` resolves to `Cursorial.UI.Controls.Button` via the default map (the map covers UI/Controls/Data — ③) | PIN (doc §4.1) |
| X14 | default xmlns; `<Binding/>` as an object element | resolve | `Binding` resolves to `Cursorial.UI.Data.Binding` (Data is in the default map) | PIN (doc §4.1 ③) |
| X15 | default xmlns; `<Style/>` | resolve | `Style` → `Cursorial.UI.Style` (the UI styling object, not `Cursorial.Output.Style` — the default map excludes `Cursorial.Output`) | PIN (doc §1.3 collision) |
| X16 | `<Border/>` and `<StackPanel/>` and `<TextBlock/>` | resolve | all resolve through the default map to their `Cursorial.UI.Controls` types | PIN |
| X17 | `xmlns:local="using:DemoApp"`; `<local:MyControl/>` | resolve | `using:` maps directly to CLR namespace `DemoApp` (in the document's compile context / referenced assemblies); resolves `DemoApp.MyControl` | AV |
| X18 | `xmlns:legacy="clr-namespace:DemoApp;assembly=Demo"`; `<legacy:MyControl/>` | resolve | `clr-namespace:`+`assembly=` resolves identically to X17 (both spellings accepted) | WPF |
| X19 | `<Bogus/>` under the default xmlns | resolve | `CUR2002` "type `Bogus` not found in namespace `https://cursorial.dev/ui`", a did-you-mean suggestion (Levenshtein over known names — e.g. `Border` for `Bordr`), line/col at the element | PIN (XD5) |
| X20 | a local name registered in two namespaces both in the default map (a synthetic ambiguity) | resolve | `CUR2001` "ambiguous type `X`" listing the candidate full names; line/col at the element | PIN (XD5) |
| X21 | `x:Name="run"` on a `Button` | parse | a `Name` **directive** record (not a member); `HasName` object flag; the name string captured | WPF |
| X22 | `x:Class="DemoApp.MainWindow"` on the root | parse | `doc.RootClassName == "DemoApp.MainWindow"`; the `x:Class` directive does not become a member | WPF |
| X23 | `x:Key="AccentBrush"` on a dictionary entry | parse | a `Key` directive; `HasKey` flag; for a plain dictionary the literal string `"AccentBrush"` is the key (XD10) | WPF |
| X24 | `x:Type="Button"` as an attribute value `Foo="{x:Type Button}"` | parse | folded immediately to the `Type` constant `typeof(Button)` (a built-in extension folded at parse — `⟦typeof(Button)⟧`) | WPF |
| X25 | `x:Null` as `Foo="{x:Null}"` | parse | folded to the `null` constant `⟦null⟧` at parse | WPF |
| X26 | `x:Static="Colors.Red"` as `Foo="{x:Static Colors.Red}"` | parse | folded to the resolved field/property value (`FieldInfo`/`PropertyInfo.GetValue` once) — `⟦Colors.Red value⟧` | WPF |
| X27 | unknown intrinsic `x:Bogus` | parse | `CUR1201` "unknown x: intrinsic `Bogus`" with position (the intrinsics set is closed: Class/Name/Key/Type/Null/Static + the rejected list) | PIN (XD2) |
| X28 | `x:TypeArguments="..."` | parse | `CUR1202` "`x:TypeArguments` (generic instantiation) is unsupported in v1" with position (recorded out, XD2) | DEV (recorded out) |
| X29 | `x:Reference`, `x:Array`, `x:FieldModifier`, `x:Shared`, `x:Uid` (one `[Theory]` case each) | parse | each → its own `CUR12xx` naming the unsupported intrinsic + position; none silently ignored (XD2) | DEV (recorded out) |
| X30 | a missing xmlns prefix `<foo:Bar/>` (prefix `foo` undeclared) | parse | `CUR2003` "undeclared xmlns prefix `foo`" with position | PIN (XD5) |
| X13b | root with `xmlns`, `xmlns:x`, `xmlns:c="clr-namespace:…"` | parse | the root's declarations are captured into `XamlDocument.Namespaces` (`["c"]` → the clr-namespace URI, `[""]` → the default xmlns) | PIN (XD26) |
| X13c | an `xmlns` declared on a **non-root** element (`<StackPanel …><Button xmlns:c="…"/></StackPanel>`) | parse | `CUR2004` "xmlns declarations are only allowed on the root element" with position (top-level-only, AV parity) | AV (XD26) |

---

## 3. Whitespace, comments, PIs — X31–X40 *(X0, frontend; whitespace rows are the System.Xaml leg)*

XD19 (whitespace) + the `IgnoreComments`/`IgnoreProcessingInstructions` reader settings. The whitespace rows are oracle-pinned against real System.Xaml (Windows-only CI leg).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X31 | `<TextBlock Text="  spaced  "/>` (attribute value) | parse | attribute values are **verbatim** — no trimming/collapse inside an attribute; `Text == "  spaced  "` | System.Xaml |
| X32 | `<TextBlock>  hello  </TextBlock>` (element text) | parse | trimmed at both ends → `"hello"` (the content member) | System.Xaml |
| X33 | element text with interior newline+indent `<TextBlock>a\n   b</TextBlock>` | parse | interior newline+indent run collapses to one space → `"a b"` | System.Xaml |
| X34 | `xml:space="preserve"` on `<TextBlock xml:space="preserve">  a\n  b  </TextBlock>` | parse | content preserved verbatim → `"  a\n  b  "` (no trim, no collapse) | System.Xaml |
| X35 | whitespace-only element body `<StackPanel>\n   \n</StackPanel>` | parse | the whitespace-only text is dropped (not a content item); `Children` empty | System.Xaml |
| X36 | mixed `<StackPanel>\n  <Button/>\n</StackPanel>` | parse | the inter-element whitespace is insignificant; `Children` has exactly one `Button` | System.Xaml |
| X37 | a comment `<!-- x -->` between elements | parse | comments are ignored (`IgnoreComments`); no node, no whitespace artifact | WPF |
| X38 | a processing instruction `<?pi data?>` | parse | PIs ignored (`IgnoreProcessingInstructions`); no node | WPF |
| X39 | a `<!DOCTYPE …>` / DTD | parse | `CUR1001` "DTDs are prohibited (`DtdProcessing = Prohibit`)" with position — XML external entities are never processed (security, XD2) | PIN (XD2) |
| X40 | an external-entity reference `&ext;` | parse | rejected by the prohibited-DTD setting (no entity expansion); `CUR1001`-class with position | PIN (XD2) |

---

## 4. Markup-extension grammar — X41–X58 *(X0, frontend; the fuzz + escape-oracle table)*

The hand-rolled recursive-descent grammar (doc §4.3; proposal §3.2 step 4). Positional-argument convention pinned at X0. The `{}` literal escape is the System.Xaml-pinned leg.

```
Extension := '{' Name ( WS Positional (',' Positional)* )? ( ',' Named )* '}'
Named     := Name '=' Value
Value     := Extension | "'" QuotedChars "'" | BareChars      // '\' escapes within both
Literal   := '{}' rest-of-text                                 // brace-escape prefix
```

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X41 | `Foo="{Binding Name}"` | parse | a `Binding` extension; positional arg `Name` maps to the primary member (`Path`); `⟨ext⟩` with `Path == "Name"` | WPF |
| X42 | `Foo="{Binding Path=Name, Mode=TwoWay}"` | parse | named args: `Path == "Name"`, `Mode == TwoWay`; order-independent | WPF |
| X43 | `Foo="{Binding Name, Mode=TwoWay}"` | parse | mixed positional + named: positional `Name` → `Path`, then named `Mode` | WPF |
| X44 | `Foo="{StaticResource AccentBrush}"` | parse | positional `AccentBrush` → the resource key (`ResourceKey`) of a `StaticResource` `ExtensionRecord`. **X5.4 lowering:** recorded and resolved at the END of `InitializeComponent` (once the tree is built+attached and every `<X.Resources>` populated) via `el.SetValue(Owner.FooProperty, global::Cursorial.UI.ResourceExtensions.FindResource(el, "AccentBrush"))` (a throw-on-miss live walk — lexical == live ancestor chain for a self-contained inline document, matching the loader's eager resolution). A non-`UIElement` target, a `{StaticResource}` inside a template (needs the captured lexical scope), or a markup-extension key stays `// TODO X5` (CURG3001). Test: `ResourceLoweringTests.Lowered_StaticResource_ResolvesFromElementResources`. | WPF |
| X45 | nested extension `Foo="{Binding Status, Converter={StaticResource StatusToBrush}}"` | parse | the `Converter` named arg is itself an `ExtensionRecord` (`StaticResource`), nested in the `Binding` | WPF |
| X44a | `Foo="{StaticResource {x:Static ThemeKeys.SurfaceBrush}}"` | parse | a `StaticResource` `ExtensionRecord` with `PayloadIsParsedExtension == true`; `Payload` indexes `ParsedExtensions` holding the **inner** `{x:Static}` key node (`Name == "x:Static"`, `PositionalArguments[0].Text == "ThemeKeys.SurfaceBrush"`), NOT an empty `Strings` key. Generalizes to any nested key extension (`{StaticResource …}` too) | DEV (XD7a; WPF `{StaticResource {x:Static …Key}}`) |
| X57a | `Foo="{DynamicResource {x:Static ThemeKeys.SurfaceBrush}}"` | parse | the `DynamicResource` analog — `PayloadIsParsedExtension == true`, the inner `{x:Static}` node captured in `ParsedExtensions` | DEV (XD7a) |
| X46 | the literal escape `Foo="{}{not an extension}"` | parse | the leading `{}` escapes; the literal value is `"{not an extension}"` (a `Text`/`Folded` member, NOT an extension) | System.Xaml |
| X47 | quoted arg with a comma `Foo="{Binding Path='A,B'}"` | parse | single-quotes protect the comma; `Path == "A,B"` (one positional/named value, not two) | System.Xaml |
| X48 | escaped brace inside a quoted value `Foo="{Binding Path='a\}b'}"` | parse | the `\}` escapes the brace within quotes; `Path == "a}b"` | System.Xaml |
| X49 | escaped backslash `Foo="{Binding Path='a\\b'}"` | parse | `\\` → one backslash; `Path == "a\b"` | System.Xaml |
| X50 | bare value with trailing spaces `Foo="{Binding Path=Name }"` | parse | trailing whitespace before `}` trimmed from a bare value; `Path == "Name"` | System.Xaml |
| X51 | empty extension body `Foo="{Binding}"` | parse | a `Binding` with empty path (the source itself); valid | WPF |
| X52 | unterminated extension `Foo="{Binding Name"` (no `}`) | parse | `CUR1301` "unterminated markup extension" with position at the `{` | PIN |
| X53 | unknown extension name `Foo="{Bogus X}"` | parse | resolved as a **custom** extension type `Bogus` (X2 path) — at parse the type resolves through xmlns; an unresolvable type is `CUR2002` (did-you-mean) with position. **Amended 2026-07-13:** extension position probes the `Extension`-SUFFIXED form FIRST (WPF parity) with the bare name as fallback — a same-named non-extension sister (`Icon` beside `IconExtension`) can never shadow the extension, while suffix-less extensions (`Binding`, `StaticResource`) resolve through the fallback. The closed-set sweep mirrors the same order (`CollectMarkupExtensionNames` + suffix-first resolution), so the generated provider bakes the extension, never the sister. The parser also STAMPS the bound xmlns on each extension node (`MarkupExtensionNode.ResolvedNamespace`, nested arguments included) — the loader re-resolves extension types at build time, when the reader scope is gone, and previously probed only the default UI namespace (a prefixed project extension `{v:Foo …}` parsed clean but failed CUR2002 at load) | PIN (XD5) |
| X54 | malformed named arg `Foo="{Binding =Name}"` (empty name before `=`) | parse | `CUR1302` "malformed markup-extension argument" with position at `=` | PIN |
| X55 | `{x:Null}`/`{x:Type T}`/`{x:Static M}` (one `[Theory]` case each) | parse | all three fold to constants at parse (X24–X26); they never produce live `ExtensionRecord`s | WPF |
| X56 | `{TemplateBinding Background}` outside any template body | parse | `CUR2202` "`{TemplateBinding}` is only legal inside a template body" with position (the parse-time restriction — doc §4.4 / ledger) | PIN/DEV |
| X57 | `{DynamicResource AccentBrush}` | parse | a `DynamicResource` `ExtensionRecord` with key `AccentBrush`; not folded, not resolved at parse | WPF |
| X58 | a fuzzed corpus of malformed extension strings (a `[Theory]` over a generated set) | parse | every malformed input throws a `CUR13xx` with a position; **no** parser crash / hang / out-of-range (the fuzz gate, doc §4.10) | PIN (fuzz) |

---

## 5. Constant folding & Setter folding — X59–X66 *(X0, frontend)*

XD3 (`IsContextFree` folding) + the Setter special fold (doc §4.3, proposal §3.2). Folded constants are shared (XD20).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X59 | `Width="10"` (context-free int) | parse | `Kind == Folded`, `Constants[i]` is the boxed `int 10`; one box per document | PIN (XD3) |
| X60 | `Margin="1,2"` (context-free Thickness/Margins) | parse | folded once to a boxed `Margins(1,2,1,2)`; a second `Load`/template `Build` reuses the same boxed reference (XD20) | PIN (XD3/XD20) |
| X61 | `Content="Hi"` on a `Button` (object-typed, context-dependent fold-or-not) | parse | stays `Text` (object slot — the access-key fold / `AccessText` decision is target-runtime-type dependent, deferred to stage 2, XD11) | PIN (XD3/XD11) |
| X62 | `Visibility="Collapsed"` (enum, context-free) | parse | folded to `⟦Visibility.Collapsed⟧` | WPF |
| X63 | `FoldConstants = false` on options; `Width="10"` | parse | `Kind == Text`; the convert is deferred to stage 2 (the profiling knob); the loaded value is still `10` (XD3) | PIN (XD3) |
| X64 | a `<Style TargetType="Button"><Setter Property="Background" Value="#66d9ef"/></Style>` | parse (end-of-object) | the Setter `Property` resolves `Background` against the lexically known `TargetType` (`Button`) at end-of-object (attribute-order independent); `Value` folds through `Background`'s converter to a brush constant | WPF (DEV: parse-time fold) |
| X65 | a `<Setter Property="X" Value="…"/>` with no resolvable owner (no `TargetType`/selector context) | parse | `CUR2110` "Setter has no resolvable target type" with position (WPF defers this to runtime; we reject at parse — XD2) | DEV (doc §4.3) |
| X66 | a Setter with a property unknown on the target type `<Style TargetType="Button"><Setter Property="Nope" .../>` | parse | `CUR2102`-class "no member `Nope` on `Button`" with position + the member list | PIN (XD4) |
| X64a | `<Style TargetType="Button"><Setter Property="Grid.Row" Value="1"/></Style>` | parse (end-of-object) | a **dotted** Property resolves the **attached** `Grid.RowProperty` via owner `Grid` (default UI xmlns), **not** the lexical `TargetType`; `Value` folds through `Grid.Row`'s converter to `1` (attached-setter Phase 1) | System.Xaml (`GetAttachableMember`) |
| X64b | `<Setter Property="Grid.Column" Value="2"/>` (any `TargetType`) | parse | resolves attached `Grid.ColumnProperty`; `Value` folds through the owner's converter | Cursorial behavior |
| X64c | `<Style TargetType="Button"><Setter Property="Control.Foreground" Value="#fff"/></Style>` | parse + `Load` | owner-qualified / added-owner property resolves `Control.ForegroundProperty` via owner `Control`; `TargetType` not consulted for a dotted name; applies through the store (App layer) | System.Xaml |
| X64d | unqualified baseline `<Style TargetType="Button"><Setter Property="Height" .../>` | parse | still resolves against `TargetType` (the only case `TargetType` is the owner) — the dot-gate regression anchor | existing X64 |
| X66b | `<Style TargetType="Button"><Setter Property="Grid.Nope" .../>` | parse | owner `Grid` resolves, member `Nope` does not → `CUR2102` naming the **owner** (`Grid`), not the `TargetType` | PIN (XD4) |
| X66c | `<Setter Property="Bogus.Row" .../>` | parse | owner `Bogus` unresolvable in the default namespace → `CUR2002` (type-not-found) naming the owner type | PIN (XD4/XD5) |
| X66d | `<Setter Property="my:Owner.Member" .../>` (a `prefix:`-qualified owner) | parse | **prefixed owner is a v1 deferral** → `CUR2111` (`PrefixedSetterOwnerUnsupported`), NOT a misleading `CUR2102`; the reader's xmlns scope is gone at end-of-object (attached-setter Phase 2 captures it) | PIN |

---

## 6. Type activation & content/collection/attached syntax — X67–X82 *(X1, loader)*

`loader.Load(doc)` → live tree. `XamlType.Activate` (cached thunk); content property (XD-content); implicit collection fill; attached-property syntax. UI-thread (host). Per doc §4.3 stage 2 / §4.2.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X67 | `<Button/>` | `Load` | returns a fresh `Button`; `Load<Button>` returns it typed; `Load<TextBlock>` on a `Button` doc throws `CUR3xxx`/`InvalidCastException`-class naming the mismatch | WPF |
| X68 | `<Button Content="Hi"/>` | `Load` | `button.Content == "Hi"` set through `SetValue(ContentProperty, "Hi", Local)` (a `UIProperty` match, XD4); `GetValueSource(ContentProperty)` is `LocalValue` | PIN (XD4/XD8) |
| X69 | the content property: `<Border><Button/></Border>` | `Load` | `border.Child` is the `Button` (the content property fills without an explicit member element); `Border`'s `ContentProperty` metadata is `Child` | WPF |
| X70 | the `ContentProperty` declaration mechanism | inspect | the loader reads the content property from `XamlType.ContentProperty`, sourced from a `[ContentProperty("…")]` attribute on the CLR type (the metadata provider surfaces it). `ContentControl.Content`/`Border.Child`/`Panel.Children` are the v1 content properties. **DECISION: `[ContentProperty]` attribute is added to the relevant `Cursorial.UI` types at X1** (additive — these types ship; the attribute is metadata-only). The row asserts the attribute resolves; a type without it has `ContentProperty == null` and rejects implicit content with `CUR2104` | PIN (doc §4.2; ledger — additive `[ContentProperty]`) |
| X71 | implicit `Children` fill: `<StackPanel><Button/><Border/></StackPanel>` | `Load` | `panel.Children` has the two elements in document order; the loader detects the collection via `XamlType.IsCollection` + `AddItem` | WPF |
| X72 | `Styles` collection fill `<StackPanel.Styles><Style .../></StackPanel.Styles>` | `Load` | each `Style` is added to the element's `Styles` collection via `AddItem` | WPF |
| X73 | `ResourceDictionary` fill: keyed entries `<ResourceDictionary><SolidColorBrush x:Key="A" .../></ResourceDictionary>` | `Load` | the brush is added via `AddDictionaryItem(dict, "A", brush)` (the dictionary item path, keyed by `x:Key`). **X5.4 lowering:** a ResourceDictionary-typed member (`<X.Resources>`) populates the get-object dictionary per plain-string `x:Key`'d child — `el.Resources.Add("A", child)` (eager realization, vs the loader's lazy `SetDeferred`; same object). A non-literal/`{x:Type}`/`{x:Static}`/escaped/missing key stays `// TODO X5`. | WPF |
| X74 | Grid definitions fill `<Grid.RowDefinitions><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>` | `Load` | two `RowDefinition`s added to `Grid.RowDefinitions`; heights converted (`*` → `GridLength.Star(1)`, `Auto` → `GridLength.Auto`) | WPF |
| X75 | attached property `<Button Grid.Row="1"/>` inside a `Grid` | `Load` | `Grid.GetRow(button) == 1`; the attached member resolves through the Fork A attached registry against owner `Grid` (XD4); set via the attached `SetValue` | WPF |
| X76 | attached property where the owner type is ambiguous / unresolvable `<Button Bogus.Row="1"/>` | `Load` | `CUR2002`/`CUR2102`-class naming `Bogus` (type) or `Row` (member) with position | PIN (XD4/XD5) |
| X77 | `ISupportInitialize` bracketing: a control implementing it | `Load` | `BeginInit` before any member set, `EndInit` after all member sets + content; no per-property invalidation storm into layout during the bracket (Fork A `DeferNotifications`) | WPF |
| X78 | activation of a type with no public parameterless ctor | `Load` | `CUR3001` "type `X` has no parameterless constructor" with the element's position (XD1 — instantiation errors carry position) | PIN (XD1) |
| X79 | a CLR-only member (no registered `UIProperty`) e.g. a plain CLR property on a custom type | `Load` | set through the cached CLR setter delegate (`XamlMember.SetClr`), not `SetValue` (XD4 rule 2) | PIN (XD4) |
| X80 | a read-only collection content member (get-object, no setter) `<Grid><Grid.ColumnDefinitions>…` | `Load` | the loader gets the existing collection via `XamlMember.Get` and `Add`s items (never tries to set the collection); a missing getter + missing setter is `CUR2105` | WPF |
| X81 | folded-constant sharing across two `Load`s of the same `doc` (`Margin="1,2"`) | `Load` twice | the two `Button`s have **equal** `Margin` values; the **boxed constant reference** is shared (an internal probe — XD20); distinct element instances | PIN (XD20) |
| X82 | a `Width="0.5"` (fractional cell) reaching stage 2 with `FoldConstants=false` (so the convert runs at instantiate) | `Load` | `CUR2401` "cells are atomic; `0.5` is not an integer cell count" with position (XD12 — works at parse with folding, at instantiate without) | DEV (XD12) |

---

## 7. The converter ladder (terminal converters) — X83–X100 *(X1, loader)*

`XamlConverters.For(Type)` + the registered set. Reuses/extends `StyleSetterConverter`'s ladder (geometry/color/enum/IConvertible) and adds the XAML-specific converters (doc §4.6/§4.8; C-7/C-11/C-13). Unit-level (the converter is `ConvertFromString(text, ctx)`); host only where the value needs services.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X83 | `Margin="2"` | convert | `Margins(2,2,2,2)`; `"2,1"` → `(2,1,2,1)`; `"1,0,1,0"` → `(1,0,1,0)`; one `[Theory]` over the arities | WPF |
| X84 | `Margin="0,-1,0,0"` (negative component) | convert | `Margins(0,-1,0,0)` — negative components are legal (P2.6/LD19, XD12); no exception | DEV (LD19) |
| X85 | `Width="0.5"` (fractional) | convert | `CUR2401` "cells are atomic" with position (XD12) | DEV |
| X86 | `Color="#66d9ef"`, `"#fff"`, `"#80ff0000"` (+AA) (one `[Theory]`) | convert | hex parse via `Color.FromHex`; the `AA` form sets alpha; matches the `StyleSetterConverter` color rule | PIN (XD13) |
| X87 | `Color="Red"`, `"Default"`, `"Transparent"`, `"Palette(123)"` (one `[Theory]`) | convert | named → the **ANSI palette** `Colors.*` entry (not web RGB); `Default`/`Transparent`/`Palette(n)` map to the corresponding `Color` kinds | DEV (XD13) |
| X88 | `Background="#66d9ef"` (an `IBrush`-typed slot) | convert | a `SolidColorBrush(#66d9ef)`; a plain named color yields the cached `Brushes.*` singleton where one exists (allocation discipline) | DEV (XD13) |
| X89 | `Background="linear:#f92672,#66d9ef"` (the `BrushMarkup` grammar) | convert | a `LinearGradientBrush` over the two stops via `BrushMarkup`; `"radial:…"`/`"conic:…"` likewise (one `[Theory]`) | DEV (XD13; `BrushMarkup` reuse) |
| X89a | `<LinearGradientBrush StartPoint="0,0" EndPoint="1,0"><GradientStop Offset="0" Color="#f00"/>…</LinearGradientBrush>` (element form) | load | the gradient brushes are element-declarable: `GradientStop` content is the brushes' ContentProperty (`Stops`), bounds-relative points convert from `"x,y"` (finite), and sampling is order-agnostic so element stops need no sort. The `linear:` text grammar (X89) remains the compact alternative. `Cursorial.UI.Themes.Xaml`-adjacent; tests in `XamlGradientBrushTests`. | DEV (#5) |
| X90 | `Visibility="Collapsed"` (enum by name, ordinal) | convert | `Visibility.Collapsed`; an unknown member is `CUR2401`-class with position; an integral value also accepted (matches `StyleSetterConverter` enum rule) | WPF |
| X91 | `IsEnabled="True"`/`"true"`/`"False"` (bool) | convert | invariant bool parse; matches `bool.Parse` semantics | WPF |
| X92 | `Width="10"`/`Opacity="0.5"` (int / double via `IConvertible`) | convert | int through the integer-cell rule; double through invariant-culture `Convert.ChangeType` (the `IConvertible` rung of the ladder) | WPF |
| X93 | a `GridLength`-typed slot `Height="*"`/`"2*"`/`"Auto"`/`"12"` (one `[Theory]`) | convert | `GridLength.Star(1)` / `Star(2)` / `Auto` / `Cell(12)`; a fractional cell (`"1.5"`) is `CUR2401` | PIN (XD12; `GridLength` confirmed) |
| X94 | a `Pen`-typed slot `BorderStyle="Heavy"`/`"Double Rounded"`/`"Dashed #888"` (one `[Theory]`) | convert | `Pens` presets via the `Pen` text converter; weight is a glyph family, never thickness (XD13) | DEV (doc §4.8.3) |
| X95 | a `TextAttributes`-typed slot `Attributes="Bold,Italic"` | convert | the flags converter → `TextAttributes.Bold | TextAttributes.Italic`; no font converters exist (XD13) | DEV (doc §4.8.4) |
| X96 | a `KeyGesture`-typed slot (`KeyBinding.Gesture="Ctrl+S"`/`"F5"`) | convert | `KeyGesture.Parse("Ctrl+S")` is the registered converter (C-13); identity matches S3's parse | PIN (C-13) |
| X97 | an animation `UIProperty`-typed slot `TargetProperty="Control.Background"` | convert | resolves through the registry + `FindOwnersByShortName`; an ambiguous short name lists the candidates (C-11; the binding-matrix B14 analog) | **S5-deferred** (see note below) |
| X98 | an `Easing`-typed slot `Easing="CubicOut"`/`"cubic-bezier(0.25,0.1,0.25,1)"` (one `[Theory]`) | convert | `Easings.TryParse` — catalog names **and** the `cubic-bezier(…)` form (C-11) | **S5-deferred** (see note below) |
| X99 | an `Optional<T>`-typed slot: empty string and a value (one `[Theory]`) | convert | empty string ⇒ `Optional<T>.Unset`; a value unwraps to the inner type's converter and re-wraps (the pinned dispatch rule, doc §4.6) | **S5-deferred** (see note below) |

> **X97–X99 deferral (amended 2026-06-13, P6 review correctness P1-1).** These three converter rows are **pinned-but-deferred to S5** — the same treatment as the deferred `MenuItem`/`TabItem` access-key rows (note ⑤). Their target types do not exist at P6: there is no animation `TargetProperty`-typed XAML slot, `Easings` exposes named-property catalog entries but **no `TryParse`** and there is no `cubic-bezier(…)` parser, and neither `RepeatBehavior` nor `Optional<T>` exists as a type. The `XamlConverters.Build` ladder therefore carries **no** case for these (and none for the C-7 `RelativePoint`/`ThemeVariantKey`/`Pen`-text-as-`RelativePoint` animation converters), and `Section07_Converters` has **no** `X097_/X098_/X099_` tests. When S5 lands the animation/easing types and their slots, these converters are added to the ladder and the tests added then; the rows stay absent (not red) until then, consistent with §16's "later-stage rows may be absent (not red) before their stage opens."
| X100 | `XamlConverters.For(typeof(Margins))` then `XamlConverters.Register(t, custom)` then `For(t)` | call | `For` returns the registered converter; `Register` overrides; the registry is a **public, load-independent** runtime seam consumed by S2 (target-type fallback) and S7 (DynamicResource conversion) (C-15) | PIN (C-15) |

---

## 8. `x:Name`, the document namescope, `FindName` — X101–X108 *(X1, loader)*

XD15. `x:Name` registers in the document namescope (`NameScope.SetNameScope(root, scope)`); `FindName` === `NameScope.FindEnclosing(this)?.Find(name)`. No generated fields (that is X4).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X101 | `<Window x:Name="root"><Button x:Name="run"/></Window>` | `Load` then `root.FindName("run")` | the document namescope is attached to the root via `NameScope.SetNameScope`; `FindName("run")` returns the `Button`; `FindName("nope")` returns `null` | PIN (XD15) |
| X102 | `NameScopeExtensions.RequireControl<Button>("run")` on the loaded root | call | returns the typed `Button`; `RequireControl<Button>("nope")` throws with the available-name list (proposal §2.5) | PIN |
| X103 | duplicate `x:Name="dup"` on two elements in one document | `Load` | a duplicate-name error: `CUR3002`/`CUR2301`-class "name `dup` already registered" with **both** positions (proposal §2.5) | WPF |
| X104 | `x:Name` on an element inside a `ResourceDictionary` (no namescope) | `Load`/parse | `CUR2304` "`x:Name` is not allowed inside a resource dictionary (no namescope)" with position | PIN (proposal §3.6) |
| X105 | `LoadComponent`: a root instance + the `x:Class` convention | `LoadComponent(inst)` | the root `ObjectRecord` populates `inst` (not a fresh activation); `x:Name`d parts register in the document scope on `inst`; `inst.FindName("run")` resolves | PIN (XD17) |
| X106 | `LoadComponent(inst, sourceUri)` (explicit URI overload) | call | loads the document at `sourceUri` into `inst` (bypasses the `x:Class` convention) | PIN (XD17) |
| X107 | the static `XamlLoader.LoadComponent(object)` (Shared + convention) and `LoadComponent(object, Uri)` | call | both exist and route through `XamlLoader.Shared`; the one-arg form uses the `x:Class` convention (closed during consolidation, doc §4.12) | PIN (doc §4.12) |
| X108 | `FindName` template-awareness scaffolding: a document content child of a (manually templated) control | call | `FindName` resolves **document** names, never template part names (the guarded walk, XD15) — the full template path is §13 | PIN (XD15) |

---

## 9. Value-vs-markup-extension disambiguation — X109–X112 *(X1, loader)*

The `{}` escape (X46) at the instantiation boundary; an attribute starting with `{` is an extension unless escaped.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X109 | `Content="{Binding Name}"` | `Load` | recognized as a `{Binding}` extension (a live binding attaches — §10), NOT the literal string `"{Binding Name}"` | WPF |
| X110 | `Content="{}{Binding Name}"` | `Load` | the `{}` escape: the literal string `"{Binding Name}"` is set as `Content` (no binding) | System.Xaml |
| X111 | `Content="plain"` | `Load` | a plain value (no `{`): the literal `"plain"` (no extension parse attempted) | WPF |
| X112 | `Content=" {Binding Name}"` (leading space before `{`) | `Load` | a leading non-`{` char (space) ⇒ NOT an extension; the literal string `" {Binding Name}"` (the WPF rule: only a value *starting* with `{` is an extension) | System.Xaml |

---

## 10. Markup extensions end-to-end (the live attach) — X113–X128 *(X2, loader)*

XD7 — results attach via the deferred-value seam, never sentinels. `StaticResource` eager; `DynamicResource` late; `Binding`/`TemplateBinding` live. Host (resource chains / binding). Per doc §4.4.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X113 | `<Window.Resources><SolidColorBrush x:Key="A" Color="Red"/></Window.Resources>` + `<Button Background="{StaticResource A}"/>` | `Load` | `StaticResource` resolves **eagerly** against the lexical scope; `button.Background` is the brush instance (an ordinary value reached `SetValue` — XD7) | WPF |
| X114 | `{StaticResource Missing}` (no such key in any scope) | `Load` | `CUR2103`-class "StaticResource `Missing` not found" rendering the searched scope chain hop-by-hop (proposal §1 example) + position | PIN (XD9) |
| X115 | a same-dictionary reference: `<Button Background="{StaticResource Second}"/>` resolving a sibling key in the same `Resources` | `Load` | **Amended at X3 (DEV).** Under the X141 deferred-dictionary optimization (all keyed entries authored as deferred slots before any realization), a same-dictionary StaticResource is **order-independent** — a sibling key (before or after) is present when another entry realizes, so the reference **resolves** rather than erroring. This is a deliberate, documented deviation from strict WPF load-order, driven by X141; a genuinely-undefined key is still `CUR2103` (X114). The original WPF "forward reference to a later key is a miss" rule is superseded by the deferred-authoring model. | DEV (X141-driven; supersedes the WPF load-order rule) |
| X116 | `<Button Background="{DynamicResource A}"/>` (a direct `StyledProperty`) | `Load` | the loader calls `ResourceExtensions.SetResourceReference(button, BackgroundProperty, "A")` — a `BindingPriority.LocalValue` producer; **no** `ResourceReference` passes through `SetValue` as a value (XD7/C-5). **X5.4 lowering:** emits the identical `global::Cursorial.UI.ResourceExtensions.SetResourceReference(el, Owner.AProperty, "A")` for a registered `StyledProperty<T>` target (the live producer resolves on attach — no eager-timing problem, works inline AND inside templates); a non-styled/attached target or a markup-extension key stays `// TODO X5` (CURG3001). Test: `ResourceLoweringTests.Lowered_DynamicResource_ResolvesLive`. | PIN (XD7) |
| X117 | `<Setter Property="Background" Value="{DynamicResource A}"/>` (a setter value) | `Load` | the loader stores a `ResourceReference("A")` carrier as the setter value (the setter-value DynamicResource form — C-5); resolution is S7's, not the loader's | PIN (C-5) |
| X116a | `<Button Background="{DynamicResource {x:Static ThemeKeys.SurfaceBrush}}"/>` (a styled property) | `Load` | the loader resolves the nested `{x:Static}` to the const `"Theme.SurfaceBrush"` at instantiate (via `ResolveResourceKey`→`ResolveNestedExtension`→`ResolveStaticMember`) then `SetResourceReference(button, BackgroundProperty, "Theme.SurfaceBrush")`; no `ResourceReference` through `SetValue` | DEV (XD7a) |
| X113a | `<Button Background="{StaticResource {x:Static ThemeKeys.AccentBrush}}"/>` | `Load` | the nested key resolves to `"Theme.AccentBrush"`, then the resource resolves **eagerly** against the lexical scope (the StaticResource analog of X116a) | DEV (XD7a) |
| X117a | `<Setter Property="Background" Value="{DynamicResource {x:Static ThemeKeys.SurfaceBrush}}"/>` | `Load` | the setter-value carrier is `ResourceReference("Theme.SurfaceBrush")` — the **resolved** const, not `"ThemeKeys.SurfaceBrush"` (object key; C-5) | DEV (XD7a; C-5) |
| X114a | nested-key failures: `{StaticResource {x:Static Bogus.Member}}` → `CUR2102` (MemberNotFound) with line+col; `{StaticResource {x:Null}}` (or any null-resolving key) → `CUR2103` (ResourceNotFound) "key … resolved to null" with position | `Load` | position-carrying diagnostic, fail-closed — **never** a silent empty-string key reaches `TryResolve`, never a bare `ArgumentNullException` | PIN (XD7a/XD9) |
| X118 | `Content="{Binding Name}"` with `Root.DataContext = vm` | `Load` then `vm.Name = "x"` | the loader builds the parsed `BindingRecord` into S2's `Binding` and applies via `BindingOperations.Apply` → a `BindingEntry<T>` at `LocalValue`; the live binding pushes `"x"` (a live attach — XD7) | PIN (doc §4.4) |
| X119 | `Content="{Binding Path=Name, Mode=TwoWay}"` | `Load` | the named args reach the `Binding` (`Path`/`Mode`); two-way write-back works (the binding engine owns the behavior — this row asserts the loader wires `Mode`) | PIN |
| X120 | `{Binding}` to a non-`UIProperty` CLR member | parse/`Load` | `CUR2210` "binding target `X` is not a bindable property" at **parse** time (doc §4.4) + position | PIN (doc §4.4) |
| X121 | `Foreground="{Binding Status, Converter={StaticResource StatusToBrush}}"` (nested extension) | `Load` | the `Converter` named arg resolves its nested `{StaticResource}` eagerly to an `IValueConverter` and sets it on the `Binding` | WPF |
| X122 | `{x:Static Colors.Red}` reaching a member at instantiate | `Load` | the folded constant (X26) is assigned directly; no extension object allocated | WPF |
| X123 | `{x:Null}` on a reference-typed member | `Load` | the member is set to `null` (the folded constant) | WPF |
| X124 | `{x:Type Button}` on a `Type`-typed member (e.g. `Style.TargetType`-shaped) | `Load` | the member receives `typeof(Button)` | WPF |
| X125 | a custom `MarkupExtension` `Foo="{my:Repeat Count=3}"` (a user type with `ProvideValue`) | `Load` | the loader activates the extension, sets its members, calls `ProvideValue(services)` where `services` exposes `IProvideValueTarget`/`IRootObjectProvider`/`IXamlLineInfo`/`IAmbientResources`/`ITemplateHost`/`INameScopeProvider`; the returned value is assigned (proposal §3.4) | PIN (doc §4.2) |
| X126 | a custom extension with positional args `{my:Add 1, 2}` | `Load` | positional args map by the ctor-arity convention (or the primary member); the result is assigned (proposal §3.4 positional convention) | PIN (doc §4.3) |
| X127 | `{TemplateBinding Background}` **inside** a template body (the valid case) | `Build` (§13) | at build the loader applies S2's `TemplateBinding` (one-way to the templated parent) — the parse-time restriction (X56) passes inside a slice; the value tracks the templated parent's `Background`. **X5.4e lowering:** inside a template factory, emits `BindingOperations.Install(part, TargetOwner.MemberProperty, new TemplateBinding(SourceOwner.SrcProperty))` — the source property resolved at codegen against the template's target type (explicit `ControlTemplate.TargetType`, else the enclosing control), via `SymbolXamlModel.FindRegisteredPropertyOwner` (base-walked); the engine finds the live templated parent at apply-time. A DataTemplate (no templated parent) or an unresolvable source stays `// TODO X5` (the runtime loader's `target.GetType()`/name fallbacks are not replicated — well-formed templates bind to the target type). Test: `TemplateLoweringTests.Lowered_TemplateBinding_TracksTemplatedParent`. | PIN (doc §4.4) |
| X128 | the `IDeferredValue.AttachTo` seam itself (unit): a parsed `Binding`/`DynamicResource` extension result | inspect | the extension's result implements the attach seam and attaches via `AttachTo`/`SetResourceReference`/`BindingOperations` — value stores **only** see values, never a sentinel (the §4.11-rejected pattern; XD7) | PIN (XD7) |

---

## 11. Resource dictionaries: merged, themed, source-loaded — X129–X140 *(X2, loader)*

`ResourceDictionary` loading; `MergedDictionaries`; theme sub-dictionaries (`ThemeDictionaries`, `x:Key` via `ThemeVariantKey.Parse`); separate-file `Source=` via `ResourceDictionaryLoader.LoadCallback` (C-9). Host. Per doc §4.5 / §11.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X129 | `<ResourceDictionary><SolidColorBrush x:Key="A" Color="Red"/><Style x:Key="ToolButton" TargetType="Button">…</Style></ResourceDictionary>` | `Load` | both entries land in the dictionary keyed by `x:Key`; `dict["A"]` is the brush, `dict["ToolButton"]` the style | WPF |
| X130 | `<ResourceDictionary.MergedDictionaries><ResourceDictionary>…</ResourceDictionary></ResourceDictionary.MergedDictionaries>` | `Load` | the inner dictionary is added to `MergedDictionaries`; lookups walk own→merged (last-to-first) per the `ResourceDictionary.TryGetResource` rule | WPF |
| X131 | `<ResourceDictionary Source="cursorial://DemoApp/Themes/Dark.xaml"/>` with a test `LoadCallback` | `Load` | setting `Source` routes through `ResourceDictionary.LoadCallback` (installed by the module initializer, C-9); the loaded entries copy in under one `BeginUpdate` (one `CatchAll` pulse — confirmed in `ResourceDictionary.Source`). **Amended 2026-07-12:** the callback binds the nested load to the loader already instantiating on the thread (`XamlLoader.Current`, thread-static save/restore) so the whole document tree resolves through ONE loader/provider — under strict trimming, the generated closed-set provider its `InitializeComponent` bound; `Shared` (the ambient default) serves only loads initiated outside any loader | PIN (C-9) |
| X132 | the module-initializer registration (C-9) | load the `Cursorial.UI.Xaml` module | `ResourceDictionary.LoadCallback` is set once at module init; tests save/restore it (process-global static); before the module loads it is `null` and `Source=` throws the documented `InvalidOperationException` | PIN (C-9) |
| X133 | `Source=` without a loader installed (the P5 state) | set `Source` | `InvalidOperationException` "no loader installed" (the existing `ResourceDictionary.Source` guard) — the module init is what removes it (C-9) | PIN |
| X134 | theme sub-dictionaries `<ResourceDictionary.ThemeDictionaries><ResourceDictionary x:Key="Dark">…</ResourceDictionary><ResourceDictionary x:Key="Light">…</ResourceDictionary></ResourceDictionary.ThemeDictionaries>` | `Load` | each `x:Key` runs through `ThemeVariantKey.Parse` (`ThemeDictionaries`' `DictionaryKeyType == ThemeVariantKey`, XD10/C-8); `"Dark"` → `ThemeVariantKey(Dark, null)` | PIN (C-8) |
| X135 | a theme key constraining both axes `x:Key="Dark+Ansi16"` | `Load` | `ThemeVariantKey(Dark, Ansi16)` via `Parse` | PIN (C-8) |
| X136 | an unparseable theme key `x:Key="Bogus"` in `ThemeDictionaries` | `Load` | `CUR2401`-class wrapping the `FormatException` from `ThemeVariantKey.Parse` with position | PIN (XD10) |
| X137 | an implicit-keyed `<Style TargetType="Button">` in a dictionary (no `x:Key`) | `Load` | keyed implicitly by a type-selector / `Button` per the styling implicit-key rule (the `Style.Selector` carries the type; the dictionary key is the implicit form) | WPF |
| X138 | an implicit-keyed `<DataTemplate DataType="local:Item">` in a dictionary | `Load` | keyed by `DataTemplateKey(typeof(Item))` (the implicit-template key, XD10; `DataTemplateKey` confirmed) | WPF |
| X139 | a `Style.TargetType="Button"` (the `Style TargetType` ⇒ selector mapping) | `Load` | produces an **exact-type** selector `Style` (the styling object uses a `Selector`, not a `TargetType` property; the loader resolves `Button` — namespace-aware via the document table — to its CLR type and builds `Selectors.OfType(type)`, falling back to a simple-name `Selector.Parse`; setter `Property` resolution still uses the lexical target type) | DEV (doc §4.3; XD26; `Style` uses `Selector`) |
| X139a | `<Style Selector="t|XamlPrefixTarget">…</Style>` with `t` a clr-namespace prefix (custom ns) | `Load` + apply | the `prefix\|Type` token binds `t` from the document table → exact-type match; the style applies to `XamlPrefixTarget` instances (not a derived type — exact) | AV (XD26; style-matrix SD25) |
| X139b | `<Style Selector=":is(t|XamlPrefixTarget)">…</Style>` over a base + derived tree | `Load` + apply | assignable match — the rule applies to BOTH the base and the derived instance (the `:is(t\|Base)` case for a namespaced base type) | AV (XD26) |
| X140 | merged-dictionary precedence at lookup: a key `A` in both own and a merged dict | resolve | own entries beat merged; a later merged beats an earlier one (the `ResourceDictionary` rule, last-to-first) — the loader does not reorder | WPF |

---

## 12. Deferred (lazy) resource entries + retry-safety — X141–X146 *(X2, loader)*

XD18 — `SetDeferred(key, IDeferredResourceEntry)`; once-on-success; throwing `Realize` resets to `Deferred`; lexical scope = definition-site chain. The deferred-dictionary optimization (doc §4.5; C-2/C-3).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X141 | a `ResourceDictionary` with 3 keyed entries loaded from XAML | `Load` | the loader fills via `SetDeferred(key, XamlDeferredResourceEntry(doc, sliceStart, capturedScope))` — **no** instantiation at load; `dict.Count == 3` but `EnumerateDeferredEntries()` shows all `Deferred` (the deferred-dictionary optimization — a 300-resource theme = parse + 300 inserts) | PIN (doc §4.5) |
| X142 | first lookup of a deferred key `dict["A"]` | get | realizes once: `Realize(lexicalScope)` walks the slice, instantiates the value, caches it in place (the slot object is not replaced — `ResourceDictionary` confirmed); `DeferredState` → `Realized`; a second lookup returns the cached value without re-realizing | PIN (XD18) |
| X143 | the lexical scope passed to `Realize` | inspect | `lexicalScope == ResourceScopes.ForDictionary(definingDict, enclosingChain)` — the definition-site chain (a `StaticResource` inside the deferred entry resolves against the defining dictionary then the enclosing scope, C-2) | PIN (XD18) |
| X144 | a deferred entry whose `Realize` **throws** (a `StaticResource` it references is missing) | get | the exception propagates; the slot resets to `Deferred` (consumes no slice state — `ResourceDictionary.Realize` catch-block confirmed); `DeferredState` is `Deferred` again (retry-safe) | PIN (XD18) |
| X145 | the retry after X144: the missing dependency is now present, lookup again | get | `Realize` runs again and **succeeds**; `DeferredState` → `Realized`; the value is correct (entries are retry-safe — the deferred-entry retry-safety test, doc §4.10) | PIN (XD18) |
| X146 | a `DeferredEntryInfo` probe: `TryGetDeferredInfo(key, out info)` after realization at a variant | inspect | `info.State == Realized`; `info.RealizedAtVariant` carries the variant the StaticResource captures froze at (C-3/C-7; `DeferredEntryInfo`/`RealizedAtVariant` confirmed) | PIN (C-3) |

---

## 13. Deferred content: templates, namescopes, lexical capture, access keys — X147–X168 *(X3, loader)*

XD6 (type-contract deferral) + the template mechanism (doc §4.5; C-16/C-21) + XD11 (access-key folding). Host. The drift gate between loader + X4 generator (doc §4.10).

### 13.1 Type-contract deferral

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X147 | `<ControlTemplate><Border><ContentPresenter/></Border></ControlTemplate>` | parse | `ControlTemplate.Content` is typed `ITemplateContent` ⇒ the `<Border>…` subtree is captured as a `Deferred` member (a node-graph slice `⟪…⟫`), **type-checked at parse** (type errors inside the body surface now with position), NOT instantiated | PIN (XD6) |
| X148 | the `IsDeferredContent` derivation | inspect | `XamlMember.IsDeferredContent == (ValueType == typeof(ITemplateContent))` — independent of runtime type / any attribute (XD6); a non-`ITemplateContent` member is never deferred | PIN/DEV (XD6) |
| X149 | a `<Storyboard>` / `<TransitionCollection>` in a resource dictionary | `Load` | instantiated **eagerly** as an ordinary resource object (no `ITemplateContent`-typed member ⇒ no deferral, C-12) | PIN (C-12) |
| X150 | a `DataTemplate` `<DataTemplate><TextBlock Text="{Binding Name}"/></DataTemplate>` | parse | `DataTemplate.Content` (typed `ITemplateContent`) defers identically (a slice); the inner `{Binding}` is parse-checked | PIN (XD6) |
| X151 | a type error inside a template body `<ControlTemplate><Bogus/></ControlTemplate>` | parse | `CUR2002` "type `Bogus` not found" at the body element's position — parse-time error inside a deferred slice (doc §4.1 "type errors inside a template body surface at parse time") | PIN (XD2) |
| X152 | an **event** inside a template body `<ControlTemplate><Button Click="OnX"/></ControlTemplate>` | parse | `CUR2301` "events are not allowed inside deferred content in v1" with position (XD14; doc §4.13) | PIN/DEV (XD14) |

### 13.2 Build: instances, namescopes, provenance

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X153 | a `ControlTemplate` built once via `XamlTemplateContent.Build(context)` | `Build` | returns a fresh element subtree; the loader's `XamlTemplateContent : ITemplateContent` walks the slice with template-local provenance; `TemplateBinding` enabled inside. **X5.4 lowering:** a `Deferred` member (ControlTemplate/DataTemplate Content) lowers to a static local-function factory `UIElement __FactoryN(TemplateBuildContext __ctx)` wrapped in a `new FuncTemplateContent(__FactoryN)` — a fresh subtree per `Build`; `x:Name` parts register via `__ctx.RegisterName` into the per-build template scope (never a document field); nested templates flatten into sibling factories. `{TemplateBinding}` inside a template lowers (X5.4e — see X127); `{StaticResource}` inside a template stays `// TODO X5` (needs the captured lexical scope). Test: `TemplateLoweringTests.Lowered_ControlTemplate_BuildsViaFactory`. | PIN (doc §4.5) |
| X154 | **double-build isolation**: the same template built twice | `Build` ×2 | **distinct element instances** (no shared elements — the foreign-`TemplatedParent` guard catches misuse, `ControlTemplate.StampTemplatedParent` confirmed); **shared folded-constant references** (XD20); **separate namescopes** | PIN (doc §4.10; XD20) |
| X155 | template `x:Name` registration `<ControlTemplate><Border x:Name="PART_Border"/></ControlTemplate>` | `Build` | `PART_Border` registers in the per-`Build` `TemplateBuildContext.NameScope` **only** (not the document scope, C-16); `context.RegisterName` / `TemplateBuildContext.NameScope` is the sink | PIN (XD15) |
| X156 | the template-scope carrier | inspect | the template scope attaches via `TemplateNameScopeProperty` on the **templated parent** (set by `ApplyTemplate`), NOT the template root (C-16; `NameScope.SetTemplateNameScope` confirmed); `GetTemplatePart<T>` / `FindEnclosing` resolve part names only for an element whose `TemplatedParent == carrier` (the guarded walk) | PIN (XD15) |
| X157 | a document content child vs a template part with the same name | resolve | the document content child resolves the document scope; the template part resolves the template scope — no leakage either direction (the guarded walk, XD15/BD21) | PIN (XD15) |
| X158 | provenance: a template ships `Background="Red"` on a part, then a document/local/Style override | `Load` + apply template | **amended 2026-06-16 (precedence-matrix §20/PD24):** the template build lands the value at `BindingPriority.Template` (one rung below Style — values a template authors on its parts are overridable by a page/theme Style, the deliberate inverse of WPF). A document-level `LocalValue` set or a Style **overrides** it; the template value resurfaces when the override is withdrawn. (Was pinned as `LocalValue` before the Template lane existed.) | PIN (XD8 + precedence §20) |
| X159 | a `null` template root (a `Build` returning null) | `Build` | `InvalidOperationException` (the `ITemplateContent` contract — `FuncTemplateContent`/`ControlTemplate.Instantiate` confirmed); the loader's slice walk must produce a non-null root | PIN |
| X160 | `TemplateBinding` inside a build `<ControlTemplate><Border Background="{TemplateBinding Background}"/></ControlTemplate>`, owner `Button` with `Background=Red` | `Build` | the part's `Background` tracks the owner's `Background` (one-way to templated parent, applied at build via `TemplateBuildContext.TemplatedParent`); a non-template `{TemplateBinding}` is X56 | PIN (doc §4.4) |
| X161 | a `ControlTemplate.TargetType="Button"` applied to a non-`Button` control | apply | `InvalidOperationException` naming the mismatch (`ControlTemplate.Instantiate` TargetType check — CD19, confirmed) | PIN |

### 13.3 Lexical resource-scope capture

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X162 | a template defined inside `<Window.Resources>`; the template body uses `{StaticResource AccentBrush}` where `AccentBrush` is in `Window.Resources` (the definition-site chain) | `Build` | the captured lexical scope = the resource chain enclosing the template **definition**; the `StaticResource` resolves: template's own `Resources` → captured definition-site chain → instantiation-site scope (the WPF-faithful rule, doc §4.5) | System.Xaml / PIN |
| X163 | the same template instantiated under a control whose local resources shadow `AccentBrush` | `Build` | the **definition-site** value wins (lexical capture, not instantiation-site) — the documented, tested rule (doc §4.5) | System.Xaml / PIN |
| X164 | a template's own `<ControlTemplate.Resources>` entry shadowing a definition-site key | `Build` | the template's own `Resources` win (innermost in the captured chain — `ControlTemplate.Resources` confirmed) | PIN (doc §4.5) |

### 13.4 Access-key literal folding (XD11)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X165 | `<Button Content="_Run"/>` (`ButtonBase.Content` carries `ParsesAccessKeyLiterals`) | `Load` | the string literal folds to `AccessText.Parse("_Run") == AccessText("Run",'R',0)` (metadata-flag-driven, XD11/C-19; `ButtonBase.Content` flag confirmed); the fold === `AccessText.Parse` | PIN (XD11) |
| X166 | `<TextBlock Text="_Run"/>` (`TextBlock.Text` is unflagged) | `Load` | **no** fold — `textBlock.Text == "_Run"` verbatim (the underscore stays literal; XD11 — `TextBlock.Text` never folds) | PIN/DEV (XD11) |
| X167 | `<Label Content="_File"/>` (`Label.Content` carries the flag) | `Load` | folds to `AccessText("File",'F',0)` (XD11; `Label.Content` flag confirmed) | PIN (XD11) |
| X168 | `<Button Content="__Verbatim"/>` (the escape) and `Content="_!bang"` (non-letter mnemonic) (one `[Theory]`) | `Load` | `"__Verbatim"` → `AccessText("_Verbatim", '\0', -1)` (doubled underscore = literal, no key); `"_!bang"` → literal underscore, no key (mnemonic must be BMP letter/digit) — both per `AccessText.Parse`, never throws (XD11; `AccessText.Parse` confirmed) | PIN (XD11) |

---

## 14. Diagnostics, AOT, threading, API shape — X169–X182 *(X0 base, X1/X2 wiring)*

The diagnostic contract (XD1), the parse fence (XD2), reflection inventory + the dual-provider gate (XD16), threading (XD20), the `Load`/`LoadComponent` API surface, the conformance-corpus drift gate (doc §4.10).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X169 | `DiagnosticMode.ThrowOnFirstError` (default), a document with two errors | `Parse` | throws `XamlParseException` on the **first** error; `ex.Line`/`ex.Column`/`ex.Code` are the first error's; `ex.Diagnostics` has ≥ 1 (the thrown one first) | PIN (doc §4.2) |
| X170 | `DiagnosticMode.CollectAll`, the same two-error document | `Parse` | does not throw; `doc.Diagnostics` lists **both** errors, each with its own code + line + col | PIN (doc §4.2) |
| X171 | a runtime (instantiation) failure: `{StaticResource Missing}` | `Load` | `XamlParseException` carrying the **node's** line/col (instantiation errors carry position too — XD1; the instantiator always knows the current record) | PIN (XD1) |
| X172 | the `CUR1xxx`/`CUR2xxx`/`CUR3xxx` banding | inspect a sample of each | parse errors `CUR1xxx`, resolution `CUR2xxx`, instantiation `CUR3xxx` (doc §4.2); a golden-file row per representative code asserts code+line+col+message exactly (the diagnostic golden-file program, doc §4.10) | PIN (doc §4.10) |
| X173 | reflection inventory | inspect `ReflectionXamlMetadata` | reflection lives only here (activation / CLR setter-getter / events / `x:Static`), cached per type; the type is annotated `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`; `UIProperty` members never reflect (XD16) | PIN (XD16) |
| X174 | the dual-provider drift gate: run a representative subset of the suite with `ReflectionXamlMetadata` and a hand-built `IXamlTypeMetadataProvider` | `Load` ×2 | identical results (the same tree, the same diagnostics) — the X5 generated provider cannot drift semantically (doc §4.10) | PIN (doc §4.10) |
| X175 | `RuntimeFeature.IsDynamicCodeSupported == false` (simulated) | `Load` | activation falls back to `Activator.CreateInstance`, setters to raw `MethodInfo.Invoke` (the honest AOT fallback); values still set; one row documents X5 is the supported trimmed mode (XD16) | PIN (XD16) |
| X176 | thread-safety of parse: `Parse` the same string on two background threads | `Parse` ×2 (off the UI thread) | both succeed; documents are immutable + shareable; no shared mutable state (XD20; the loader never touches `TerminalSession`/scenes — invariants 2/6/7) | PIN (XD20) |
| X177 | `GetOrParse(uri)` cache | call twice | the second call returns the **same** cached `XamlDocument` (per-loader cache keyed by URI); a third call after a different URI does not collide | PIN (doc §4.2) |
| X178 | `Load` is UI-thread-only (the contract) | call `Load` off the UI thread (DEBUG) | a DEBUG `VerifyAccess`-class diagnostic / the documented contract (instantiation is single-UI-thread — invariant 6); parse is the thread-safe half (XD20) | PIN (invariant 6) |
| X179 | the conformance-corpus drift gate (doc §4.10) | a shared document set | the same corpus drives the loader rows here and (later) the X4 generator diagnostics; this matrix's documents ARE that corpus; fold-equivalence (constants, `AccessText`, `Optional<T>`) is asserted so the generator fold === the loader parse (recorded; the generator side is X4-deferred) | PIN (doc §4.10) |
| X180 | a 50-element document (a realistic terminal window) | `Parse` then `Load` | parse + resolve completes (no per-frame cost — the loader allocates nothing during a render loop; this is a load-time-only cost); a `[Trait("Category","Benchmark")]` row records single-digit-ms parse (proposal §3.9) | PIN (proposal §3.9) |
| X181 | `XamlLoaderOptions.ConverterCulture` (non-invariant) | `Load` with a culture-sensitive double | the configured culture is used for context-dependent converts; default is invariant (doc §4.2) | PIN |
| X182 | the immutable-artifact reuse: parse once, `Load` ×3 | `Load` ×3 | three distinct trees; one parse; folded constants shared across all three (XD20) — the "parse once, instantiate many" invariant | PIN (XD20) |

---

## 15. X4 generator handshake (contract-pinned, deferred) — X183–X188 *(X4, deferred — descriptor/contract shape only)*

Doc §4.9. **Not implemented at P6** (prompt scope fence ⑦) — these rows pin the contracts the X4 generator binds to and assert the **runtime loader's** half is producer-agnostic. Rows are descriptor-/seam-shape only; the generator side lands at P10.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| X183 | the compiled-binding descriptor (`CompiledBinding<TSource,TValue>`, already P4) | inspect | the loader can *consume* a `Binding.Compiled(static vm => vm.X)` descriptor in markup-equivalent code paths with no engine change (the X4 generator is a second producer of the same type — C-17; the descriptor exists per binding-matrix §15) | **REALIZED (X5/B3a+B3b)** — the lowering emitter is the third producer. **Compiled lane (B3a):** an `x:DataType`-scoped, DataContext-relative, single-hop, instance-leaf `{Binding}` lowers to a typed `new CompiledBinding<TData,TLeaf>(getter, setter, steps, path)` + `BindingOperations.Install` (zero reflection; writable non-init-only leaf ⇒ reverse setter, else null ⇒ OneWay degrade per B152; `Mode` carried). **Reflective fallback (B3b):** every other shape (no `x:DataType` / multi-hop / indexer / `Source`/`ElementName`/`RelativeSource`-Self-or-TemplatedParent / `StringFormat`/`FallbackValue`) lowers to a faithful `new Binding(path){…}` mirroring the runtime handler's `BuildBinding` — the binding still works (reflectively; the AOT warning points the author at it). Only a `Converter`-bearing binding (needs resource lowering) or an unsupported `RelativeSource`/`Mode` stays a `// TODO X5`. Behavioral tests in `LoweringEmitterTests`: `Lowered_Binding_WithDataType_CompilesAndResolvesLive` (compiled, live + INPC), `Lowered_Binding_Uncompilable_FallsBackToReflectiveBinding` (multi-hop reflective, live), `Lowered_Binding_InitOnlyLeaf_CompilesWithNullSetter` + `…StaticMemberPath_FallsBackToReflective` (the two emit-correctness audit fixes). **Reachable via the WS-X5.5 opt-in:** `<CursorialXamlLowering>full</CursorialXamlLowering>` (a compiler-visible MSBuild property) switches each `x:Class` document from the X4.6 loader code-behind to this lowering; a member the lowering can't emit is a `CURG3001` build warning at its `.xaml` position (never a silent drop). Driver tests in `LoweringGeneratorTests` (opt-in lowers + binds live; default keeps `LoadComponent`; `{StaticResource}` → `CURG3001`). |
| X184 | `x:DataType` build-time path diagnostics | parse | `x:DataType` is parsed/recorded now (a directive) but build-time path validation is the X4 generator's (C-17/C-20); at P6 a bad path produces a **runtime** `PathError` trace (the reflective fallback), not a build error | X4 (deferred) |
| X185 | `[TemplatePart]` cross-check (C-20) | inspect | the loader does not gate on `[TemplatePart]`; the X4 generator cross-checks template `x:Name` sets against `TargetType`'s `[TemplatePart]`s as Roslyn diagnostics (assist, not a gate) — recorded, deferred | X4 (deferred) |
| X186 | the `IXamlTypeMetadataProvider` seam | inspect | the loader's metadata is fully behind `IXamlTypeMetadataProvider`; the X5 generated provider (trim/AOT-clean) plugs in via `[assembly: XamlMetadataProvider(typeof(…))]` — the seam exists now, the generated provider is deferred (XD16). **Realized (X4.5) + two fixes the ARCH-1 XAML themes surfaced:** (1) an **init-only CLR property** (e.g. `SolidColorBrush.Color`, `Pen.Brush`, `Style.Selector`) can't bake a compiled `t.Prop = v` setter (CS8852), so the emitted provider sets it **reflectively** — same as `ReflectionXamlMetadata`, correct for a boxed-struct (`readonly record struct Pen`) too (a future AOT-clean upgrade is an `[UnsafeAccessor]` `set_<Name>` thunk). (2) installation is **pull, not push** (amended 2026-07-12): the generator emits only the `[assembly: XamlMetadataProvider]` attribute and the lazy `XamlLoaderOptions.DefaultMetadataProvider` discovers the **entry** assembly's attribute — a library's attribute is inert and merely *loading* an assembly can never repoint a host's default. Further amended 2026-07-13: discovery engages **only when the reflection provider is feature-switched off** (trimming/AOT) — with reflection available, the ambient default stays open-world `ReflectionXamlMetadata`, so user-initiated loose-XAML parsing keeps working in JIT builds (an inspector demo parsing C# string literals was the motivating case); generated loads bind their provider explicitly and never consult the ambient default, so this ordering affects user-initiated ambient parses only. (The original X4.5 shape push-installed via a generated `[ModuleInitializer]` gated by `CursorialXamlInstallProvider`; it hijacked any host that loaded a user assembly — the designer's language service most visibly — and no beneficial case for push-registration survived review, so both the module init and the opt-out property were retired.) The `<CursorialXaml>` item is also embedded by the consumer `.targets` (opt out via `CursorialXamlEmbedResources=false`) so a build-validated `ResourceDictionary` theme still loads at runtime. `Cursorial.UI.Themes.Xaml` originally consumed the generator this way (build-time CUR2 validation, runtime load via reflection); the theme XAML parsed with **zero CUR2** — the gaps were in the emitter, not the symbol model. **Superseded at WS-X5.4j:** the themes are now FULLY LOWERED (`CursorialXamlLowering=full`) and `CursorialXamlTheme` calls the generated `GeneratedXamlLoaders.Build…()` builders — no runtime parse, no provider (the embedded `.xaml` stays only as the dual-run oracle). | X4/X5 (realized) |
| X187 | the `ITemplateContent`/`LoadComponent` producer-agnostic seams | inspect | a future compiled-XAML producer is an alternate producer behind `ITemplateContent`/`LoadComponent`/the metadata provider — zero consumer source change (proposal §1/§7); the seams are in place at P6 | X4/X5 (deferred) |
| X188 | fold-equivalence (the drift gate, doc §4.10) | inspect | the X4 generator folds constants / `AccessText` / `Optional<T>` by the **identical** rule as the loader parse (XD3/XD11/doc §4.6); this matrix's fold rows (X59–X66, X99, X165–X168) ARE the equivalence oracle — the generator must match them exactly | X4 (deferred) |

### §15a. Resource-dictionary / Style / theme lowering (WS-X5.4f–j) — realized

The WS-X5.5 `CursorialXamlLowering=full` opt-in (X183) covers no-`x:Class` `<ResourceDictionary>` roots too: such a document lowers to an internal static `GeneratedXamlLoaders.Build<File>()` (a per-assembly partial) that returns the populated dictionary — reflection-free, no runtime parse, no metadata provider. Tests in `Cursorial.UI.Xaml.Generator.Tests/ResourceDictionaryLoweringTests`.

- **Keyed entries / `MergedDictionaries` / `ThemeDictionaries` / `Source`** lower per `XamlObjectGraphBuilder.FillResourceDictionaryMembers`; an `{x:Static}`/`{x:Type}` `x:Key` resolves at codegen to the static member / `typeof`.
- **`Style`** → `new Style(selector)` with `BasedOn`/`Key` initializers + `Setters`/`Children`. An explicit `Selector="…"` is **baked** to a reflection-free `Selectors` fluent chain (a port of `SelectorParser`; unsupported/fenced constructs ⇒ `// TODO X5`, never wrong); else `TargetType` → `Selectors.OfType`. An attached/prefixed Setter property (`ToggleGlyph.Glyphs`, `input:AccessKeyManager.ShowUnderline`) resolves to `Owner.<lastSegment>Property`.
- **Setter values** convert to the property's value type via the runtime `__ConvertXamlValue` ladder — matching the reflection frontend's parse-time **context-free fold** (the Roslyn-symbol frontend can't fold a runtime converter, so the typed value is produced at `Build()` runtime; `StyleSetterConverter` passes it through at Seal). `{DynamicResource}` (literal or `{x:Static}` key) → a `ResourceReference` carrier.
- **`{StaticResource}` (same dictionary)** → a direct reference to the already-built entry's var (StaticResource's load-time snapshot), tracked by raw string key as entries build (define-before-use). Inside a template factory the reference is an enclosing local, so the factory drops `static` and captures it.
- **`System.Type`-valued members** (e.g. `ControlTemplate.TargetType`) resolve the text as a type **token** → `typeof(...)`, in both the lowering AND the runtime loader's `ConvertText` (there is no string→Type value converter) — so the embedded theme stays reflection-loadable. A keyed `ControlTemplate` with no statically-known target type resolves its `{TemplateBinding}` sources against the `Control` base (always its applied type).

**The built-in theme is fully lowered (WS-X5.4j).** `Cursorial.UI.Themes.Xaml` builds all three `Themes/*.xaml` with **0 CURG3001** and `CursorialXamlTheme` calls the builders. The **X174 dual-run drift gate** for the themes is `XamlThemeLoweringTests`: each theme loaded via the lowered builder AND the reflective `XamlLoader` over the embedded source is asserted structurally equivalent (keys, value shapes, brush colors, glyph carriers, resource references, styles + setters/children, theme-variant sub-dictionaries). The 41 `ArchOne`/`Palette`/`Styles` render tests (now through the builders) are the behavioral-parity gate against the code-first `CursorialTheme.BuiltIn`.

### §15b. Strict NativeAOT (WS-X4.7 / P1E) — realized

`CursorialXamlStrictAot` (generator `build/*.props`, default `$(PublishAot) or $(PublishTrimmed)`) auto-emits the
`Cursorial.UI.Xaml.ReflectionMetadataProvider.IsSupported=false` `RuntimeHostConfigurationOption` (`Trim="true"`) from
the paired `.targets`, so the trimmer constant-folds the reflection metadata provider away — an app no longer
hand-authors the switch (a deliberate reflection-baseline consumer sets `CursorialXamlStrictAot=false`).

**The two AOT routes are NOT equivalent — full lowering (X5) is the trim-clean one:**

- **X4.6 (generated provider + runtime loader):** the StrictAot switch trims `ReflectionXamlMetadata`, but the runtime
  `XamlLoader` still has reflection that is NOT behind that switch — generic-collection `Add` (`XamlObjectGraphBuilder.
  IsGenericList`/`InvokeGenericAdd`), the reflective `{Binding}` lane, `ValueConversion`/`TypeDescriptor`, the
  `NamedColors`/`NamedBrushes` tables, `ReflectionXamlType.ComputeIsCollection`. So a generated-provider app still
  emits `IL2026`/`IL3050` for those. **Not fully trim-clean.**
- **X5 full lowering (`CursorialXamlLowering=full`):** `InitializeComponent` is straight-line construction with NO
  runtime loader — the entire XAML loading path drops out. The XAML loader/provider/frontend reflection warnings go
  to **zero**.

**Proof:** `Cursorial.Demo.XamlAotStrict` (full lowering, `InstallProvider=false`, a literal x:Class view) publishes
NativeAOT (`dotnet publish -c Release -r osx-arm64`) to a native Mach-O binary that **runs** (loads the view, exit 0),
with **zero** XAML-loader/provider/frontend trim/AOT warnings and the switch baked into `runtimeconfig.json`. The
**residual** `IL2026`/`IL3050` (≈13) are all `Cursorial.UI.Data` (the reflective binding engine `AccessorCache`/
`ReflectionBindingExpression`/`ValueConversion`) + `DefaultSelectorTypeResolver.ExportedElementTypes` — Cursorial.UI's
OWN reflection, reachable from the always-linked binding/styling infrastructure, NOT the XAML generator's concern.
AOT-hardening the reflective binding lane + the selector resolver (DynamicallyAccessedMembers annotations / a
feature-switched reflective-binding lane) is a separate S2/S3 workstream. `Cursorial.Demo.XamlAot` stays the
reflection-baseline (`StrictAot=false`, `TrimMode=partial`); both are in `Cursorial.sln`.

---

### §15c. `x:Array` + element-valued built-in primitives (XAML2009) — XA1–XA10 *(all pipelines)*

The XAML2009 `<x:Array Type="T">` intrinsic and element-valued built-in primitives (`<x:String>`, `<x:Int32>`, …),
implemented across the frontend parser + node-graph, the runtime loader, and the generator (symbol-backed parse,
generated provider, full lowering). The **attribute-directive** forms `x:Array="…"` / `x:Reference="…"` stay
`CUR1203` (X29 — those are not valid XAML); these rows are the **element** form `<x:Array>` and built-in primitive
elements. Tests: `Cursorial.UI.Xaml.Tests/XamlMatrix/Section18_XArray.cs` (loader), `LoweringEmitterTests` +
`DualRunDriftTests` (generator).

- **XD27 — `<x:Array Type="T">` is a special element, not a resolvable type.** The parser recognizes the intrinsic
  element `(x-namespace, "Array")` BEFORE normal type resolution: it reads the unqualified `Type` attribute
  (prefix-bound from the live reader scope, like `{x:Type}`), resolves it to the element type T (`CUR2002` on a
  miss), stamps the object `ObjectFlags.IsArray` with T as its `TypeId` (the ELEMENT type, not the object's own
  type), and captures child elements as the array's items (a synthetic `Items` member, `MemberId = -1`). A missing
  `Type` is `CUR1204`; `x:Key`/`x:Name` are honored (an array is a valid keyed resource / named element); any other
  attribute is `CUR2102`. The loader builds `Array.CreateInstance(T, n)` and fills it (an item not assignable to T
  is `CUR2401`, positioned); the generator lowers to `new T[] { … }` (a named array's code-behind field is typed
  `T[]`). The generated metadata provider needs no x:Array entry (the construct is structural in the parser) — only
  the element type T (and built-in item types) enter the closed set, resolved exactly as the reflection provider.
  PIN/DEV (XAML2009; WPF `ArrayExtension` parity in element position).
- **XD28 — a built-in primitive element initializes from its content text.** `<x:String>hi</x:String>` → `"hi"`;
  `<x:Int32>5</x:Int32>` → `5`; etc. — the element's content text is converted to the built-in's CLR type through
  the same converter ladder a member value uses (XD3 fold-equivalence: loader `ConvertInitText` ≡ generator
  `__ConvertXamlValue` ≡ `XamlConverters.For(T)`). An empty element converts the empty string (string→`""`; a
  primitive that rejects `""` is `CUR2401`). This is the minimum initialization-text slice (the general
  `x:Arguments`/`x:FactoryMethod` stays deferred); it exists so a primitive `x:Array` (`String[]`, `Int32[]`) has
  authorable items, and so a standalone primitive resource (`<x:Double x:Key="Pi">3.5</x:Double>`) works. The
  detection is by CLR-type membership in the built-in set (`XamlSchemaContext.IsBuiltInType`), independent of whether
  the type happens to be activatable — a value type would otherwise activate to its default and reject the text. PIN.
  **Known limitation (recorded out):** the supported form is the intrinsics-namespace one (`x:Int32`, `x:String`,
  `x:TimeSpan`), consistent across all three pipelines. Referencing a built-in via a `using:System` / `clr-namespace:System`
  xmlns (`<sys:Int32 xmlns:sys="using:System">`) is **not** a supported form: the runtime loader's `XamlSchemaContext`
  does not probe the corelib assembly for it (CUR2002), so it is rejected by the reflection loader and the generated
  provider — a pre-existing resolver asymmetry (`XamlSymbolResolver` resolves it; `XamlSchemaContext` does not), not an
  x:Array guarantee. Use the `x:` form.

| Row | XAML | stage | expected | source |
|-----|------|-------|----------|--------|
| XA1 | `<x:Array x:Key="b" Type="Button"><Button/><Button/></x:Array>` in a dictionary | `Load` | a `Button[]` of length 2 with the two built Buttons (object items) | XAML2009 |
| XA2 | `<x:Array Type="x:String"><x:String>a</x:String><x:String>b</x:String></x:Array>` | `Load` | a `string[]` `["a","b"]` (element-valued built-ins as items) | XAML2009 |
| XA3 | `<x:Array Type="x:Int32"><x:Int32>7</x:Int32><x:Int32>42</x:Int32></x:Array>` | `Load` | an `int[]` `[7,42]` (each item's text converted) | XAML2009 |
| XA4 | `<x:Array Type="x:String"/>` (empty) | `Load` | a zero-length `string[]` | XAML2009 |
| XA5 | `<x:Array x:Key="x"><Button/></x:Array>` (no `Type`) | parse | `CUR1204` "x:Array requires a Type attribute" with position (XD27) | DEV (XD27) |
| XA6 | `<x:Double x:Key="Pi">3.5</x:Double>` / `<x:Boolean>true</…>` / `<x:String>hello</…>` | `Load` | the converted primitive value (`3.5` / `true` / `"hello"`) per XD28 | XAML2009 |
| XA7 | `<ListBox.ItemsSource><x:Array Type="x:String">…</x:Array></ListBox.ItemsSource>` | `Load` | the `string[]` is assigned to the `IEnumerable` member (an array IS `IEnumerable`) | XAML2009 |
| XA8 | `<x:Array Type="x:Int32"><x:String>nope</x:String></x:Array>` | realize | `CUR2401` (positioned at the item) — the string item is not assignable to `int` (XD27) | DEV (XD27) |
| XA9 | the same `<x:Array>` (object + primitive items) through the **generated provider** | `Load` | byte-identical to the reflection provider — the X174 dual-run gate over x:Array (`DualRunDriftTests`) | PIN (X174) |
| XA10 | `<x:Array Type="x:String">…</x:Array>` under full lowering (`CursorialXamlLowering=full`) | lower | `new string[] { … }` (built-in items as literals / converter calls), matching the loader (`LoweringEmitterTests`) | PIN (X188) |

---

### §15d. Attribute-driven metadata (`Cursorial.Shared`) — XM1–XM8 *(all pipelines)*

The XAML metadata that was hard-coded in per-provider tables is now **attribute-driven** off
`Cursorial.Markup` attributes in the new netstandard2.0 **`Cursorial.Shared`** assembly (referenced by every
layer, so the framework decorates its own types and both providers — reflection + symbol — read the same source).
Tests: `Cursorial.UI.Xaml.Tests/XamlMatrix/Section19_AttributeMetadata.cs` (reflection precedence) +
`MetadataProviderEmitterTests.Bakes_MemberLevelConverterAndSerializer` (generator baking).

- **XM-D1 — `[ContentProperty]` is attribute-only (the base-type tables are retired).** The framework base types
  carry `Cursorial.Markup.[ContentProperty("Name")]` (`Inherited`), so a subclass picks up its nearest decorated
  ancestor; both providers read it (reflection `GetCustomAttributes(inherit:true)`; the generator walks the base
  chain, since Roslyn `GetAttributes()` is direct-only). The former hard-coded base-type maps
  (`ContentPropertyTable.Known` + the generator's mirror) are **deleted**. Matched by attribute simple name (any
  equivalent attribute is honored). PIN/DEV.
- **XM-D2 — converter/serializer resolution follows WPF `GetSerializerFor` precedence, in `ForMember` only.** A
  member's string converter resolves **member `[ValueSerializer]` → member `[TypeConverter]` → type
  `[ValueSerializer]` → type `[TypeConverter]` → the built-in ladder** via `XamlConverters.ForMember(member,
  memberType)` — the **single** attribute-consulting entry point. `XamlConverters.For(type)` stays a **pure,
  reflection-free ladder** (no attribute lookup), so the generated/lowered providers can bake `For(typeof(T))`
  AOT-clean; a type-level `[TypeConverter]` therefore applies where the type is used as a member value (via
  `ForMember`), not through a bare `For(type)`. A `[ValueSerializer]`'s deserialize leg (`IValueSerializer.
  ConvertFromString`) is used at load and **wins over** a co-present `[TypeConverter]`. Cursorial's attributes are
  matched by **full** name and require the named type to implement `Cursorial.UI.Xaml.ITypeConverter` /
  `IValueSerializer`; the BCL `System.ComponentModel.TypeConverter` is **also** honored for interop, *below*
  Cursorial's own attributes (XM-D4). **The generated provider emits a runtime
  `ForMember(typeof(Owner).GetProperty(name), typeof(T))` for a member whose member-or-type carries a Cursorial
  attribute, or that carries a member-level BCL `[TypeConverter]`** — the identical resolution the reflection
  provider runs (so accessibility, the string-name ctor form, and the BCL adaptation all match → zero drift, no
  `new T()` baking that could break on a non-public type/ctor). A member with no converter attribute (every
  framework member) bakes the pure `For(typeof(T))` ladder, so the framework's generated provider stays
  reflection-free; a consumer's custom-converter member resolves reflectively (honestly AOT-flagged). PIN (WPF parity).
- **XM-D4 — the BCL `System.ComponentModel.TypeConverter` is honored for interop.** A member-level
  `[System.ComponentModel.TypeConverter]` resolves in `ForMember` (after Cursorial's member attrs), adapting the
  converter's `ConvertFrom(string)` leg to `ITypeConverter`. A **type-level** BCL converter
  (`TypeDescriptor.GetConverter(memberType)` — covering `[TypeConverter]`-decorated types and BCL defaults like
  enums / `Guid` / `Version`) is the loader's **last conversion fallback** in `ConvertText`, *after* the curated
  ladder — so the ladder keeps precedence for the types Cursorial handles (an `int` keeps its cell-aware converter,
  not the BCL `Int32Converter`), and `For`/`Build` stay pure (the type-level BCL is resolved at load, not baked, so
  the generator needn't flag every BCL-convertible primitive). Both BCL legs are reflection (`TypeDescriptor` /
  `Activator`) — an opt-in interop seam, AOT-flagged honestly; the member-level form supports both the parameterless
  and the `(Type)` ctor (`EnumConverter`/`NullableConverter`) and is `TypeDescriptor`-cached per type. **Interop
  fidelity:** a BCL converter brings its OWN semantics (e.g. `DateTimeOffsetConverter` maps `""`→`MinValue`,
  `NullableConverter` `""`→`null`) — Cursorial does not second-guess them. The `[TypeConverter(typeof(X))]` (assembly-
  qualified) form is the supported declaration; a simple-name string-ctor across assemblies may not resolve (same
  limitation as Cursorial's own string-ctor + WPF). The generated provider's flagging matches reflection exactly
  (Nullable unwrap + overridden-member walk) — the X174 zero-drift invariant holds for BCL members/types too. PIN (WPF interop).
- **XM-D3 — `[ValueSerializer]` save leg + `[DictionaryKeyProperty]` are defined, not yet consumed.**
  `IValueSerializer.ConvertToString` (save) has no consumer (Cursorial has no XAML save path); the load leg is
  live (XM-D2). `Cursorial.Markup.[DictionaryKeyProperty]` is defined for consumer / future implicit-dictionary-key
  use (resources use explicit `x:Key` today, so it is not yet read by the loader). Recorded out.

| Row | scenario | expected | source |
|-----|----------|----------|--------|
| XM1 | a decorated framework base type (`ContentControl`/`Panel`/`Style`/…) resolves its content property | the inherited `[ContentProperty]` name; the retired tables are gone | XM-D1 |
| XM2 | a member-level `[TypeConverter]` on an `int` property | `ForMember` returns it (beats the built-in int ladder) | XM-D2 |
| XM3 | a member with BOTH `[ValueSerializer]` and `[TypeConverter]` | the ValueSerializer wins (WPF `GetSerializerFor`) | XM-D2 |
| XM4 | a type-level `[TypeConverter]` | `For`/`ForMember`(null member) returns it | XM-D2 |
| XM5 | a member with BOTH a Cursorial `[TypeConverter]` and a BCL `[System.ComponentModel.TypeConverter]` | Cursorial's wins (its attribute is matched first) | XM-D2/D4 |
| XM6 | the generated provider over a member with a `[TypeConverter]`/`[ValueSerializer]` (Cursorial member/type, or member-level BCL) | emits a runtime `ForMember(typeof(Owner).GetProperty(name), typeof(T))` (drift-free with reflection); a plain member bakes the pure `For(typeof(T))` ladder | XM-D2 |
| XM7 | a member-level BCL `[System.ComponentModel.TypeConverter]` | honored — its `ConvertFrom(string)` adapted to `ITypeConverter` (below Cursorial's own) | XM-D4 |
| XM8 | a member typed with a BCL-convertible type (`Guid`, `[TypeConverter]`-decorated, enum, …) and no member attr | converts via the `ConvertText` type-level BCL fallback (after the ladder); a type with no string converter stays the raw string | XM-D4 |

---

## 16. Test authoring contract

Each numbered row above becomes **exactly one** xUnit test in `Cursorial.UI.Xaml.Tests`, named after its row id with a behavior slug (`X060_FoldedMargins_SharedAcrossLoads`), one file per section under `Cursorial.UI.Xaml.Tests/XamlMatrix/` (`Section01_Parsing.cs` … `Section15_Generator.cs`), namespace `Cursorial.Tests.UI.Xaml.XamlMatrix`. Rows whose Expected cell enumerates a family (the `[Theory]` rows — X29 the recorded-out intrinsics, X55 the three folded intrinsics, X83 the margin arities, X86/X87 color forms, X89 brush kinds, X93 grid lengths, X94 pens, X98 easings, X99 `Optional<T>`, X168 the access-key escapes) become a single `[Theory]` with one case per family member, keeping the row↔test bijection at the row level.

**Assembly/project setup (this stage's first code task at X0):** add `Cursorial.UI.Xaml.Frontend` (netstandard2.0), `Cursorial.UI.Xaml` (net10.0, references the frontend + `Cursorial.UI`), and `Cursorial.UI.Xaml.Tests` (net10.0, xUnit, references both + `Cursorial.UI.Testing`) to `Cursorial.sln`. The node-graph probe surface is `InternalsVisibleTo("Cursorial.UI.Xaml.Tests")` on the frontend assembly — **the content is the contract; record/field member names are implementation freedom** (the binding-matrix §16 stance). The module-initializer `ResourceDictionary.LoadCallback` registration (C-9, X132) is process-global static — tests **save and restore** it in a fixture `IDisposable` (the `ResourceDictionary.Source` rows depend on it). The fixture VM (`Vm`) is shared with the binding matrix so `{Binding}` rows pin against one oracle.

**The Windows-only System.Xaml oracle leg (doc §4.10).** Rows tagged `System.Xaml` (X31–X36 whitespace, X39–X40 DTD, X46–X50 escape/quoting, X110/X112 the `{}` disambiguation, X115 forward-reference order, X162–X163 ambient `StaticResource` resolution) are additionally pinned against **real System.Xaml** in a Windows-only CI leg (`[Trait("Oracle","SystemXaml")]`, skipped off-Windows via `[Trait("Category","WindowsOnly")]` + a runtime OS guard). The non-Windows runs assert Cursorial's documented behavior directly (the table's Expected cell); the Windows leg additionally asserts System.Xaml produces the same result for the same input (the drift detector). A divergence is resolved by a PR amending this matrix (and the XD ledger when the row carries a `PIN`/`DEV` tag) — not by silently matching whatever System.Xaml does.

**Staging.** Rows are staged per the §0 stage map: §§1–5 + §14-X0 rows (X0) green at X0 exit (frontend, no instantiation); §§6–9 + §14-X1 (X1) at X1; §§10–12 + §14-X2 (X2) at X2; §13 + §14-X3 (X3) at X3. §15 rows (X4/X5) are recorded now and stay **absent (not red)** until P10 — they assert the loader's producer-agnostic seams exist, which they do at P6, but the generator behavior they describe is deferred. Later-stage rows may be absent (not red) before their stage opens, but every row is binding from now.

**Allocation rows** (X60/X81/X154 folded-constant sharing + double-build isolation, X180/X182 the parse-once/instantiate-many claims) follow the repo norm: `GC.GetAllocatedBytesForCurrentThread()` deltas / reference-equality probes after warm-up, single-threaded, not BenchmarkDotNet (the X180 ms-scale parse row is the one `[Trait("Category","Benchmark")]`). DEBUG-only rows (X178 the UI-thread `VerifyAccess`) compile their assertion under `#if DEBUG`. Internal-probe rows (the node-graph shape in §§1–5, X128 the `AttachTo` seam, X141–X146 the deferred-slot state, X154 the shared-box probe) use the `InternalsVisibleTo` surface — pinned loosely as above.

**Fold-equivalence + dual-provider (the drift gates).** X174 (dual-provider) and X188 (fold-equivalence) are the structural drift gates: the dual-provider gate runs a representative subset of the whole suite twice (reflection vs hand-built provider) asserting identical trees + diagnostics; the fold-equivalence gate is recorded against the X4 generator (deferred) but its loader half (the fold rows produce exactly `AccessText.Parse` / the boxed constant / `Optional<T>.Unset`) is asserted now.

**X2/X3 implementer decisions (recorded at X3 exit).** (1) `Style`'s implicit content property is its `Setters` (WPF parity; added to the loader's `ContentPropertyTable`) — additive, loader-only. (2) The default xmlns map gains `Cursorial.Drawing.Media` (where `Colors`/`Brushes`/the brush types live) so `{x:Static Colors.Red}`/`{x:Static Brushes.Red}` and the XD13 color mini-language resolve — additive, loader-only. (3) `Setter` is construction-immutable (no parameterless ctor), so the loader builds it from its `Property`/`Value` members; the frontend's `ResolveSetter` rewrites the `Property` member to carry the resolved target `UIProperty` and re-classifies a markup-extension `Value` as an `Extension` member (a `{DynamicResource}` setter value stores a `ResourceReference` carrier per X117). (4) A deferred (template) body has its OWN namescope, so the enclosing resource-dictionary flag does NOT propagate into it — an `x:Name` on a template part inside a `<ControlTemplate>` declared in `<Foo.Resources>` is a part name, never `CUR2304`. (5) Items runs are walked by `ObjectRecord.SubtreeLength` (depth-first SoA — item *k* is not at `first + k` when items carry subtrees); this corrects a latent X1 leaf-only assumption. (6) `Cursorial.UI` gains `InternalsVisibleTo("Cursorial.UI.Xaml.Tests")` for the X141–X146 deferred-slot-state probes (test-only, additive). (7) `XamlModule` installs `ResourceDictionary.LoadCallback` at module load (C-9, X132) routing `Source=` through an overridable `IXamlResourceProvider` (default: embedded resources); tests save/restore the global static.

**P6-integration decisions (recorded at the P6 exit pass).** (1) **The System.Xaml oracle leg's oracle node is decorated with a portable `[TypeConverter]` (System.ComponentModel), NOT the Windows-only WPF `[ContentProperty]`** — referencing `System.Windows.Markup.ContentPropertyAttribute` is a *compile-time* dependency on System.Xaml, which ships only with the Windows Desktop runtime and is not in the cross-platform ref packs (so the test project cannot reference it on the single `net10.0` TFM without breaking the macOS/Linux build). The oracle node therefore routes element text through a `TypeConverter.ConvertFrom(string)`; System.Xaml applies its (XD19-shared) whitespace collapse to element text *before* invoking the converter, so the converted node's text carries the oracle's collapsed value — exercising exactly the whitespace semantics the leg pins (X32–X34). The `{}` brace-escape leg (X46/X47) sets the `Content` member by name, which needs no content property. System.Xaml is still loaded reflection-only at runtime (`Assembly.Load("System.Xaml")`), so the whole class compiles cross-platform and skips off Windows with a documented reason. (2) The **`uixaml` demo** (`Cursorial.Demo/Demos/UIXamlDemo.cs` + the embedded `Resources/uixaml-demo.xaml`) is the live P6 proof: the entire control tree (a themed `DockPanel` of access-key `Button`s, `CheckBox`/`RadioButton` toggles, a `{Binding}` status line + caption, `{StaticResource}` brushes, and a `{TemplateBinding}` `ControlTemplate`) is loaded from one embedded XAML string at runtime by `XamlLoader.Shared.Load` and run on the real `UIApplication` frame loop. The demo's XAML-visible brush fixture (`Cursorial.Demo.DemoBrush`) lives in an explicit `Cursorial.Demo` namespace (the demo command classes use the global namespace; a XAML-resolvable type needs a namespace to register in the default xmlns via `XamlSchemaContext.Default.RegisterDefaultNamespace`). The `.csproj` embeds `Resources/*.xaml` with the default manifest name (no `LogicalName` override, unlike the `*.png` glob). The automated equivalent of this exit criterion is `Phase6XamlEndToEndTests.ThemedTree_FromXaml_RendersAndBindingsResourcesResolveLive` (a demo-shaped document loads + renders + binds through `UITestHost`).

Rows are not merged, reordered, or "covered implicitly by" other rows: a row without a matching test is a P6 exit-criterion failure (§14 P6: the parse/loader oracle matrix green — node-graph shape, the diagnostic golden files with line+column, the markup-extension fuzz + escape oracle, the deferred-template double-build isolation + namescope sealing, the deferred-entry retry-safety, the access-key fold equivalence, the dual-provider drift gate). When the loader cannot honor a row, the resolution is a PR that amends this file (and, where the row carries a `PIN`/`DEV` tag, the XD ledger) **before** the loader change lands — the matrix is the oracle, not the implementation. Oracle tags document provenance and do not alter test behavior.
