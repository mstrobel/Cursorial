# S7 + S8 — oracle-pinned resource & control-infrastructure matrix (dictionaries, lookup chain, variants, templates, content, the P5 catalog)

Status: **normative test specification**, authored 2026-06-13 *before any S7/S8 code exists* (design doc §14 P5; the repo's matrix-first discipline, mirroring `precedence-matrix.md`, `layout-matrix.md`, `input-matrix.md`, `style-matrix.md`, and `binding-matrix.md`). Every numbered row below becomes exactly one xUnit `[Fact]`/`[Theory]` in `Cursorial.UI.Tests` (test authoring contract at the end, §17). The S7 resource engine and S8 control infrastructure are written *to* this matrix; a red row is an implementation bug unless a PR amends this file first.

Canonical semantics sources, in precedence order: `docs/ui-layer-design.md` §11 (S7) + §12 (S8) + §0 invariants + §13 resolutions + §14 P5 (incl. §14.3 inversions 5/6) **over** `docs/ui-layer-design/spec-resources.md` and `docs/ui-layer-design/spec-controls.md`. Places a spec is superseded by the doc and this matrix pins the doc's side:

- ① **The capability gate's algebra is the doc form**: `(Keyboard.DistinguishesKeyUpDown && Keyboard.ReportsRepeats) || Protocol.Win32InputMode` (doc §12.5), equivalent to the spec's `DistinguishesKeyUpDown && (ReportsRepeats || Win32InputMode)` only because Win32 input mode implies key-up/down — the doc's disjunctive form is canonical and is the one the rows assert. Sourced from the **undecorated negotiated** snapshot (`UIApplication.Capabilities`), never the decorated pipeline.
- ② **`When`/resource/binding-valued setters in theme styles** are P4/P5-live (the style-matrix P3 fence excluded them); theme control-style `Children` use `ResourceReference` color setters (doc §11.8) and may carry `When` (the P4 wiring). The style-matrix stays the frozen structural/pseudo oracle; this matrix owns the theme-channel + ControlTheme(0)/Template(1)/Theme(2) layer content that P3 pinned only at the key level.
- ③ **`UIApplication` is the merged S6+S7 type** (doc §13.2, punch 44): there is no separate `Application` class — the spec's `Application.Current`/`Application.Resources`/`Application.Theme`/`Application.ResourcesChanged` map onto the existing `Cursorial.UI/Hosting/UIApplication.cs` partial. Rows name `UIApplication`.
- ④ **Capability-class stamping re-points to the EFFECTIVE tier here** (inversion 6 closes at P5): the P3 scaffolding stamped color-tier classes from negotiated caps; from P5 they stamp from `ActualThemeVariant.Tier` (honoring `RequestedColorTier`). The non-color classes (`caps-motion`/`caps-kitty-keyboard`/`caps-unicode|ascii`) still stamp from negotiated caps. Rows in §3 assert the re-point.
- ⑤ **`ITemplateContent` is the runtime template-content seam** (doc §13: "ITemplateContent deferral"); Fork C wires XAML at P6. At P5 the only producer is the code-first `FuncTemplateContent` (a delegate `Build(TemplateBuildContext) → UIElement`); the typed-deferred XAML node is a recorded P6 follow-up. Rows construct templates via `FuncTemplateContent`.
- ⑥ **Window is NOT referenced** (P5 scope fence; `Window : ContentControl` lands P7). The chain walk's "Window is the last logical ancestor" hop degenerates at P5 to "the visual root" (`UIApplication.RootElement` / `host.ShowRoot`'s root); rows assert root → `UIApplication.Resources` → `UIApplication.Theme` → `CursorialTheme.BuiltIn`. No row mints a `Window` type. ScrollViewer/ScrollBar reference no Window type (inversion 5).
- ⑦ **No ItemsControl/ListBox/TextBox/Menu/ToolTip/TabControl/ProgressBar** (P9). The ContentPresenter/DataTemplate seam ItemsControl will reuse is *designed and tested* here (§8); `ParsesAccessKeyLiterals` is tested on `ButtonBase.Content`/`Label.Content` only (the other flagged owners — `MenuItem.Header`/`TabItem.Header` — are P9, recorded not tested).

**Phase 5 scope fence** (rows are written inside it — pinned by the prompt + doc §14 P5 + §14.3): **R0–R3** of S7 (dictionary + chain + variant-probe oracle; variants + subscriptions + the `UIApplication` merge + `GetResourceVersion`; `CursorialTheme.BuiltIn` + `ThemeKeys` + builders + `ResourceBrushResolver`; diagnostics) and **S8 C0 + C1-minus-TextBox + the C3 split**: `Control`/`ControlTemplate`/`TemplateInstance`/`ITemplateContent` + part validation + `OnApplyTemplate`/`Detach`; `ContentPresenter` + auto-aliasing + the DataTemplate lookup chain; `ContentControl`/`HeaderedContentControl` shells; `Decorator`/`Border`; `TextBlock`; the `AccessText` data model + `AccessTextPresenter` + `Label`; `ButtonBase`/`Button`/`RepeatButton`/`ToggleButton`/`CheckBox`/`RadioButton`; `ScrollViewer` + `ScrollBar` over S1's banded `ScrollContentPresenter` (landed P1). **`TemplateBinding` fast path** (S2 B2) is exercised by template bodies here. **Out of scope** (recorded, not tested): TextBox + `TextPresenter` + clipboard (P9, inversion 4); ItemsControl/`ItemContainerGenerator`/`SelectionModel`/ListBox (P9, C2); Menu/ContextMenu/Separator/ToolTip (P9, C4); TabControl/ProgressBar (P9, C5); Window/chrome/Popup (P7); access-keys end-to-end UX — `ShowUnderline` cue route, multi-match cycling, F10/`IMainMenu` (P9, the AccessKeyManager *core* landed P2). The `IsMultiMatch ⇒ focus-only` invocation rule and `Label.OnAccessKey → (Target ?? FindNext).Focus()` are tested as control-side behavior (the manager exists since P2).

