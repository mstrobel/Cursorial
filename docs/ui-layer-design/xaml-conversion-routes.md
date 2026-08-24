# XAML conversion routes — the parse-time route probe, generic converters, and the bridge rung

Status: **W2 design (accepted direction). W2a + W2b IMPLEMENTED** (TSA `FullName`/`IsAssignableFrom`;
CR5 generalized token resolution — the parser resolves every `UIProperty`-typed member's token and stamps
`XamlMember.ResolvedPropertyMember`, the loader assigns the identity, the emitter bakes the static
registration field; CR6/CR10 transitions reshape; CR8 single-child assignability incl. the self-list
`IsCollection` stamping + emitter self-fill both providers missed. Rows: XamlMatrix Section23 XB1–XB9,
TransitionsLoweringTests GB1–GB3, AnimationMatrix N160). **W2c/W2d/W2e/W3 IMPLEMENTED** (per-wave status blocks in §1a). Motivated by the animation
XAML-friendliness sweep (2026-08-24; findings recorded in `animation-matrix.md` §17 and the W1 commit) and
the design conversation pinned alongside it. W1 (converter wiring, content properties, fill accessors,
lane-parity fences) shipped separately; this document governs W2 (routes, `UIProperty` tokens, generic
converters, the bridge rung, the transitions API reshape) and sketches W3 (`x:TypeArguments`).

## 0. Pinned decisions (the conversation ledger)

- **CR1 — the route probe is the keystone.** The shared frontend parser probes each member's conversion
  mechanism ONCE and adorns `XamlMember` with a `ConversionRoute`; both lanes execute the same recorded
  decision. Lane parity becomes a construction property, not a discipline: precedence and ambiguity are
  decided in one place, and violations surface as parse-time diagnostics with line+col identically in both
  lanes (a CUR2xxx build error in the generator, the same `XamlParseException` at runtime). This closes the
  G4 blindness class (documents that Fatal at load building with zero warnings) structurally.
- **CR2 — route vocabulary**: `RegisteredConverter | AttributedConverter | GenericConverter | ImplicitOp |
  ExplicitOp | Constructor | ParseMethod | Contextual`. `Contextual` generalizes `SelectorConverter`'s
  `IsContextFree == false` into vocabulary (occupants: `Selector`, `UIProperty`, and whatever comes next).
- **CR3 — precedence**: registered converter > attributed converter (incl. the generic-converter
  convention) > implicit operator > explicit operator > constructor > static `Parse`. Between KINDS the
  order is fixed; WITHIN a kind, exactly one viable candidate is required — two viable routes of the same
  kind is a loud parse-time ambiguity error ("ambiguous conversion routes into `T`: add a converter"),
  never a silent pick. The bridge rungs (ops/ctor/Parse) are LAST so no existing conversion changes
  behavior.
- **CR4 — generic converters** (the AOT half): a converter type with type arguments MIRRORING the
  converted type's closes over them. For `Optional<T>` the LADDER is the registration (W2c realized —
  `Cursorial.UI` cannot reference the converter's assembly, so the attribute form is unrealizable there);
  the open-generic `[TypeConverter(typeof(C<>))]` closing convention for USER types is a W2e
  route-vocabulary target, not yet implemented in either backend. The BAKED lane emits the closed
  `new OptionalConverter<double>()` — statically-rooted, reflection-free, no DAM scaffolding; the runtime
  reflective lane closes via `MakeGenericType` (licensed — that lane is RUC and trimmed from strict
  publishes; value-type instantiations are exactly what NativeAOT cannot create at runtime). The typed
  `OptionalConverter<T>` routes its INNER conversion through the ladder (not `TypeDescriptor`) and
  retires the reflective `OptionalConverter` + the W1 interim ladder rung.
