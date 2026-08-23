# Consolidation Design — Single Value-Emission Funnel for `LoweringEmitter.cs`

Author goal (verbatim): *"With a few specific exceptions (bindings on UIProperty only; DynamicResource on UIElement UIProperty only), the same code should be emitting pretty much anything anywhere."*

All line numbers reference `/Users/mike.strobel/Workspace/Cursorial/.claude/worktrees/emitter-consolidation/Cursorial.UI.Xaml.Generator/LoweringEmitter.cs` (`LE`) and `…/Cursorial.UI.Xaml.Frontend/Parsing/XamlParser.cs` (`XP`) unless noted.

---

## 0. The core insight the audits converge on

The matrix's divergences (D1–D9) are **not** twelve different problems. They are three orthogonal knobs that each position sets bespokely today:

| Knob | What varies | Divergences it explains |
|---|---|---|
| **Delivery** — does the slot *install* a live producer, or does it want a *value expression*? | `Assign` (scalar member, collection add, template member) vs `Value` (setter, dict entry, DataCondition, converter/custom arg, BasedOn) | D2 (`DynamicResource` install vs carrier), D3 (`Binding` install vs descriptor), D4 (`x:Reference` record vs fence) |
| **Text typing** — how a raw `Text` becomes the slot's type | cast / object / verbatim / key-form | D1 (four Text rules) |
| **Extension target** — what the extension's `ProvideValue` services see as target | real `varExpr` vs `null` | D8 (target-less custom) |

Everything else in the matrix is *the same code that simply isn't being reused*. The design makes Delivery + TextPolicy + ExtensionTarget explicit fields on a `ValueSlot`, funnels all thirteen positions through one `EmitValue`, and leaves exactly the two exception constraints as the only kind×slot special-cases — precisely the author's "few specific exceptions."

---

## 1. The core funnel — `EmitValue`

### 1.1 The slot descriptor

```csharp
internal enum Delivery { Assign, Value }
// Assign: a live member / collection add / template member — may INSTALL a producer.
// Value:  a pure expression slot (setter, dict entry, DataCondition, arg, BasedOn) — must reduce to one C# expr.

internal enum TextPolicy { ConverterCast, ConverterObject, Verbatim, KeyForm }
// D1's four Text rules, chosen by the slot rather than by the position's bespoke function.

internal readonly struct ValueSlot
{
    public Delivery      Delivery;
    public string        TargetVar;      // "__e" / childVar for Assign; "" for pure-expression Value slots
    public XamlMember?   Member;         // null for a standalone entry / collection item / nested arg
    public ITypeSymbol?  SlotType;       // slot value type — drives Text cast + custom-ext Coerce
    public TextPolicy    TextPolicy;
    public INamedTypeSymbol? DataType;   // compiled-binding source type in scope
    public bool          ExtensionTargetIsSelf;  // custom ProvideValue target: true→TargetVar, false→null (D8)
    public bool          StaticResourceMustResolve; // converter-arg's RequireConverter contract (D5)
}
```

### 1.2 The one funnel

```csharp
// The single place a XAML value becomes C#. Owns element-form unwrap, nested-extension recursion,
// and (via the caller's reference frame) forward-reference recording. Returns:
//   Expr!=null  → a value expression the caller/position places (both deliveries),
//   Installed   → statements were already emitted into slot.TargetVar (Assign only),
//   Fenced      → a diagnostic was emitted; the caller degrades (null placeholder / DropStyle).
internal readonly struct Emitted { public string? Expr; public bool Installed; public bool Fenced;
    public static readonly Emitted Done = new() { Installed = true };
    public static readonly Emitted Fence = new() { Fenced = true };
    public static Emitted Value(string e) => new() { Expr = e }; }

private static Emitted EmitValue(Context c, in ValueSlot slot, in MemberRecord value)
{
    switch (value.Kind)
    {
        // (a) Element-form ⇔ curly convergence: an Object flagged IsMarkupExtension is a wrapper.
        //     Unwrap to its single Extension/Folded member and re-enter — every position inherits
        //     element-form support for free (replaces MarkupExtensionEntryExpr LE:2732 +
        //     EmitMarkupExtensionMember LE:2777 + the EmitObject bypass LE:1883-1904).
        case XamlValueKind.Object when c.Doc.Objects[value.ValueIndex].HasFlag(ObjectFlags.IsMarkupExtension):
            return EmitValue(c, in slot, ExtensionValueMember(c, in c.Doc.Objects[value.ValueIndex]));

        case XamlValueKind.Text:     return EmitText(c, in slot, c.Doc.Strings[value.ValueIndex]);
        case XamlValueKind.Folded:   return FoldedValue(c, in slot, c.Doc.Constants[value.ValueIndex]);
        case XamlValueKind.Extension:return EmitExtension(c, in slot, in c.Doc.Extensions[value.ValueIndex]);
        case XamlValueKind.Object:   return EmitObjectValue(c, in slot, value.ValueIndex);
        case XamlValueKind.Items:    return EmitCollection(c, in slot, in value);   // Assign only
        case XamlValueKind.Deferred: return EmitDeferred(c, in slot, value.ValueIndex); // owns a ref frame
        default: c.Todo($"value kind {value.Kind} not lowered"); return Emitted.Fence;
    }
}
```

