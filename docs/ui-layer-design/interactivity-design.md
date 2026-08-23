# Interactivity (Behaviors) Design — `Cursorial.UI.Interactivity`

A Blend / `Microsoft.Xaml.Behaviors`-style interactivity layer — attached **behaviors**, **triggers**, and
**actions** — over `Cursorial.UI`. This is the declarative *event→action* surface: "when this event fires (or this
condition holds), run these actions." It is a peer module to `Cursorial.UI.Bars` / `Cursorial.UI.DataViews` /
`Cursorial.UI.Dialogs`.

---

## §0 — Scope, and the one framing decision that shapes everything

The instinct with a Behaviors library is to build lane-aware *triggers that set values* — because in Blend/Silverlight
that was the pain (Interactivity triggers slammed values in at a single fixed precedence, so they couldn't be used
inside templates; a template trigger either clobbered the templated parent's local value or couldn't override the
part's resting template value). **We already solve that, and not here.**

Cursorial's styling engine owns lane-correct conditional value-setting, *including inside templates*, via
`ControlTemplate.Styles` + `Style.When`:

- `ControlTemplate.Styles` is gathered by the StyleEngine at the **Template layer** (CD30, `StyleEngine.cs:871`); a
  `Control` hands its template's styles to the engine (`Control.cs:174`); the diagnostics direct authors to put
  `/template/` part rules there (`StyleDiagnostics.cs:406`).
- A `Style.When` condition installs a **retractable `ValueFrame` at `BindingPriority.StyleTrigger`** when satisfied,
  removed when it stops matching (store-owned, no set-back).
- `StyleTrigger` **pierces** the `Template` lane (the part's resting template value), while resting rules cannot —
  "state looks pierce the template, resting rules cannot." So a template-scoped `Style.When` rule overrides the
  part's resting value when active and reverts cleanly when it deactivates.

That is exactly "a template trigger that applies its value at the right lane, composes with the resting part value,
and reverts on deactivation." **It is solved today, by the styling engine, for the data/state-condition case.**

So this module deliberately does **NOT** reinvent lane-aware setters. It fills the two gaps styling *cannot*:

1. **Events.** `Style.When` reacts to a *data/state condition* (a property value), never an *event*. "On `Click` /
   `Loaded` / `PointerPressed` → do X" is undeclarable today.
2. **Actions beyond setters.** `Style.When` sets values. It cannot `InvokeCommand`, `CallMethod`, `BeginStoryboard`,
   or run imperative logic — the whole `TriggerAction` catalog.

Everything below follows from that: **Interactivity is the event→action subsystem; conditional *values* belong to
`Style.When`.**

---

## §1 — Module & namespace

- **Module:** `Cursorial.UI.Interactivity` (new project; peer to Bars/DataViews). Depends on `Cursorial.UI` only
  (routed events, the command surface, the property system, the animation layer).
- **Namespace:** `Cursorial.UI.Interactivity`. Blend/`Microsoft.Xaml.Behaviors` naming parity throughout
  (`Interaction`, `Behavior`, `TriggerBase`, `TriggerAction`) so the model is instantly familiar and XAML ports
  read the same.
- **XAML default xmlns:** contributed to the `https://cursorial.dev/ui` map so `<Interaction.Triggers>` needs no
  prefix. (An optional compatibility alias for the Microsoft `i:`/`b:` namespaces is a §12 deferral.)

---

## §2 — The object model

Mirrors the Blend triad, with Cursorial's `UIObject` as the base so behaviors/triggers/actions are first-class
property-system objects (they get `UIProperty`s, bindings, resource lookup, `x:Name`).

```
UIObject
 ├─ Behavior            AssociatedObject; OnAttached()/OnDetaching()          (attached, standalone logic)
 │   └─ Behavior<T>     typed AssociatedObject : T
 ├─ TriggerBase         Actions : TriggerActionCollection; Fire() runs them   (a source of firings)
 │   └─ TriggerBase<T>
 └─ TriggerAction       Execute(object sender, object? parameter); IsEnabled  (a unit of effect)
     └─ TriggerAction<T>
```

Attach points, both `AttachedProperty`s on `UIObject` (WPF/Blend allow behaviors on non-visuals):

- `Interaction.Behaviors` → `BehaviorCollection` — standalone behaviors on the host.
- `Interaction.Triggers`  → `TriggerCollection` — triggers, each owning an `Actions` collection.

The collections are **lifecycle-aware** (§3): adding/removing an item attaches/detaches it against the current host;
attaching/detaching the host cascades to all items. `IAttachedObject { object? AssociatedObject; Attach(host);
Detach(); }` is the shared contract (Behavior, TriggerBase, TriggerAction all implement it — a trigger attaches its
actions when it attaches).

---

## §3 — Lifecycle (rides the existing attach/detach spine)

- **Attach:** when `Interaction.Behaviors`/`Triggers` is populated on a host that is (or becomes) attached to a live
  surface, each item's `Attach(host)` runs → `OnAttached()`. Uses S1's attach walk / `UIElement` attach notification;
  a non-`UIElement` host attaches immediately on collection assignment.
- **Detach:** host detach, `TearDown`, or removal from the collection → `Detach()` → `OnDetaching()`. Behaviors MUST
  unhook every event/subscription here (the leak-tracker discipline from S2/S5 applies — a DEBUG never-detached
  tracker).
- **Re-attach:** parked-and-re-armed like S3 focus / S8 transitions — detach unhooks, a later re-attach re-runs
  `OnAttached`. Each template instantiation gets its **own** behavior instances with their own `AssociatedObject`,
  so a templated trigger is per-instance for free (the same property that makes `Style.When` per-instance).
- **Thread affinity + dispatcher:** behaviors run on the UI thread (the `UIDispatcher`), like all `UIObject` work.

---

## §4 — Triggers

### `EventTrigger` — the core value-add (styling cannot do events)

Hooks a routed event on the `AssociatedObject` (or a named `SourceObject`) and fires its `Actions` when it raises.

- `EventName : string` — resolved against the **S3 routed-event registry** to a `RoutedEvent`, then
  `host.AddHandler(routedEvent, handler, handledEventsToo)`. `HandledEventsToo : bool` (default false).
- `SourceObject`/`SourceName` — retarget to another element (Blend parity); defaults to the AssociatedObject.
- Cleanup: `RemoveHandler` in `OnDetaching`.
- Args flow: the `RoutedEventArgs` is the `parameter` passed to each action's `Execute` (so an action can read the
  event's `Source`, mouse position, key, etc.).

### `DataTrigger` — fires *actions* on a condition (NOT a `Style.When` replacement)

A condition that runs **actions** (not setters) when it becomes true — the imperative complement to `Style.When`.

- Reuses S2's `DataCondition` + `BindingOperations.Watch` machinery (a binding + `Value`/predicate + `Negate`), so
  the semantics match `Style.When` exactly; only the *effect* differs (actions vs a retractable value frame).
- **Decision:** if you want to *set a value* on a data condition, use `Style.When` (lane-correct, retractable). Use
  `DataTrigger` when the condition must *do* something imperative (invoke a command, call a method, start a
  storyboard). The doc's §0 boundary is enforced here.

Deferred triggers (KeyTrigger, PropertyChangedTrigger, gesture triggers) — §12.

---

## §5 — Actions (the catalog)

| Action | Effect | Integrates with |
|---|---|---|
| `InvokeCommandAction` | `Command.Execute(CommandParameter ?? eventArgs)`, gated on `CanExecute` | the `ICommand` surface / `IsEnabledCore` |
| `CallMethodAction` | invoke `MethodName` on `TargetObject` | typed delegate preferred; reflection = AOT fallback |
| `ChangePropertyAction` | set `PropertyName` on `TargetObject` to `Value` | the property system — see §6 |
| `BeginStoryboardAction` | `Storyboard.Begin(scope)` | S5 `AnimationScheduler` / `Storyboard.cs:37` |
| `ControlStoryboardAction` | pause/resume/stop/seek a running storyboard | S5 `StoryboardHandle` |
| `SetFocusAction` | `FocusManager.SetFocus(target)` | S3 focus |

All actions expose `IsEnabled` and resolve `TargetObject`/`SourceObject` by direct reference, `x:Self` (§8), name,
or a `RelativeSource`-style anchor. The catalog is open (`TriggerAction<T>` is public); the table is the built-in set.

---

## §6 — `ChangePropertyAction` and the value lane (the decision the twist resolves to)

Because §0 hands all *conditional* value-setting to `Style.When`, `ChangePropertyAction` is an **imperative,
event-driven, one-shot set** — semantically identical to code-behind writing `part.SetValue(...)` in an event
handler. So:

- **Default: `SetValue` at `LocalValue`.** LocalValue is the highest non-animation lane, so an imperative set
  correctly pierces the template, styles, and triggers — which is what "the user clicked and I set this" should do.
  No lane capture is needed or wanted; the lane-aware-in-templates problem does not arise because value composition
  lives in the styling lattice, not here.
- **Option: `SetCurrentValue`.** A `Mode` on the action selects `SetCurrentValue` (change the effective value
  without seizing the LocalValue provenance, so a later binding/animation still wins and `ClearValue` reverts) — the
  right choice for "nudge the current value" rather than "pin it."
- **No auto-revert.** An `EventTrigger` + `ChangePropertyAction` is a permanent set until overwritten. If you want
  "while the condition holds, set X, revert when it drops," that is `Style.When` (a retractable `StyleTrigger`
  frame), by design.

This is the whole resolution of the earlier "capture the value lane and forward it to children" idea: we *don't*,
because the conditional-value lane already exists and is owned by styling. `ChangePropertyAction` stays a blunt
imperative instrument, which is exactly what an event-driven action should be.

---

## §7 — XAML integration

Authoring is the Blend shape:

```xml
<Button Content="Save">
  <Interaction.Triggers>
    <EventTrigger EventName="Click">
      <InvokeCommandAction Command="{Binding SaveCommand}" CommandParameter="{x:Self}"/>
    </EventTrigger>
    <DataTrigger Binding="{Binding IsDirty}" Value="True">
      <BeginStoryboardAction Storyboard="{StaticResource Pulse}"/>
    </DataTrigger>
  </Interaction.Triggers>
</Button>
```

- **Loader / generator:** triggers and actions are ordinary `UIObject` graphs, so the runtime loader and the X4
  generator lower them with **no new node kind** — `Interaction.Triggers` is an attached-property collection member.
  The precedent is concrete: S8's `Transition.Transitions` (`AttachedProperty<TransitionCollection>`) is already a
  shipped, XAML-authorable attached-property collection lowered in both lanes, and the loader already fills read-only
  collection members (`ApplyItems` / `BindCollectionAdders`, `XamlObjectGraphBuilder.cs:647`). So `Interaction.Triggers`
  reuses that exact path — model `Interaction.Behaviors`/`Triggers` as `AttachedProperty<…Collection>` in the same
  shape. The *attach wiring* is pure runtime (`OnAttached`), so the generator emits object construction + collection
  `.Add`s and nothing special. This is the payoff of building on `UIObject`.
- **`x:Self`** (§8) supplies the construction-time self-reference for `CommandParameter`/`TargetObject`.
- Attached-collection content: `<Interaction.Triggers>` uses the same content-collection path `Style.Setters` /
  `ItemsControl.Items` use.

---

## §8 — `x:Self` (folded in — a value intrinsic, motivated here but independent)

A **construction-time, read-only** self-reference value intrinsic — the thing `RelativeSource Self` isn't (that is
binding-only, `UIElement`-only, and runtime). Motivated by actions ("pass myself as a command parameter / converter
arg") but usable in any value slot.

- **What it resolves to:** the object the value is being **assigned onto** — always the assignment target, *seeing
  through* any enclosing markup extensions (`ConverterParameter={x:Self}` is the Button, never the Binding). The
  extension-vs-target ambiguity is resolved by fiat (target), because referencing the extension is never useful.
- **`Level`** (optional, default 0): walks the **XAML construction/object-graph stack** — Level 0 = the immediate
  target, Level 1 = the enclosing object being constructed, etc. This is Avalonia's `$self`/`$parent[N]` idea, but
  construction-time and usable in any position rather than binding-path-only. Beyond-depth `Level` → a CUR error.
- **Construction-time, not runtime** (the load-bearing property): resolves when the value is assigned, to the object
  *as it exists then* — partially constructed (later members unset), a stable identity, **not** reactive and **not**
  a live binding target. Anything needing the live tree/DataContext stays `RelativeSource Self` or an attach-time
  behavior (§9). In particular, **`x:Self` can never touch `DataContext`** — DataContext is inherited via tree
  attachment, which hasn't happened at construction.
- **Implementation:** the same shape as `{x:Type}` — the frontend folds `{x:Self Level=N}` to a typed
  `XamlSelfReference(level)` token; each lane resolves it against a construction-object stack (the loader nests
  construction depth-first; the emitter already tracks the enclosing object's local var). One frontend token, both
  lanes.
- **Build order:** ship the **Level-0, non-template slice first** (covers converter-arg + self-as-source, de-risks
  the stack resolution), then `Level` and the per-instantiation template case (§12).

---

## §9 — `ViewRegistration` (the first concrete behavior; the self→VM pattern `x:Self` can't do)

The motivating gallery need: a view hands *itself* to its view-model (as a resource-resolution root, so the VM can
`FindResource` without the element tree). This is **attach-time**, not construction-time — `DataContext` only exists
once the element is attached — so it is a behavior, not `x:Self`.

```csharp
public interface IResourceRootSink { void SetResourceRoot(UIElement? root); }   // typed → trimming/AOT-clean

// ViewRegistration.RegisterAs (attached property) → a Behavior<UIElement> that, on DataContextChanged AND initial
// attach, does: if (host.DataContext is IResourceRootSink sink) sink.SetResourceRoot(host);  and SetResourceRoot(null)
// on detach so a dead view isn't pinned.
```

- Hooks `DataContextChanged` (not just an attach event) — the DataContext can arrive after attach or swap later, and
  the registration must track it.
- Typed `IResourceRootSink` rather than a reflection set, so it survives trimming (consistent with the AOT posture
  of the whole XAML stack).
- Lands as the library's first real `Behavior<T>` and the gallery-demo canary.

---

## §10 — Resolved decisions (pinned)

- **D1 — Scope is event→action.** Conditional *value*-setting (incl. in templates, lane-correct) is `Style.When` +
  `ControlTemplate.Styles`; this module does not reinvent it.
- **D2 — `ChangePropertyAction` is an imperative one-shot** (`SetValue` LocalValue default; `SetCurrentValue`
  option). Not lane-aware, no auto-revert. (The "capture the value lane" twist resolves to *don't* — styling owns it.)
- **D3 — `DataTrigger` fires ACTIONS**, `Style.When` fires SETTERS. Same `DataCondition`/`Watch` substrate, different
  effect; the boundary is enforced, not blurred.
- **D4 — Blend naming parity** (`Interaction.Behaviors`/`Triggers`, `Behavior<T>`, `TriggerBase`, `TriggerAction`).
- **D5 — `EventTrigger` over the S3 routed-event registry** (`AddHandler`, `HandledEventsToo`, `SourceObject`).
- **D6 — `CallMethodAction` prefers a typed delegate**; reflection is the `RequiresUnreferencedCode` fallback.
- **D7 — `x:Self` is construction-time, read-only, target-object (see-through-extensions), `Level` over the
  construction stack.** Self→VM is attach-time (`ViewRegistration`), never `x:Self`.
- **D8 — Lifecycle rides UIElement attach/detach + `TearDown`**; per-template-instance behavior instances; DEBUG
  leak tracker.
- **D9 — Built on `UIObject`**, so triggers/actions lower through the existing loader/generator with no new node kind.

---

## §11 — Phase plan

- **P0 — Core model.** `IAttachedObject`, `Behavior`/`Behavior<T>`, `TriggerBase`/`TriggerBase<T>`,
  `TriggerAction`/`TriggerAction<T>`, the three collections, `Interaction.Behaviors`/`Triggers` attached properties,
  and the attach/detach/re-attach lifecycle over the S1 spine. Headless tests (`UIHeadlessHost`): attach on populate,
  detach on teardown, per-instantiation isolation, leak-tracker green.
- **P1 — The MVP triad.** `EventTrigger` (routed-event hook) + `InvokeCommandAction` + `ChangePropertyAction`
  (§6 semantics). End-to-end: `<EventTrigger EventName="Click"><InvokeCommandAction .../></EventTrigger>` fires a
  command through the loader AND the generator (lane/parity tests).
- **P2 — Action catalog + `DataTrigger`.** `CallMethodAction`, `BeginStoryboardAction`/`ControlStoryboardAction`,
  `SetFocusAction`; `DataTrigger` on the `DataCondition` substrate. S5/S3 integration tests.
- **P3 — XAML + `x:Self` (Level-0).** Confirm the attached-collection lower path in both lanes (it should be free);
  ship `x:Self` Level-0 (frontend token + both-lane construction-stack resolution + parity tests).
- **P4 — `ViewRegistration` + gallery + `x:Self` Level/templates.** The `IResourceRootSink` behavior, a gallery
  demo (event→command, data-trigger→storyboard, self-registration), then `x:Self` `Level` and the per-instantiation
  template case. Adversarial audit per the house pattern.

Each phase: a normative test matrix + an adversarial audit pass (the Bars/DataViews discipline).

---

## §12 — Deferrals / open questions

- **`x:Self` in templates (per-instantiation) + `Level > 0`** — P4; the construction-stack resolution inside a
  deferred template slice needs a spike.
- **Advanced triggers** — `KeyTrigger`, `PropertyChangedTrigger`, gesture/`ToggleButton`-state triggers.
- **Microsoft `Interaction`/`i:` XAML-namespace compatibility alias** — for porting Blend behaviors verbatim.
- **Behavior-authored resources / nested behaviors on a behavior** — probably YAGNI; revisit if a real case appears.
- **The shared-vs-own `StyleTrigger` lattice slot** — **moot under D2** (imperative `ChangePropertyAction` never
  installs a `StyleTrigger` frame), recorded here so the question isn't re-opened by reflex.
- **`InvokeCommandAction` async / `CanExecute` change tracking** — whether the trigger disables/greys when
  `CanExecute` flips (like a bar button) or only gates at fire time.

---

## §13 — Implementation status (2026-08-23)

**P0–P3 complete** on `feature/interactivity` (`Cursorial.UI.Interactivity` + tests, registered in the
solution): the §2 model on `UIObject`; the §3 lifecycle over `AttachedToTree`/`DetachedFromTree` with
re-attach, snapshot walks, re-entrancy guards, attach rollback, and the exactly-one-host rule;
`EventTrigger` (the `{Name}Event` convention + the typed `RoutedEvent` fast path) / `DataTrigger` (the
`Watch` substrate, unmet→met edges, string-Value coercion to the delivered type — WPF parity) /
`InvokeCommandAction` (payload only when `CommandParameter` is unauthored — value-source-gated) /
`ChangePropertyAction` (§6 semantics, validated `SetCurrentValue`) / `CallMethodAction` /
`BeginStoryboardAction`+`ControlStoryboardAction` / `SetFocusAction`; §9 `ViewRegistration`
(+ per-sink ownership arbitration). The BD13 inheritance hookup makes `Command="{Binding …}"` work.

**Both XAML lanes ship the §7 shape**: the loader gained the attached-collection get-or-create probe
(the WPF `Get{Name}` idiom, general); the generator's `EmitChildAssign` fills attached collections via
the static accessor (gated on `IsAttachable`); `DataTrigger.Binding` lowers as a descriptor (the
loader's `AttachBinding` twin). Flagship parity is test-pinned in both lanes (a bound command fires on
a real Click through loaded AND generated code).

**Teardown**: the new `Cursorial.UI.ITearDownParticipant` seam (the `InputBindings` special case
generalized); the hosted collection sweeps item bindings (cascading trigger actions) at the element's
end of life.

**Audited**: the house adversarial pass confirmed 13 findings (lifecycle steal/cross-kill, the teardown
binding leak, snapshot/re-entrancy corruption, semantic silent-drops, two generator-lane divergences) —
all fixed with per-finding regression tests (47 module tests total).

**Remaining (§11 P4 / §12)**: the gallery demo page (the app is the author's surface); `x:Self`
`Level > 0` + templates; the deferred trigger catalog (`KeyTrigger`, `PropertyChangedTrigger`); the
`i:` compatibility alias; `InvokeCommandAction` `CanExecute` change-tracking. Known framework-level
issue recorded out of scope: a throwing `AttachedToTree` subscriber corrupts the attach walk for any
subscriber (the module rolls back its own state; the walk hardening belongs to `Cursorial.UI`).
