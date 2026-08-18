# curio — the Cursorial.CLI commandlet suite

*Design proposal — 2026-08-17. Status: draft for review.*

`curio` is a single AOT-compiled executable (`Cursorial.CLI` → `curio`) offering a suite of
**commandlets** — interactive choosers, prompts, confirmations, spinners, and visualizers — for use
in shell scripts, in the vein of Charmbracelet's `gum` and Terminal.Gui's `clet`. Its differentiators
are Cursorial itself (styling, animation, layout, XAML, real capability negotiation) and a
**composition model**: chained steps run in one process against one negotiated terminal session, so
a multi-step flow pays Cursorial's startup cost once, not per prompt.

Guiding principles:

1. **stdout is sacred.** The UI paints on the controlling terminal; results — and only results — go
   to stdout. `VALUE=$(curio choose …)` and `curio … | jq` always work.
2. **Startup must feel instant.** Native AOT, no runtime XAML parsing, negotiated-capability
   caching, and in-process chaining all serve this one goal.
3. **Chainable by construction.** Every commandlet works standalone gum-style; the pipeline syntax
   is sugar over the same commandlets, not a second system.
4. **Inline first.** Commandlets live in the shell's flow via the inline presentation and leave
   receipts in scrollback; the alternate screen is reserved for surfaces that genuinely need it.
5. **MVVM all the way down.** Each commandlet is a View + ViewModel with commands; the runner and
   wire layer are the only imperative shells.

---

## 1. What the framework already provides

The design leans on shipped behavior (cites are to the current `feature/cursorial-cli` tree):

| Fact | Where | Design consequence |
|---|---|---|
| Redirected-IO tty attach: when stdin/stdout is a pipe, the session opens the controlling terminal (real pty device via `ttyname_r`; `CONIN$`/`CONOUT$` on Windows) and never touches fd 0/1 | `PosixStdioTransports.cs:100-119`, `WindowsStdioTransports.cs:149-180` | the gum model works today; results print to real stdout |
| Inline presentation: content-sized region at the shell cursor, grows into scrollback, `InlineExitBehavior.Clear\|Retain`, **re-assignable at runtime** | `UIApplicationBuilder.cs:110-150`, `UIApplication.cs:199-205` | receipts ("retain on accept, clear on cancel") are already expressible |
| Sequential `UIApplication` runs per process are supported (Demo REPL); builder/app are single-use; thread-local `Current` cleared on dispose | `Cursorial.Demo/Program.cs:83-114`, `FrameLoop.cs:1285-1319` | app-per-step chaining is safe |
| BYO session: `WithSession(TerminalSession, disposeWithApp)`; alt screen is a **ref-counted session scope** (`PushAltScreenAsync`) | `UIApplicationBuilder.cs`, `TerminalSession.cs:546-569` | one session can serve a whole pipeline; fullscreen steps nest cleanly |
| Negotiation: 3 sentinel-bounded probe rounds, 500 ms/phase budget, ~2–3 RTTs typical, no cross-run cache | `VtTerminalNegotiator.cs:106-183`, `NegotiationOptions.cs:44` | the caching + seed API below is the single biggest startup lever |
| Signal-safe teardown: synchronous emergency restore (alt-screen pop, cursor, SGR, autowrap, OSC 112), idempotent at every layer, `128+signo` exits | `TerminalSession.cs:864-942` | a scriptable tool that never wedges the terminal is achievable with zero new work |
| XAML full lowering (X5): reflection-free generated C
#, strict-AOT gate proven (`Cursorial.Demo.XamlAotStrict` publishes with zero IL2026/IL3050 and runs) | `Cursorial.UI.Xaml.Generator`, `XamlAotStrict.csproj` | Native AOT is a paved road, with caveats (§7) |
| Compiled bindings (typed, zero reflection) with generator emission for `x:DataType`-scoped paths; reflective fallback is silent (CURG2002 info) | `CompiledBinding.cs:38-44`, `LoweringEmitter.cs:2950-3116` | commandlet views must stay on the compiled lane (§7) |
| `DelegateCommand` (+ preview pair), `KeyBinding` gesture→command, `ObservableObject` | `DelegateCommand.cs:24`, `InputBinding.cs:61-76` | the MVVM kit exists; no new command framework needed |
| Style capability stamps: `caps-*` classes on surface roots (`caps-truecolor`, `caps-nocolor`, `caps-emoji`, …), re-stamped on capability change | `StyleEngine.cs` | the presentation-model stamps (§3.3) join an existing family |
| `ApplicationModel { FullScreen, Inline }` (WIP) | `Cursorial.UI/Hosting/ApplicationModel.cs` | §3 adds `InlineWithSwitching` |