Stage mapping (the S7 R-phases + S8 C-phases sliced into this plan's P5 stages; rows for a later stage may stay unimplemented — not red — until that stage opens, but every row is binding from now):

| Stage | Sections | Delivers |
|---|---|---|
| **R0 — dictionary + chain + variant probe** | §§1–2 | `ResourceDictionary` keyed storage (string/`Type`/`DataTemplateKey` keys), `MergedDictionaries`/`ThemeDictionaries`/`Styles`/`Seal`/`BeginUpdate`/`Version`/`Changed`, `SetDeferred` + `IDeferredResourceEntry` realization, the single-parent rule + sealed-exempt; `ThemeVariant`/`ThemeVariantKey` + the variant-probe truth table; the full `ResourceParent` chain walk + `FindResource`/`TryFindResource` + `ResourceNotFoundException`; StaticResource ambient-stack vs the walk. |
| **R1 — variants + subscriptions + the merge + version** | §3 | `ThemeVariant.FromCapabilities` derivation + `RequestedThemeBase`/`RequestedColorTier`/`ActualThemeVariant`; the `UIApplication` theme-surface merge (`Resources`/`Theme`/`ResourcesChanged`/`OnCapabilitiesChanged`); `ResourceServices.Subscribe`/`SubscribeControlTheme`/`GetResourceVersion`; `SetResourceReference`/`ResourceReference`; the subscription registry + pulse routing + snapshot/tombstone sweep + scope containment + Pause/Resume; capability-class re-point (inversion 6); the staleness contract. |
| **R2 — built-in theme + builders** | §4 | `CursorialTheme.BuiltIn` + `CreateDefault` + `ThemeKeys`; the tier-keyed palette + Type-keyed control themes (S8 content); the theme-styles channel armed at `Theme(2)`; `Cursorial.UI.Media` builders (`SolidColorBrush`/`LinearGradientBrush`/`Pen` + `IResourceValueBuilder`); `ResourceBrushResolver`; override paths. |
| **R3 — resource diagnostics** | §5 | `ResourceDiagnostics.Trace`/`Explain`/`Subscriptions`/`DeferredEntries`; `StyleDiagnostics.Explain` surfacing `ResourceReference.Key`. |
| **C0 — template spine** | §§6–8, §9 | `Control` + `ControlTemplate`/`TemplateInstance`/`ITemplateContent` + `[TemplatePart]` validation + `OnApplyTemplate`/`Detach`/`GetTemplatePart` + the template namescope + barrier; `ContentPresenter` + auto-aliasing + the DataTemplate lookup chain; `ContentControl`/`HeaderedContentControl`; `Decorator`/`Border`; `TextBlock`; `AccessText`/`AccessTextPresenter`/`Label`; `Button`. |
| **C1 — interactive leaves (minus TextBox)** | §§10–12 | `ButtonBase` completion (`ClickMode`, capture, `:pressed` via `SetInteractionState`, Space/Enter activation, `Command`/`CanExecute`→`IsEnabledCore`); `RepeatButton` (on the P2 `UITimer`); `ToggleButton`; `CheckBox`; `RadioButton`. |
| **C3 — scrolling** | §13 | `ScrollViewer` + `ScrollBar` over the banded `ScrollContentPresenter`; offset DirectProperty two-way mirrors; wheel; `:horizontal`/`:vertical`; Track/Thumb/arrow-RepeatButton parts; `Auto` visibility. |
| **Perf** | §14 | themed-control attach cost; the §14 P5 motion-storm re-assert over templated controls; invariant-3 (`:pointerover` restyle re-rasters only its zone). |

---

## 0. Conventions

### 0.1 Namespaces and placement

S7 resource types live in **`Cursorial.UI`** (`ResourceDictionary`, `IDeferredResourceEntry`, `IResourceScope`/`ResourceScopes`, `IResourceHost`, `ResourcesChangedEventArgs`/`ResourceChangeKind`, `ThemeVariant`/`ThemeVariantKey`/`ThemeBase`, `ResourceReference`, `ResourceSubscription`, `IResourceChangeListener`, `ResourceServices`, `ResourceExtensions` (`FindResource`/`TryFindResource`/`SetResourceReference`), `ResourceNotFoundException`, `DataTemplateKey`, `ResourceBrushResolver`, `ResourceDiagnostics`), source under `Cursorial.UI/Resources/`. The theme assets are `Cursorial.UI.Themes` (`CursorialTheme`, `ThemeKeys`), source under `Cursorial.UI/Themes/`. The XAML-facing builders are `Cursorial.UI.Media` (`SolidColorBrush`/`LinearGradientBrush`/`Pen`/`GradientStop`/`IResourceValueBuilder`), source under `Cursorial.UI/Media/`. The S7 theme surface merges into the existing `Cursorial.UI/Hosting/UIApplication.cs` (`Cursorial.UI`).

S8 control types live in **`Cursorial.UI.Controls`** (`Control`, `ControlTemplate`, `TemplateInstance`, `ITemplateContent`/`FuncTemplateContent`/`TemplateBuildContext`, `TemplatePartAttribute`, `ContentControl`, `HeaderedContentControl`, `ContentPresenter`, `DataTemplate`, `Decorator`, `Border`, `TextBlock`, `Label`, `AccessTextPresenter`, `ButtonBase`, `Button`, `RepeatButton`, `ToggleButton`, `CheckBox`, `RadioButton`, `ScrollViewer`, `ScrollBar`, `ClickEventArgs`, `ScrollEventArgs`, `ClickMode`), source under `Cursorial.UI/Controls/` (alongside the panels). `AccessText` (the struct) and `TextElement` live in `Cursorial.UI.Controls`; `NameScopeExtensions.RequireControl<T>` is `Cursorial.UI`. Framework source uses `using CellStyle = Cursorial.Output.Style;` where the SGR record is needed. Tests live under `Cursorial.UI.Tests/ControlMatrix/`, namespace `Cursorial.Tests.UI.ControlMatrix`.

### 0.2 Fixture

| Symbol | Meaning |
|---|---|
| `host` | `UITestHost.Create()` — 80×24, `TestCapabilities.KittyTruecolor` unless a row states a preset; `app = host.Application`. Trees attach via `host.ShowRoot(Root)`; rows follow mutations with `RunFrame()` unless asserting unit-level synchronous behavior. Unit-level rows (dictionary ops, variant-key parse, `AccessText.Parse`, chain walk over a hand-built tree) need no host — they exercise the engine on the calling (UI) thread. |
| `dict`, `d1`, `d2` | a fresh `ResourceDictionary`. `merged(d, m1, m2…)` = `d.MergedDictionaries.Add(mi)`. `theme(d, "Dark+Ansi16", entries…)` = a `ThemeDictionaries`-keyed sub-dictionary. |
| `K`, `K2` | string resource keys (`"Brush.A"`); `Vbrush`, `Vbrush2` = distinct `SolidColorBrush` values (Drawing instances); `UnsetValue` = `UIProperty.UnsetValue`. |
| `tree` | a hand-built logical tree of `Probe`-style `UIElement`s with optional `Resources`: `Root → midA → leaf` unless a row restates. `attach(tree)` = `host.ShowRoot(Root)`. `e.Res[K] = v` = `e.Resources[K] = v`. |
| `Widget` | `UIElement` subclass measuring rigid (`Widget(w×h)`), used where a non-control leaf is needed; `FancyButton : Button`; `MyButton : Button` (the exact-key miss case). |
| `tmpl(parts…) { … }` | a `ControlTemplate` with `Content = new FuncTemplateContent(ctx => …)`; `part("PART_X", () => new Border())` declares a built element with `x:Name`-equivalent registration into `ctx.NameScope`. `[TemplatePart("PART_X", typeof(Border))]`/`{ IsRequired = true }` on a test control class declares the contract. |
| `ctrl` | a test `Control` subclass exposing `ApplyTemplate()`, `GetTemplatePart<T>(name)`, override-recording `OnApplyTemplate`/`OnTemplateDetaching`, and `TemplateInstance`. `cp` = a `ContentPresenter`; `cc` = a test `ContentControl`. |
| `Vm` | the canonical INPC viewmodel (`string? Name`, `int Age`, `Addr? Address`); `Addr` = `{ string? City }`. `DT(typeof(Vm)) { … }` = a `DataTemplate { DataType = typeof(Vm) }`; `dtkey(t)` = `new DataTemplateKey(t)`. |
| `caps(...)` | a `TerminalCapabilities` built from a `TestCapabilities` preset or explicit `ColorDepth`/keyboard flags. `bg(rgb)` sets `ColorCapabilities.DefaultBackground`. `renegotiate(caps)` = `host`'s `ScriptRenegotiatedCapabilities` + `RenegotiateAsync` (drives `OnCapabilitiesChanged`). |
| `obs(e, P)` | a recording value observer on `e`'s property `P` (the precedence-matrix `notify`/`silent` surface); `eff(e,P)` = effective value; `src(e,P)` = `GetValueSource` lane. |
| `arm(e)` | `StyleDiagnostics.MatchedRules(e)` (style-matrix surface) — entries carry (selector text, **layer**, key, `IsActive`); used for theme-channel layer-ordering rows. |
| `cell(c,r)` / `row(r)` | `host` cell-grid readbacks after `RunFrame()` (the UITestHost cell/row assertion surface); `bytes()` = the frame's emitted bytes (teardown-byte/QueueControlSequence capture). |
| `ver(e)` | `ResourceServices.GetResourceVersion(e)`; `explain(e,K)` = `ResourceDiagnostics.Explain(e, K)` rendered as lines. |
| `flip(e, Bit, on)` / `batch{…}` | the element's protected `SetInteractionState` via a test shim / a `BeginInteractionUpdate` scope (style-matrix surface). |
| `RC(e)` | cumulative `Render`-call count on `e` (the layout-matrix re-raster observability surface, LD12); "zero re-raster" = zero `Render` calls that frame. `P(b)` = a boundary `b`'s published `CompositeParameters`. |
| "0 B" | `GC.GetAllocatedBytesForCurrentThread()` delta of zero after warm-up, single-threaded (repo norm). |

### 0.3 Oracle tags

`WPF` = WPF behavior (the primary oracle, terminal-adapted); `AV` = Avalonia 11; `PIN` = a Cursorial pin with no direct parent-framework analog (this matrix is the decision record); `DEV` = a deliberate deviation from a parent framework, always with rationale (inline or via the CD ledger).

### 0.4 Pinned decisions made by this matrix (CD ledger)

Each goes beyond — but never against — the canonical doc text; deliberate and binding until amended.

- **CD1 — `ResourceDictionary` comparer.** `string` keys are **ordinal** (case-sensitive); `Type`/`DataTemplateKey`/`ThemeVariantKey` by value; any other object by its own `Equals`/`GetHashCode`. `"theme.x"` and `"Theme.X"` are distinct keys. Oracle: WPF (ordinal string keys). Backed by the §3.1 private comparer.
- **CD2 — `UnsetValue` rejected at insert.** `dict[K] = UIProperty.UnsetValue` and `dict.Add(K, UnsetValue)` throw `ArgumentException` — it is the miss sentinel and may never be a stored value (a stored `null` is a legal, distinct value). Doc §11.1. PIN.
- **CD3 — duplicate-key `Add` throws; indexer-set replaces + pulses `Keyed`.** `Add` of an existing key ⇒ `ArgumentException`; `this[K] = v` overwrites and raises `Changed(Keyed, K)` + `Version++`. `Remove` of a present key raises `Changed(Keyed, K)` + `Version++` and returns true; of an absent key returns false with no pulse. Doc §11.1. WPF-shaped.
- **CD4 — sealed dictionaries never pulse and never mutate.** Post-`Seal()`, any set/`Add`/`Remove`/`MergedDictionaries`-mutate/`Styles`-set throws `InvalidOperationException`; `Changed` never fires; `Version` is frozen; deferred-entry realization (cache fill) is still legal and does **not** bump `Version`. `Seal()` deep-freezes entries/merged/theme/`Styles` recursively. Doc §11.1/§11.2/§3.1. PIN.
- **CD5 — single-parent rule, sealed-exempt.** A non-sealed dictionary added to a second owner (`IResourceHost.Resources`, another dictionary's `MergedDictionaries`/`ThemeDictionaries`) throws `InvalidOperationException`. A **sealed** dictionary is freely shared (it never pulses) — this is what legalizes template-resource multi-instance slot-in and the process-shared `BuiltIn`. Doc §11.1. PIN.
- **CD6 — deferred realization is in-place, once, version-stable.** `SetDeferred(K, entry)` then a lookup at `K` calls `entry.Realize(scope)` exactly once on success, **mutating the slot payload in place** (the backing `Dictionary` slot object is never replaced — enumeration-safe); `Version` does not bump. A throwing `Realize` resets the slot to `Deferred` (retried next lookup) and propagates the exception. A re-entrant `Realize` of the same entry (cycle) throws naming both keys. `ContainsKey`/`Keys`/`Count` never realize; `TryGetValue`/indexer-get/value-enumeration realize. Doc §11.1/§3.1. PIN.
- **CD7 — StaticResource freezes inside a deferred entry at first realization under the then-current variant.** `DeferredEntryInfo.RealizedAtVariant` makes the freeze observable; variant-sensitive references must use DynamicResource. Doc §11.1. PIN.
- **CD8 — the variant probe order is exactly** `(B,T) → (B,T−1) → … → (B,NoColor)` then `(·,T) → … → (·,NoColor)` then `(B,·)` **last** (doc §11.2; spec §3.2 worked truth table). Tier descent never ascends (a tier key declares a *minimum* capability); `(B,·)` is contractually NoColor-renderable; the `(B,·)` + `(·,T)` co-presence is ambiguous-by-construction and flagged by a **seal/load-time lint**, but resolution still follows the order (the `(·,T)` wins at tiers ≥ T by descent). `ColorDepth` numeric order is `NoColor=0 < Ansi16 < Ansi256 < Truecolor`. PIN (the truth table is pinned verbatim in §2).
- **CD9 — `ThemeVariant.FromCapabilities` derivation.** Base = relative luminance `0.2126R + 0.7152G + 0.0722B` over sRGB-normalized `ColorCapabilities.DefaultBackground` channels: `> 0.5` ⇒ `Light`, else `Dark`; a `null` or non-RGB `DefaultBackground` ⇒ `Dark`. Tier = `ColorCapabilities.Depth`. Effective variant honors `RequestedThemeBase`/`RequestedColorTier` per axis independently. Doc §11.2/§11.7, spec §3.6. PIN.
- **CD10 — `ThemeVariantKey.Parse`.** `"Dark"` → `(Dark, null)`; `"Ansi16"` → `(null, Ansi16)`; `"Dark+Ansi16"` → `(Dark, Ansi16)`; `(null, null)` (empty / unparseable) is rejected (`FormatException`/`ArgumentException`). Case-insensitive token match; base token = `Dark|Light`, tier token = `NoColor|Ansi16|Ansi256|Truecolor`. Doc §11.2. PIN.
- **CD11 — the chain walk is `ResourceParent = LogicalParent ?? TemplatedParent`** with the template-root hop slotting the owning template's sealed `Resources` in (read `tp.TemplateInstance?.Template.Resources`), terminating at the visual root → `UIApplication.Resources` → `UIApplication.Theme` → `CursorialTheme.BuiltIn` (always the final hop). `HasResources` short-circuits the per-node probe; the walk is allocation-free (static probe span, indexed merged loops). Doc §11.4, spec §3.3. WPF-shaped (no owner-window chaining).
- **CD12 — DynamicResource is a producer, not a priority.** In a `Setter`: rides `BindingPriority.Style` at the owning frame's `StyleSortKey` layer; a pulse mutates the entry in place (`OnEntryChanged`) — never frame removal/re-add. On a direct property (`SetResourceReference`/`{DynamicResource}`): a `LocalValue` producer, evicted via `IValueEvictionListener` on a later `SetValue`/`Bind`, detached by `ClearValue`. A miss = `UIProperty.UnsetValue` end-to-end (entry `HasValue = false`, lower sources promote); never conflated with a null-valued resource. Doc §11.5, spec §3.4. PIN.
- **CD13 — `ControlThemeKey : object`, exact-key, no base-chain probing.** `protected virtual object ControlThemeKey => GetType()`. `MyButton : Button` resolves nothing (incl. in `BuiltIn`) unless it overrides to `typeof(Button)` or ships its own theme; a miss fires a one-time debug diagnostic naming the key + chain. `SubscribeControlTheme` owns BOTH the `ThemeProperty` observer and the chain node under one handle; styling arms at `ControlTheme(0)` and re-arms on identity change. Doc §11.3/§11.5. PIN.
- **CD14 — capability classes stamp from the EFFECTIVE tier (inversion 6).** Color-tier classes (`caps-truecolor|ansi256|ansi16|nocolor`) stamp from `ActualThemeVariant.Tier`; non-color classes (`caps-motion`, `caps-kitty-keyboard`, `caps-unicode|ascii`) stamp from negotiated caps. A `RequestedColorTier` "preview Ansi16" gets Ansi16 resources **and** Ansi16-gated styles; a `RequestedThemeBase` flip changes only resources (no class change, no re-match). Doc §11.7, §14.3 inversion 6. PIN (re-points the P3 scaffolding).
- **CD15 — variant flip is resource-event-only.** A variant change raises `ActualThemeVariantChanged` + `ResourcesChanged(CatchAll, null)` and pulses every root's registry; **no selector re-match, no style frame re-arm, no dictionary mutation, no dictionary `Changed`**. Control-theme subscriptions re-resolve to the *same* `Style` instance (keyed per `Type`, not per variant) → identity short-circuit → no re-templating. Doc §11.7, spec §3.6. PIN.
- **CD16 — `GetResourceVersion` is root-global + monotonic; 0 when detached.** Bumps on every pulse (keyed or catch-all, incl. variant flips) reaching that root. The S8 staleness contract: text-bearing controls (`TextBlock`) include `(GetResourceVersion(this), ActualThemeVariant)` in their `FormattedText` cache key and **never** subscribe to `ResourceDictionary.Changed` (sealed dictionaries never pulse). Element attach forces one re-resolve regardless of stored version (covers cross-root moves). Doc §11.6, spec §2.4/§3.8. PIN.
- **CD17 — measure-time template expansion.** `Control.ApplyTemplate()` runs at the head of `Measure` before `MeasureOverride`. Dirty sequence (doc §12.2): ① `OnTemplateDetaching(old)` → `old.Detach()` → remove `old.Root` as visual child; ② resolve `Template` (null ⇒ no child + one-time diagnostic); ③ `Instantiate(this)` — build, stamp `TemplatedParent = this` on null-stamped elements (**foreign non-null `TemplatedParent` throws**), arm `Styles` at Template layer, set the template namescope on the control; ④ validate `[TemplatePart]` **immediately after Instantiate, before visual attach**; ⑤ attach `Root` as **visual child only**; ⑥ `OnApplyTemplate()` (re-entrant `Template` sets defer behind a guard); ⑦ `MeasureOverride`. WPF-shaped.
- **CD18 — the template barrier (invariant 5).** `ControlTemplate`-built elements carry `TemplatedParent != null` ⇒ stylable only via `/template/`. `DataTemplate.Build`-content gets `TemplatedParent = null` (DEV from WPF — data content is app-styleable; the hybrid model has no `DataTemplate.Triggers`). Part names live only in `TemplateInstance.NameScope`; document and template namescopes never see each other. Doc §12.2, §0 invariant 5. WPF for the barrier; DEV for DataTemplate.
- **CD19 — `[TemplatePart]` validation timing + verdicts** (doc §12.2 step 4, spec §3.3): declared part of wrong type ⇒ throw always (`InvalidOperationException` naming `TargetType`/part/expected/actual); `IsRequired` missing ⇒ throw always; optional missing ⇒ `GetTemplatePart` returns null and control degrades. Seal-time validation is impossible (`ITemplateContent` opaque until `Build`). PIN.
- **CD20 — `Detach()` is store-owned retraction, never set-back** (invariant 4): cookie/frame retraction of Template-layer style frames + `TemplateBinding` teardown + presenter auto-alias observer teardown + `TemplateNameScope` clear. The DEBUG subscription-leak tracker asserts zero live resource/binding/style nodes after a `Detach()` (and at root teardown). Doc §12.1/§12.2, §11.10, §11.6. PIN.
- **CD21 — ContentPresenter auto-aliasing is a read-through fallback, never an installed binding** (doc §12.3, spec §2.2): when the presenter's `Content`/`ContentTemplate` have no frame and no local entry (`IsSet == false`), it reads through to `TemplatedParent.Content`/`.ContentTemplate`; a typed property-changed observer on the templated parent (no presenter store entry) re-realizes on notification, re-checking `IsSet` so a later explicit presenter value wins; lifetime = template instance, torn down in `Detach()`. PIN.
- **CD22 — the DataTemplate lookup chain** (doc §12.3, spec §3.4): ① explicit `ContentTemplate` (post-aliasing) → ② implicit `DataTemplateKey(t)` walk (presenter → templated-parent hop → logical ancestors → `UIApplication` → `BuiltIn`, runtime type then each base up to but excluding `object`; interfaces deferred) → ③ `UIElement` passthrough (logical child of the templated parent, visual child of the presenter) → ④ `AccessText` ⇒ `AccessTextPresenter` (extended to plain strings when `RecognizesAccessKey`) → ⑤ fallback `TextBlock(Convert.ToString(content))`. Same template identity on a content change reuses the subtree (DataContext update only). PIN.
- **CD23 — activation on key Down** (doc §13, spec §3.9): Space activates `ButtonBase` on **Down** (`IsRepeat`-guarded); Enter = immediate click; the pressed-latch visual (Space held → `:pressed` until Up) is a capability-gated nicety where Up is reported, never the activation gate. ButtonBase takes mouse **capture for both `ClickMode` values**; `OnLostMouseCapture` ⇒ unpressed, no click; Space latch cleared on `OnLostFocus`. WPF-shaped activation; the down-activation pin is terminal-legacy honesty.
- **CD24 — `IsPressed` is the `InteractionState.Pressed` mirror** (doc §12.7, ND12): `Pressed` is set via S3's `SetInteractionState` (window-wide focus-out/deactivation clears participate via the pressed-holder set); `IsPressed` (read-only `DirectProperty`) mirrors it for binding/`When`; `:pressed` flips via the interaction-state path. PIN.
- **CD25 — `IsEnabledCore` includes `CanExecute`** (doc §12.7, spec §3.9): `ButtonBase` overrides `IsEnabledCore` to `&& (Command is null || Command.CanExecute(CommandParameter))`; effective-enabled = `IsEnabled ∧ IsEnabledCore ∧ ancestor-effective` (S1 plumbing → `InteractionState.Disabled` → `:disabled`). `CanExecuteChanged` is subscribed on attach, unsubscribed on detach **and** on `Command` change; an `IsEnabledCore` input change calls S1's `InvalidateIsEnabledCore()`. WPF-shaped.
- **CD26 — ToggleButton 3-state cycle is WPF order** (doc §12.7, spec §3.9): `IsChecked : bool?`; `IsThreeState` false ⇒ `false→true→false`; true ⇒ `false→true→null(indeterminate)→false`. `:checked` (true) and `:indeterminate` (null) flip via `PseudoClassMapping`'s multi-class projection. CheckBox/RadioButton glyphs are theme resources (ASCII default `[ ] [x] [-]` / `( ) (*)`). PIN/WPF.
- **CD27 — RadioButton grouping** (doc §12.7, spec §3.9): checking a radio sets group peers' `IsChecked = false` via `SetCurrentValue` (preserves their bindings); group = same logical parent when `GroupName` is null, else all same-named radios within the surface root; arrow keys move + check within the group (consuming the event). WPF-shaped.
- **CD28 — ScrollViewer offsets are DirectProperty two-way mirrors of SCP's styled offsets** (doc §12.4, spec §3.9): `ScrollViewer.HorizontalOffset`/`VerticalOffset` (`DirectProperty`, two-way bindable) mirror `ScrollContentPresenter.ScrollOffsetColumn/Row` (**styled**, `AffectsComposite`, animatable — the doc supersedes the spec's "DirectProperty offsets, not animatable, hand-routed"). Wheel: `WheelDeltaY/120 × 3` lines, Shift/`WheelDeltaX` horizontal, unconsumed bubbles. `Auto` re-measure loop broken by remember-last-verdict. ScrollBar = 1 cell wide; rail `Pen` + `█` thumb (min 1); arrows are `RepeatButton`s. `:horizontal`/`:vertical` on ScrollBar. Doc §12.4/§12.7 over spec §3.9 (the DirectProperty/non-animatable spec stance is the inverted one). PIN/DEV.
- **CD29 — RepeatButton repeats on the P2 `UITimer` slice** (doc §12.7, §14.3 inversion 1): `ClickMode.Press` default; after `Delay` (400 ms) then every `Interval` (60 ms) while pressed + pointer-over; timer canceled on release/capture-loss and unhooked per the unhook-before-rewire convention. Frame-aligned (ND20 coalescing). PIN.
- **CD30 — theme channels arm at the pinned layers** (doc §11.8, §12.1): a Type-keyed control theme (a selector-less `Style` rooted at `^`) arms at `ControlTheme(0)` wherever found in the chain; `ControlTemplate.Styles` arm at `Template(1)`; `UIApplication.Theme`'s `ResourceDictionary.Styles` (flattened merged-order-then-own-last) arm at `Theme(2)`. Element/window `Styles` slots are ignored in v1 (debug-flagged). Layer beats specificity (a P3 pin), so app styles (3+) always beat theme styles. PIN.

---

## 1. `ResourceDictionary` storage, keys, merged/seal/deferred (R0) — C1–C30

Unit-level rows over a fresh `dict` (no host) unless a row attaches a tree.

### 1.1 Keyed storage + comparer + the UnsetValue floor

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C1 | `dict` | `dict[K] = Vbrush`; `dict[K]` | round-trips `Vbrush`; `ContainsKey(K)` true; `Count == 1` | WPF |
| C2 | `dict` | `dict["Theme.X"] = v`; `dict["theme.x"]` | second key is **distinct** (ordinal) — read returns null/`TryGetValue` false; two entries coexist | WPF (CD1) |
| C3 | `dict` keyed by `typeof(Button)` and `dtkey(typeof(Vm))` | get each | `Type` and `DataTemplateKey` keys resolve by value; `typeof(Button)` and `dtkey(typeof(Button))` are **distinct** keys (CD3 collision-free) | PIN (CD1) |
| C4 | `dict` | `dict[K] = UnsetValue` / `dict.Add(K, UnsetValue)` | both throw `ArgumentException`; `dict[K] = null` then `TryGetValue(K, out v)` ⇒ true, `v == null` (stored null is legal, distinct from miss) | PIN (CD2) |
| C5 | `dict` with `K` present | `dict.Add(K, v2)` | throws `ArgumentException` (duplicate); `dict[K] = v2` (indexer) overwrites and pulses `Keyed(K)`, `Version++` | WPF (CD3) |
| C6 | `dict` with `K` present, a `Changed` recorder | `dict.Remove(K)` then `dict.Remove("absent")` | first returns true + raises `Changed(Keyed, K)` + `Version++`; second returns false, no pulse, `Version` unchanged | WPF (CD3) |
| C7 | `dict` with a deferred entry at `K` | `ContainsKey(K)`, `Keys`, `Count` | none realize the entry (`DeferredEntryInfo(K).State == Deferred` afterward); only `TryGetValue`/indexer-get/value-enumeration realize | PIN (CD6) |
| C8 | `dict` with a deferred entry | `TryGetResource(K, variant, out v)` | a single-hop probe of **this** dictionary (no chain); realizes; returns true (§2 exercises the variant probe) | PIN (doc §11.1) |

### 1.2 MergedDictionaries + own-beats-merged + later-wins

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C9 | `d` own `K=A`; merged `[m1: K=B]` | resolve `K` via `d.TryGetResource` | `A` — **own beats merged** | WPF |
| C10 | `d` no own `K`; merged `[m1: K=B, m2: K=C]` | resolve `K` | `C` — **later merged wins** (last-to-first scan) | WPF |
| C11 | `d` no own `K`; merged `[m1: K=B]`, `m1` itself merges `[mm: K=D]` | resolve `K` | `B` — own-of-merged beats merged-of-merged, recursively | WPF |
| C12 | `d` with merged `m1`; add `m1` to `d2.MergedDictionaries` | the second add | throws `InvalidOperationException` (single-parent, `m1` not sealed) | PIN (CD5) |
| C13 | sealed `m1`; add to `d.MergedDictionaries` and `d2.MergedDictionaries` | both adds | both succeed (sealed-exempt sharing) | PIN (CD5) |

### 1.3 Seal + BeginUpdate + Version

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C14 | `dict` with entries + merged + theme + `Styles` | `Seal()` then mutate any of them | every mutation throws `InvalidOperationException`; `IsSealed` true; merged/theme/`Styles` are recursively sealed | PIN (CD4) |
| C15 | sealed `dict`, a `Changed` recorder | force a deferred-entry realization via lookup | realization succeeds (cache fill legal on sealed); `Changed` never fires; `Version` unchanged | PIN (CD4/CD6) |
| C16 | `dict`, a `Changed` recorder | `using (dict.BeginUpdate()) { dict[K1]=…; dict[K2]=…; }` | **one** `Changed(CatchAll, null)` at dispose, not two `Keyed` pulses; `Version` bumped once | PIN (doc §11.1) |
| C17 | `dict` | nested `BeginUpdate` scopes | outermost dispose pulses once (refcounted); a `Seal()` inside an open scope throws; a scope outliving the dispatcher turn is DEBUG-asserted | PIN (doc §11.1) |
| C18 | `dict`, `Version` read before/after | `dict[K]=v` (new), `dict[K]=v` (same value), `dict[K]=v2`, `Remove(K)` | `Version` bumps on each **structural** change (insert/overwrite/remove); a deferred-realization cache-fill does not bump (C15) | PIN (CD3/CD6) |

### 1.4 Deferred entries (the ITemplateContent/lazy-slice runtime contract)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C19 | `dict.SetDeferred(K, entry)` where `entry.Realize` returns `Vbrush` and counts calls | two lookups at `K` | first realizes (1 call, returns `Vbrush`), second returns the cached `Vbrush` (still 1 call) — at-most-once on success | PIN (CD6) |
| C20 | a deferred entry; enumerate `dict` mid-realization (a value enumerator) | enumerate values | realization mutates the slot payload **in place** — the backing `Dictionary` slot is not replaced, so the enumerator's version check does not trip; `Version` unchanged | PIN (CD6) |
| C21 | `entry.Realize` throws on first call, returns `Vbrush` on second | lookup, catch, lookup again | first lookup propagates the exception and resets the slot to `Deferred`; second lookup realizes successfully (transient failure not poisoned) | PIN (CD6) |
| C22 | `entry.Realize` re-enters the same key (StaticResource cycle) | lookup | throws naming **both** keys (cycle); the `Realizing` tri-state is observed only within the synchronous realization stack | PIN (CD6) |
| C23 | a deferred entry whose `Realize` captures a StaticResource; `dict` under variant `(Dark,Ansi256)` at first lookup, then variant flips to `(Light,Ansi256)` | lookup, flip, lookup | the captured StaticResource is **frozen** at the first-realization variant; `DeferredEntryInfo(K).RealizedAtVariant == (Dark,Ansi256)` makes the freeze observable; a variant-sensitive reference would need DynamicResource | PIN (CD7) |
| C24 | `IDeferredResourceEntry` realizing through `IResourceScope lexicalScope` (`ResourceScopes.ForDictionary(d, parent)`) | realize | the entry receives the lexical scope Fork C would have captured; resolves a StaticResource against it (forward-reference-free) | PIN (doc §11.1) |

### 1.5 Styles slot + ThemeDictionaries shape (storage only; behavior in §2/§4)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C25 | `dict.Styles = new Styles{…}` with a `Changed` recorder | set + mutate the `Styles` | each routes one owner `Changed(CatchAll, null)` (the theme-styles channel pulse path); reading it back returns the same instance | PIN (doc §11.1/§3.1) |
| C26 | `dict.ThemeDictionaries["Dark+Ansi16"] = sub` | get by `ThemeVariantKey.Parse("Dark+Ansi16")` | the sub-dictionary round-trips; `ThemeDictionaries` is keyed by `ThemeVariantKey` (value equality), lazy-allocated | PIN (CD10) |
| C27 | `dict` with no `ThemeDictionaries` allocated | `TryGetResource(K, v)` | the null `ThemeDictionaries` check short-circuits the whole probe step (common-case allocation-free) | PIN (spec §3.2) |
| C28 | `dict.Source = uri` with `ResourceDictionaryLoader.LoadCallback == null` | trigger load | informative `InvalidOperationException` (callback set by `Cursorial.UI.Xaml`'s module init at P6; absent at P5); tests save/restore the static | PIN (doc §11.1) |
| C29 | `IResourceHost` over a `UIElement`: `HasResources` before any `Resources` touch | read `HasResources` then `Resources` then `HasResources` | `false` before (no lazy-alloc on the `HasResources` read), `Resources` lazy-allocates, `true` after | PIN (doc §11.1) |
| C30 | `dict` with `K` deferred-then-realized; `ResourceDiagnostics.DeferredEntries(dict)` | inspect before/after a lookup | reports the entry as `Deferred` then `Realized` with `RealizedAtVariant` (R3; here the data contract is asserted at the storage layer) | PIN (doc §11.10) |

---

## 2. Theme variants + the lookup chain + StaticResource (R0) — C31–C58

### 2.1 ThemeVariant / ThemeVariantKey

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C31 | — | `ThemeVariantKey.Parse` on `"Dark"`, `"Ansi16"`, `"Dark+Ansi16"`, `"light+truecolor"`, `""`, `"Mauve"` | `(Dark,null)`, `(null,Ansi16)`, `(Dark,Ansi16)`, `(Light,Truecolor)` (case-insensitive); `""` and `"Mauve"` throw; `(null,null)` is rejected | PIN (CD10) |
| C32 | `caps(bg=#000000, Depth=Truecolor)` | `ThemeVariant.FromCapabilities` | `(Dark, Truecolor)` — luminance 0 ≤ 0.5 | PIN (CD9) |
| C33 | `caps(bg=#FFFFFF, Depth=Ansi256)` | `FromCapabilities` | `(Light, Ansi256)` — luminance 1 > 0.5 | PIN (CD9) |
| C34 | `caps(bg=null, Depth=Ansi16)` and `caps(bg=Palette(7), Depth=Ansi16)` | `FromCapabilities` | both `(Dark, Ansi16)` — null / non-RGB ⇒ Dark | PIN (CD9) |
| C35 | `caps(bg=#808080…)` straddling the threshold | `FromCapabilities` | luminance `> 0.5` ⇒ Light, `== 0.5`/`< 0.5` ⇒ Dark (the boundary is strictly-greater for Light) | PIN (CD9) |

### 2.2 The variant probe truth table (doc §11.2 / spec §3.2, pinned verbatim)

`K present in` lists the `ThemeDictionaries` sub-keys carrying `K`; the cells are the **winning sub-key** at each effective variant (a `[Theory]` per row, one case per cell). `D` = `Dark`. Tiers high→low: `Truecolor > Ansi256 > Ansi16 > NoColor`.

| # | K present in | (D,True) | (D,256) | (D,16) | (D,NoColor) | Oracle |
|---|---|---|---|---|---|---|
| C36 | (D,256), (D,16), (·,NoColor) | (D,256) | (D,256) | (D,16) | (·,NoColor) | PIN (CD8) |
| C37 | (D,·) only | (D,·) | (D,·) | (D,·) | (D,·) — the NoColor-safety contract | PIN (CD8) |
| C38 | (D,·), (·,16) | (·,16) | (·,16) | (·,16) | (D,·) | PIN (CD8) — *descent reaches `(·,16)` before the catch-all at tiers ≥ Ansi16; this pair is exactly what the lint flags* |
| C39 | (D,True) only | (D,True) | **miss** | **miss** | **miss** | PIN (CD8) — a Truecolor entry serves only Truecolor (descent never ascends) |
| C40 | (D,16) only | (D,16) | (D,16) | (D,16) | **miss** | PIN (CD8) — `(B,16)` serves Ansi16 and **above**; NoColor is below ⇒ miss |
| C41 | (D,256) and (·,256) co-present | (D,256) | (D,256) | miss-then-(D,256)? | — | PIN (CD8) — exact-base `(D,256)` beats wildcard `(·,256)` at every tier ≥ 256; at Ansi16 both miss (256 is above 16) |
| C42 | `(B,·)` + `(·,T)` co-presence | seal/load lint | a `seal()`/load-time **lint diagnostic** names `K` and tells the author to use exact `(B,T)` keys; resolution still follows CD8 (the `(·,T)` wins at tiers ≥ T) | PIN (CD8) |
| C43 | per-dictionary order | `TryGetResource(K, v)` with `K` in ThemeDictionaries AND own AND merged | ThemeDictionaries probe (variant, recursive) → own entries → merged last-to-first — variant-specific beats generic within a dictionary | PIN (spec §3.2) |

### 2.3 The chain walk (FindResource / TryFindResource)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C44 | `tree` Root→midA→leaf; `K=A` on `midA`, `K=B` on Root | `leaf.FindResource(K)` | `A` — nearest logical ancestor with the key wins (walk stops at first hit) | WPF (CD11) |
| C45 | `K` only on `UIApplication.Resources` | `leaf.FindResource(K)` | found at the app hop (root → `UIApplication.Resources`) | WPF (CD11) |
| C46 | `K` only on `UIApplication.Theme` | `leaf.FindResource(K)` | found at the Theme hop (after app Resources, before BuiltIn) | WPF (CD11) |
| C47 | `K` only in `CursorialTheme.BuiltIn` | `leaf.FindResource(K)` | found at the final hop — BuiltIn is always last | PIN (CD11) |
| C48 | `K` nowhere | `leaf.FindResource(K)` / `leaf.TryFindResource(K, out v)` | `FindResource` throws `ResourceNotFoundException` whose `SearchedScopes` renders every hop (one per line); `TryFindResource` returns false, `v == null` | PIN (doc §11.4) |
| C49 | a template part `p` (`LogicalParent == null`, `TemplatedParent == ctrl`); `K` in the template's sealed `Resources`; `K2` on `ctrl`'s logical ancestor | `p.FindResource(K)` and `p.FindResource(K2)` | `K`: the template-resource hop slots in (`tp.TemplateInstance.Template.Resources`); `K2`: after the template hop the walk continues at the templated parent and up *its* logical chain | PIN (CD11) |
| C50 | DataTemplate-generated content (normal `LogicalParent`, `TemplatedParent == null`) with the data root's own `Resources` carrying `K` | `FindResource(K)` from inside | DataTemplate-own `Resources` are **excluded** from the chain in v1 — `K` is not found via the data root; the walk uses the normal logical chain | DEV (doc §11.4, recorded v1 cut) |
| C51 | a leaf with no `Resources` between two ancestors that have them | `FindResource` | `HasResources == false` nodes are skipped (probe short-circuit); the walk reaches the next ancestor with resources | PIN (CD11) |
| C52 | `K` on `midA` (a `(Dark,Ansi256)` ThemeDictionaries entry); `app.ActualThemeVariant == (Dark, Truecolor)` | `leaf.FindResource(K)` then `leaf.TryFindResource(K, (Dark,Ansi16))` | the no-variant overload probes at `ActualThemeVariant` (Truecolor → descends to Ansi256, hit); the explicit-variant overload probes at the given `(Dark,Ansi16)` (miss — 256 is above 16) | PIN (CD11/CD8) |

### 2.4 StaticResource vs the walk + edge cases

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C53 | a StaticResource reference (Fork C's lexical/ambient stack, default `ResourceScopes.ForApplication()`) | resolve | resolves against the **ambient stack at instantiation**, NOT the chain walk — load-order explicit, forward-reference-free; a key not yet defined in the ambient stack is a load error, never a runtime miss | PIN (doc §11.4) |
| C54 | a detached `leaf` (no root) | `leaf.TryFindResource(K)` with `K` only on `UIApplication.Resources` | the app/BuiltIn hops still resolve (`UIApplication.Current` is reachable); element-scoped misses return false; `GetResourceVersion(leaf) == 0` (detached) | PIN (CD16) |
| C55 | `tree` with `K=A` on Root; move `leaf` from Root's subtree to a second root with `K=B` | resolve `K` before and after the move | `A` before; `B` after — the chain re-resolves from the new logical position (attach forces one re-resolve, CD16) | PIN (CD16) |
| C56 | `FindResource` chain with a deferred entry mid-chain that realizes to `Vbrush` | resolve | the deferred entry realizes during the walk and contributes its value; subsequent walks hit the cached value | PIN (CD6/CD11) |
| C57 | child "window" (P5: a second root) does not chain to an "owner" | resolve `K` present only on the first root | not found from the second root — roots do not chain to each other (WPF-parity no owner-chaining; degenerate at P5 with no `Window` type) | WPF (CD11) |
| C58 | `ResourceDiagnostics.Trace(leaf, K)` / `.Explain(leaf, K)` | inspect a hit and a miss | one line per hop incl. the variant probe keys tried, merged recursion, hit/miss, deferred-then-realized (R3 acceptance test; the data shape is pinned now) | PIN (doc §11.10) |

---

## 3. Variants live + subscriptions + the UIApplication merge + version (R1) — C59–C92

Host-level rows (`app = host.Application`); `RunFrame()` after mutations unless asserting synchronous behavior.

### 3.1 The UIApplication theme-surface merge (punch 44)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C59 | `app` (the merged type) | read `app.Resources`, `app.Theme`, `app.RequestedThemeBase`, `app.RequestedColorTier`, `app.ActualThemeVariant`, subscribe `ActualThemeVariantChanged`/`ResourcesChanged` | all exist on the single `UIApplication` (no separate `Application`); `Theme` defaults null (BuiltIn only); the type also carries the S6 host/loop surface (`Dispatcher`, `InputDispatcher`, the frame loop) | PIN (③) |
| C60 | `app.Resources` replaced with a fresh dictionary | assign + observe `ResourcesChanged` | one `ResourcesChanged(CatchAll, null)` fans to all roots (app-scope replace is a catch-all) | PIN (doc §11.3) |
| C61 | resolution across layers: `K` on `app.Resources` and a different value on `app.Theme` | `leaf.FindResource(K)` | `app.Resources` wins (it precedes `app.Theme` in the chain, doc §11.4) | WPF (CD11) |
| C62 | a scoped value: `K` on `midA.Resources` shadowing `app.Resources[K]` | resolve from `leaf` vs from a sibling outside `midA` | nearer scope (`midA`) wins for `leaf`; the app value wins for the sibling | WPF (CD11) |

### 3.2 Variant lifecycle (OnCapabilitiesChanged / RequestedThemeBase / RequestedColorTier)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C63 | `app`, startup with `caps(bg=#000, Truecolor)` | inspect `ActualThemeVariant` | `(Dark, Truecolor)`; `OnCapabilitiesChanged` was called at startup (one of the explicit fan-out calls) marshaled to the UI thread | PIN (CD9, doc §11.7) |
| C64 | `app.ActualThemeVariant == (Dark,Truecolor)`; subscribe both events | `renegotiate(caps(bg=#FFF, Truecolor))` | `ActualThemeVariant → (Light, Truecolor)`; `ActualThemeVariantChanged` then `ResourcesChanged(CatchAll, null)` raised (order pinned); every root's registry pulsed; **no** dictionary mutates, **no** dictionary `Changed` | PIN (CD15) |
| C65 | `app.RequestedThemeBase = Light` over a Dark terminal | set + inspect | `ActualThemeVariant.Base == Light` (override beats derived, per axis); `Tier` still negotiated; a `ResourcesChanged(CatchAll)` fires; **no capability-class change, no re-match** (a base flip changes only resources, CD14) | PIN (CD9/CD14) |
| C66 | `app.RequestedColorTier = Ansi16` over a Truecolor terminal | set + inspect | `ActualThemeVariant.Tier == Ansi16`; resources resolve at Ansi16 AND capability color-tier classes stamp `caps-ansi16` (the effective tier, CD14) | PIN (CD14) |
| C67 | clear `RequestedThemeBase`/`RequestedColorTier` back to null | set null | re-derives from the terminal (`derivedBase`, `negotiatedDepth`); one `ResourcesChanged` if the effective variant changed, none if it didn't | PIN (CD9) |
| C68 | a `RenegotiateAsync` that changes nothing material | renegotiate(same caps) | `ActualThemeVariantChanged`/`ResourcesChanged` do **not** fire (effective variant unchanged); idempotent | PIN (doc §11.7) |

### 3.3 Capability-class re-point (inversion 6 closes here)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C69 | `app` over Truecolor; `app.RequestedColorTier = Ansi256` | inspect the root's stamped classes | color-tier class is `caps-ansi256` (from `ActualThemeVariant.Tier`, NOT the negotiated Truecolor — the P5 re-point) | PIN (CD14) |
| C70 | negotiated `caps-motion`/`caps-kitty-keyboard`/`caps-unicode` flags with a `RequestedColorTier` override | inspect classes | non-color classes stamp from **negotiated** caps (unchanged by the tier override); only the color-tier class follows the effective tier | PIN (CD14) |
| C71 | a `caps-ansi16`-classed style rule reassigning a glyph resource reference; preview Ansi16 | resolve the glyph | the Ansi16-gated style activates (effective tier) and the glyph resource resolves at the Ansi16 dictionary — resources and styles agree (no desync) | PIN (CD14, doc §11.7) |
| C72 | re-point timing | the class re-stamp fires off `ActualThemeVariantChanged` | the color-tier class flip rides the variant-changed event, not a separate negotiated-caps hook | PIN (CD14) |

### 3.4 Subscriptions, registry, pulse routing, scope containment

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C73 | `leaf.SetResourceReference(P, K)` with `K` on Root | inspect `src(leaf,P)`/`eff(leaf,P)` | a producer at `BindingPriority.LocalValue`; `eff` = the resolved value; `src == LocalValue` | PIN (CD12) |
| C74 | C73, then `Root.Resources[K] = Vbrush2` | mutate + `RunFrame()` | the keyed pulse re-resolves the node → `OnResourceChanged(K, Vbrush2)` → entry mutated in place → `eff(leaf,P) == Vbrush2`; `AffectsRender` ⇒ a re-raster of `leaf`'s zone | PIN (CD12, doc §11.6) |
| C75 | C73, then `set(leaf, P, vLocal)` (a later `SetValue` at LocalValue) | the SetValue | the resource producer is evicted via `IValueEvictionListener`; it disposes its subscription (no zombie clobber on the next pulse); `eff == vLocal` | PIN (CD12) |
| C76 | C73, then `clear(leaf, P)` | `ClearValue` | the producer detaches; `eff` falls to the next source; the registry node is disposed | PIN (CD12) |
| C77 | `K` missing everywhere; `leaf.SetResourceReference(P, K)` | inspect | `initialValue == UnsetValue` ⇒ entry `HasValue == false` ⇒ lower sources promote (`eff == default(P)`); one-time debug miss diagnostic with the rendered chain | PIN (CD12) |
| C78 | C77, then `Root.Resources[K] = Vbrush` (key appears) | mutate | the node re-resolves to `Vbrush` (transition out of missing); `eff == Vbrush` | PIN (CD12) |
| C79 | shadowing: `leaf.SetResourceReference(P, K)` resolving to `app.Resources[K]`; then `midA.Resources[K] = Vbrush2` (a nearer scope) | add at `midA` + `RunFrame()` | the keyed pulse's **scope-containment filter** re-resolves `leaf` (it's a descendant of `midA`) even though its previously-hit dictionary (app) never changed → `eff == Vbrush2` | PIN (doc §11.6) |
| C80 | a sibling outside `midA`'s subtree, also referencing `K` | the C79 mutation | the sibling is **not** re-resolved (not contained by `midA`) — stays at the app value | PIN (doc §11.6) |
| C81 | a node Paused (`subscription.Pause()`), then several keyed pulses, then `Resume()` | pause, pulse×3, resume | Pause/Resume are O(1) flag writes; on `Resume` the node re-resolves **at most once** via version compare regardless of pulse count; Resume re-resolves **before** the value is read | PIN (doc §11.6, spec §2.4) |
| C82 | a CatchAll sweep that, mid-sweep, re-arms styling (a `SubscribeControlTheme` listener disposes a node and a new `ApplyRule` Subscribes) | trigger a theme-origin catch-all | snapshot/tombstone: a node subscribed during the sweep is **not** visited (fresh at Subscribe); a node disposed during the sweep is tombstoned (`Dead`, skipped) and compacted after — no crash, no double-visit (the re-templating-during-theme-swap regression) | PIN (doc §11.6) |
| C83 | a listener that mutates *resources* during a sweep | trigger | a follow-up pulse is queued and drained to a fixpoint (generation cap 16 + cycle diagnostic) | PIN (doc §11.6) |
| C84 | a detached element holding subscriptions | detach | the element's inline handle list unregisters its nodes (O(own subscriptions)); a re-attach forces one re-resolve regardless of stored version | PIN (CD16) |
| C85 | DEBUG: a root teardown with live nodes | tear down | the subscription-leak tracker asserts zero live nodes for the root (`ResourceDiagnostics.Subscriptions(root)` reports none) | PIN (doc §11.6/§11.10) |

### 3.5 SubscribeControlTheme (one handle, both watches) + GetResourceVersion staleness

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C86 | `ctrl` with `ControlThemeKey == typeof(ctrl)`, a Type-keyed theme in `BuiltIn`; `ResourceServices.SubscribeControlTheme(ctrl, listener, out theme)` | subscribe | `theme` = `ctrl.Theme ?? chain lookup`; the single handle owns **both** a `ThemeProperty` observer and a chain registry node; styling arms the result at `ControlTheme(0)` | PIN (CD13) |
| C87 | C86, then `ctrl.Theme = explicitStyle` | set `ThemeProperty` | the listener fires (identity change); styling re-arms (frame removal + add, store-owned retraction — never set-back) | PIN (CD13) |
| C88 | C86 under a variant flip (CD15) | flip variant | the control-theme subscription re-resolves to the **same** `Style` instance (themes keyed per `Type`, not per variant) → identity short-circuit → no re-templating | PIN (CD15) |
| C89 | `ver(leaf)` before/after a keyed pulse, a catch-all pulse, and a variant flip | read each | bumps (monotonic) on **every** pulse reaching the root (keyed, catch-all, variant); root-global (any pulse invalidates every cache in the root) | PIN (CD16) |
| C90 | a `TextBlock` whose `FormattedText` cache key includes `(ver(this), ActualThemeVariant)`; a resource pulse changes a referenced brush | pulse + `RunFrame()` | the next render re-parses (the key changed via `ver`); the TextBlock holds **no** `ResourceDictionary.Changed` subscription (sealed dictionaries never pulse — the staleness mechanism IS the cache key) | PIN (CD16, doc §11.6) |
| C91 | a variant flip with no `RunFrame()` between | flip then read `ver` | `ver` bumps synchronously on the pulse (UI thread); the cache invalidation is realized at the next render | PIN (CD16) |
| C92 | cross-surface routing (P5: a popup-less analog — a child registered under a different root); a root-scope pulse | pulse | a node registers under the registry of the root its **logical chain** tops out at; a root-scoped pulse sweeps that root's nodes (the popup→host fan rule degenerates at P5; the registration-root rule is asserted) | PIN (doc §11.6) |

---

## 4. Built-in theme + ThemeKeys + builders + ResourceBrushResolver (R2) — C93–C112

### 4.1 CursorialTheme.BuiltIn structure

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C93 | `CursorialTheme.BuiltIn` | inspect | sealed (`IsSealed`), process-shared (same instance across reads), the final lookup hop; code-first (no XAML dependency) | PIN (doc §11.8) |
| C94 | `CursorialTheme.CreateDefault()` | inspect + mutate | returns an **unsealed** structural copy (fresh shells, shared value/control-theme instances); mutating it does not affect `BuiltIn`; assignable to `app.Theme` | PIN (doc §11.8) |
| C95 | `ThemeKeys.*` constants | resolve `Theme.SurfaceBrush`/`TextBrush`/`AccentBrush`/`FocusPen`/`BorderPen`/`ObscuredOverlayBrush`/`AccessKeyUnderlineBrush` from a leaf | each resolves through `BuiltIn` (string keys, typo-proof from C#, verbatim from XAML) | PIN (doc §11.8) |
| C96 | the palette tier layout | inspect `BuiltIn.ThemeDictionaries` | **no color-bearing value in `(B,·)`**: RGB brushes at `(Dark,Ansi256)`/`(Light,Ansi256)` (served at Truecolor via descent), hand-picked `Colors.*` + ASCII-glyph pens at `(B,Ansi16)`, attribute-only values at `(·,NoColor)`; lint-clean (no `(B,·)`+`(·,T)` collisions) | PIN (doc §11.8, CD8) |
| C97 | `Theme.AccentBrush` resolved at Truecolor, Ansi256, Ansi16, NoColor (a `[Theory]`) | resolve per tier | Truecolor/Ansi256 → the RGB brush (`(B,Ansi256)` via descent); Ansi16 → the hand-picked palette brush; NoColor → the attribute-only/safe value (never a stranded RGB) | PIN (doc §11.8, CD8) |

### 4.2 Control themes + the theme-styles channel + layer ordering (S8 content authored into S7)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C98 | a Type-keyed control theme (selector-less `Style` rooted at `^`, color-bearing setters as `ResourceReference`) for `Button` in `BuiltIn` | attach a `Button`, inspect `arm(button)` | the control theme arms at **`ControlTheme(0)`**; its color setters resolve through the palette via `ResourceReference` | PIN (CD30) |
| C99 | C98, then shadow `ThemeKeys.AccentBrush` at app scope | shadow + `RunFrame()` | every control re-skins with zero template work (the inheritance spine — overriding one palette key re-points all `ResourceReference` setters) | PIN (doc §11.8) |
| C100 | `app.Theme` with a populated `ResourceDictionary.Styles` (selector styles) | set `app.Theme`, inspect `arm` of a matched element | the theme's `Styles` arm at **`Theme(2)`**, flattened merged-order-then-own-last; re-read on theme-origin CatchAll pulses (version-compared) | PIN (CD30) |
| C101 | a populated `Styles` on a non-theme dictionary (element/window `Resources.Styles`) | inspect | **ignored** in v1 with a debug diagnostic (only `app.Theme.Styles` is consumed) | PIN (CD30) |
| C102 | layer-beats-specificity: an app style (App(3)) and a theme style (Theme(2)) both matching, the theme style more specific | resolve the winner | the **app** style wins (layer beats specificity); a theme style never beats an app style regardless of selector specificity | PIN (CD30, P3 pin) |
| C103 | control-theme layer ordering: a control theme found at a nearer chain scope vs `BuiltIn` | resolve | both arm at `ControlTheme(0)` (layer is the same wherever found); the nearer one wins via scope/order within the layer (`SubscribeControlTheme` resolves nearest) | PIN (CD13/CD30) |

### 4.3 Cursorial.UI.Media builders + ResourceBrushResolver + override paths

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C104 | `new Cursorial.UI.Media.SolidColorBrush { Color = …, Opacity = 0.5 }` implementing `IResourceValueBuilder` | `.Build()` | returns an immutable `Cursorial.Drawing.Media.SolidColorBrush`; the loader uses the **result** everywhere the builder would be used (the builder is one-shot, never stored) | PIN (doc §11.9) |
| C105 | `new Media.LinearGradientBrush { GradientStops = […], StartPoint, EndPoint, Spread }` | `.Build()` | returns a Drawing `LinearGradientBrush` with the stops/points/spread; the value is boxed once and shareable (sealed-dictionary-safe) | PIN (doc §11.9) |
| C106 | `new Media.Pen { Color = …, Brush = … }` (both set) | `.Build()` | build-time `InvalidOperationException` (Color XOR Brush); a single-source `Pen` builds an immutable Drawing `Pen` record | PIN (doc §11.9) |
| C107 | a built value placed in a dictionary, the dictionary then sealed | seal + share | the built Drawing value is immutable and the dictionary is freely shared post-seal (folded-constant-friendly) | PIN (doc §11.9/CD5) |
| C108 | `ResourceBrushResolver.Create(leaf)` over a chain with `Theme.AccentBrush` resolving to an `IBrush` | `resolver("Theme.AccentBrush")` and `resolver("linear:#f92672,#66d9ef")` | the resource name resolves through the chain to the brush; the inline `linear:` grammar resolves via BrushMarkup — `[brush=Theme.AccentBrush]` markup and `{DynamicResource Theme.AccentBrush}` share one namespace | PIN (doc §11.9) |
| C109 | `resolver` over an unknown name | `resolver("Mystery")` | returns null (the parser raises "Unrecognized brush"); resolution is static-per-parse, freshness via the `GetResourceVersion` cache key | PIN (doc §11.9) |
| C110 | per-control override: shadow `typeof(Button)` at app scope with a custom theme | resolve a Button's theme | the app-scope Type key wins over `BuiltIn` (the chain); zero `BuiltIn` template work | PIN (doc §11.8) |
| C111 | wholesale override: `app.Theme = CursorialTheme.CreateDefault()` mutated | set + resolve | the custom theme participates at the Theme hop before `BuiltIn`; `BuiltIn` backstops any control whose Type key the partial theme omits (the shipped control set) | PIN (doc §11.8) |
| C112 | `MyButton : Button` with the default `ControlThemeKey => GetType()` | attach + resolve theme | resolves **nothing** (exact-key, no base probing — not even in `BuiltIn`); a one-time debug diagnostic names `typeof(MyButton)` + the chain; overriding `ControlThemeKey => typeof(Button)` makes it resolve | PIN (CD13) |

---

## 5. Resource diagnostics (R3) — C113–C117

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C113 | `ResourceDiagnostics.Trace(leaf, K)` for a hit several hops up | inspect | one record line per hop searched (incl. variant probe keys tried, merged recursion); the hit hop is marked; deferred-then-realized noted | PIN (doc §11.10) |
| C114 | `ResourceDiagnostics.Explain(leaf, K)` for a miss | inspect | renders the full searched chain hop by hop (the `ResourceNotFoundException.SearchedScopes` shape), ending in BuiltIn | PIN (doc §11.10) |
| C115 | `ResourceDiagnostics.Subscriptions(root)` after arming several `ResourceReference` setters | inspect | lists live registry nodes (the leak-hunting surface); reports zero after the owning elements detach | PIN (doc §11.10) |
| C116 | `ResourceDiagnostics.DeferredEntries(dict)` with a mix of deferred + realized | inspect | reports each entry's state + `RealizedAtVariant` (+ Fork C line info when present, absent at P5) | PIN (doc §11.10) |
| C117 | `StyleDiagnostics.Explain(e, P)` for a resource-fed setter value | inspect | surfaces the originating `ResourceReference.Key` for the winning value | PIN (doc §11.10) |

---

## 6. Control base + ControlTemplate + TemplateInstance + parts (C0) — C118–C140

### 6.1 Control properties + ControlThemeKey

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C118 | `Control` properties | inspect metadata | `Template` (`StyledProperty<ControlTemplate?>`, `AffectsMeasure`), `Background` (`StyledProperty<IBrush?>`, `AffectsRender`, NOT inherited), `Foreground` (`TextElement` AddOwner, inherits, `AffectsRender`), `BorderPen` (`StyledProperty<Pen?>`, `AffectsRender` + nullity escalation), `Padding` (`StyledProperty<Margins>`, `AffectsMeasure`) | PIN (doc §12.1) |
| C119 | `TextElement.ForegroundProperty`/`TextAttributesProperty` (attached, `Inherits | AffectsRender`) AddOwner'd onto `Control`/`TextBlock` | set Foreground on `Root`, read on a descendant `TextBlock` | inherits down the logical tree (the text-attribute spine) | WPF (doc §12.1) |
| C120 | `Control.ControlThemeKey` default | inspect | `=> GetType()` (exact-key); overridable to e.g. `typeof(Button)` | PIN (CD13) |

### 6.2 Template instantiation + the namescope + TemplatedParent stamping

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C121 | `tmpl` with a `FuncTemplateContent` building `Border → ContentPresenter`; `ctrl.Template = tmpl` | `ctrl.ApplyTemplate()` (or first measure) | `Instantiate(ctrl)` builds the subtree; `ctrl.TemplateInstance` is set; the `Root` is attached as a **visual** child (not logical); `OnApplyTemplate` ran after attach | WPF (CD17) |
| C122 | C121 | inspect `TemplatedParent` on each built element | every null-stamped element gets `TemplatedParent = ctrl`; the template namescope is set on `ctrl` (from `TemplateInstance.NameScope`) | PIN (CD17/CD18) |
| C123 | a template that returns a subtree containing an element with a **foreign** non-null `TemplatedParent` (shared/aliased across instantiations) | `ApplyTemplate()` | throws (`InvalidOperationException` — shared-subtree misuse); a nested control's own parts are exempt (they stamp themselves at their own expansion) | PIN (CD17) |
| C124 | `tmpl` with `TargetType = typeof(Button)`; `Instantiate` on a `ctrl` not assignable to `Button` | apply | throws at apply (`TargetType` mismatch) | PIN (CD19) |
| C125 | `tmpl.Seal()` then arming it; an **unsealed** `tmpl` armed | seal vs arm-unsealed | a sealed template arms (its `Resources` are sealed as part of the template seal); arming an unsealed template throws naming the template | PIN (doc §11.4, CD17) |
| C126 | `GetTemplatePart<Border>("PART_Foo")` before first measure vs after | call before/after `ApplyTemplate()` | returns null before first expansion (documented caveat — call `ApplyTemplate()` explicitly if needed earlier); returns the part after; resolves in the template namescope **only** | PIN (CD17) |
| C127 | `NameScopeExtensions.RequireControl<Border>(root, "PART_Foo")` for a present and an absent name | call both | returns the part when present; throws naming scope + name when absent (the runtime counterpart of X4's generated `x:Name` fields) | PIN (doc §12.1) |

### 6.3 [TemplatePart] validation timing + verdicts

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C128 | `ctrl` with `[TemplatePart("PART_T", typeof(Border)) { IsRequired = true }]`; template provides `PART_T` as a `Border` | apply | validates OK; `OnApplyTemplate` runs; the part is reachable | PIN (CD19) |
| C129 | C128 but the template provides `PART_T` as a `TextBlock` (wrong type) | apply | throws `InvalidOperationException` naming `TargetType`/part/expected (`Border`)/actual (`TextBlock`) — **immediately after Instantiate, before visual attach** | PIN (CD19) |
| C130 | C128 but the template omits `PART_T` (required) | apply | throws `InvalidOperationException` (required part missing), deterministic across Debug/Release | PIN (CD19) |
| C131 | `[TemplatePart("PART_Opt", typeof(Border))]` (optional) omitted | apply | no throw; `GetTemplatePart<Border>("PART_Opt") == null`; control degrades gracefully (ScrollBar-without-arrows pattern) | PIN (CD19) |
| C132 | validation order proof: a template with both a wrong-type required part and a built `OnApplyTemplate` side effect | apply | the throw happens **before** `OnApplyTemplate` and before visual attach (no partial wiring) | PIN (CD19) |

### 6.4 OnApplyTemplate / re-application / Detach retraction

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C133 | `ctrl` with template T1 expanded; set `ctrl.Template = T2` | re-measure | sequence: `OnTemplateDetaching(old)` → `old.Detach()` → remove old `Root` (subtree detach retracts DynamicResource subs / `When` watchers / Template-layer frames) → instantiate T2 → validate → attach → `OnApplyTemplate` | WPF (CD17/CD20) |
| C134 | a control that re-sets `ctrl.Template` from **inside** `OnApplyTemplate` | apply | the re-entrant set does NOT recurse: a guard records dirty and expansion defers to the next measure | WPF (CD17) |
| C135 | `ctrl.Template = null` | re-measure | no visual child; desired size = padding + border; one-time diagnostic | PIN (CD17) |
| C136 | DEBUG leak tracker: T1 with an armed Template-layer style frame + a `TemplateBinding` + an auto-alias observer; then `ctrl.Template = T2` | re-measure, inspect tracker | `Detach()` retracts all three by cookie (store-owned promotion, never set-back); the tracker reports **zero** live nodes from T1 | PIN (CD20) |
| C137 | `OnTemplateDetaching(old)` "unhook before rewire" | a control that wires a part event handler in `OnApplyTemplate` and unhooks in `OnTemplateDetaching` | the handler is unhooked **before** `old.Detach()` and the old `Root` removal (ScrollViewer is the reference impl, §13) | PIN (CD17/doc §12.2) |

### 6.5 The template barrier (invariant 5) + TemplateBinding fast path

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C138 | a style rule `Border { Background: X }` (no `/template/`) with a `Border` that is a template part (`TemplatedParent != null`) | match | the rule does **not** match the part (the engine skips `TemplatedParent != null` elements before candidate scanning); a `/template/ Border { … }` rule matches | PIN (CD18, invariant 5) |
| C139 | a part `TemplateBinding`'d to `ctrl.Background` (`{TemplateBinding Background}`) | set `ctrl.Background = Vbrush` | the part's bound property tracks `ctrl`'s value (one-way fast path); the binding tears down in `Detach()` | PIN (CD18, S2 B2) |
| C140 | document vs template namescope isolation | look up a document `x:Name` from a part, and a part name from the document | each fails across the barrier — document namescopes never see part names and vice-versa | PIN (CD18) |

---

## 7. ContentControl + ContentPresenter + auto-aliasing (C0) — C141–C152

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C141 | `ContentControl` properties | inspect | `Content` (`StyledProperty<object?>`, `AffectsMeasure`), `ContentTemplate` (`StyledProperty<DataTemplate?>`, `AffectsMeasure`); `ContentPresenter` mirrors both + `RecognizesAccessKey` (`StyledProperty<bool>`, default false) + `Child` (realized visual, diagnostic) | PIN (doc §12.3) |
| C142 | a `cp` inside a template, neither `Content` nor `ContentTemplate` set (`IsSet == false`); `ctrl.Content = "hello"` | apply + inspect `cp.Child` | auto-alias **read-through** to `TemplatedParent.Content` ("hello") — **no installed binding**, no frame, `cp.Content.IsSet == false` still | PIN (CD21) |
| C143 | C142, then `ctrl.Content = "world"` | change templated parent's Content | the typed property-changed observer on `ctrl` (no presenter store entry) re-realizes the presenter with "world" | PIN (CD21) |
| C144 | C142, then `cp.Content = "explicit"` (a local presenter value) | set | the explicit value wins (`IsSet` flips true ⇒ read-through stops); a later `ctrl.Content` change no longer reaches the presenter | PIN (CD21) |
| C145 | C142, then `ctrl.Template = T2` (re-template) | re-measure | the auto-alias observer is torn down in `Detach()` (lifetime = template instance) | PIN (CD21/CD20) |
| C146 | `HeaderedContentControl` (`Header`/`HeaderTemplate`) | inspect | extends `ContentControl` with the header pair (MenuItem's base shape; the items half is P9) | PIN (doc §12.3) |
| C147 | recursion guard: a presenter whose realized content would re-enter presenter realization | realize | the recursion guard prevents infinite expansion (a degenerate self-referential content/template) | PIN (doc §12.3) |
| C148 | `cp.Content` a `UIElement` directly | realize | element passthrough — `cp.Child` is the element itself; it becomes a logical child of the templated-parent `ContentControl`, visual child of the presenter (chain ③) | PIN (CD22) |
| C149 | `ContentPresenter.RecognizesAccessKey = false`, `Content = "snake_case.txt"` | realize | fallback `TextBlock("snake_case.txt")` — underscores are **not** folded (no access-key parse on a plain presenter; chain ⑤) | PIN (CD22, doc §12.5) |
| C150 | `ContentPresenter.RecognizesAccessKey = true`, `Content = "_Save"` | realize | an `AccessTextPresenter` over `AccessText.Parse("_Save")` (chain ④ extended to plain strings) | PIN (CD22) |
| C151 | `cp.Content` an `AccessText` value | realize | an `AccessTextPresenter` (chain ④) regardless of `RecognizesAccessKey` | PIN (CD22) |
| C152 | `cp.Content = 42` (no template, not a string/element/AccessText) | realize | fallback `TextBlock(Convert.ToString(42))` = `"42"` (chain ⑤, `CurrentCulture`) | PIN (CD22) |

---

## 8. DataTemplate lookup chain (the ItemsControl-reused seam) (C0) — C153–C160

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C153 | `cp.Content = vm` (a `Vm`); `cp.ContentTemplate = DT(typeof(Vm))` explicit | realize | the explicit `ContentTemplate` wins (chain ①); `Build(vm)` runs; the built root's `DataContext == vm`; `TemplatedParent == null` (data content, CD18) | PIN (CD22) |
| C154 | `cp.Content = vm`, no explicit template; `dtkey(typeof(Vm))` in `app.Resources` | realize | the implicit `DataTemplateKey` walk finds it (chain ②); built with `DataContext == vm` | PIN (CD22) |
| C155 | implicit walk from a presenter that is a template part (`LogicalParent == null`, `TemplatedParent == owner`) | realize `vm` content | the **templated-parent hop** continues up the owner's logical chain to find `dtkey(typeof(Vm))` (the part has no logical ancestors) | PIN (CD22) |
| C156 | `cp.Content` of type `Derived : Vm`; `dtkey(typeof(Vm))` defined, no `dtkey(typeof(Derived))` | realize | probes runtime type `Derived` then base `Vm` (up to but excluding `object`) — finds the `Vm` template; first hit wins | WPF (CD22) |
| C157 | content change `vm1 → vm2` (same `Vm` type, same resolved template identity) | change Content | the realized subtree is **reused** — only `DataContext` updates (no rebuild) | PIN (CD22) |
| C158 | template identity change (a different `DataTemplate` resolves) | change | the subtree is rebuilt | PIN (CD22) |
| C159 | `DataTemplate.Build(data)` directly | build | a fresh namescope is attached via `NameScope.SetNameScope(root, scope)`; `DataContext = data` on the root; `TemplatedParent` stays null (ElementName bindings inside resolve template-locally) | PIN (CD18/CD22) |
| C160 | `dtkey(typeof(Vm))` and a Type key `typeof(Vm)` both in a dictionary | resolve each | distinct, collision-free (`DataTemplateKey` value vs `Type` reference) — a data template and a (hypothetical) control theme for the same type don't collide | PIN (doc §11.1, CD3) |

---

## 9. TextBlock + Border/Decorator + AccessText/Label (C0) — C161–C180

### 9.1 TextBlock (FormattedText, markup, the cache key)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C161 | `TextBlock { Text = "Hi" }` attached, `RunFrame()` | inspect `cell(0,0)`… | renders element-local via `RenderContext` (`DrawText` per wrapped line); `Text` is never access-key-folded; `:` pseudo-classes none; `Foreground` inherits | PIN (doc §12.7) |
| C162 | `TextBlock` with `TextWrapping`/`TextAlignment`/`TextTrimming` over a width-constrained slot | layout | wraps/aligns/trims per the grapheme-aware `TextFormatter` math; multi-line `\r\n|\n|\r` honored (the P2.6 text-tier behavior) | PIN (doc §12.7, P2.6) |
| C163 | `TextBlock.Markup = "[brush=Theme.AccentBrush]Hi[/brush]"` with the brush in the chain | render | markup parses via `TextMarkup` with a `ResourceBrushResolver` `BrushResolver`; `[brush=…]` resolves the theme brush; `Markup` **wins over `Text`** when both set | PIN (doc §12.7) |
| C164 | the `FormattedText` cache key | format twice at the same `(text/markup identity, width, caps, ver(this), ActualThemeVariant)` | the second format is a cache hit (no re-parse) | PIN (CD16) |
| C165 | C164, then a resource pulse bumps `ver(this)` | pulse + render | the cache key changes ⇒ the next render re-parses with fresh resolver output; the TextBlock holds **no** `ResourceDictionary.Changed` subscription | PIN (CD16) |
| C166 | C164, then `renegotiate(caps with a different ColorDepth)` | renegotiate + render | the `caps`/`ActualThemeVariant` component changes ⇒ re-parse (caps change invalidates the cache) | PIN (CD16) |

### 9.2 Border / Decorator

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C167 | `Decorator { Child = Widget(4×2) }`, no border/padding | measure/arrange | `Decorator` is a single-child layout passthrough; desired = child desired | WPF |
| C168 | `Border { Child, Padding = M(1,1,1,1), BorderPen = Pens.Light }` | measure | desired = child + padding + 1 cell/edge (border present); render draws `DrawBox`-equivalent + background fill | PIN (doc §12.7, spec §3.9) |
| C169 | `Border.BorderPen` null→non-null (nullity escalation) | flip the pen | `BorderPen` is `AffectsRender`; the change handler **imperatively calls `InvalidateMeasure()`** iff nullity flipped (±1 cell/edge); a pen *restyle* (`:focus → Pens.Heavy`, both non-null) is render-only (no measure) | PIN (doc §12.4, spec §3.5) |
| C170 | `Border.Title = "Group"` (presence flip) | set Title | `Title` is the GroupBox story (`DrawTitledBox`); presence flip forces the top border row ⇒ `InvalidateMeasure`; a title *text* change within presence is render-only | PIN (doc §12.7, spec §3.5) |
| C171 | `Border { Occludes = true, Background }` | render | `FillOpaque(bounds, Background)` + overwrite box (floating-surface idiom) vs `Occludes = false` ⇒ tint `FillRectangle` + non-overwrite box | PIN (doc §12.7) |
| C172 | two adjacent `Border`s in the same zone scene | render | their pen strokes junction-merge (`JunctionMode.Merge` default) — free TUI line-merging chrome | PIN (doc §12.7) |

### 9.3 AccessText / AccessTextPresenter / Label

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C173 | `AccessText.Parse` on `"_File"`, `"a__b"`, `"_1"`, `"_!"`, `"plain"` | parse each | `"_File"`→`("File",'F',0)`; `"a__b"`→ literal `_` at the doubled position, `("a_b", …)`; `"_1"`→ digit mnemonic `('1', …)`; `"_!"`→ underscore stays literal, **no key** (mnemonic must be a BMP letter/digit, never throws); `"plain"`→ no key (`HasKey == false`) | PIN (doc §12.5) |
| C174 | `(AccessText)"x"` (explicit) vs implicit | conversion | the `string→AccessText` operator is **explicit** (parsing is lossy); `AccessText.Literal(s)` skips parsing | PIN (doc §12.5) |
| C175 | `AccessTextPresenter { Text = AccessText("File",'F',0) }` | render with `AccessKeyManager.ShowUnderline` true vs false | underlines the `KeyIndex` grapheme (GraphemeWidth column math) when `ShowUnderline` true on it; no underline when false; `TextProperty` `AffectsMeasure`, `KeyAttributesProperty` default Underline, `AffectsRender` (the cue route end-to-end is P9; the presenter mechanism is C0) | PIN (doc §12.5) |
| C176 | `ParsesAccessKeyLiterals` metadata flag resolution | a `Button.Content = "_Save"` vs a `ContentControl.Content = "_Save"` vs `TextBlock.Text = "_Save"` | the flag is set on `ButtonBase.Content`/`Label.Content` (resolved against the **runtime type**) ⇒ folds to `AccessText`; never on `ContentControl`/`TextBlock` ⇒ the underscore is literal data (snake_case safe by construction) | PIN (doc §12.5) |
| C177 | runtime `GetAccessText()` parsing under the flag | `button.Content = "_Save"` (code-first) and a bound string | `GetAccessText()` parses under the flag ⇒ works code-first and for bound strings; registers `(Key, this)` with `AccessKeyManager` on attach/content-change, unregisters on detach | PIN (doc §12.5) |
| C178 | `Label { Content = "_Name", Target = textTarget }` | `OnAccessKey` invoked | folds `Content` (flagged), `RecognizesAccessKey` true; `OnAccessKey → (Target ?? FindNext(this)).Focus()`; `Label` is never focusable / never a tab stop | PIN (doc §12.5/§12.7) |
| C179 | `Label.Target == null` | `OnAccessKey` | focuses `FocusManager.FindNext(label)` (the next focusable) | PIN (doc §12.7) |
| C180 | `IsMultiMatch ⇒ focus only` (the AccessKeyManager core landed P2) | two controls registering the same key, access-key activation | the manager moves focus through matches (tab-order cycling), **never invokes** when `IsMultiMatch`; single-match invokes the control's default (`Button → click`) | PIN (doc §12.5, ND18) |

---

## 10. ButtonBase + Button (C0/C1) — C181–C198

### 10.1 Click event + ClickMode + command

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C181 | `Button` properties | inspect | `ClickMode` (default `Release`), `Command`/`CommandParameter` (BCL `ICommand`), `IsPressed` (read-only `DirectProperty`); routed `ClickEvent` (`RoutedEvent<ClickEventArgs>`, Bubble) + CLR `Click` sugar | PIN (doc §12.7) |
| C182 | a `Button` with a `Click` handler and a `Command` whose `CanExecute` is true | `OnClick` (programmatic or via a click) | raises `Click` (bubbles), then `Command.Execute(CommandParameter)` because `CanExecute` | WPF (doc §12.7) |
| C183 | C182 with `CanExecute == false` | click | `Click` raises; `Execute` is **not** called (and the button is effectively disabled via `IsEnabledCore`, C25) | WPF (CD25) |
| C184 | `ClickMode.Press` | mouse down on the button | clicks on **down**; `ClickMode.Release` clicks on up-over | WPF (doc §12.7) |

### 10.2 Mouse capture + :pressed + cleanup

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C185 | mouse Left-down on a `Button` (`ClickMode.Release`) | down | `CaptureMouse()` (taken for **both** ClickModes); `Pressed` set via `SetInteractionState` ⇒ `IsPressed == true`, `:pressed` flips | PIN (CD23/CD24) |
| C186 | C185, drag pointer off then back while captured | move | `IsPressed` tracks pointer-over (`= (pointer over self)`) while captured | WPF (doc §12.7) |
| C187 | C185, up while pointer over self (`ClickMode.Release`) | up | release capture; `OnClick` fires (over + release ⇒ click) | WPF (doc §12.7) |
| C188 | C185, up while pointer **off** self | up | release capture; **no** click | WPF (doc §12.7) |
| C189 | C185, then `OnLostMouseCapture` (capture stolen) | lose capture | `IsPressed = false`, **no** click | PIN (CD23) |
| C190 | `Pressed` window-wide clear: a pressed Button, then terminal focus-out | focus-out | the pressed-holder set (ND12) clears `Pressed` window-wide; `IsPressed == false` | PIN (CD24, ND12) |

### 10.3 Keyboard activation (down-activation) + IsEnabledCore/CanExecuteChanged

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C191 | a focused `Button`, Space **down** (not repeat) | Space down | clicks on Down (`IsRepeat`-guarded); on Kitty/Win32 (Up reported) the pressed latch shows `:pressed` until Space up | PIN (CD23) |
| C192 | C191 with `IsRepeat == true` (auto-repeat) | Space-repeat down | does **not** re-activate (guarded) | PIN (CD23) |
| C193 | a focused `Button`, Enter | Enter | immediate click (no pressed latch) | PIN (CD23) |
| C194 | a `Button` with Space held (latch), then `OnLostFocus` | lose focus | the Space latch is cleared, **no** click | PIN (CD23) |
| C195 | `ButtonBase.IsEnabledCore` with a `Command` | inspect | `IsEnabledCore` includes `Command is null || Command.CanExecute(param)`; effective-enabled = `IsEnabled ∧ IsEnabledCore ∧ ancestor` ⇒ `InteractionState.Disabled` ⇒ `:disabled` | WPF (CD25) |
| C196 | a `Command` whose `CanExecuteChanged` fires | raise `CanExecuteChanged` | the button calls S1's `InvalidateIsEnabledCore()` ⇒ effective-enabled recomputes ⇒ `:disabled` flips | WPF (CD25) |
| C197 | `CanExecuteChanged` subscription discipline | attach, change `Command`, detach | subscribed on attach; unsubscribed on **detach AND on `Command` change** (a long-lived static command must not pin a discarded button) | WPF (CD25) |
| C198 | `Button { IsDefault = true }` / `{ IsCancel = true }` | attach | installs Enter/Esc `KeyBinding`s on the surface root on attach, removes on detach (focused-element-wins via bubble order); `:default` pseudo-class on `IsDefault` | PIN (doc §12.7) |

---

## 11. RepeatButton + ToggleButton (C1) — C199–C206

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C199 | `RepeatButton { Delay = 400, Interval = 60 }` properties | inspect | `ClickMode.Press` default; `Delay`/`Interval` defaults; repeats via the P2 `UITimer` slice | PIN (CD29) |
| C200 | a pressed `RepeatButton` (down + over), advance the clock | `host.AdvanceTime(Delay)` then `+Interval` repeatedly + `RunFrame()` | first click after `Delay`, then one click per `Interval` while pressed + pointer-over; frame-aligned (ND20: at most one fire per frame) | PIN (CD29) |
| C201 | C200, then release / capture loss | release | the timer is canceled and unhooked (unhook-before-rewire); no further clicks | PIN (CD29) |
| C202 | C200, pointer moves off while held | move off | repeats pause (pressed tracks pointer-over); resume on move back | PIN (CD29) |
| C203 | `ToggleButton { IsChecked }` properties | inspect | `IsChecked : bool?` (two-way), `IsThreeState : bool`; `:checked` (true) / `:indeterminate` (null) via `PseudoClassMapping` multi-class projection | PIN (CD26) |
| C204 | `ToggleButton`, `IsThreeState == false`, repeated Space/click | toggle×3 | `false → true → false → true` (2-state cycle) | WPF (CD26) |
| C205 | `ToggleButton`, `IsThreeState == true`, repeated toggle | toggle×4 | `false → true → null → false → true` (WPF 3-state order); `:checked` then `:indeterminate` then neither | WPF (CD26) |
| C206 | access-key / Space / click on a `ToggleButton` | each | all toggle (`OnAccessKey → toggle`); the toggle rides the same path as Space/click | PIN (doc §12.5/§12.7) |

---

## 12. CheckBox + RadioButton (C1) — C207–C218

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C207 | `CheckBox` default template, `KittyTruecolor`/`caps-unicode` | render unchecked/checked/indeterminate | glyph cell + space + `ContentPresenter`; glyphs are **theme resources** (strings); ASCII tier `[ ] [x] [-]`; `caps-unicode` tier swaps via resources (`☐ ☑ ◪`-class, defense-covered) | PIN (CD26, doc §12.7) |
| C208 | `CheckBox` under `Ansi16Legacy`/`caps-ascii` | render | the ASCII glyphs `[ ] [x] [-]` render identically everywhere (zero ambiguous-width risk); the `caps-ascii` class selects them | PIN (CD26, doc §12.7) |
| C209 | `CheckBox` Space/click | toggle | toggles `IsChecked` (2- or 3-state per `IsThreeState`); `:checked`/`:indeterminate` flip | WPF (CD26) |
| C210 | `CheckBox.Content = "_Accept"` | render + access key | folds the mnemonic (flagged Content); access key toggles | PIN (doc §12.5) |
| C211 | `RadioButton` properties | inspect | `GroupName`; `:checked`; glyphs `( ) (*)` ASCII default | PIN (CD26/CD27) |
| C212 | three `RadioButton`s, same logical parent, `GroupName == null`; check one | check `r2` | `r1`/`r3` set `IsChecked = false` via `SetCurrentValue` (group = same logical parent when `GroupName` null) — peers' bindings/styles survive | WPF (CD27) |
| C213 | C212 but `r3` is under a different logical parent with the **same** `GroupName` | check `r1` | the group spans all same-named radios within the surface root (`GroupName` overrides logical-parent grouping); `r3` unchecks | WPF (CD27) |
| C214 | a checked radio; check it again | re-check | stays checked (a radio can't uncheck itself by clicking — WPF) | WPF (CD27) |
| C215 | a group of radios, arrow keys | Down/Right within the group | moves focus + checks the next radio (consuming the event) | WPF (CD27) |
| C216 | `RadioButton` peers' binding survival | a peer with a two-way binding on `IsChecked`, check another | the peer's `IsChecked = false` via `SetCurrentValue` preserves the binding (not cleared) | PIN (CD27) |
| C217 | `SetCurrentValue` race stance for `IsChecked` | inspect | the group uncheck uses `SetCurrentValue` (not a style frame) — styling `IsChecked`/`IsSelected` via setters is unsupported (documented stance); selectors react to `:checked`, never set it | PIN (doc §12.6) |
| C218 | `RadioButton` 3-state | `IsThreeState` semantics | radios are 2-state in practice (check/uncheck-by-peer); the `bool?` shape from `ToggleButton` exists but radios don't cycle to indeterminate by click | WPF (CD26) |

---

## 13. ScrollViewer + ScrollBar (C3 — inversion 5) — C219–C236

Over S1's banded `ScrollContentPresenter` (landed P1); the SCP banded-scene rows are the layout matrix's §12 (L201–L218) — these rows own the **ScrollViewer/ScrollBar control layer**, not the SCP mechanics.

### 13.1 ScrollViewer offsets + extent/viewport + wheel + keyboard

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C219 | `ScrollViewer : ContentControl` properties | inspect | `HorizontalScrollBarVisibility` (default `Auto`), `VerticalScrollBarVisibility` (default `Auto` — H is `Disabled` per spec note for v1 horizontal), `HorizontalOffset`/`VerticalOffset` (`DirectProperty`, two-way), `Extent`/`Viewport` (readbacks), `ScrollBy`, `EnsureVisible` | PIN (CD28, doc §12.7) |
| C220 | `ScrollViewer` over tall content; set `VerticalOffset = 5` | set | mirrors to the SCP's **styled** `ScrollOffsetRow` (`AffectsComposite`); the DirectProperty is a two-way mirror — setting either side syncs the other; a value out of `[0, Extent − Viewport]` coerces | PIN (CD28) |
| C221 | the offsets are animatable (the doc supersedes the spec's non-animatable DirectProperty stance) | `BeginAnimation` on the SCP styled offset; the ScrollViewer DirectProperty mirrors | the **styled** SCP offset is storyboard-animatable (smooth scroll in v1); the ScrollViewer DirectProperty reflects the animated value | PIN/DEV (CD28) |
| C222 | mouse wheel over the content (`WheelDeltaY = 120`) | wheel | scrolls `120/120 × 3 = 3` lines; `WheelDeltaY = 240` ⇒ 6 lines | PIN (CD28) |
| C223 | Shift+wheel / `WheelDeltaX` | wheel | horizontal scroll | PIN (CD28) |
| C224 | wheel at the scroll extreme (already at offset 0, wheel up) | wheel | the unconsumed wheel **bubbles** to an outer ScrollViewer (router gives the deepest scrollable; unconsumed bubbles out) | PIN (CD28) |
| C225 | keyboard inside, focused control didn't consume: Up/Down, PageUp/Down, Ctrl+Home/End, Left/Right | each | ±1 row / ±viewport / extremes / ±1 col respectively | PIN (doc §12.7) |
| C226 | `EnsureVisible(rect)` for a rect outside the viewport | call | scrolls minimally to bring the rect into view (ListBox/TextBox call it at P9) | PIN (doc §12.7) |
| C227 | extent cap: content desiring 50 000 rows | measure | `Extent.Rows == LayoutLimits.MaxScrollExtent (32 000)`, one-time diagnostic; offset coercion uses the capped extent | PIN (doc §12.4, L215) |

### 13.2 Auto visibility + the re-measure loop

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C228 | `VerticalScrollBarVisibility = Auto`, content extent ≤ viewport | measure | the vertical bar collapses (layout participates) | PIN (doc §12.7) |
| C229 | C228, content grows past the viewport | measure | the bar appears; the `Auto` re-measure loop (showing the bar shrinks the viewport which could re-hide it) is broken by the standard **remember-last-verdict** two-pass trick — converges, no oscillation | PIN (doc §12.7) |
| C230 | `VerticalScrollBarVisibility = Visible` / `Hidden` / `Disabled` | each | Visible: always shown; Hidden: never shown but scrollable by wheel/keys; Disabled: no scroll on that axis (offset coerced to 0, constraint passes through — L203) | PIN (doc §12.7) |

### 13.3 ScrollBar parts + track/thumb/arrows

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C231 | `ScrollBar : Control` template parts | inspect | optional `PART_LineUpButton`/`PART_LineDownButton` (`RepeatButton`s with `▲▼` glyph resources, ASCII `^v`), **required** `PART_Track`; 1 cell wide; `Orientation` (S1-owned); `:horizontal`/`:vertical` select glyph/orientation styling | PIN (doc §12.7, CD19) |
| C232 | `ScrollBar` render | render | `│` rail (Pen) + proportional `█` thumb (min 1 cell) | PIN (doc §12.7) |
| C233 | mouse on the track above/below the thumb | click | pages (±viewport); thumb drag = capture + cell-quantized proportional value | PIN (doc §12.7) |
| C234 | arrow `RepeatButton`s | press-hold | ±SmallChange repeating (RepeatButton via UITimer, §11) | PIN (CD29) |
| C235 | ScrollViewer wires the bars in `OnApplyTemplate`, unhooks in `OnTemplateDetaching` | re-template | the bar wiring is code-behind (not two-way TemplateBinding — TemplateBinding is one-way); unhooked on detaching (ScrollViewer is the unhook-before-rewire reference impl) | PIN (CD17, doc §12.7) |
| C236 | a ScrollBar missing the optional arrow parts (template omits them) | apply + use | degrades gracefully (track-only scrolling); no throw (optional-part rule, CD19) | PIN (CD19) |

---

## 14. Perf + invariants (re-asserted at P5) — C237–C242

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C237 | cold attach of 300 themed controls (Button/CheckBox under the BuiltIn control themes) | `host.ShowRoot` + first frame, time it | one-time attach cost recorded informationally (the §14 P5 row companion to S178's 20-rule cold attach); template expansion + control-theme arming + `ResourceReference` subscription is the cost — budget-asserted only as "not the per-frame constraint" | PIN (doc §14 P5) |
| C238 | the §14 P5 motion-storm re-assert: the loaded probe-4 storm over **templated** controls (Buttons with a `:pointerover` theme rule) | 200-move sweep, 1 frame | flip path zero-allocation steady-state (asserted on the worst repetition); frame-loop leg ≤ 33 ms (budget); recorded in the doc's P5 re-assert blockquote | PIN (doc §14 P5, probe 4) |
| C239 | invariant 3: a `Button`'s `:pointerover` restyle (background brush swap) | hover enter/leave + `RunFrame()` | re-rasters **only** the button's owning zone (`RC` bumps for the affected zone, not unrelated zones); the flip is an `AffectsRender` zone re-raster, never a full-window relayout | PIN (invariant 3) |
| C240 | invariant 3: a ScrollViewer in-band scroll (offset within ±K of the anchor) | scroll + `RunFrame()` | **zero `Render` calls** — pure composite slide (the SCP styled offset is `AffectsComposite`); the ScrollViewer DirectProperty mirror does not force a re-raster | PIN (invariant 3, L207) |
| C241 | invariant 2: a control's style/template never touches Scene/CellBuffer | a styled+templated control through a frame, DEBUG render-pass guard | no Scene/CellBuffer access outside the element-local `Render(RenderContext)`; the property/styling engines only raise notifications routed by `PropertyEffects` | PIN (invariant 2) |
| C242 | invariant 4: a `Detach()` / style deactivation / DynamicResource pulse-to-miss | each | the store promotes the next value — nothing "sets the old value back" (template `Detach` cookie retraction, style frame removal, `UnsetValue`-fed entry promotion) | PIN (invariant 4, CD20/CD12) |

---

## 15. Section → stage map

| Section | Rows | Stage | Gate |
|---|---|---|---|
| §1 ResourceDictionary storage/keys/merged/seal/deferred | C1–C30 | **R0** | the dictionary engine |
| §2 Variants + the lookup chain + StaticResource | C31–C58 | **R0** | the chain + variant-probe oracle (authored before the engine) |
| §3 Variants live + subscriptions + the merge + version | C59–C92 | **R1** | registry/pulse/merge/version |
| §4 Built-in theme + ThemeKeys + builders + resolver | C93–C112 | **R2** | BuiltIn + S8-authored content + media builders |
| §5 Resource diagnostics | C113–C117 | **R3** | the inspector surfaces |
| §6 Control base + templates + parts | C118–C140 | **C0** | the template spine |
| §7 ContentControl + ContentPresenter + auto-aliasing | C141–C152 | **C0** | the content pipeline |
| §8 DataTemplate lookup chain | C153–C160 | **C0** | the presenter/items seam |
| §9 TextBlock + Border/Decorator + AccessText/Label | C161–C180 | **C0** | the first leaves |
| §10 ButtonBase + Button | C181–C198 | **C0/C1** | C0 ships Button; C1 completes ButtonBase |
| §11 RepeatButton + ToggleButton | C199–C206 | **C1** | interactive leaves |
| §12 CheckBox + RadioButton | C207–C218 | **C1** | interactive leaves |
| §13 ScrollViewer + ScrollBar | C219–C236 | **C3 (inversion 5)** | scrolling controls over the banded SCP |
| §14 Perf + invariants | C237–C242 | **all** | re-asserted at the P5 exit |

Rows for a later stage may stay unimplemented (not red) until that stage opens, but every row is binding from now. **P5 exit criteria** (doc §14 P5): themed Button/CheckBox/ScrollViewer demo across `ThemeVariant` tiers; template subscription-leak tracker clean; motion-storm gate green over templated controls — proven by §4 (C97/C98/C99 tier coverage), §6 (C136 leak tracker, C133/C145 Detach retraction), and §14 (C238) respectively.

---

## 16. Cross-matrix boundaries (what this matrix does NOT own)

- **The SCP banded-scene mechanics** (band sizing, re-anchor predicate, zero-re-raster in-band, viewport clip) are the **layout matrix §12 (L201–L218)** — §13 here owns only the ScrollViewer/ScrollBar control layer atop them.
- **Selector grammar / specificity / pseudo-class structural matching / the conformance kit** are the **style matrix** — §4/§14 here exercise only the theme-channel **layer ordering** (ControlTheme(0)/Template(1)/Theme(2)) and capability-class re-point, which P3 pinned at the key level and P5 fills with content.
- **`When`/`DataCondition` semantics** are the **binding matrix §13 (B157–B168)** — theme styles may carry `When` (the P4 wiring) but this matrix does not re-test `When` evaluation.
- **`TemplateBinding` engine semantics** (descriptor validation, the untyped→typed bridge, `Detach`-eviction conformance against the `ValueFrame` kit) are the **binding matrix §12 (B2)** — C139 here asserts only the control-author-visible one-way fast path + `Detach` teardown.
- **Routed-event / focus / capture / `InteractionState` / `KeyGesture`/`KeyBinding` mechanics** are the **input matrix** — §10–§12 here assert control behavior *over* those (capture-for-both-ClickModes, down-activation, `:pressed` via `SetInteractionState`, `IsDefault` KeyBindings).
- **Property precedence / `SetCurrentValue` / `IValueEvictionListener` / inheritance** are the **precedence matrix** — §3/§12 here assert resource/control consumption of those seams (LocalValue producer eviction, `SetCurrentValue` for RadioButton group uncheck, inherited `Foreground`).
- **Out of P5 entirely** (recorded, not tested): TextBox/`TextPresenter`/clipboard (P9); ItemsControl/`ItemContainerGenerator`/`SelectionModel`/ListBox (P9, C2); Menu/ContextMenu/Separator/ToolTip (P9, C4); TabControl/ProgressBar (P9, C5); Window/chrome/`Popup` (P7); the access-key cue route (`ShowUnderline` theme rule), Alt-tracking UX, F10/`IMainMenu` re-point (P9). `MenuItem.Header`/`TabItem.Header` `ParsesAccessKeyLiterals` owners are recorded (C176 tests the `ButtonBase.Content`/`Label.Content` owners only).

---

## 17. Test authoring contract

Each numbered row above becomes **exactly one** xUnit test in `Cursorial.UI.Tests`, named after its row id with a behavior slug: `C38_VariantProbe_CatchAllAndWildcardTier_DescentReachesWildcardFirst` (`[Fact]`) — rows whose Expected cell enumerates a family (C31, C36–C42 truth-table cells, C97 tier coverage, C173 parse cases, C204/C205 cycles, C230 visibilities) become a single `[Theory]` with one case per family member, keeping the row↔test bijection at the row level. Tests live under `Cursorial.UI.Tests/ControlMatrix/`, one file per section (`Section01_ResourceDictionary.cs` … `Section14_PerfInvariants.cs`), namespace `Cursorial.Tests.UI.ControlMatrix`, sharing the §0.2 fixture via a common harness class (instrumented `Probe`/`Widget`/`ctrl`/`cp`/`cc` types and the `tmpl`/`part` builders registered once — dense property ids are process-global, so registrations must be idempotent across test classes; the `UITestHost` harness mirrors the style/binding matrices'). Rows are not merged, reordered, or "covered implicitly by" other rows: a row without a matching test is a P5 exit-criterion failure (§14 P5).

Rows are staged per §15: R0 (§§1–2) must be green at the R0 milestone, R1 (§3) at R1, R2 (§4) at R2, R3 (§5) at R3, C0 (§§6–9 + Button rows in §10) at C0, C1 (the rest of §10 + §§11–12) at C1, C3 (§13) at C3, and the perf/invariant rows (§14) at the P5 exit — later-stage rows may be absent (not red) before their stage opens. DEBUG-diagnostic rows (C17's dispatcher-turn assert, C77/C112 one-time miss diagnostics, C85/C136 leak trackers, C101 ignored-Styles diagnostic, C135 null-template diagnostic, C227 extent-cap diagnostic) compile their diagnostic assertion under `#if DEBUG` and assert the absence of a throw in Release where practical. Allocation rows (C238's zero-byte clause; any "0 B" cell) follow the repo norm: `GC.GetAllocatedBytesForCurrentThread()` deltas after warm-up, single-threaded `[Fact]`s, not BenchmarkDotNet (the perf timing rows C237/C238 carry `[Trait("Category","Benchmark")]`). The variant-probe truth table (C36–C42) is pinned **verbatim** from doc §11.2 / spec §3.2 — its cells are the oracle, and a divergence is a PR that amends this file (and CD8) before the code. When the implementation cannot honor a row, the resolution is a PR that amends this file (and, where the row carries a `PIN`/`DEV` tag, the CD ledger) **before** the code change lands — the matrix is the oracle, not the implementation. Oracle tags document provenance and do not alter test behavior.