- **CR5 — `UIProperty` tokens resolve at PARSE, in the frontend.** The mechanism already exists: Setter's
  `Property` resolution (`TryResolveQualifiedSetterMember`) resolves `Owner.Member`-qualified tokens
  xmlns-aware and unqualified tokens against the lexical `TargetType`, stamping the resolved property
  identity onto the member record (reflection lane: the `UIProperty`; Roslyn lane: the symbol — the
  generator emits the static field reference directly, and an unresolvable name is a CUR2xxx build
  diagnostic). W2 GENERALIZES this from the Setter special case to every `UIProperty`-typed member:
  - **Owner-qualified** (`Property="UIElement.Opacity"`, `"Button.Background"`): always valid, resolved
    against the document xmlns — the only form valid in target-less positions (a standalone
    `Storyboard`/`TransitionCollection` resource).
  - **Unqualified** (`Property="Opacity"`): valid where a LEXICAL target type is in scope — a Setter's
    `TargetType`, a `ControlTemplate`'s `TargetType`, or the enclosing object element's type for an
    attached-collection child (`<Border><Transition.Transitions><DoubleTransition Property="Opacity">` —
    the parser knows the host element type). No lexical target ⇒ a positioned error naming the
    owner-qualified form; conversion stays eager and diagnosable — never WPF's resolve-at-`Begin`
    surprise.
- **CR6 — typed pipelines stay unboxed** (the owner constraint): markup-facing members are base-typed
  `UIProperty`; the generic subclass downcasts ONCE at seal/arm (`Property is StyledProperty<T>`) with a
  diagnostic naming the track/transition, the property, and the expected value type. The member is
  configuration metadata — the per-frame path (`AnimationInstance<T>` → `AnimatedValueHandle<T>.SetValue`)
  never touches it, so no boxing enters any hot path. Code-first authors keep compile-time widening
  (`Property = UIElement.OpacityProperty` still compiles).
- **CR7 — the bridge rung**: when no converter exists for member type `T`, probe a single-parameter route
  into `T` — implicit op, explicit op, ctor, or `static T Parse(string)` (the Avalonia sibling
  convention) — from a source type `S` the ladder can convert; parse the string as `S`, take the route.
  The generator emits typed code (the `S` expression for an implicit op — the C# compiler applies the
  conversion; the cast for explicit; `new T(...)` for ctor); the runtime lane executes reflectively.
  Landed loader-first (the semantic authority), with the emitter lowering the identical recorded route.
- **CR8 — parser single-child assignability**: a single property-element child whose type is ASSIGNABLE to
  a collection-typed member is `Object` (assign), not `Items` (fill) — the WPF rule; closes the
  `<Transition.Transitions><TransitionCollection/></…>` hole. A child assignable only as an ITEM keeps
  Items classification. (The parse-time decision needs `IXamlType` assignability — part of the TSA
  widening.)
- **CR9 — TSA widening** (serves CR1/CR4/CR8 and W3): `IXamlType` gains `TypeArguments`
  (`IReadOnlyList<IXamlType>`, empty for non-generic), `GenericTypeDefinitionName`, `IsAssignableFrom`,
  and the route-probe capability queries (public parameterless ctor, single-param ctors/ops/Parse into the
  type). Both backends answer from their native representations; the probe logic itself is shared
  netstandard2.0 code and cannot drift.
- **CR10 — transitions become markup-shaped**: `Transition`/`Transition<T>`/the sealed leaves gain public
  parameterless ctors; `Property` becomes an init-settable `UIProperty?` on the base (CR6 validation at
  arm); `Duration`/`Delay`/`Easing` stay `init` (the reflection lane sets init accessors via `SetValue`;
  the generator emits object initializers). The primary ctors REMAIN as the code-first fast path.
- **CR11 — folding rider**: a route whose source conversion is context-free folds at parse into the SoA
  document (the inner constant for `Optional<T>`, the resolved property identity for `UIProperty` tokens)
  — cached documents re-instantiate with zero string parsing; the emitter emits literals.

## 1. Sequencing

