# Cursorial.UI — Resolved fork decisions (BINDING for all subsystem designs)

Decided 2026-06-10 via advocate panels + 3-lens judge panels (9 proposals, 9 judgments, in this directory).
Canonical winning proposals: `proposal-property-system-avalonia.md`, `proposal-styling-hybrid.md`, `proposal-xaml-runtime-loader.md` (each amended below — amendments override the proposal text). Losing proposals remain useful reference for grafted ideas.

## Fork A — Property system: Avalonia-style typed chassis

- Types: abstract `UIProperty`; `StyledProperty<T>`; `AttachedProperty<T>`; `DirectProperty<TOwner,T>`; registrar `UIProperty.Register<TOwner,T>(...)`; structural read-only via `UIPropertyKey<T>`.
- Host: `UIObject` — `GetValue<T>(StyledProperty<T>)` / `SetValue<T>(..., BindingPriority)` / `ClearValue` / `SetCurrentValue<T>` / `GetBaseValue<T>` / `GetValue<T>(prop, maxPriority)`; zero-box CLR wrappers; typed change notification via a **copied-value** `readonly struct` args carrier passed by `in` (NOT a ref-struct wrapping live store entries — reentrancy corruption; a second small carrier covers inherited-change notification on entry-less descendants).
- Storage: per-instance `ValueStore`, effective/base two-slot split, **priority frames** (`ValueFrame`). Restoration-on-deactivation is OWNED BY THE STORE (frames removed ⇒ next value promotes; never "set old value back").
- `BindingPriority` (high→low): **Animation > LocalValue > Style (single slot; within-slot ordering = styling engine's `StyleSortKey` via frames) > Template > Inherited > Default**. Bindings are producers at a priority (`BindingEntry<T>`), animations via `AnimatedValueHandle<T>`.
- **Template lane** (added 2026-06-16, precedence-matrix §20/PD24): `BindingPriority.Template` (wire 150) carries everything a control/data template *authors on its parts* — a literal `SetValue`, a `{TemplateBinding}`/`{Binding}`, a `SetResourceReference` — one rung **below** Style, so a page/theme Style overrides a template default (the **deliberate inverse of WPF**, where template values outrank style). Reached only through a thread-static **template-instantiation scope** open during `ControlTemplate.Instantiate` / `DataTemplate.Build` (not a `SetValue` priority argument — PD1 stands); `Bind`/`BindUntyped` now accept `LocalValue` or `Template` (A6). **Not** the same as Fork B's `StyleLayer.Template` sort-key layer (a rule from a control template's `Styles`, still Style-priority — and therefore beats a Template-lane part value). Within-lane provenance is surfaced as `ValueSource.Kind : ValueSourceKind` (PD25 — `TemplateLiteral`/`TemplateBinding`/`TemplateResource`/`StyleSetter`/`StyleWhen`/…), a non-equality annotation.
- Metadata: per-type frozen tables; `PropertyEffects` flags = `AffectsMeasure | AffectsArrange | AffectsRender | AffectsComposite | AffectsParentMeasure | AffectsParentArrange | Inherits` (+ `BindsTwoWayByDefault`, `NotDataBindable`). **`AffectsComposite` is mandatory**: offset/opacity/clip-shaped properties route to CompositeParameters refresh (cached raster reused); only content/brush-shaped changes route to `Scene.Invalidate()` re-raster.
- `SetCurrentValue<T>` semantics (P3 graft, verbatim): replace the effective value in place without changing its source; no entry ⇒ behaves as Local.
- Also mandated: `IValueEvictionListener` (binding-death notification); box-interning cache for the untyped lane; fully specified untyped `SetValue(UIProperty, object?, BindingPriority)` + `GetValueSource` diagnostics + frame/local enumeration (XAML/serialization/DevTools need them); inheritance ships lazy-read/eager-notify v1 with push-down shared-box O(1) reads as the documented API-compatible upgrade (benchmark-gated); oracle-pinned precedence test matrix authored BEFORE the engine; `ValueFrame` conformance kit for the styling engine.
- Rejected: boxed-object storage; one-winner-per-priority; coercion-as-priority-slot (coercion happens inside effective-value computation); any `IObservable` surface; WPF's 10-bucket ladder (cut rungs recorded as re-addable).

## Fork B — Styling: hybrid (selector subset + `When` data-conditions)

- `Cursorial.UI.Style` (name collision with `Cursorial.Output.Style` resolved: UI keeps `Style`; framework source uses `using CellStyle = Cursorial.Output.Style;`). `Setter`, `Selector`, `Styles` collection, seal-on-attach.
- Selector grammar: type, `.class`, `#name`, `:pseudo-class`, child `>`, descendant (space), `/template/` hop, **selector lists (`,`) supported**. NO sibling/positional combinators (`:nth-child` etc.) — fenced out by the invalidation-graph razor.
- `When` (DataCondition collection, all-must-hold) gives per-rule DataTrigger power. Pinned semantics: unknown binding value ⇒ unmet; watcher lifetime = armed lifetime; DataContext change ⇒ rebind. Requires self-source and ancestor-source bindings from the binding engine (numbered requirement).
- One `Style` priority slot; packed `StyleSortKey` = [layer][names][classLike][types][scopeDepth][order]; **layer beats specificity** (documented deliberate divergence from WPF/Avalonia); cookie-based batch retraction.
- Pseudo-class state: `InteractionState` bitmask + `PseudoInterestMask` early-out; `PseudoClassMapping` for control-registered property→pseudo-class; interaction pseudo-classes writable only by framework services/control authors.
- **Template barrier** (§0-grade): rules never match elements with `TemplatedParent != null` except via `/template/`; engine skips such elements before candidate scan.
- Capability classes stamped on the visual root (`caps-truecolor|ansi256|ansi16|nocolor`, `caps-motion`, `caps-kitty-keyboard`, `caps-unicode|ascii`), re-stamped on `RenegotiateAsync`. Theme-variant flip = DynamicResource re-resolution only (no re-match).
- Template seam: `ITemplateContent` deferred-content interface + `TemplateInstance` with `Detach()` retraction contract (graft from wpf-triggers proposal §2.6) + debug subscription-leak tracker.
- Diagnostics-first: `StyleDiagnostics.Explain` renders every winning value's full sort-key derivation in one line (acceptance test); seal-time errors name (style, rule index, property); in-terminal style-inspector overlay demo.
- Styling/property engines NEVER touch Scene/CellBuffer (invariant); modal dimming = window manager sets `obscured` class on background windows; `:access-keys` pseudo-class on the root drives requirement-6 underscore visibility.
- Storyboard ignition vocabulary on activation/retraction edges: `BeginStoryboard`/`StopStoryboard` with `HandoffBehavior.SnapshotAndReplace` (named contract to the animation subsystem).