**Dispatch table (replaces the EmitObject switch `LE:2034-2143`):**

| Kind | Handler | Replaces |
|---|---|---|
| `Text` | `EmitText` (TextPolicy-driven) | `EmitScalarAssign`/`ScalarConvertExpr` `LE:2479/2517`, `SetterTextValueExpr` `LE:1507`, DataCondition verbatim `LE:1438`, `CustomExtensionArgValue` text `LE:3025` |
| `Folded` | `FoldedValue` | `EmitFoldedAssign`/`FoldedValueExpr` `LE:2691/2715` |
| `Extension` | `EmitExtension` (delivery-aware) | `EmitExtensionAssign` `LE:2786`, `SetterExtensionValueExpr` `LE:1485`, `ResourceValueExpr` `LE:1526`, `MarkupExtensionEntryExpr` `LE:2732` |
| `Object` | `EmitObjectValue` | recurse+`EmitChildAssign` `LE:2062-2072`, inline `EmitObjectToLocal` `LE:1436/1457` |
| `Items` | `EmitCollection` | Items loop `LE:2084-2091` |
| `Deferred` | `EmitDeferred` | Deferred arm `LE:2099-2124` → `EmitTemplateFactory` |

### 1.3 `EmitExtension` — the delivery-aware per-kind core (replaces `EmitExtensionAssign` `LE:2786`)

```csharp
private static Emitted EmitExtension(Context c, in ValueSlot slot, in ExtensionRecord ext) => ext.Kind switch
{
    ExtensionKind.StaticResource  => StaticResource(c, in slot, in ext),   // one impl, both deliveries (kills D5)
    ExtensionKind.DynamicResource => DynamicResource(c, in slot, in ext),  // Assign+UI→install; else carrier (D2)
    ExtensionKind.Binding         => Binding(c, in slot, in ext),          // Assign→install; Value→descriptor (D3)
    ExtensionKind.TemplateBinding => TemplateBinding(c, in slot, in ext),  // Assign→install; Value→fence
    ExtensionKind.Reference       => Reference(c, in slot, in ext),        // Assign→record; Value→fence (D4)
    ExtensionKind.Custom          => Custom(c, in slot, in ext),           // target = slot.ExtensionTargetIsSelf (D8)
    _ => Fence(c, $"extension {ext.Kind} not lowered"),
};
```

- **`StaticResource`** returns the *same* expression regardless of delivery — a same-dict var (`ResolveVisibleResourceVar` `LE:1543`) else `ExternalStaticResolveExpr`; if `slot.StaticResourceMustResolve` it wraps in `RequireConverter`-style throw-on-miss. **This collapses D5 into one flag.** `Assign` places it as `TargetVar.Prop = expr`; `Value` returns it.
- **`Binding`** consults `slot.Delivery`: `Assign` → `EmitBinding` install (`LE:3372`) returning `Installed`; `Value` → `ReflectiveBindingExpr` descriptor (`LE:4358`) returning `Value`. **One arm, both of D3's live outcomes.** The compiled-binding exception (§3) gates here.
- **`DynamicResource`**: `Assign` **and** the exception holds → `SetResourceReference` (`LE:3195`) returning `Installed`; otherwise the `ResourceReference(key)` carrier expression (`LE:1530`). **Collapses D2** and folds the exception in-line.
- **`Reference`**: `Assign` → `c.References.Add(...)` against **`c.CurrentNameScopeExpr`** (see §4/GAP-A), guarded by `ClrSetBlocked` (fixes the CS8852 secondary note in the references audit); `Value` → fence. **Collapses D4.**

