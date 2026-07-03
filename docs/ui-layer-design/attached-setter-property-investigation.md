# Attached / owner-qualified `<Setter Property>` resolution — investigation

> **Status: PHASE 1 + PHASE 2 (prefixed owners) IMPLEMENTED.** Phase 1 (2026-06-14) — the default-namespace
> dot-split in `ResolveSetter` via `TryResolveQualifiedSetterMember` — covers every built-in and
> app-default-namespace attached / owner-qualified Setter (`Grid.Row`, `DockPanel.Dock`, `Control.Foreground`).
> **Phase 2 (2026-06-15)** — `prefix:`-qualified owners (`my:Owner.Member`) now resolve via parse-time namespace
> capture (the 4C variant: `HandleMemberAttribute` stashes the owner's namespace — `_reader.LookupNamespace` for a
> value-embedded prefix, the in-scope default otherwise — in the `Property` member's `ItemCount`; `ResolveSetter`
> reads it back to resolve the owner). The `CUR2111` deferral is retired. Rows X64a–X64e / X66b–X66d; tests in §8
> green plus `AttachedSetterEndToEndTests.PrefixedSetterOwner_OutsideUiNamespace…` (a custom attached property
> OUTSIDE Cursorial.UI, resolved only because the captured ns is honored). **Still deferred (separate follow-up,
> §6):** the shared prefix helper for `ResolveExtensionType` (`{x:Type my:Foo}`) and `ResolveStyleTargetType`
> (`TargetType="my:X"`) — they need their own ns capture (the `{x:Type}` leg a `MarkupExtensionNode` ns field).
> §1–§5 are the original investigation; §6 is the recommendation that was adopted.

---

## 1. Problem statement

A `<Setter Property="..." Value="..."/>` inside a `<Style>` cannot set an **attached** property
(`Grid.Row`, `DockPanel.Dock`, `Canvas.Left`) nor an **owner-qualified plain** property
(`Control.Foreground`). It fails with `CUR2102` "No member 'Grid.Row' on '&lt;TargetType&gt;'".

WPF / System.Xaml is the parity target: `<Setter Property="Grid.Row">` sets the attached DP. The owner
is resolved through xmlns; the Style's `TargetType` is consulted **only** for an *unqualified* property
name. A dotted name bypasses `TargetType` entirely — the qualifier *is* the lookup owner (and for
added-owner DPs the qualifier is just a resolution hint; DP identity is shared).

## 2. Verified mechanics — why it fails today

Cited against the real source (line numbers verified 2026-06-14):

1. **The attribute path is correct.** `XamlParser.ParseAttributes`
   (`Cursorial.UI.Xaml.Frontend/Parsing/XamlParser.cs:306-312`) detects a dotted attribute local name
   (`Grid.Row="1"`) and computes the owner namespace **while the reader is live** —
   `string ownerNs = attrPrefix.Length == 0 && attrNs.Length == 0 ? elementNamespace : attrNs;`
   (`:309`) — then `HandleAttachedAttribute` (`:372-404`) splits on the **first** dot (`:384-385`,
   `attrLocal.IndexOf('.')` at `:306`), resolves the owner via the xmlns-aware seam
   `ResolveType(ownerNs, ownerName, …)` (`:387`), and calls `ownerResolution.Type!.TryGetMember(memberName)`
   (`:395`). The resulting `XamlMember` carries the attached `UIProperty`.

2. **The Setter capture throws away namespace context.** `HandleMemberAttribute`
   (`XamlParser.cs:406-454`) has a special Setter branch (`:418-427`): for `Property` / `Value` /
   `TargetType` it stores the value as a **raw interned string** with **no namespace context** and
   `itemCount = 0` (`:426`), deferring resolution to end-of-object. The `Property` attribute's own
   prefix/NS describe the `Property` member (default UI ns), *not* the `Grid` owner embedded in the
   value string — and by end-of-object the reader has advanced past the attribute anyway.