Known constraints the design routes around:

- Presentation is **fixed at `Build()`** — no runtime inline↔fullscreen switch inside one app today
  (`FrameLoop.cs:191,202`). §3 proposes `InlineWithSwitching` as the framework answer.
- The inline region cannot exceed one terminal height — long lists scroll internally.
- Ctrl+C's default gesture exits **0** (`UIApplication.cs:681`); curio needs 130 (§5.4).
- `DefaultBuilder` may pop the first-run wizard (`UIApplication.Configuration.cs:184-189`) —
  curio builds with `ShowFirstRunWizard=false`.
- No non-interactive fallback: with no controlling terminal the transport throws (§5.5).

---

## 2. Execution model: session-per-pipeline, app-per-step

```
curio choose --var branch -- $(git branch) ++ confirm "Delete {branch}?" ++ spin -- git branch -D {branch}
└──────────────┬──────────────┘   └────────────┬───────────┘   └───────────┬────────────┘
            step 1                          step 2                       step 3
```

One `curio` process runs the whole pipeline:

1. **Open one `TerminalSession`** via the owned happy path (`TerminalSession.OpenAsync`) — tty
   attach, raw mode, signal net. Negotiate once (from the capability cache when warm, §6).
2. **Per step**: build a fresh `UIApplication` with `WithSession(session, disposeWithApp: false)`,
   the step's `ApplicationModel`, and `ShowFirstRunWizard=false`; run the commandlet's View+VM;
   read the typed result after `RunAsync` completes; the app's teardown leaves the receipt
   (`InlineExitBehavior.Retain` on accept, `Clear` on cancel — set at runtime by the VM).
3. **Between steps**: bind `--var` captures; interpolate `{name}` into the next step's argv
   (in-process, array-safe — no shell quoting hazards, no injection surface).
4. **On completion**: emit results in the chosen wire format (§4.3), dispose the session (the 50 ms
   settle is paid once per pipeline, not per step), exit.

A canceled step (Esc / Ctrl+C / declined confirm) aborts the chain with that step's exit code;
already-streamed `lines` output stands, buffered formats (`env`, `json`) emit nothing.

Standalone invocation is the degenerate one-step pipeline — same code path, same semantics.

The fullscreen transcript story: a fullscreen step (file chooser, pager) runs inside the session's
ref-counted `PushAltScreenAsync` scope; on exit the main screen returns untouched — prior receipts
intact — and the step prints its own one-line receipt into the inline flow. A chained run reads as a
tidy transcript afterward regardless of how many steps went fullscreen.

---

## 3. Presentation: `ApplicationModel` and `InlineWithSwitching`

### 3.1 The third model

```csharp
public enum ApplicationModel
{
    FullScreen,
    Inline,
    InlineWithSwitching,   // proposed: inline until a window opens, fullscreen until the last closes
}
```

`InlineWithSwitching` is the curio default. The app runs inline; when the **window** count goes
0 → 1 (MessageBox, TaskDialog, tool window — anything routed through the WindowManager as a window),
the app transitions to the alternate screen; when the last window closes it returns to the inline
region. **Popups** (dropdowns, completion, context menus) never escalate — they are transient,
anchor-attached, and the inline region already grows to host them (within the terminal-height cap).
The rule is `window ⇒ escalate, popup ⇒ inline`.