### 1.4 How the funnel OWNS forward-references and nested recursion

**Nested extensions (arbitrary depth).** Any arm carrying a nested node (Binding `Converter=`/`Source=`, custom arg, resource key) builds a child slot and calls **back into the funnel** through one carrier:

```csharp
// The recursion carrier — a nested markup-extension argument is just a Value-delivery slot.
private static string? NestedValueExpr(Context c, ITypeSymbol? argType, MarkupExtensionNode node, bool mustResolve)
    => EmitValue(c, new ValueSlot { Delivery = Delivery.Value, SlotType = argType,
                                    TextPolicy = TextPolicy.ConverterCast,
                                    StaticResourceMustResolve = mustResolve },
                 SyntheticMember(node)).Expr;
```

Because the carrier re-enters `EmitValue`, `{Binding Converter={StaticResource {x:Type T}}}` recurses funnel→Binding→NestedValueExpr→StaticResource→NestedValueExpr→x:Type with no per-level bespoke code. This generalizes the only deep-recursion site today, `CustomExtensionArgValue` `LE:3006`, to every extension. (Honest constraint carried forward from the extensions audit §4.2: *built-in outer* extensions still only expose the specific nested slots the loader defines — Converter/Source/key — so `{StaticResource {StaticResource X}}` remains unsupported by **semantics**, not by emitter shape.)

**Forward references.** `EmitReference` and the three flush sites stop hardcoding scope strings. A single context field is the funnel's authority:

```csharp
public string CurrentNameScopeExpr = "__scope";   // pushed to "__ctx.NameScope" by EmitDeferred/EmitTemplateFactory
```

`EmitDeferred` and every top-level driver open a **reference frame** (`savedRefs`/`savedScopeLines`), recurse, and flush against `c.CurrentNameScopeExpr` — so every caller inherits correct deferral automatically, and the deferred-dict closure (`LE:670-682`) and RD builder (§4) stop being special.

---

## 2. Thin position adapters (each ~a few lines over the core)

Each adapter builds a `ValueSlot` and calls `EmitValue`. The bespoke function it deletes is named.

| Adapter | Slot it builds | Adds over core | Replaces |
|---|---|---|---|
| **member-assign** | `Delivery.Assign, TargetVar=varExpr, Member=xm, SlotType=xm.ValueType, TextPolicy=ConverterCast, ExtensionTargetIsSelf=true` | nothing — the loop arm becomes `EmitValue(c, slot, member)` | `EmitObject` arms `LE:2036-2138`, `EmitScalarAssign` `2479`, `EmitFoldedAssign` `2691`, `EmitExtensionAssign` `2786`, `EmitMarkupExtensionMember` `2777` |
| **collection-add** | same as member-assign but `Assign` writes via `.Add` | build local, `EmitChildAssign(single:false)` on the returned expr | Items loop `LE:2084-2091` |
| **dict-key** | *(key, not value)* — thin key resolver over `ResolveIntrinsicExpr` (§5-type-audit) | canonicalization flag | `ResourceKeyExpr` `LE:744`, `ResourceKeyArgExpr` `LE:1556` (intrinsic arms) |
| **dict-entry-value** | `Delivery.Value, ExtensionTargetIsSelf=false` | bind to entry local / lazy closure; open ref frame | `EmitDictionaryEntry` value part `LE:613-688`, `MarkupExtensionEntryExpr` `2732` |
| **setter-value** | `Delivery.Value, SlotType=propValueType, TextPolicy=ConverterObject, ExtensionTargetIsSelf=false` | assign to `Setter.Value` | `SetterValueExpr` `1471`, `SetterExtensionValueExpr` `1485` |
| **datacondition-value** | `Delivery.Value, TextPolicy=Verbatim` | assign to `.Value` | `DataConditionValueExpr` `1434` |
| **datatemplate-datatype** | *(type slot)* — `ResolveIntrinsicExpr(Type, token)` | needs frontend x:Type-token fix (type-key audit) | `DataTemplateDataType` folded arm `LE:810-816` |
| **converter-arg** | `Delivery.Value, SlotType=IValueConverter, StaticResourceMustResolve=true` | none | `ConverterInit` nested `LE:4732-4770` |
| **custom-ext-arg** | `Delivery.Value, SlotType=argType, TextPolicy=ConverterCast` | recurse via `NestedValueExpr` | `CustomExtensionArgValue` `LE:2975` |
| **template-body** | `Delivery.Assign` + Deferred kind | `EmitDeferred` opens ref frame, sets `CurrentNameScopeExpr` | Deferred arm `LE:2099-2124` |
| **basedon** | `Delivery.Value, SlotType=Style` | cast to `Style` | `BasedOnExpr` `LE:1595` |
| **init-only member** | `Delivery.Value` (initializer expr) | collect into `new T{…}` | `ScanInitOnlyMembers`/`InitOnlyExtensionExpr` `LE:2373-2445` — becomes "Value delivery, no install" and D7 disappears (full kind set, install kinds naturally fence) |

