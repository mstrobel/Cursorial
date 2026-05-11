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
- **Input parsing foundation** (`GlowTerm.Core/Input/Parsing/`, same `GlowTerm.Core.Input.Parsing` namespace) —
  `VtSequenceClassifier` is a Williams-derived state machine that frames bytes into classified tokens dispatched to
  `IVtSequenceTokenSink`. Covers ground / ESC / CSI / OSC / DCS / SS3 (as ESC + intermediate `O`). Does NOT cover
  APC, SOS, PM, or 8-bit C1 controls (deliberately out of scope for input). `VtInputMode` is the mutable mode bag
  (DECCKM, modifyOtherKeys level, mouse encoding, Kitty flags, etc.) the interpreter holds and the negotiator updates.
  `VtInputSequences` centralizes UTF-8 byte-string constants.

The interpreter (token-sink → `InputEvent`) and concrete `IAsyncInputDevice`/`IEventInputDevice` implementations are
not yet started. Rendering and layout are not started.

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