3. **End-of-object resolution uses the wrong type, with no dot-split.** `ResolveSetter`
   (`XamlParser.cs:949-1025`) reads the lexical Style `TargetType` from `_styleTargetStack.Peek()`
   (`:951`), finds the `Property` slot (`:962-973`), and resolves the literal dotted name against the
   **wrong** type: `targetType.TryGetMember(propertyName)` (`:978`) — i.e.
   `Button.TryGetMember("Grid.Row")` → `UIPropertyRegistry.Find(typeof(Button), "Grid.Row")` → `null` →
   `ReportMemberNotFound(targetType, …)` → **CUR2102 against the wrong type**.

4. **Downstream is already correct — there is no apply-time asymmetry.** Once `ResolveSetter` rewrites
   the `Property` slot to a resolved member whose `.Property` is the attached `UIProperty`
   (`:987-994`, `AddResolvedMember` + slot rewrite keeping `XamlValueKind.Text` and the original
   `ValueIndex`), the loader consumes it verbatim:
   - `XamlObjectGraphBuilder.BuildSetter` (`Cursorial.UI.Xaml/XamlObjectGraphBuilder.cs:1013`) keys the
     `Property` slot off `resolved.Property is UIProperty targetProperty && member.Kind == XamlValueKind.Text`
     — it **never** checks `resolved.Name == "Property"`, so a resolved member named `"Row"` carrying
     `Grid.RowProperty` is consumed transparently → `new Setter(Grid.RowProperty, value)` (`:1046`).
   - `ReflectionXamlMetadata.BuildMember` (`Cursorial.UI.Xaml/ReflectionXamlMetadata.cs:181-193`) already
     returns `XamlMember{ Property = uiProperty, isAttachable: uiProperty.IsAttached }` **when the owner
     type is the lookup target** — attached members arrive correctly the moment the lookup hits the
     right type.
   - `Setter` (`Cursorial.UI/Styling/Setter.cs`) holds only `UIProperty Property` + `object? Value` — no
     attached-vs-direct notion. `StyleRuleFrame` keys entries by `UIProperty` identity via
     `CreateStyleEntry` (`Cursorial.UI/Styling/StyleRuleFrame.cs`). `AttachedProperty<T> : StyledProperty<T>`
     (`Cursorial.UI/Properties/AttachedProperty.cs`) applies through the identical store path
     ("need nothing special at the store").

   **Conclusion: the entire gap is producing the right `XamlMember` at parse time. No change is needed
   anywhere downstream of `ResolveSetter`.**

## 3. Constraints that shape any fix

- **Reflection-free frontend / X5-generator seam.** `Cursorial.UI.Xaml.Frontend` is `netstandard2.0` and
  is shared with the future X4/X5 source generator (design doc §4 / §1.3). The fix **must not** use
  runtime reflection. All type/member resolution must flow through the type-system seams —
  `ResolveType` → `IXamlTypeMetadataProvider.TryGetType` (the only resolution authority,
  `XamlParser.cs:1104`) and `XamlType.TryGetMember` (`TypeSystem/XamlType.cs:75`, → the injected
  `_memberResolver`). Both the reflection loader (`ReflectionXamlMetadata`, loader-only) and the future
  generator implement these. `BuildMember` is loader-only and is **not** where a frontend fix belongs.
  The fold/resolution-equivalence drift gate (matrix X188) requires the generator's parse to be
  byte-identical to the loader's.

- **The deferred-resolution point is the crux.** `ResolveSetter` runs at end-of-object
  (`XamlParser.cs:226`, after `ParseElementBody` at `:209` has walked the Setter subtree). The
  `XmlReader`'s xmlns scope for the `Property` attribute is gone by then. The frontend keeps **no xmlns
  stack of its own** — it relies entirely on the live reader (NamespaceURI is read off `_reader` at
  `:154` / `:266` / `:684`). Therefore any namespace context the resolution needs must either be (a)
  captured while the reader is live in `HandleMemberAttribute`, or (b) for the unprefixed case, derived
  from the element's default namespace threaded in.

- **`_resolvedTypes` has no mid-parse reader.** `XamlDocumentBuilder._resolvedTypes` is private
  (`XamlDocumentBuilder.cs:23`); only the `AddResolvedType` writer (`:91-95`) and `ResolvedMemberName` /
  `GetString` exist. Any design that stashes an *owner type id* and reads it back at end-of-object needs
  a new `GetResolvedType(int)` accessor.