`EmitStyle` (`LE:863`), `EmitSetter` (`LE:1219`), `EmitDataCondition` (`LE:1335`), `EmitResourceDictionaryBody` (`LE:208`) keep their *structural* shape but their value reads route through the adapters instead of the bespoke `*ValueExpr` helpers.

---

## 3. The two exception constraints — enforced inside the funnel

Both live as guards **inside `EmitExtension`'s arms**, not as separate code paths, so no position can forget them and none can double-enforce.

**(a) Compiled-binding-only-on-UIProperty.** In `Binding` (`EmitExtension` arm):
```csharp
if (RegisteredOwner(slot.Member) is not { } owner)      // the ONE gate (was scattered: LE:3375)
    return Fence(c, $"{{Binding}} target '{slot.Member?.Name}' is not a bindable UIProperty");
```
Applies uniformly to compiled and reflective (audit §5 point 5). Value-delivery descriptors pass through the same gate, so a Setter targeting a non-UIProperty fences identically to a live member.

**(b) DynamicResource-only-on-UIElement-UIProperty.** In `DynamicResource`, gating only the **install** branch:
```csharp
bool canInstall = slot.Delivery == Delivery.Assign
               && XamlDataTypeScope.IsUIElement(OwnerTypeOf(slot))   // was LE:3197
               && IsNonDirectStyledProperty(slot.Member);            // was LE:3203
return canInstall ? DynamicResourceInstall(c, slot, ext)            // SetResourceReference
                  : DynamicResourceCarrier(c, slot, ext);           // new ResourceReference(key)
```
Non-UI or `Value` delivery degrades to the carrier (which is exactly what Setter.Value/entry positions already do) — so D2 stops being "handled in three positions, fenced in one": the *carrier* is always available; only the *live install* carries the constraint.

These two `if`s are the entire "few specific exceptions." Every other kind×slot cell is uniform.

---

## 4. Each scenario mapped onto the funnel

