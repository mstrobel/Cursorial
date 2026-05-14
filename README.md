# Cursorial

A cross-platform .NET library for building visually rich terminal applications with first-class mouse and keyboard support.

Cursorial is library-first — designed to be consumed by app authors, not used directly as an end-user tool. It targets Windows, macOS, and Linux terminals as equal peers; design choices that would bake in single-platform assumptions (VT-only, Win32-only, etc.) need an explicit story for the others before they land.

## Status

Early-stage and pre-1.0. The lower layers — input parsing, output emission, capability negotiation, session orchestration, and a diffing cell-buffer renderer — are implemented and tested. Higher-level concerns (widget tree, layout, focus management, input routing) are not yet started; when they land they will live in a separate `Cursorial.UI` package on top of the rendering layer.

The public API is not yet stable. Expect breaking changes between commits.

## What works today

- **Input parsing.** Williams-derived VT state machine framing CSI / OSC / DCS / SS3 / ESC sequences into typed `InputEvent`s. Decoder coverage includes printable runs (grapheme-aware), C0 controls, focus events, bracketed paste, cursor / function / special keys (xterm, vt220, SS3, SGR-modifier, modifyOtherKeys level 2, and the CSI-u shorthand), the full Kitty keyboard protocol (press/repeat/release), SGR + SGR-Pixels + X10 mouse encodings with held-button tracking, Win32 Input Mode, device-attribute responses (DA1 / DA2 / DA3 / DSR-CPR / DECRQSS / XTGETTCAP / XTVERSION), and OSC color responses. Unrecognized sequences surface as `UnknownEvent` with the original bytes rather than being silently discarded.

- **Output writers.** Byte-emitting writers that target any `IBufferWriter<byte>`: `CursorWriter`, `ScreenWriter`, `SgrEncoder` (with `WriteAbsolute` / `WriteDelta` for diff rendering), `StyleQuantizer` (capability-aware RGB → palette degradation), `HyperlinkWriter` (OSC 8), and `TextSizingWriter` (Kitty OSC 66, including grapheme-cluster chunking past the 4096-byte payload cap).

- **Capability negotiation.** `VtTerminalNegotiator` runs an XTVERSION + DA1 sentinel handshake, identifies the terminal family, and applies opt-in protocols (SGR mouse + button/any-event tracking, focus events, bracketed paste, Kitty keyboard with configurable flag set, Win32 input mode, synchronized output) gated on what the family actually supports. Returns `TerminalCapabilities` reflecting realized capabilities, not advertised ones. Restores everything it pushed on disposal in LIFO order, and emits a defensive Kitty multi-cursor clear on every session teardown.

- **Terminal session.** `TerminalSession` orchestrates negotiation, the input device, and the output sink. Two factories: `OpenAsync(source, sink)` (bring-your-own transports) and `OpenAsync()` (opens stdio with platform-specific raw-mode handling). The happy-path overload registers POSIX signal handlers (SIGINT / SIGTERM / SIGHUP / SIGQUIT) and an `AppDomain.ProcessExit` hook so the terminal is restored even on abnormal exit. POSIX stdin uses a `poll(2)` + self-pipe pump to avoid the zombie-`read(2)` first-keystroke-swallow.

- **Rendering.** `Cursorial.Rendering` provides a `CellBuffer` (with proper wide-cell handling), a stateful `FrameRenderer` that diffs against the previous frame and emits SGR-delta-encoded byte streams, capability-aware quantization, a blending mode stack (`SourceOver`, `Multiply`, `Screen`, `Overlay`, `Darken`, `Lighten`, `Plus`, plus user-defined), and Porter-Duff alpha compositing on top of those modes.

- **Text utilities.** Grapheme-aware width computation (handles East Asian wide, emoji, VS15/VS16, ZWJ) and an ANSI-aware word-wrap that passes escape sequences through with zero column accounting.

## Layout

| Project | Contents |
| --- | --- |
| `Cursorial.Core` | Input parsing, output writers, terminal session, capability negotiation, text utilities. |
| `Cursorial.Rendering` | Cell buffer, frame renderer, blending modes. |
| `Cursorial.Core.Tests` | xUnit tests for everything in `Cursorial.Core`. |
| `Cursorial.Rendering.Tests` | xUnit tests for the rendering layer. |
| `Cursorial.Demo` | Interactive REPL that drives the library end-to-end. |

## Requirements

- .NET SDK **10.0.0** or later (pinned in `global.json` with `rollForward: latestMinor`).
- A terminal. For development, the test suite has been driven against kitty, Ghostty, iTerm2, WezTerm, Apple Terminal, and Windows Terminal.

## Building and testing

```bash
dotnet build
dotnet test
```

## Trying the demo

```bash
dotnet run --project Cursorial.Demo
```

The demo opens an interactive prompt. Useful commands:

| Command | What it does |
| --- | --- |
| `negotiate` | Run the negotiator and dump the realized capabilities. |
| `read` | Stream decoded input events to stdout. Type, click, scroll, resize. |
| `raw` | Dump raw stdin bytes verbatim with no parsing. |
| `trace` | Live side-by-side view of raw bytes and decoded events. |
| `render` | Diff-rendered cell-buffer showcase: ANSI palette, truecolor gradient, wide glyphs, text attributes, alpha-blended overlay, OSC 66 sized text. |
| `sizing` | Kitty OSC 66 text-sizing demonstration. |
| `probe` | XTVERSION + DA1 raw response capture. |
| `help` / `quit` | Self-explanatory. |

Each command opens its own raw-mode `TerminalSession` and restores cooked mode before the next prompt.

## License

Apache License 2.0. See [LICENSE](LICENSE) for the full text.