- **`MemberRecord` SoA economy.** `MemberRecord` (`NodeGraph/Records.cs:48`) is the densest array in the
  model; adding a real field costs +4–8 B on every attribute/property-element node. `ItemCount` (`:73`,
  doc: "For Items: count; otherwise 0") is **provably dead for a `Text`-kind member** — every loader
  reader (`XamlObjectGraphBuilder.cs:370/718/778/833/868`) is `Items`-kind-gated — so it is a free
  zero-cost slot, but overloading it is semantically muddy.

- **The broader prefix gap is frontend-wide.** `ResolveExtensionType` (`XamlParser.cs:640-657`) strips a
  prefix (`:645-647`) then **hardcodes `ns = XmlnsNamespaces.CursorialUi`** (`:644`) — it does not honor
  a `my:` prefix, so `{x:Type my:Foo}` mis-resolves. `ResolveStyleTargetType` (`:930-942`) has the same
  defect (`:937`, `ResolveTypeQuiet(XmlnsNamespaces.CursorialUi, …)`). Attached-Setter is one instance
  of a single root cause: *resolving a (possibly-prefixed) type name embedded in an attribute-value
  string after the reader has moved off the attribute.*

## 4. Candidate approaches

All five proposals agree on the downstream contract (§2.4) and stay behind the seam. They differ on
**where** resolution happens (capture-time vs end-of-object), **how** namespace context survives to the
resolution point, and **how broadly** they fix the root cause.

### 4A. Minimal default-namespace dot-split in `ResolveSetter` (prefixed owners deferred)

- **Mechanism.** Insert a dotted-name branch in `ResolveSetter` between `:976` and `:978`.
  `int dot = propertyName.IndexOf('.')`. Unqualified (`dot < 0`) → unchanged TargetType path. Qualified
  (`dot > 0`) → if a `prefix:` precedes the dot, emit an explicit "prefixed Setter owners unsupported in
  v1" diagnostic and return; otherwise split, resolve the owner against the **default** UI namespace
  `ResolveType(XmlnsNamespaces.CursorialUi, ownerName, …)`, then `ownerType.TryGetMember(memberName)`,
  then the existing rewrite (`:987-994`) and Value-fold (`:996-1024`).
- **Change sites.** `ResolveSetter` only (plus a small `TryResolveQualifiedSetterMember` helper to make
  the Phase-2 swap one-site). No `HandleMemberAttribute` change, no SoA change, no new builder accessor.
- **Parity.** Attached ✔, owner-qualified plain ✔ (both via the default UI/Controls/Data namespace map,
  which covers every built-in owner and every app-default-namespace custom owner). Prefixed owners
  (`my:Grid.Row`) **explicitly deferred** with a dedicated diagnostic — `ResolveSetter` runs after the
  reader is dead, so the prefix's namespace is unrecoverable without capture.
- **Generator-compat.** Full. Hardcoded `CursorialUi` is a compile-time constant the generator's
  default-namespace map also keys on.
- **Scope.** Small. **Risk.** Low — additive, gated on the dot, the unqualified path is byte-for-byte
  unchanged.

### 4B. Eager capture-time owner.member resolution

- **Mechanism.** In the `HandleMemberAttribute` Setter branch (`:418-427`), when `memberName == "Property"`
  and the value is dotted: peel an optional value-embedded prefix (first `:`), resolve its namespace via
  `_reader.LookupNamespace(prefix)` (live, netstandard2.0-safe) or the element default for the unprefixed
  case, split owner/member, resolve owner via `ResolveType`, `ownerType.TryGetMember(member)`,
  `AddResolvedMember`, and emit the `Property` `MemberRecord` **already carrying the resolved member**.
  `ResolveSetter` then recognizes the pre-resolved slot (by `resolved.Name != "Property"`, or an
  `ItemCount` sentinel) and skips its TargetType rewrite, folding `Value` through the eager member.