## Fork C — XAML: custom runtime loader (generator endgame planned, not first)

- Assemblies: `Cursorial.UI` (framework), `Cursorial.UI.Xaml` (loader), `Cursorial.UI.Xaml.Generator` (X4+: build-time validation, typed `x:Name` fields/`InitializeComponent`, generated `IXamlTypeMetadataProvider`, `CursorialXamlStrictAot` auto-set by `PublishAot`), plus a **netstandard2.0 parser frontend assembly from X0** shared by loader and generator.
- Pipeline: XmlReader → node graph → instantiator. Deferred content = parse-time-checked **node-graph slices** for ControlTemplate/DataTemplate/Style values + lazy resource-dictionary entries. Deferral is **type-contract-driven** (a property typed `ITemplateContent` defers; no `[DeferredContent]` attribute).
- Markup extensions: `{Binding}`, `{StaticResource}`, `{DynamicResource}`, `{TemplateBinding}` (parse-time restricted to template bodies), `{x:Static}`, `{x:Null}`, `{x:Type}`; positional-argument convention pinned at X0. Extension results attach via the **`IDeferredValue.AttachTo` seam** — no sentinel objects through `SetValue`.
- Compiled-binding descriptor contract designed NOW (implemented later): `Binding.Compiled<TSource,TValue>(static vm => vm.X)` — lambda is the sole path source; `x:DataType` enables build-time path diagnostics.
- Access-key literals (`Header="_File"`) folded at parse time into `AccessText(text, key, underscoreIndex)` — one data model, two producers (loader parse / generator fold).
- `ThemeVariant` keyed on negotiated `ColorDepth` + background-luminance dark/light; re-resolved on `RenegotiateAsync`, pulsing `ResourceDictionary.Changed`.
- Diagnostics: line/column everywhere; conformance corpus; Windows-only CI leg pinning escape/whitespace cases against real System.Xaml as oracle; hot reload (later) = "the terminal's designer".
- Rejected: vendoring Portable.Xaml/System.Xaml; source-gen-first (it is the X4/X5 endgame, with one semantic implementation in the library).

## Named invariants (every subsystem must hold)

1. **Frame coherence:** a property set during frame N's input drain is visible to frame N's layout and render; no dispatcher priority tiers exist.
2. **Styling/property systems never touch Scene/CellBuffer**; only `PropertyEffects` metadata drives invalidation, routed by the element tree.
3. **Re-composite vs re-raster:** offset/opacity/clip → `AffectsComposite` (CompositeParameters refresh); content/brush → `AffectsRender` (`Scene.Invalidate()`). Animated slides/fades must never re-raster.
4. **Retraction is store-owned:** deactivation = frame/cookie removal + promotion, never set-back.
5. **Template barrier** (Fork B above).
6. **Single UI thread:** one render/dispatch thread; input pump marshals to it; `UIObject` has thread affinity (debug-asserted); `VerifyAccess` in debug.
7. **Lower layers (Core/Rendering/Drawing/Animation) accept only additive changes** via opaque seams.

## Shared vocabulary (use these names verbatim)

**Naming convention (user-mandated 2026-06-10): the acronym "UI" is fully capitalized in type names — `UIElement`, `UIObject`, `UIProperty`, `UIPropertyKey<T>`, `UIApplication`, `UIDispatcher` — never `UIElement`/`UIObject`/`UIProperty`. Subsystem specs written before this date use `Ui*`; treat the spellings as equivalent (NOT a naming-drift finding) and apply the `UI*` spelling in all new text.**

`UIObject` (property host) → `UIElement` (tree/layout/render/input node) → `Control` (templated) → `Window`. `StyledProperty<T>`, `AttachedProperty<T>`, `DirectProperty<TOwner,T>`, `UIProperty`, `UIPropertyKey<T>`, `BindingPriority`, `ValueStore`, `ValueFrame`, `BindingEntry<T>`, `AnimatedValueHandle<T>`, `PropertyEffects`. `Style`, `Setter`, `Selector`, `When`/`DataCondition`, `StyleSortKey`, `InteractionState`, `PseudoClassMapping`, `ITemplateContent`, `TemplateInstance`. `AccessText`. Root namespace `Cursorial.UI`. Integer cell coordinates throughout layout (`Size`/`Rect` from Cursorial.Rendering where they fit; mind: `Rect` is ushort-backed, non-negative, `Translate` throws on negative results — signed math needs different carriers; negative placement is expressed via composite offsets).
