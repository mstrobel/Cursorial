# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project intent

GlowTerm will be a **cross-platform .NET library for building high-quality, visually rich terminal applications with
robust mouse support**. It is library-first (consumed by app authors), not an end-user app. Cross-platform means
Windows, macOS, and Linux terminals are all first-class — design choices that bake in assumptions about one platform
(VT sequences only, Win32 console only, etc.) need an explicit story for the others.

## Status

Early-stage. Two projects: `GlowTerm.Core` and `GlowTerm.Core.Tests` (xUnit). Modules landed:

- **Input** (`GlowTerm.Core/Input/`, namespace `GlowTerm.Core.Input`) — see "Input module conventions" below.
- **Output** (`GlowTerm.Core/Output/`, namespace `GlowTerm.Core.Output`) — minimal `IOutputByteSink` (a
  `PipeWriter`-shaped sink, mirror of `IInputByteSource`) plus the output-side capability records.
- **Terminal** (`GlowTerm.Core/Terminal/`, namespace `GlowTerm.Core.Terminal`) — `ITerminalNegotiator` is the single
  public entry point for capability detection and opt-in negotiation, returning a `TerminalCapabilities` aggregate.
  `VtTerminalNegotiator` is the VT/ANSI implementation; it owns the probe-and-respond handshake (XTVERSION + DA1
  sentinel pattern) using its own ephemeral classifier + interpreter, then applies opt-in enable sequences for the
  protocols the application requested (SGR mouse + button-event tracking + optional any-event motion, focus events,
  bracketed paste, Kitty keyboard with configurable flag set, Win32 input mode on Windows-family terminals,
  synchronized output on supporting families). The shared `VtInputMode` is updated to reflect what was actually
  enabled. `RestoreAsync` reverses every enable in LIFO order, is idempotent, and is invoked automatically on
  disposal. Kitty / Win32 / synchronized output opt-ins are gated on family identification so capability claims
  don't lie about features the terminal silently ignores. `IEnvironmentReader` abstracts environment access so tests
  can be deterministic.

`VtInputDevice` (`GlowTerm.Core.Input.VtInputDevice`) is the concrete `IAsyncInputDevice` over an `IInputByteSource`.
It owns its own `VtSequenceClassifier` + `VtInputInterpreter`, runs a background pump that reads from the source's
`PipeReader`, and bridges the synchronous interpreter sink to the consumer via an unbounded `Channel<InputEvent>`.
The device owns the bare-ESC ambiguity timer (default 50 ms — the xterm convention, configurable via constructor)
and calls `classifier.Flush()` when the idle window elapses with a pending lone ESC. Single-shot per instance:
calling `ReadAllAsync` twice throws. Does NOT take ownership of the byte source — caller (typically `TerminalSession`)
is responsible for transport lifecycle.

`TerminalSession` (`GlowTerm.Core.Terminal.TerminalSession`) is the orchestrated entry point with two factories:

- **BYO**: `TerminalSession.OpenAsync(source, sink, options?, ct)` — runs the negotiator over caller-supplied
  transports; disposal stops the input pump and runs negotiator restore but leaves the transports open.
- **Happy path**: `TerminalSession.OpenAsync(options?, ct)` — opens platform stdio transports via
  `StdioTransports.Open()` (POSIX `stty raw -echo` / Windows `SetConsoleMode` with VT input + output flags),
  applies negotiation, and returns a fully-wired session. Disposal restores prior terminal-mode state and closes
  the owned transports. Throws `InvalidOperationException` when standard I/O isn't a real terminal — typical in CI
  or under pipes; use the BYO overload there.

Both factories return a session exposing `Capabilities`, `Input` (`IAsyncInputDevice`), and `Output`
(`IOutputByteSink`). Disposal order: stop input pump → run negotiator restore (writes opt-in disable sequences) →
dispose owned transports (restore terminal mode + close streams). `TerminalSessionOptions` carries the
`NegotiationOptions` and the `EscapeAmbiguityTimeout` for the input device.