- **Change sites.** `HandleMemberAttribute` (new branch + an `elementNamespace` parameter — threaded from
  `ParseAttributes`), `ResolveSetter` (recognize the pre-resolved slot; relax the `CUR2110` no-TargetType
  early-return so a qualified Setter needs no TargetType).
- **Parity.** Attached ✔, owner-qualified plain ✔, **prefixed ✔** (the prefix is resolved while the
  reader is live — this approach's distinguishing advantage). Diagnostics fire at the attribute's
  line/col (better than end-of-object).
- **Generator-compat.** Full — `LookupNamespace` is a parse-time `XmlReader` API the generator's own
  parser pass already drives over the same reader.
- **Scope.** Small. **Risk.** Medium — the `CUR2110` early-return interaction (qualified Setter without
  TargetType must become valid), the slot-recognition discriminator choice, and a diagnostic-position
  shift that changes existing negative-row line/col assertions.

### 4C. Namespace-snapshot threading (lazy resolution at end-of-object)

- **Mechanism.** At capture (`HandleMemberAttribute`), compute the **owner namespace string** (via
  `_reader.LookupNamespace(valuePrefix)` for a prefixed value, else `elementNamespace`), intern it, and
  stash `internedNsId + 1` in the dead `MemberRecord.ItemCount` slot of the `Property` Text member. At
  `ResolveSetter`, read the stashed namespace id back, split the dotted name, and resolve the owner via
  `ResolveType(stashedNs, ownerSimpleName, …)` then `TryGetMember`.
- **Change sites.** `HandleMemberAttribute` (+ `elementNamespace` param), `ResolveSetter` (read-back +
  dot-split), `Records.cs` doc-comment for the `ItemCount` dual meaning. Uses existing `GetString` — **no
  new builder accessor** (its edge over 4D).
- **Parity.** Attached ✔, owner-qualified plain ✔, **prefixed ✔**.
- **Generator-compat.** Full — both legs are seam calls + pure interning.
- **Scope.** Small. **Risk.** Low-moderate — double-walks the seam (prefix→ns at capture, ns→type at
  end-of-object); `ItemCount` overload; first-vs-last-dot split must be reconciled with the
  attribute path's `IndexOf` (first dot).

### 4D. SoA resolved-owner-type token (capture type id, resolve member later)

- **Mechanism.** Like 4C but resolve the owner **type** eagerly at capture (`AddResolvedType(ownerType)`)
  and stash `typeId + 1` in `ItemCount`; at `ResolveSetter`, read `ResolvedTypes[id-1]` and
  `TryGetMember(postDotName)`. WPF-faithful (owner resolved in the live xmlns scope) and avoids the second
  `ResolveType`.
- **Change sites.** Same as 4C **plus a new `GetResolvedType(int)` builder accessor** (`_resolvedTypes`
  is private with no reader today — the "zero builder change" claim is false).
- **Parity / generator-compat.** Same as 4C/4B (full, prefixed ✔).
- **Scope.** Small. **Risk.** Low-medium — new accessor + `ItemCount` overload. The proposal's own honest
  framing: this reduces to 4B's "resolve at capture" plus an unnecessary type→member round-trip; if you
  resolve the *member* at capture (4B) you need neither the accessor nor the `ItemCount` overload.

### 4E. Unified `ResolveQualifiedTypeName` helper for all three prefix sites

- **Mechanism.** Factor a shared primitive
  `ResolveQualifiedTypeName(maybePrefixed, defaultNs, prefixLookup, line, col)` and route the Setter fix,
  `ResolveExtensionType` (`{x:Type my:Foo}`), and `ResolveStyleTargetType` (`TargetType="my:X"`) through
  it.
- **Honest shortfall (per its own critique).** The three consumers share only a ~10-line split-and-resolve
  primitive; the **namespace-context capture is bespoke per consumer**. The Setter leg captures via
  `_reader.LookupNamespace` into a record slot; the `{x:Type}` leg needs ns context threaded onto
  `MarkupExtensionNode` (`MarkupExtensions/MarkupExtensionNode.cs:23-52`, which carries **no** ns today —
  a real SoA schema touch). So "one code path" is largely cosmetic and the `{x:Type}` leg is materially
  larger than the Setter leg.
- **Scope.** Medium. **Risk.** Medium — bundling invites scope creep; the proposal itself recommends a
  hybrid (build the small primitive, fix the Setter leg now, defer the extension/TargetType legs).

## 5. Critique highlights — blockers and trade-offs

- **No hard blockers in any approach.** All five are seam-bound, reflection-free, generator-symmetric, and
  leave the loader / `Setter` / `StyleRuleFrame` / value store untouched. The downstream contract (§2.4)
  is verified end-to-end.
- **The added-owner identity worry is a non-issue (verified).** `UIPropertyRegistry.Find` is keyed by
  exact `(ownerType, name)` and base-walks; `AddOwner` registers the *same* `UIProperty` instance under
  the new key. `Control.Foreground` and `TextElement.Foreground` resolve to the one shared
  `ForegroundProperty` singleton the store keys on — exactly WPF's "qualifier is a hint" semantics. No
  mis-resolution.
- **The `ItemCount` overload is functionally safe but semantically muddy.** It is provably dead for a
  `Text` member, so reuse compiles and resolves correctly today — but the field is documented as the
  Items-run count, and a future contributor adding an unguarded `ItemCount` reader could silently corrupt
  Setter resolution. A name-based discriminator (the eager member is named `"Row"`, never `"Property"` —
  4B) needs *no* extra state at all and is the cleanest.
- **Capture-time vs end-of-object resolution is the real fork.** Anything that supports **prefixed
  owners** must capture namespace context while the reader is live (4B/4C/4D). 4A cannot do prefixed
  owners by construction and defers them.
- **Diagnostic quality.** Every approach upgrades the misleading wrong-type `CUR2102` to: owner-resolves /
  member-doesn't → `CUR2102` naming the **owner** (`Grid`), owner-unresolvable → `CUR2001/CUR2002` naming
  the type (parity with the attached-attribute X76 row). Capture-time approaches (4B/4D) report at the
  attribute's line/col — strictly better, but a behavior change for existing negative-row line/col
  assertions.
- **First-dot vs last-dot.** The attached-attribute path uses `IndexOf('.')` (first dot, X75). System.Xaml
  splits on the last dot. They are observationally equivalent today (no owner names contain dots). Pick
  one convention and document it; match the attribute path (`IndexOf`) for intra-frontend consistency.
- **Scope-creep trap (4E).** The "unified" framing overstates shared code; the `{x:Type}` / `TargetType`
  prefix legs are a separate, larger touch and should not block the Setter fix.

## 6. RECOMMENDATION

**Adopt a phased hybrid of 4A → 4B, with 4A as the shippable first slice.**

**Phase 1 (ship now): the minimal default-namespace dot-split in `ResolveSetter` (4A),** factored through
a small helper so Phase 2 is a one-site swap. Rationale tied to the constraints:

- It covers **100% of built-in attached/owner-qualified properties** (`Grid.Row`, `DockPanel.Dock`,
  `Canvas.Left`, `Control.Foreground`, `KeyboardNavigation.*`) and every app-default-namespace custom
  owner — because they all live in the default-mapped `CursorialUi` namespace (`RegisterDefaultNamespace`
  feeds the app CLR namespaces into `CursorialUi` resolution, `XamlSchemaContext.cs`).
- It is the lowest-risk change: additive, dot-gated, the unqualified path is byte-for-byte unchanged, it
  touches one method, adds no SoA state, and needs no new builder accessor.
- It unblocks the `uixaml` demo and the built-in control themes immediately.
- It is reflection-free and generator-symmetric by construction (a compile-time-constant default
  namespace + the existing `TryGetType`/`TryGetMember` seams).

**Phase 2 (when prefixed owners are required): promote to eager capture-time resolution (4B),** which is
the WPF-faithful design and the only one that resolves a value-embedded `my:` prefix (the reader is dead
at end-of-object). Prefer **4B over 4C/4D** because resolving the *member* at capture needs neither the
`ItemCount` overload nor a new builder accessor, and discriminating the pre-resolved slot by
`resolved.Name != "Property"` is zero new state. Phase 2 is the right home for the **shared prefix helper**
(4E's primitive) that also fixes `ResolveExtensionType` (`:644`) and `ResolveStyleTargetType` (`:937`) —
those legs need their own ns capture (extension nodes carry none today) and should be a separate follow-up,
not a Phase-1 blocker.

### 6.1 Phase-1 implementation sketch (file-level)

**File: `Cursorial.UI.Xaml.Frontend/Parsing/XamlParser.cs`**

1. **New private helper** (near `ResolveType`, ~`:1104`):

   ```csharp
   /// Resolves a Setter Property name that may be Owner.Member-qualified. Returns the resolved member,
   /// or null after reporting a diagnostic. Phase 1: owner resolves against the default UI namespace;
   /// prefixed owners (my:Owner.Member) are a documented v1 deferral (CURxxxx). Phase 2 swaps the
   /// owner-namespace source to a captured value.
   private XamlMember? TryResolveQualifiedSetterMember(
       string propertyName, XamlType targetType, int line, int column)
   {
       int dot = propertyName.IndexOf('.');               // first dot — matches HandleAttachedAttribute
       if (dot < 0)
           return targetType.TryGetMember(propertyName);  // unqualified: TargetType is the owner (WPF)

       int colon = propertyName.IndexOf(':');
       if (colon >= 0 && colon < dot)
       {
           _builder.Error(XamlDiagnosticCodes.PrefixedSetterOwnerUnsupported, // NEW dedicated code
               $"Prefixed attached/qualified Setter owners are not supported in v1 ('{propertyName}').",
               line, column);
           return null;
       }

       string ownerName  = propertyName.Substring(0, dot);
       string memberName = propertyName.Substring(dot + 1);

       var ownerResolution = ResolveType(XmlnsNamespaces.CursorialUi, ownerName, line, column);
       if (!ownerResolution.IsResolved)
           return null; // ResolveType already emitted CUR2001/CUR2002 naming the owner

       var member = ownerResolution.Type!.TryGetMember(memberName);
       if (member is null)
           ReportMemberNotFound(ownerResolution.Type!, memberName, line, column); // names the OWNER
       return member;
   }
   ```

2. **`ResolveSetter` (`:978`):** replace the single `targetType.TryGetMember(propertyName)` /
   `ReportMemberNotFound(targetType, …)` pair with:

   ```csharp
   var targetMember = TryResolveQualifiedSetterMember(propertyName, targetType, line, column);
   if (targetMember is null)
       return; // helper already reported the diagnostic
   ```

   The existing rewrite (`:987-994`) and Value-fold loop (`:996-1024`) are **unchanged** — `targetMember`
   is now the attached/owner member and the fold keys off its converter correctly.

3. **New diagnostic code** in `XamlDiagnosticCodes` (frontend): `PrefixedSetterOwnerUnsupported` (next free
   `CUR21xx`), so the negative matrix row is unambiguous and Phase 2 simply removes the branch.

**No changes** to `HandleMemberAttribute`, `XamlDocumentBuilder`, `Records.cs`, `XamlObjectGraphBuilder`,
`BuildSetter`, `Setter`, `StyleRuleFrame`, `AttachedProperty`, or the value store.

### 6.2 Phase-2 promotion sketch (deferred)

- Add an `elementNamespace` parameter to `HandleMemberAttribute` (already in scope at the `ParseAttributes`
  call site, `:315`).
- In the Setter `Property` branch (`:418-427`): when the value is dotted, compute the owner namespace
  (`_reader.LookupNamespace(valuePrefix)` for a prefixed value, else `elementNamespace`), resolve the
  owner + member eagerly, `AddResolvedMember`, and emit the `Property` `MemberRecord` already carrying the
  resolved member.
- In `ResolveSetter`: recognize the pre-resolved slot (`_builder.ResolvedMemberName(memberId) != "Property"`)
  and skip the helper, folding `Value` through the eager member; relax the `CUR2110` early-return so a
  qualified Setter without an enclosing `TargetType` is valid.
- Delete the `PrefixedSetterOwnerUnsupported` branch; the helper's owner-namespace source becomes the
  captured value.
- Extract the split/prefix logic into the shared `ResolveQualifiedTypeName` primitive and reuse it in
  `ResolveExtensionType` and `ResolveStyleTargetType` (each with its own ns capture).

## 7. xaml-matrix rows to ADD when implemented

Extend the X64/X66 family (`docs/ui-layer-design/xaml-matrix.md` §5) and amend XD4 (member-resolution
order). Row format `| id | input | when | expect | oracle |`:

| id | input | when | expect | oracle |
|----|-------|------|--------|--------|
| **X64a** | `<Style TargetType="Button"><Setter Property="Grid.Row" Value="1"/></Style>` | parse (end-of-object) | `Property` resolves the **attached** `Grid.RowProperty` via owner `Grid` (default UI xmlns), **not** the lexical TargetType; `Value` folds to `1` | System.Xaml (Tier A `GetAttachableMember`; Tier B `setter.Property == Grid.RowProperty`) |
| **X64b** | `<Setter Property="DockPanel.Dock" Value="Top"/>` (any TargetType) | parse | resolves attached `DockPanel.DockProperty`; `Value` folds to `Dock.Top` through the property converter | Cursorial behavior |
| **X64c** | `<Style TargetType="Button"><Setter Property="Control.Foreground" Value="#fff"/></Style>` | parse | owner-qualified/added-owner property resolves `Control.ForegroundProperty` via owner `Control`; TargetType not consulted for a dotted name | System.Xaml (Tier B) |
| **X64d** | unqualified baseline: `<Style TargetType="Button"><Setter Property="Background"/></Style>` | parse | still resolves against TargetType (the only case TargetType is the owner) — regression anchor that the dot-gate routes unqualified names to the old path | existing X64 |
| **X66b** | `<Style TargetType="Button"><Setter Property="Grid.Nope" Value="1"/></Style>` | parse | owner `Grid` resolves, member `Nope` does not → **CUR2102** "No member 'Nope' on 'Grid'" + DidYouMean, naming the **owner** (not the TargetType) | PIN (XD4) |
| **X66c** | `<Setter Property="Bogus.Row" Value="1"/>` | parse | owner `Bogus` unresolvable in the default namespace → **CUR2001/CUR2002** naming the type (parity with the attribute-form X76) | PIN (XD4/XD5) |
| **X66d** (Phase 1) | `xmlns:my="using:Foo"` then `<Setter Property="my:MyPanel.Slot" Value="1"/>` | parse | **prefixed owner is a v1 deferral** → new `CUR21xx` (`PrefixedSetterOwnerUnsupported`), NOT a misleading CUR2102; documents the limitation shared with `ResolveExtensionType` / `ResolveStyleTargetType` | PIN |
| **X66d′** (Phase 2) | same as X66d | parse | the `my:` prefix resolves via captured `LookupNamespace`; owner `MyPanel` resolves within that namespace → attached property applies | System.Xaml (Tier B) |

**XD4 amendment:** a *dotted* Setter `Property` name resolves the owner xmlns-aware (TargetType ignored);
an *undotted* name resolves against the lexical Style `TargetType`. Prefixed dotted owners are deferred to
Phase 2 (`CUR21xx` in Phase 1).

**Prefixed-owner coverage:** explicitly **deferred** in Phase 1 (X66d → diagnostic). Covered in Phase 2
(X66d′).

## 8. Test plan

**Frontend node-graph (`Cursorial.UI.Xaml.Tests/XamlMatrix/Section05_Folding.cs` or a new Setter section):**
parse X64a–X64c, walk to the Setter, assert the rewritten `Property` member's `XamlMember.Property` is the
expected `UIProperty` by **reference identity** (`Grid.RowProperty`, `DockPanel.DockProperty`,
`Control.ForegroundProperty`) and `IsAttachable` is set for attached, plus the folded `Value` constant. The
frontend fixture (`TestMembers.cs`) already registers attached `Grid.Row`/`Grid.Column` via
`AddAttached(isAttachable: true)`, so these assert reflection-free at the frontend layer.

**Negative diagnostics (`Section14_Diagnostics` or the folding section):** X66b → `CUR2102` naming `Grid`
(not `Button`) with DidYouMean; X66c → `CUR2001/CUR2002` naming `Bogus`; X66d → the new
`PrefixedSetterOwnerUnsupported` code. Assert code + line/col + message.

**Loader end-to-end (`Cursorial.UI.Xaml.Tests/Integration/Phase6XamlEndToEndTests` via `UITestHost`):** load
a `<Style TargetType="Border"><Setter Property="Grid.Row" Value="2"/></Style>` applied to a child in a
`Grid`; after the style activates, assert `child.GetValue(Grid.RowProperty) == 2` **and** that the child
arranges in row 2 — proving the attached value reaches `Grid` arrange through the full
`BuildSetter`→`StyleRuleFrame`→`AttachedProperty` store path with zero loader change. A second case asserts
`GetValue(Control.ForegroundProperty)` at `BindingPriority.Style` for `Property="Control.Foreground"`.

**Regression:** re-run existing X64 (`Property="Background"`) to confirm the dot-gate leaves the unqualified
path unchanged (and its existing line/col assertions hold).

**`uixaml` demo smoke:** add a `<Setter Property="DockPanel.Dock" Value="Top"/>` to the embedded `.xaml` to
confirm the enum-converter Value fold through the attached member works against the real frame loop.

**System.Xaml oracle leg (`SystemXamlOracleTests`, doc §4.10 — Windows-gated, reflection-only,
skip-elsewhere with a documented reason):**

- **Tier A (System.Xaml only — recommended first).** On a portable attachable-member-bearing test type,
  assert `XamlType.GetAttachableMember("Foo").IsAttachable == true && .TargetType == owner` and
  `GetMember("Plain").TargetType == DeclaringType`, and that the namespace resolver maps a prefix to the
  CLR namespace for `GetAttachableMember`. This pins the **owner.member split + attachable-lookup +
  prefix** semantics with only `System.Xaml` (already loaded by the existing leg) plus a portable type —
  the cheapest oracle and the closest mirror of `TryResolveQualifiedSetterMember`.
- **Tier B (full `<Setter>` parse — highest fidelity, heavier).** Reflectively load `PresentationFramework`
  on a Windows STA agent, parse `<Style TargetType="Button"><Setter Property="Grid.Row" Value="1"/></Style>`
  and `Property="Control.Foreground"` / `Property="my:Grid.Row"` via `XamlServices.Parse`, and assert
  `setter.Property == Grid.RowProperty` / `Control.ForegroundProperty` by reflection. Pins the end-to-end
  "TargetType only for unqualified names" rule. Worth the surface escalation for the three crux rows; Tier A
  is sufficient pinning if `PresentationFramework` on CI is undesirable.

## 9. Phasing / scope recommendation

- **Minimal first slice (Phase 1, scope: small, risk: low):** approach **4A** via
  `TryResolveQualifiedSetterMember` in `ResolveSetter` + the `PrefixedSetterOwnerUnsupported` diagnostic.
  Covers all built-in and app-default-namespace attached/owner-qualified Setters. Lands X64a–X64d, X66b,
  X66c, X66d. Unblocks the demo and control themes. One method changed; no SoA / builder / loader change.
- **Full general fix (Phase 2, scope: small per leg, deferred):** promote to capture-time resolution
  (**4B**) for prefixed owners (X66d′), then extract the shared `ResolveQualifiedTypeName` primitive (the
  useful core of **4E**) and apply it to the sibling prefix gaps `ResolveExtensionType` (`:644`) and
  `ResolveStyleTargetType` (`:937`) — each with its own reader-scope capture (extension nodes carry no ns
  today, the larger of the two follow-up touches). Do **not** block Phase 1 on the sibling legs.
- The helper factoring in Phase 1 makes the Phase-2 promotion a one-site swap of the owner-namespace source
  (constant → captured value), so the two-phase landing costs one extra (small) touch of `ResolveSetter`,
  not a rewrite.