- **W2a** — TSA widening (CR9). Prerequisite for everything below.
- **W2b** — transitions reshape + generalized `UIProperty` token resolution (CR5/CR6/CR10) + single-child
  assignability (CR8). Unlocks the sweep's remaining blockers end-to-end (`<DoubleTransition
  Property="Opacity" Duration="0:0:0.1"/>` in both lanes; the child-bearing `Transition.Transitions` fill
  rows land here).
- **W2c** — generic converters (CR4): DONE — the typed `OptionalConverter<T>` (Cursorial.UI.Xaml; the
  LADDER is its registration — the attribute convention proper joins W2e route vocabulary, since
  Cursorial.UI cannot reference the converter assembly), the ladder closes it reflectively in the RUC
  lane, the emitter bakes the closed form, and the reflective OptionalConverter + its DAM scaffolding
  are retired from Optional<T>.
- **W2d** — the bridge rung (CR7): DONE — ConversionBridge as the loader's LAST fallback (implicit >
  explicit > ctor > Parse; exactly-one-viable-per-kind else the loud ambiguity error; registered
  converters keep precedence); the emitted __ConvertXamlValue helper CHAINS the same probe at runtime —
  parity by construction (typed emission + folding join W2e). Rows XC1-XC6 + GC1.
- **W2e** — the route probe itself (CR1/CR2/CR3/CR11): `ConversionRoute` on `XamlMember`, the existing
  special cases (Setter.Property, Selector, the ladder's own dispatch) re-expressed as routes, parse-time
  convertibility diagnostics in both lanes.
- **W3** — `x:TypeArguments` (XAML 2009 semantics: legal on any object element; System.Xaml `XamlTypeName`
  grammar with parenthesized nesting and backtick-arity resolution, oracle-pinned on Windows CI; `x:`
  intrinsic type tokens). Cursorial extensions marked separately: array/nullable shorthand in
  type-argument position, generic curly markup extensions, build-time constraint diagnostics.
  Type-argument INFERENCE is a severable phase 2. Generic `x:Class` deferred. The keyframe markup story
  (`Keyframe<T>` — the sweep's largest hole) is W3's flagship consumer.

## 1a. W2e implementation plan (pinned before code, 2026-08-24)

The probe's home is `XamlType`/`XamlMember` construction time (NOT parse time per se — the route is a
property of the MEMBER, cached with it): when a metadata provider builds a `XamlMember`, the shared
frontend computes its `ConversionRoute` once from backend-answered capability queries.

- **`ConversionRoute`** (frontend, a small readonly struct): `Kind` (the CR2 vocabulary) + `SourceType`
  (`IXamlType?` — the bridge route's S) + `RouteMemberName` (ctor/op/Parse disambiguation for the
  emitter) + `IsContextFree`. Stored as `XamlMember.Route`.
- **TSA capability queries** (CR9's remainder): `IXamlType` gains `TypeArguments`
  (`IReadOnlyList<IXamlType>`), `HasPublicParameterlessConstructor`, and
  `GetConversionRouteCandidates(kind)` — backend-enumerated single-param routes (reflection walks
  methods/ctors; Roslyn walks symbols). The PROBE (precedence, one-viable-per-kind, denials) is shared
  netstandard2.0 code over those answers — it cannot drift between lanes.
- **Existing systems re-expressed, not rewritten**: the ladder's curated rows become
  `RegisteredConverter` routes (the provider consults `XamlConverters`-equivalent registries it already
  owns); `Selector`/`UIProperty` become `Contextual` (CR5's `ResolvedPropertyMember` machinery is
  unchanged — the route only NAMES the mechanism); the BCL attribute is `AttributedConverter`; the W2d
  runtime bridge STAYS as the loader's execution of `ImplicitOp/ExplicitOp/Constructor/ParseMethod`
  routes (its probe is then downgraded to a consistency assert against the recorded route).
- **The G4 close**: the parser, seeing a Text value on a member whose route is `None` (no mechanism),
  reports a positioned CUR2402-class "no conversion route to `T`" in BOTH lanes at parse — today that
  document builds silently and dies at load. The emitter's `__ConvertXamlValue` raw-string fallthrough
  becomes unreachable for diagnosed members.
- **Per-type opt-out** replaces the deny-list: `[NoConversionBridge]`-shaped metadata (a
  `Cursorial.Markup` attribute both backends read) supersedes the hardcoded `Style`/array denials
  (arrays stay denied structurally — the pseudo-ctor is never a conversion).
- **The open-generic attribute closing** (CR4's remainder): `[TypeConverter(typeof(C<>))]` on a USER
  generic type closes with the converted type's arguments — resolvable in both backends via
  `TypeArguments`; the baked lane emits the closed form (the `OptionalConverter<T>` emission
  generalized), the reflective lane `MakeGenericType`s.
- **CR11 folding**: `Route.IsContextFree` drives the parse-time fold uniformly (bridged values fold too
  — today they never do); the fold executes the ROUTE, not a converter lookup.
- Sequenced as: routes + queries + `None` diagnostics first (additive), then the special-case
  re-expressions one at a time, each behind the full gate suite + drift tests, then the audit.

**W2e status (2026-08-24):** the vocabulary, both backends' candidate queries, the shared `RouteProbe`
(with the Style denial IN the probe — the audited rule: the recorded route and the executing bridge are
one decision, pinned by the XC11 consistency assert), `XamlMember.Route` stamping in the reflection
provider (None/Ambiguous routes deliberately un-memoized so a late `Register` heals them), and the
CUR2402 parse diagnostics (direct attributes + Setter.Value, fold-first, extensions exempt) are
IMPLEMENTED and audited (10 findings fixed). **Recorded deferrals:** the symbol-lane / emitted-provider
route stamp (CUR2402 fires in the reflection lane only until the generator's converter knowledge becomes
queryable metadata — the emitted provider could stamp cheaply once `MetadataProviderEmitter` learns the
probe); the `[NoConversionBridge]` per-type opt-out generalizing the Style denial; `RouteMemberName` +
`Route.IsContextFree` + CR11 folding; downgrading `ConversionBridge`'s own probe to a consistency assert.

**W3 status (2026-08-24):** `x:TypeArguments` is IMPLEMENTED in both lanes and audited (14 findings
fixed, commit b0148891): the `XamlTypeName` grammar (2009 core + the Cursorial `?`/`[]` suffixes), the
`ParseElement` pre-scan (the element resolves CLOSED before attributes parse), `QualifiedTypeName` +
`IXamlGenericTypeProvider` with reflection (`MakeGenericType`, RUC lane) and Roslyn (`Construct()` with
pre-validated constraints — `SatisfiesConstraints`) backends, member SUBSTITUTION feeding the W2 route
machinery, and the closed `new T<args>()` emission in full lowering. Keyframe markup
(`<Keyframe x:TypeArguments="x:Double">`) is the flagship consumer (XT11). Rows: Section26 XT1–XT15,
TypeArgumentsLoweringTests GT1–GT6; matrix X28 superseded. **Recorded deferrals:** the emitted-provider
generics leg — `ClosedTypeSet` does not collect closed-generic element types and `MetadataProviderEmitter`
does not implement `IXamlGenericTypeProvider`, so a DEFAULT-lane x:Class document with a closed generic
element is fenced with a build-failing CURG3002 (GT6) rather than dying at runtime; lifting the fence
means teaching the emitter to enumerate closed constructions and emit their activators. The System.Xaml
oracle leg (Windows CI) pins the 2009 parenthesized/backtick resolution rules against real System.Xaml.

## 2. Non-goals

- No accept-path change to pending-edit semantics (binding-matrix §17's cancel-semantics note — separate
  concern).
- No runtime PropertyPath-style late binding for `UIProperty` tokens (CR5's eager rule is deliberate;
  `TargetPath` remains the late-bound track-targeting form).
- No WPF `TypeConverter` attribute compatibility beyond what exists — the BCL attribute rung stays the
  last-resort fallback it is today.