Mechanics (framework work item FW-7): on escalation, push the session's alt-screen scope, swap the
`CellBuffer` to full terminal size, rebuild the `FrameRenderer` with `Inline: false`, full
restyle/layout/raster — the same renderer-rebuild pattern renegotiation already uses
(`FrameLoop.cs:1115-1117`). On return, dispose the fullscreen buffer, pop the alt scope (DECSET 1049
restores the main screen — the inline region's raster and the saved cursor come back for free), then
re-run the DSR-CPR re-anchor (the resize path's machinery, `FrameLoop.cs:765-783`) in case the
terminal resized while fullscreen, and resume inline frames.

### 3.2 Inline-hostable MessageBox and TaskDialog

Escalation should be the *exception*: a two-button confirmation must not flash the whole terminal.
MessageBox and TaskDialog gain an **inline presentation lane** (FW-8): under `Inline`/
`InlineWithSwitching`, dialogs flagged inline-capable render as modal overlays *within* the region —
hosted in the overlay/popup layer, region grown to fit (up to `maxHeight`), modality via the focus
scope. Only dialogs that opt out (or genuinely oversized surfaces) trigger the §3.1 switch.

curio's `confirm` and `alert` commandlets are thin shells over exactly these inline dialogs, so the
framework feature and the CLI ship the same visual.

### 3.3 Style stamps

The presentation model joins the `caps-*` stamp family on surface roots:

- `caps-inline` — stamped while presenting inline;
- `caps-fullscreen` — stamped while on the alternate screen.

The presentation model stamps use the **`app-`** prefix — `app-inline` / `app-fullscreen` —
keeping `caps-*` strictly for negotiated terminal facts (decided). Under `InlineWithSwitching` the
pair flips on each transition through the existing re-stamp fan-out, so one `TaskDialog` template
can be compact and chrome-less inline but bordered and centered fullscreen — selector-driven, no
control variants.

---

## 4. Command-line surface

### 4.1 Grammar

```
curio <commandlet> [options] [--] [args]              # standalone
curio <step> ++ <step> [++ <step> …]                  # pipeline, one process
```

`++` separates steps (decided; `--sep <tok>` overrides it for the rare argv that must contain a
literal `++`). Within a step, `--` ends option parsing as usual.

### 4.2 Variables and interpolation

- `--var NAME` on any step binds the step's accepted result to `NAME`.
- `{NAME}` in any later step's argv token interpolates the value. Interpolation happens in-process
  on the argv array — values are never re-parsed by a shell, so there is no quoting or injection
  hazard. `{{` escapes a literal brace.
- Multi-value results (multi-select `choose`) interpolate space-joined; wire formats preserve the
  item structure (§4.3).
- Selection steps capture more than the label (decided): `{name}` is the selected label,
  `{name.index}` the selected position (0-based; multi-select space-joins indices for
  interpolation). Wire formats carry both — `json` emits `{ "name": …, "name.index": … }` (arrays
  for multi-select), `env` emits `NAME` and `NAME_INDEX`. Field-splitting of formatted labels
  (`--delimiter`/`--value-field`) is deferred to M3; shells can `awk` in the meantime.

### 4.3 Wire formats (`--emit`, or `CURIO_EMIT`)

| Format | Behavior |
|---|---|
| `lines` (default) | each accepted step's value streams to stdout as it lands (multi-values one per line) — gum-pipe compatible |
| `env` | buffered; on full success emits shell-quoted `NAME=value` lines for every `--var` — `eval "$(curio …)"` |
| `json` | buffered; on full success emits one object of the captured variables — `curio … \| jq` |

Buffered formats emit nothing on abort: a pipeline either completes and hands the shell its
variables, or fails with a meaningful exit code and no partial state.

### 4.4 Exit codes and cancel semantics

| Code | Meaning |
|---|---|
| 0 | accepted / confirmed (pipeline: completed, skipped optionals included) |
| 1 | declined (`confirm` "no") or backed out (Esc) on a required step |
| 2 | usage error |
| 130 | Ctrl+C (requires FW-4: today's default gesture exits 0) |
| 128+n | killed by signal n (framework convention, already shipped) |

Two abort tiers (decided):

- **Hard abort** — Ctrl+C (and signals) always ends the pipeline, exit 130 / 128+n, buffered
  emits suppressed. No option overrides this.
- **Soft cancel** — Esc back-out or a declined `confirm` ends the pipeline with exit 1 *by
  default*, but a step marked `--optional` continues instead: the step's variable stays
  **unbound** (`{name}` interpolates empty; the key is *absent* from `env`/`json` emits, so
  `jq has(...)`-style checks work), or binds the step's `--default` when one is given. An
  optional `confirm` with `--var` binds `true`/`false` rather than aborting on "no".

**Skip vs. back out.** Overloading Esc as both "skip this step" and "abort the pipeline" gets
ambiguous the moment two optional steps are adjacent (is the second Esc a second skip, or an
abort?). The proposal keeps the two intents on separate keys: Esc always carries abort-intent,
while an `--optional` step additionally renders an explicit **Skip** affordance (its own key, shown
in the step's hint bar) that advances with the variable unbound. A pipeline-level policy flag then
tunes what Esc's abort-intent does:

```
--on-cancel abort     # default: Esc ends the pipeline (exit 1)
--on-cancel skip      # Esc skips optional steps (double-Esc — two in a row — aborts);
                      # on a required step it aborts
--on-cancel confirm   # Esc pops an inline "Abort pipeline?" dialog (dogfoods §3.2)
```

Backward *navigation* (Shift+Tab to re-open the previous step) is deliberately out of scope for
v1 — receipts are already committed to scrollback — but the step/result model doesn't preclude it.

### 4.5 Non-interactive fallback

With no controlling terminal (CI, cron, `ssh` without `-t`) the transport throws today. curio
defines the policy: a commandlet with `--default <value>` emits it and exits 0; otherwise exit 2
with a one-line stderr explanation. `curio --help`/`--version` and the pure filters (`style`,
`format`) never need a tty.

### 4.6 stdin discipline

Choosers accept items via argv *or* piped stdin (`git branch | curio choose`). The UI reads keys
from the tty (already how the input source works when stdin is a pipe); item data is read from fd 0
directly — never `System.Console`, per the termios-mutation hazard (`PosixStdioTransports.cs:27-34`).
FW-5 adds the small safe-stdin-reader + `IsInteractive` helpers to Core.

---

## 5. The commandlet model (MVVM)

### 5.1 Contract

```csharp
public interface ICommandlet
{
    static abstract CommandletDescriptor Descriptor { get; }  // name, summary, option table (drives --help)
}

public abstract class Commandlet<TOptions, TResult> : ICommandlet
{
    public abstract TOptions            Bind(ArgReader args);          // AOT-clean manual binding
    public abstract ApplicationModel    Model { get; }                  // InlineWithSwitching default
    public abstract CommandletView<TResult> Create(TOptions o, StepContext ctx);
}
```

- **Options binding** is a hand-rolled `ArgReader` over per-commandlet static option tables — no
  `System.CommandLine` (startup weight, reflection posture). The tables also generate `--help`.
- **ViewModels** derive from a `CommandletViewModel<TResult> : ObservableObject` base:
  `AcceptCommand`/`CancelCommand` (framework `DelegateCommand`) complete the typed result and call
  `Shutdown(0/1)`; the base flips `InlineExitBehavior` (Retain on accept, Clear on cancel) before
  shutdown. Enter/Esc arrive via root `KeyBinding`s.
- **Results** are read from the VM after `RunAsync` and written by the wire layer *after* app
  teardown (the "only now is `Console.WriteLine` safe" boundary, `FrameLoop.cs:1280`) through a
  direct fd-1 writer (FW-5).
- **Views**: X5-lowered XAML from M0 (decided — the generator is well-tested, and curio is the
  forcing function that flushes out remaining lowering gaps *now* rather than later). Every
  binding is compiled-lane (`x:DataType` on every scope); in this project CURG2002
  ("binding stayed reflective") and CURG3001 ("member not lowerable") are promoted to **build
  errors** so gaps surface as generator fixes, not silent reflective fallbacks. Code-first views
  remain the escape hatch for anything genuinely blocked mid-fix.
- **Testing**: every commandlet gets `UIHeadlessHost` tests — typing through the real input path,
  frame-buffer assertions, wire-format golden tests (the `TextBoxBindingEchoRepaintTests` pattern).

### 5.2 Roster

| Milestone | Commandlets |
|---|---|
| M0 | `choose` (single/multi), `input` (placeholder, password), `confirm` |
| M1 | `filter` (fuzzy), `write` (multiline), `spin` (spinner around a child command), `style` (static styled output) |
| M2 | `file` (fullscreen picker), `pager` (fullscreen), `form` (multi-field inline form), `alert`/`msgbox` (inline TaskDialog shells) |
| M3 | `table`, `progress` (stdin-driven), `format` (markdown/template), `log` |

`form` deserves emphasis: one inline multi-field form (text, choice, toggle fields declared via
repeated options) replaces an entire chain of prompts, with one receipt. It is the commandlet that
best shows Cursorial's layout/styling advantage over gum.

`spin` runs its child with the pipeline's tty paused where needed (`PauseIOAsync`,
`TerminalSession.cs:678-702`) so interactive children (e.g. `$EDITOR` from `write`) work.

---

## 6. Startup latency plan

Budget: **< 50 ms** cold-process to first painted frame on a warm capability cache, local terminal.

1. **Native AOT** (§7) removes JIT and runtime startup: single-digit-ms process start.
2. **Capability cache** (FW-1, the big lever): serialize `TerminalCapabilities` + the applied
   opt-in set to `~/.cache/curio/caps/<key>.json`, keyed by
   `TERM`/`TERM_PROGRAM`/`TERM_PROGRAM_VERSION` (+ multiplexer flags). On a warm key, seed the
   session (`TerminalSessionOptions.CachedCapabilities`) — the negotiator applies the opt-in
   enables *without* the probe rounds (round 2 is already write-only; rounds 1/3/4 are skipped).
   Cold or version-drifted keys run full negotiation and refresh the cache. Optionally revalidate
   in the background after first frame and persist drift for next time.
3. **Minimal negotiation preset** (FW-2): a curated low-latency `NegotiationOptions` profile for
   prompt-sized commandlets — no mouse tracking, no Kitty push where unneeded, truecolor via cache.
4. **Pipeline amortization** (§2): one negotiation, one 50 ms dispose settle, N steps.
5. **Deferred niceties**: user-configuration load, theme resolution beyond the stamped tier, and
   background cache revalidation all happen after the first frame.

Worst cases stay bounded and honest: a mute terminal still degrades to the 500 ms/phase budgets on
a cold cache only.

---

## 7. AOT strategy

Mirror `Cursorial.Demo.XamlAotStrict` (the proven zero-IL2026/IL3050 gate):

- `PublishAot=true` **in the csproj body** (the CLI-global `/p:` form breaks on the netstandard2.0
  generator references — NETSDK1207), `TrimMode=full`, `InvariantGlobalization=true`,
  `CursorialXamlLowering=full`, `CursorialXamlEmbedResources=false`.
- The strict-AOT auto-default already flips the reflection XAML metadata provider off under
  `PublishAot` (`RuntimeHostConfigurationOption … ReflectionMetadataProvider.IsSupported=false`).
- **Reflective-binding hygiene**: the reflective binding engine has no feature switch — it vanishes
  only by trimmer reachability. Curio's rule: no runtime `Binding` construction; XAML views must
  compile every binding (treat CURG2002 "stayed reflective" as a **build error** in this project);
  code-first views use `CompiledBinding` instances in static fields. Avoid `Binding.Compiled(...)`
  (Expression trees run interpreted under AOT).
- **Generator hardening**: full lowering's proven surface is literal views; curio's XAML-first
  stance (§5.1) deliberately drives lowered DataTemplate/resource/binding coverage to completion —
  every CURG2002/CURG3001 hit in curio is a generator work item, fixed at the source rather than
  routed around.
- Publish matrix: `osx-arm64`, `osx-x64`, `linux-x64`, `linux-musl-x64`, `linux-arm64`, `win-x64`,
  `win-arm64`. Single file per RID; symbol files kept aside; size target ≤ ~10 MB
  (`IlcOptimizationPreference=Size`).

### Distribution

Dual-channel from the same publish artifacts:

1. **dotnet tool** with .NET 10 RID-specific Native-AOT tool packages (`dotnet tool install -g
   curio`, `dnx curio` one-shots), optional IL fallback package for exotic RIDs.
2. **Bare binaries** on GitHub Releases; Homebrew tap / scoop / winget as adoption grows. The gum
   audience installs via `brew`, not the .NET SDK.

Single-binary matters beyond distribution: the warm server (§8) is the same executable
dispatching on argv, so client/server version skew cannot exist.

---

## 8. Warm server mode (later)

For scripts with shell logic *between* steps — where a single-run pipeline can't amortize —
`curio serve` keeps a warm host: `eval "$(curio serve --env)"` exports `CURIO_SESSION`; subsequent
`curio` invocations detect it and become thin IPC clients over a Unix domain socket, passing their
tty to the server via `SCM_RIGHTS` fd-passing (fallback: `ttyname` + open, same-user). The server
runs the commandlet against the client's terminal and returns result + exit code. Lifetime: idle
timeout, `curio serve --stop`, and session-death cleanup. Windows ships direct mode only at first
(ConPTY handle passing is a research item). This is M4 — the pipeline covers most flows first.

---

## 9. Framework work items

| # | Item | Size |
|---|---|---|
| FW-1 | ~~Capability cache seed~~ **DONE**: `TerminalSessionOptions.CachedCapabilities` + `VtTerminalNegotiator.ApplyCachedAsync` (opt-in enables via the same `DecideOptIns`/`EmitOptInEnables` producers, restore-sequence parity, no DA1 waits / DECRQM / OSC probes) + hand-rolled `TerminalCapabilitiesSerializer` (Utf8JsonWriter/JsonDocument, AOT-clean); CLI side: `CapabilityCache` at `$XDG_CACHE_HOME/curio/caps/<key>.json`, `--no-caps-cache` / `CURIO_NO_CAPS_CACHE` kill-switches, corrupt entries delete-and-renegotiate | M |
| FW-2 | ~~Minimal preset~~ **DONE**: `NegotiationOptions.MinimalPrompt` — mouse + Kitty push off, focus/paste/Win32/sync per flags; trims the opt-in and DECRQM batches, does NOT reduce probe-round count (trade-offs documented on the property) | S |
| FW-3 | ~~Shared-session multi-run~~ **DONE**: `VtInputDevice` supports sequential re-enumeration (one active enumerator; events buffer between consumers); contracts updated | S |
| FW-4 | ~~Cancel exit-code hook~~ **DONE (CLI-side)**: the runner disables `ExitOnUnhandledCtrlC` and maps Ctrl+C→130 / Esc→1 in `PreProcessInput`; no framework change needed | S |
| FW-5 | Core helpers: `IsInteractive` predicate, safe stdin-pipe reader, post-teardown fd-1 result writer | S |
| FW-6 | `ApplicationModel.InlineWithSwitching`: window-driven alt-screen escalation with buffer/renderer swap and CPR re-anchor on return | L |
| FW-7 | Inline-hostable `MessageBox`/`TaskDialog` (overlay-layer modal lane under inline models) | M |
| FW-8 | `caps-inline`/`caps-fullscreen` style stamps, re-stamped on presentation transitions | S |
| FW-9 | Fix stale `TerminalSessionHost` remarks (emergency-restore seam landed) | XS |

FW-1/2/4/5 unblock M0–M1 and are individually small; FW-6/7/8 land with M2.

---

## 10. Milestones

> **Status (2026-08-18):** M0 COMPLETE and verified — 3 commandlets (lowered XAML, zero CURG2002),
> pipeline runner on one shared session, receipts, 14 MB AOT binary, sub-10 ms non-interactive start.
> M1 PARTIAL: `--emit env|json` (+`CURIO_EMIT`), the non-interactive `--default` policy, and the stdin
> item feed are done and E2E-verified; `filter`/`write` in flight; the capability cache (FW-1/2) is
> DONE and E2E-verified (warm pty run interactive at 600 ms where the cold probe budget alone is
> ~1.5 s on a mute terminal); `spin`/`style` remain. The Setter.Value custom-extension lift landed
> as a THREE-lane change (frontend + loader + emitter).

- **M0 — proof of the spine.** `Cursorial.CLI` csproj (strict-AOT gate), `choose`/`input`/
  `confirm` as lowered-XAML views, standalone + `++` pipeline with shared session, receipts,
  `--var`/interpolation, `lines` emit, exit codes (FW-4), headless test rig. *Acceptance: an AOT
  binary chains three prompts with receipts; startup measured and recorded; every generator gap
  encountered is filed (or fixed) rather than worked around.*
- **M1 — scriptable for real.** `env`/`json` emits, non-interactive policy, stdin item feed,
  `filter`/`write`/`spin`/`style`, capability cache (FW-1/2/5). *Acceptance: < 50 ms warm-cache
  first frame; a README demo script covering gum's core flows.*
- **M2 — presentation depth.** FW-6/7/8; `file`, `pager`, `form`, `alert`. *Acceptance: a pipeline
  mixing inline and fullscreen steps reads as a clean transcript; `form` replaces a 4-step chain.*
- **M3 — breadth + polish.** `table`/`progress`/`format`/`log`, theming flags, docs, dotnet tool +
  brew packaging, startup-budget CI guard.
- **M4 — warm server.** Unix-socket serve mode with fd passing; Windows evaluation.

---

## 11. Decisions log & open questions

Resolved 2026-08-17: `++` separator (Q1); `app-*` stamp prefix (Q2); selection steps capture the
index too — `{name}` + `{name.index}`, 0-based, field-splitting deferred to M3 (Q3, §4.2);
two-tier abort semantics with `--optional` continuation (Q4, §4.4); XAML-first views with
generator gaps fixed at the source (Q5, §5.1/§7).

Open:

1. **Esc policy under `--optional`** (§4.4): default proposal is a distinct Skip affordance with
   Esc always meaning abort-intent; the pipeline-level `--on-cancel abort|skip|confirm` flag covers
   the skip-through and confirm-to-abort styles. Which default, and is double-Esc-aborts worth
   having under `skip`?