`Terminal/Stdio/` houses the platform-specific stdio code: `IStdioTransports` is the public abstraction,
`StdioTransports.Open()` the platform-detecting factory. POSIX uses the `stty` subprocess (one `-g` save + one
`raw -echo` apply at open, one `<saved-state>` restore at dispose) — pragmatic v1 choice that avoids the per-OS
termios struct layout problem (Linux glibc and macOS Darwin layouts diverge) at the cost of two short subprocess
spawns at startup; direct termios P/Invoke is a future optimization. Windows uses `GetConsoleMode`/`SetConsoleMode`
P/Invoke (via `LibraryImport`) — clears `ENABLE_PROCESSED_INPUT` / `ENABLE_LINE_INPUT` / `ENABLE_ECHO_INPUT`, sets
`ENABLE_VIRTUAL_TERMINAL_INPUT` for stdin, sets `ENABLE_VIRTUAL_TERMINAL_PROCESSING` + `DISABLE_NEWLINE_AUTO_RETURN`
for stdout. `LibraryImport` requires `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the csproj.

**Signal handling for restoration on Ctrl-C / SIGTERM is not yet implemented.** Callers should ensure
`await using` or `try`/`finally` patterns to guarantee `DisposeAsync` runs. A future pass will register a
`PosixSignalRegistration` / `AppDomain.ProcessExit` safety net.
- **Input parsing** (`GlowTerm.Core/Input/Parsing/`, same `GlowTerm.Core.Input.Parsing` namespace) —
  `VtSequenceClassifier` is a Williams-derived state machine that frames bytes into classified tokens dispatched to
  `IVtSequenceTokenSink`. Covers ground / ESC / CSI / OSC / DCS / SS3 (the SS3 state recognizes `ESC O <byte>` as a
  3-byte sequence and dispatches it through `OnEscDispatch` with intermediate `O`). Does NOT cover APC, SOS, PM, or
  8-bit C1 controls (deliberately out of scope for input). `VtInputMode` is the mutable mode bag (DECCKM,
  modifyOtherKeys level, mouse encoding, Kitty flags, etc.) the interpreter holds and the negotiator updates.
  `VtInputSequences` centralizes UTF-8 byte-string constants. `VtInputInterpreter` consumes classifier tokens and
  emits `InputEvent`s to an `IInputEventSink`. Decoder coverage so far: printable runs (one event per Rune, with
  cross-feed UTF-8 buffering), C0 controls (Tab, Enter, Backspace, NUL→Ctrl+Space, Ctrl+letter), bare ESC,
  focus events, bracketed-paste accumulation, CSI cursor keys + Home/End + special keys (Insert/Delete/PageUp/Down),
  function keys F1–F20 (xterm + vt220 codes), F1–F4 + cursor + Home/End via SS3, BackTab (`CSI Z`), xterm
  modifier-bearing variants (Shift / Alt / Ctrl / Super / Hyper / Meta / CapsLock / NumLock — full xterm + Kitty
  modifier-bit range), SGR mouse (DECSET 1006) — press / release / drag / motion / wheel and X1–X4 extended buttons,
  with the interpreter accumulating `MouseButtons` state across events so drag and motion carry an accurate
  held-button mask, device responses: DA1 (`CSI ? … c`), DA2 (`CSI > … c`), DSR-CPR (`CSI row;col R`), OSC 4 / 10 /
  11 / 12 color responses, and DCS XTVERSION (`DCS > | … ST`), and the Kitty keyboard protocol
  (`CSI key[:shifted:base][;mods[:event]][;text] u`) — full functional key mapping (Esc, Enter, Tab, arrows, Home,
  End, F1–F24, numpad, media, per-side modifier keys), up / down / repeat distinction via the event-type
  sub-parameter, text payload reporting, and codepoints in the Unicode private-use area mapped to the matching
  `Key` enum values. Alternate-key sub-parameters are parsed but not surfaced (`KeyEvent` doesn't carry shifted /
  base-layout keys yet). Not yet decoded (silently dropped): X10 mouse (legacy), SGR-Pixels mouse, modifyOtherKeys
  character keys, ESC charset designators, DA3 / DECRQSS / XTGETTCAP DCS responses, Win32 input mode.

Concrete `IAsyncInputDevice`/`IEventInputDevice` implementations and a `VtTerminalNegotiator` are not yet started.
Rendering and layout are not started. `TerminalSession.OpenAsync()` is the agreed-upon entry point but is not built
until those dependencies exist (see project memory).

## Input module conventions (`GlowTerm.Core.Input`)

The input API is designed to be usable both inside the future GlowTerm framework and by existing apps that just want
better terminal input. Key shape:

- All public types live in the single namespace `GlowTerm.Core.Input` regardless of folder location.
- A consumer picks a delivery surface per device instance: `IAsyncInputDevice` (pull, `IAsyncEnumerable<InputEvent>`)
  or `IEventInputDevice` (push, classic `EventHandler<>` events). A device may implement either or both. Both extend
  `IInputDevice`, which carries the `InputCapabilities` and `IAsyncDisposable` contract.
- `IInputByteSource` (a `PipeReader` wrapper) is the abstraction for parser-based devices. Devices not built on a
  byte stream (e.g. Win32 console input records) bypass it.
- Devices chain via decoration — a wrapper takes another `IInputDevice` and produces a new one whose
  `InputCapabilities` may differ. Decorators that want to expose their inner device implement
  `IInputDeviceDecorator`. The motivating example is a key-up/repeat synthesizer that fabricates events for terminals
  that don't natively report key release.
- Events are a sealed `record class` hierarchy rooted at `InputEvent`; consumers pattern-match on type.
  `InputEvent.Synthesized` flags fabricated events so consumers can distinguish them from device-reported truth.
- Capabilities are categorized records (`MouseCapabilities`, `KeyboardCapabilities`, `PointerCapabilities`,
  `ProtocolCapabilities`) aggregated under `InputCapabilities`. Each has a `None` static for defaults.
- The interface layer must stay free of framework-specific concepts so it remains usable standalone.

## Capability negotiation conventions (`GlowTerm.Core.Terminal`)

`ITerminalNegotiator` is the orchestrator that turns a raw terminal connection (an
`IInputByteSource` + `IOutputByteSink` pair, or a Win32 console handle) into a known set of capabilities. It is
**both detector and negotiator** — by default it actively enables opt-in protocols (Kitty keyboard, bracketed paste,
SGR mouse, focus events, Win32 input mode, synchronized output) and records what it enabled so it can restore on
dispose. `NegotiationOptions.EnableAllOptIns = false` reduces it to a passive probe.

- Returned `TerminalCapabilities` reflects **realized** capabilities, not advertised ones — features the terminal
  claimed but did not honor are reported as unavailable. Consumers can branch on flags directly.
- Negotiation is **single-shot per instance**: re-negotiating requires a new instance. This keeps "what to restore to"
  unambiguous.
- Restore is best-effort and idempotent; failures (broken pipe, terminal closed) are swallowed. Disposal must run
  before process exit or the terminal will be left in a non-default state — register a signal handler.
- The Win32 implementation produces the same `TerminalCapabilities` shape via structured APIs (`GetConsoleMode`,
  parent-process inspection, etc.) — consumers see capabilities, not how they were detected.

## Output conventions (`GlowTerm.Core.Output`)

Currently scoped to the bytes layer needed for capability negotiation:

- `IOutputByteSink` is a `PipeWriter` wrapper, parallel in shape to `IInputByteSource`. Consumers MUST NOT call
  `PipeWriter.Complete` directly; sink ownership of completion is enforced via `IAsyncDisposable.DisposeAsync`.
- Output capabilities are categorized records: `ColorCapabilities` (with the `ColorDepth` enum), `TextStylingCapabilities`,
  `GraphicsCapabilities`, `CursorCapabilities`, `WindowCapabilities`, `OutputProtocolCapabilities`, aggregated under
  `OutputCapabilities`. Each has a `None` static for defaults.
- Higher-level output (string/text writers, SGR builders, renderers) is not yet designed.

## Toolchain

- .NET SDK **10.0.0** is pinned in `global.json` with `rollForward: latestMinor` and `allowPrerelease: false`. Any newer
  10.x SDK works; pre-release SDKs and 11.x will be rejected.
- Target framework: `net10.0`. `ImplicitUsings` and `Nullable` are both enabled.
- Embrace latest C# language featuers to the extent they make code easier to read and maintain.
- Prefer using [ReadOnly]Span<T> and Memory<T> for buffers and I/O.

## Common commands

Run from the repository root:

```bash
dotnet build              # build the whole solution
dotnet build -c Release   # release build
dotnet test               # run all tests (xUnit, in GlowTerm.Core.Tests)
dotnet test --filter "FullyQualifiedName~VtSequenceClassifierTests"   # filter by class
dotnet test --filter "DisplayName~OscTerminatedByBel"                 # filter by single test
```

## Parser / interpreter conventions

- **Classifier is purely a framing layer.** It frames bytes into classified tokens; it does not interpret meaning.
  Decoders (mouse, keyboard, focus, paste, device responses) live in the interpreter that consumes the sink callbacks.
- **ESC ambiguity timing belongs to the device, not the parser.** The classifier holds a lone ESC pending in its
  `Escape` state. The device above it is responsible for calling `Flush()` after the bare-ESC quiet period
  (xterm convention: 50 ms with no further input). This keeps the classifier deterministic and synchronously testable.
- **Mode state is shared mutable config.** `VtInputMode` is a mutable class shared between the interpreter (reads)
  and the negotiator (writes). When the negotiator pushes/pops an opt-in (Kitty keyboard, modifyOtherKeys, mouse
  protocol, …), it updates the corresponding property and the interpreter reads on the next event.
- **Use UTF-8 byte-string literals for sequence constants.** `"\x1b[<"u8` is a `ReadOnlySpan<byte>` matched against
  input with zero allocation. Centralize these in `VtInputSequences.cs`.
- **Buffer-lifetime contract still applies.** Sink callbacks receive `ReadOnlySpan<byte>` valid only for the call's
  duration. Implementations that retain data must copy it into event-owned memory.