- **Deferred resolution in EVERY value position.** `EmitDeferred` and each driver open a reference frame and set `c.CurrentNameScopeExpr`. `Reference`/`Binding-ElementName` record against that field. **Fixes GAP-A** (`LE:679` hardcodes `"__scope"` — replaced by `c.CurrentNameScopeExpr`, which is `__ctx.NameScope` inside a template factory). **Fixes GAP-B** by making `EmitResourceDictionaryBuilder` (`LE:142-204`) a driver that flushes its frame at the end, exactly as `EmitCodeBehind` (`LE:70`) does. Forward `{StaticResource}` dict-deferral (`EntriesNeedDeferral` `LE:509`) is unchanged — the lazy closure is just an `Assign` slot inside a frame.
- **Nested markup extensions, arbitrarily deep.** `NestedValueExpr` re-enters `EmitValue` per level (§1.4). `{Binding Converter={StaticResource {x:Type T}}}` composes with zero per-level code.
- **StaticResource on ANY writeable property, incl. nested.** `StaticResource` returns a plain assignable expression; the non-UIElement over-fence (`LE:3243`) is **removed** — the gate only ever guarded the `FindResource`/`SetResourceReference` receiver, which the eager var/`ResolveStatic` form doesn't use. Nested `Source={StaticResource external}` gains the `ExternalStaticResolveExpr` fallback for free (the asymmetry at `LE:4435` vanishes because Source uses `NestedValueExpr`→`StaticResource`, same as Converter).
- **x:Static almost anywhere.** Already `Folded`; `FoldedValue` handles it in every position uniformly. The one gap — curly `Setter Value="{x:Static M}"` hard-erroring at `XP:2030` while element form works — is a **frontend** fix (route `ClassifySetterValueExtension`'s intrinsic cases through `BuildExtensionValue` like everything else); the funnel already accepts it.
- **Element-form extensions in ANY position.** The `IsMarkupExtension` unwrap at the *top* of `EmitValue` (§1.2a) makes element and curly converge before dispatch — no position needs its own unwrap. Standalone `<Binding>`/`<TemplateBinding>` still `Error` (loader-rejected) because `TemplateBinding`/`Binding` in `Value` delivery fence, and `EmitObject`'s standalone driver upgrades that fence to `Error` at `LE:1897` — preserved.

---

## 5. `{x:Boolean True}` / `{x:Int32 4096}` — frontend-only

Per the frontend audit §4, this is **zero new emitter path**. The parser produces the *identical* node the element form yields today: a plain `ObjectRecord(TypeId=primitive, flags=None)` + one synthetic `MemberRecord(-1, Text, →raw)`.

1. `NodeEnums.cs` (after `Reference`): add transient `ExtensionKind.Primitive` — a parser routing token, **never written to an `ExtensionRecord`**.
2. `ClassifyExtension` (`XP:1435`): before `_ => Custom`, match the 17 XAML2009 `x:`-prefixed primitive names (mirror `BuiltInPrimitiveLocalNames` `LE:2157` / `XamlSchemaContext.BuiltInTypeNames`) → `Primitive`.
3. `BuildExtensionValue` (`XP:1137`): change out-contract to `out XamlValueKind valueKind` (existing arms set `Folded`/`Extension` mechanically) and add a `Primitive` case that `ReserveObject`/`AddMember(-1,Text,raw)`/`SetObject(primitive)` and returns `valueKind=Object`. Nested positional → CUR diagnostic.
4. Callers: `ParseAndFoldExtension` (`XP:1089`) and `ParseExtensionElement` (`XP:1296`) add the `Object` member from `valueIndex`. `ClassifySetterValueExtension` (`XP:1946`) gains a `Primitive` case building the same synthetic object (so `Value="{x:Boolean True}"` works).
5. Layout invariant holds (frontend audit "Layout-invariant note"): the reserved leaf lands at `objectIndex+1`, inside the parent's `subtreeLength`.

In the emitter, `EmitValue`'s `Object` arm sees a **non**-`IsMarkupExtension` primitive object → `EmitObjectValue` → `EmitObject` → `IsBuiltInPrimitive` → `EmitInitTextPrimitive` (`LE:2208`). Conversion stays at emit time. **No new node rep, no new emitter branch.**

---

## 6. Incremental, test-guarded migration (no big-bang)

Baseline gate at every step: **257 generator + 524 runtime**. Each step adds *named cross-position tests* and ships independently. Order is strictly lowest-risk-first; the funnel begins as a **shim delegating to today's functions**, so early steps are provably behavior-neutral.

| # | Step | Behavior change? | New tests | Risk |
|---|---|---|---|---|
| 1 | Introduce `ValueSlot`/`Emitted`/`EmitValue` as a **shim** delegating to existing `Emit*Assign`/`*ValueExpr`. Wire nothing. | none | compile-only | ~0 |
| 2 | Route the **member-assign** loop (`LE:2034-2143`) through `EmitValue`. Arms move behind the funnel unchanged. | none | golden-file diff = empty | low |
| 3 | Extract `ResolveIntrinsicExpr` (type-key audit) and adopt in keys/nested-keys/converter/RelativeSource/custom-args. Pure consolidation. | none | intrinsic parity | low |
| 4 | Fold **element-form unwrap** into `EmitValue` (delete `MarkupExtensionEntryExpr`/`EmitMarkupExtensionMember`; `EmitObject` bypass calls the funnel). | none (same output) | `element-form-equals-curly` per kind × position | low |
| 5 | **collection-add** + **init-only** adapters. D7 collapses (init-only = Value delivery, install kinds fence naturally). | equivalent | `init-only-full-kind-set` | med |
| 6 | **setter-value** adapter (+ frontend: route `ClassifySetterValueExtension` intrinsics through `BuildExtensionValue`). Unlocks curly `{x:Static}`/`{x:Type}` in setters. | **yes** (unlock) | `setter-xstatic-curly`, `setter-lane-parity` | med |
| 7 | **datacondition-value** adapter. | none | `datacondition-crosskind` | low |
| 8 | **dict-entry-value** adapter (open ref frame). | none | `dict-entry-crosskind` | med |
| 9 | **converter-arg** + **custom-ext-arg** via `NestedValueExpr`; enable deep nesting + Source external fallback. | **yes** (unlock external Source) | `nested-depth`, `source-external-staticresource` | med |
| 10 | Remove non-UIElement `{StaticResource}` over-fence (`LE:3243`); fold DynamicResource exception into the arm (§3). | **yes** (unlock) | `staticresource-nonuielement`, `dynamicresource-exception-matrix` | med-high |
| 11 | **Reference-lane** fixes: `c.CurrentNameScopeExpr` frame (**GAP-A**), RD-builder flush (**GAP-B**), `EmitReference` `ClrSetBlocked` guard. | **yes** (bug fix) | `xreference-deferred-in-template`, `rd-builder-compiled-binding-flush`, `xreference-initonly-fence` | high |
| 12 | Frontend `XamlTypeReference` token placeholder (`XP:1485`) + `FoldedValue` arm → un-drops `{x:Type}` in member/entry/setter/DataCondition/DataType (A–F). | **yes** (bug fix: silent-null → typeof) | `xtype-member-value`, `datatemplate-datatype-folded` | high |
| 13 | `{x:Boolean True}`/`{x:Int32 4096}` frontend feature (§5). | **yes** (new) | `primitive-curly-equals-element` × 17 types, setter/collection/dict positions | med |

Steps 1–5 are pure refactors (green on the existing 781 tests, no new behavior). 6–13 each carry a named behavior contract and ship one at a time; any can be reverted without touching the funnel.

---

## 7. Risks and the invariant matrix a consolidation test must pin

### Risks
- **Install-vs-value regressions (D2/D3/D4).** A slot mislabeled `Assign` would install where a descriptor is required (setter would silently gain a live install). *Mitigation:* lane-parity tests (emitter output vs loader semantics) at steps 6/8.
- **Init-only CS8852.** `Assign`-install kinds on init-only slots must fence, not emit post-construction sets. *Mitigation:* `EmitReference` gains the missing `ClrSetBlocked` guard (step 11); `init-only-full-kind-set` test.
- **Deferred scope-expr correctness.** `c.CurrentNameScopeExpr` must be pushed/popped exactly around template factories and deferred closures — a leaked frame bakes the wrong scope. *Mitigation:* the two GAP tests + an assertion that the frame stack is balanced at driver exit.
- **Text-typing drift (D1).** Consolidating four Text rules under `TextPolicy` risks converting where verbatim was intended (DataCondition) or casting where object was (setter). *Mitigation:* keep the four policies explicit; per-position Text golden tests.
- **`{x:Type}` folding change (step 12).** The token-placeholder must resolve identically to the raw-key path; regression risk in the shared resolver. *Mitigation:* `xtype-member-value` asserts `typeof(T)` in all of A–F and equality with the key-position output.

### Invariants the matrix must pin (every value kind × every position)
1. **Uniformity except the two exceptions.** For each of the 13 positions × 11 value kinds, the emitted C# is either (a) *identical in shape* to the canonical member-assign output for that kind, or (b) the documented fence/error — and the **only** two cells allowed to diverge on a kind×slot basis are compiled-`Binding` (non-UIProperty → fence) and live-`DynamicResource` (non-UIElement-UIProperty → carrier, not install). Any third divergence fails the invariant.
2. **Element-form ≡ curly-form**, per kind per position (step 4 test, permanent).
3. **Emitter ≡ loader lane parity**: whatever the runtime `XamlObjectGraphBuilder` accepts/rejects for a (kind, position), the emitter emits/fences the same — no "AOT silently drops what reflection loads."
4. **Never-silent-drop preserved**: every unhandled value cell emits `Todo`/`Error`; the only no-marker skips remain the intended directives/`MemberId<0` set.
5. **Deferred correctness**: every position that can defer (member, template body, dict entry, both inside and outside a template) resolves `x:Reference`/`ElementName` against the correct scope; the standalone-RD builder flushes its lanes (GAP-B closed).
6. **Two-exception minimality as an executable test**: a parameterized `[Theory]` over the full kind×position grid whose oracle is "canonical output OR documented fence," with exactly two `[InlineData]` exemptions — so any future position that reintroduces a bespoke divergence turns the grid red.

The test matrix is thus a single `Theory(kind, position) → {Handled(canonical) | Fenced(diag) | Error(diag)}` grid of ~140 cells; consolidation is "done" when every cell is green under one funnel and only the two exemption cells carry a kind×slot special-case.

---

**Files this design touches:** `LoweringEmitter.cs` (funnel + adapters, replacing the bespoke `Emit*Assign`/`*ValueExpr`/`MarkupExtensionEntryExpr` sites cited throughout §1–§4); `XamlParser.cs` (`ClassifyExtension` `1435`, `BuildExtensionValue` `1137`, `ClassifySetterValueExtension` `1946`, `TryFoldIntrinsicExtension` `1472` for the `XamlTypeReference` twin); `NodeEnums.cs` (`ExtensionKind.Primitive`); a new `Cursorial.UI.Xaml.Frontend/MarkupExtensions/XamlTypeReference.cs` alongside the existing `XamlStaticReference.cs`.

---

## 8. Progress log

Baseline at fork (`feature/cursorial-cli` @ `dd3ded0f`): 257 generator + 524 runtime. Current: **263 generator + 530 runtime**, full solution builds clean (0 errors).

**Landed (committed on `feature/emitter-consolidation`):**

- **Step 1–2** (`9e7bbf09`) — `ValueSlot`/`Emitted`/`Delivery`/`TextPolicy`/`EmitValue` funnel; the **member-assign** loop routed through it (Assign delivery fully funneled, delegating to `Emit*Assign`).
- **Step 4** (`fd20d8ca`) — element-form ⇔ curly convergence for Assign (the `IsMarkupExtension` unwrap at the top of `EmitValue`).
- **Step 6 emitter** (`29591187`) — `EmitSetter` value routed through `EmitValue` (Value delivery), deleting `SetterValueExpr`.
- **`8a80a31c`** — `BuildExtensionValue` out-contract `folded`→`valueKind` (frontend prep for primitives).
- **Step 13 core** (`e4f74228`) — `{x:Boolean True}`/`{x:Int32 4096}` built-in primitives as single-positional-arg markup extensions (frontend `TryBuildPrimitiveObject` → the SAME synthetic Object the element form yields; zero new emitter path).
- **Step 12** (`86e5eb5c`) — **`{x:Type}` un-drop**: frontend folds to a `XamlTypeReference` token (twin of `XamlStaticReference`), resolved per-lane (loader→`System.Type`, generator→`typeof`), fixing the symbol-only-provider silent null drop. X024/X024b assert the token shape; `XTypeTokenLoweringTests` locks the curly-form un-drop.
- **Step 6 frontend** (`cc0fbf34`) — `ClassifySetterValueExtension` routed through `BuildExtensionValue`, retiring its fail-closed "not supported in v1" arm; curly `{x:Static}`/`{x:Type}`/`{x:Boolean}` now work in `Setter.Value`. Both consuming lanes were already general (loader `BuildSetter`, emitter `EmitSetter`), so parity held for free.
- **Step 11 / GAP-B** (`f5d1ac5a`) — `EmitResourceDictionaryBuilder` now flushes `FlushDeferredScopeLines` at end-of-tree, fixing the silent drop of a compiled anchored binding (`RelativeSource FindAncestor`) on an inline standalone-RD entry. Provably `__scope`-safe (the only two `DeferredScopeLines.Add` sites are the compiled install — no scope ref — and the reflective `Source={x:Reference}` — fenced before recording in an RD).

**Reference-lane finding (the "maddening" x:Reference/ElementName inconsistency):** the picture is narrow. Contexts that WORK (both lanes, consistent): document level (x:Class), inside templates (incl. templates nested in RDs), inline `<X.Resources>` → document names. The one real bug was GAP-B (fixed). The remaining "failures" are **by design**: `x:Name` inside any ResourceDictionary is the shared frontend error **CUR2304**, so an `{x:Reference}` to an RD-level name can't resolve in either lane (generator emits a visible CURG3001, loader throws — neither silent). GAP-A (the hypothesized `LE:679` wrong-scope) did NOT surface as a failing cell in the systematic context-map; treat as non-reachable/already-correct pending a concrete repro.

**Remaining (the funnel is still a SHIM for Value delivery):** `EmitValue`'s Value-delivery arms delegate to the setter's helpers (`TextValueExpr`/`FoldedValueExpr`/`SetterExtensionValueExpr`/`MarkupExtensionEntryExpr`) and **fence on a normal Object** ("not yet funneled" — currently unreachable/dead, since no position routes an Object through Value delivery). Steps 3 (`ResolveIntrinsicExpr`), 5 (collection-add/init-only), 7 (datacondition-value), 8 (dict-entry-value), 9 (converter-arg/custom-arg nesting), 10 (StaticResource non-UIElement over-fence + DynamicResource exception) are unstarted. Each is behavior-changing toward loader-parity/uniformity (not a pure refactor): e.g. routing DataCondition through the funnel would unify its Extension handling with the setter's (unlocking Binding-descriptor + DynamicResource-carrier as `DataCondition.Value`), which needs per-cell loader-parity verification — several target increasingly exotic kind×position cells. The invariant-grid `[Theory]` (§7 item 6) is the closing test.

### Progress log — round 2 (value-emission core unified)

Current: **265 generator + 530 runtime**, full solution clean.

- **Step 7** (`51f47ad7`) — `DataCondition.Value` routed through `EmitValue`; `DataConditionValueExpr` deleted. The funnel's Value-delivery core became real (normal-Object → `EmitObjectToLocal`; element-form ⇔ curly for both deliveries; delivery-aware Extension arm).
- **Extension-value authority** (`f0477e3d`) — `ExtensionValueExpr` is now the SINGLE place a markup extension lowers in any Value slot, consumed by Setter.Value, DataCondition.Value, and (via `MarkupExtensionEntryExpr`) every standalone element-form entry. Leniency is two orthogonal `ValueSlot` axes, replacing the binary `ResourceLenient`.

**The empirical leniency finding (revises the design's §3 assumption that "the carrier is always available"):** a Value slot has one of THREE loader-matched profiles, NOT a uniform "carrier fallback":

| Slot | `{Binding}` | `{DynamicResource}` | verified against |
|---|---|---|---|
| **Setter.Value** (dedicated) | DESCRIPTOR | CARRIER | `BuildSetter` |
| **standalone entry** (dict/merged/theme/Setters-item/When-item) | fence | CARRIER | `MarkupExtensionEntryExpr` / loader entry path |
| **DataCondition.Value** (generic member) | fence (frontend CUR2210 at parse) | fence (loader CUR2210 at attach) | empirical probe |

`{StaticResource}` + custom are valid in all three; `{x:Reference}`/`{TemplateBinding}` fence in all three. The two flags (`AllowBindingDescriptor`, `AllowResourceCarrier`) encode exactly these rows.

**Remaining positions are NOT generic value slots — they are TYPED or STRUCTURAL, and don't fold into the generic funnel without per-position restriction:**

- **Typed slots** — `BasedOn` (→`Style`: accepts only Object + StaticResource; a Verbatim-Text funnel arm would emit `(Style)"literal"` → CS error, and its init-only forward-reference fence is position-specific) and `converter-arg` (→`IValueConverter`, with `StaticResourceMustResolve`). These are legitimately bespoke — the "few specific exceptions" the author anticipated, now enumerated.
- **Structural** — `EmitDictionaryEntry` (key resolution + deferred-realization closure + reference frame), `collection-add`, init-only initializers. The VALUE within is already routed (element-form entries go through `MarkupExtensionEntryExpr`→`ExtensionValueExpr`); the key/defer/structure is inherently position-specific.

**Net:** the "many divergent value paths" the consolidation targeted are unified — every generic member value flows through `EmitValue`, and every Extension-in-a-Value-slot decision flows through `ExtensionValueExpr`. The residual bespoke code is the typed/structural tail, which is specialized by necessity (the loader's own semantics differ per slot type), not by accident. The invariant-grid `[Theory]` (§7 item 6) remains as a durable capstone for whichever cells are folded next.
