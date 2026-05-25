![Cursorial](https://raw.githubusercontent.com/mstrobel/Cursorial/main/readme_title_banner.png)

A cross-platform .NET library for building visually rich terminal applications with first-class mouse and keyboard support.

Cursorial is library-first — designed to be consumed by app authors, not used directly as an end-user tool. It targets Windows, macOS, and Linux terminals as equal peers; design choices that would bake in single-platform assumptions (VT-only, Win32-only, etc.) need an explicit story for the others before they land.

## Status

Early-stage and pre-1.0. The lower layers — input parsing, output emission, capability negotiation, session orchestration, and a diffing cell-buffer renderer — are implemented and tested. Higher-level concerns (widget tree, layout, focus management, input routing) are not yet started; when they land they will live in a separate `Cursorial.UI` package on top of the rendering layer.

The public API is not yet stable. Expect breaking changes between commits.

## A taste

```csharp
await using var session = await TerminalSession.OpenAsync();

var buffer = new CellBuffer(Console.WindowWidth, Console.WindowHeight);
var renderer = new FrameRenderer(session.Capabilities.Output);

buffer.Set(0, 0, "Hello, terminal.",
           Style.Default.WithForeground(Color.FromRgb(64, 224, 208))
                        .WithAttributes(TextAttributes.Bold));

var frame = new ArrayBufferWriter<byte>();
renderer.Render(buffer, frame);
await session.Output.Writer.WriteAsync(frame.WrittenMemory);
await session.Output.Writer.FlushAsync();

await foreach (var evt in session.Input.ReadAllAsync())
{
    if (evt is KeyEvent { Kind: KeyEventKind.Press, Key: Key.Q }) break;
}
```

`TerminalSession.OpenAsync()` puts stdio into raw mode, runs the capability handshake, and applies the opt-in protocols (mouse, focus, paste, Kitty keyboard, …) the negotiator's policy allows. Disposal restores everything it touched, including termios / Windows console state, in LIFO order. Signal handlers (SIGINT / SIGTERM / SIGHUP / SIGQUIT / `AppDomain.ProcessExit`) are registered automatically so the terminal isn't left in raw mode on abnormal exit.

There is also a bring-your-own-transport overload — `TerminalSession.OpenAsync(source, sink)` — for embedding inside a tool that already manages terminal state, or for driving the pipeline from a recorded trace.

## Input delivery

`InputEvent` is a sealed record hierarchy. Consumers pattern-match on the concrete type — `KeyEvent`, `MouseEvent`, `PointerEvent`, `FocusEvent`, `PasteEvent`, `ResizeEvent`, `UnknownEvent`, etc. Unrecognized CSI / OSC / DCS / SS3 sequences surface as `UnknownEvent` with the original wire bytes so consumers can log, forward, or parse them without us silently swallowing protocol surface.

Two delivery shapes, picked per-device:

- **Pull (async).** `IAsyncInputDevice` exposes `IAsyncEnumerable<InputEvent>` via `ReadAllAsync(CancellationToken)`. Natural fit for app code structured around `await foreach`.

  ```csharp
  await foreach (var evt in session.Input.ReadAllAsync(ct))
  {
      switch (evt)
      {
          case KeyEvent { Kind: KeyEventKind.Press } k: HandleKey(k); break;
          case MouseEvent m: HandleMouse(m); break;
          case ResizeEvent r: HandleResize(r); break;
      }
  }
  ```

- **Push (event-driven).** Wrap any `IAsyncInputDevice` in an `EventInputDevice` to get classic `EventHandler<>` style events:

  ```csharp
  await using var events = new EventInputDevice(session.Input);
  events.Input     += (_, e) => HandleEvent(e);
  events.Error     += (_, ex) => Log(ex);
  events.Completed += (_, _) => Log("input pipeline closed");
  ```

Both extend `IInputDevice`, which carries the realized `InputCapabilities` and the `IAsyncDisposable` contract. A device may implement either or both. Devices chain via decoration — `IInputDeviceDecorator` is the contract for wrappers (e.g., a future key-up / repeat synthesizer that fabricates events for terminals that don't natively report key release). The `InputEvent.Synthesized` flag distinguishes fabricated events from device-reported truth.

## Drawing: cell buffer + frame renderer

The rendering layer is a two-piece system: a `CellBuffer` you write into, and a stateful `FrameRenderer` that diffs successive buffers and emits the minimal byte stream to update the terminal.

```csharp
var buffer = new CellBuffer(columns: 80, rows: 24);

buffer.Set(row, col, "x", Style.Default);
buffer.Set(row, col, "中", style);        // wide glyph — occupies two cells
buffer.Set(row, col, "👨‍👩‍👧", style);   // grapheme cluster — single Set call

buffer.CursorRow = 5;
buffer.CursorColumn = 12;
buffer.CursorVisible = true;
buffer.CursorShape = CursorShape.SteadyBar;
```

`CellBuffer.Set` is grapheme-aware: it computes the cluster width, writes a `WideLeft` + `WideContinuation` pair when the glyph is double-width, and cleans up adjacent cells to maintain wide-cell consistency. It returns the width that was painted.

`FrameRenderer` owns the previous frame, the SGR state currently active on the terminal, and the cursor position the renderer believes the terminal is at. Each call to `Render(buffer, output)` emits either a full redraw (first frame, dimension change, or `ForceFullRedraw`) or a per-cell delta. The renderer is the single owner of SGR + cursor state across frames — interleaving raw output that mutates those will desync the next frame; use the writers (`CursorWriter`, `ScreenWriter`, `HyperlinkWriter`, `TextSizingWriter`) when you need ad-hoc emissions, and call `renderer.Reset()` afterwards to force a clean redraw.

Wide-cell continuations are skipped during emission because the wide-glyph bytes emitted at the `WideLeft` position paint both cell columns as a single terminal operation.

## Style and color

A `Style` is a value type carrying foreground, background, text attributes, underline shape, and underline color. Fluent `With…` helpers compose them; `default(Style)` is "no styling."

```csharp
var s = Style.Default
    .WithForeground(Color.FromRgb(64, 224, 208))
    .WithBackground(Color.FromPalette(4))
    .WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
    .WithUnderlineStyle(UnderlineStyle.Curly)
    .WithUnderlineColor(Color.FromRgb(255, 80, 80));
```

`Color` is discriminated by `ColorKind`:

- **`Color.Default`** — whatever the terminal's default foreground / background is. Bytes-on-wire: no foreground / background SGR parameters.
- **`Color.FromPalette(byte index)`** — ANSI palette indices 0–15 (the basic 16) plus 16–255 (xterm 6×6×6 cube + grayscale ramp).
- **`Color.FromRgb(r, g, b)`** — 24-bit truecolor.
- **`Color.FromRgba(r, g, b, alpha)`** / `color.WithAlpha(a)` — RGB with an alpha channel for blending. Alpha is consumed at composite time inside the cell buffer; stored cells always end up at alpha 255 (terminals cannot render translucent SGR colors).

`TextAttributes` is a `[Flags]` enum — `Bold`, `Faint`, `Italic`, `Underline`, `Blink`, `Inverse`, `Hidden`, `Strikethrough`, `Overline`. `UnderlineStyle` selects the shape (`Single`, `Double`, `Curly`, `Dotted`, `Dashed`) when the `Underline` flag is set.

### Capability-aware quantization

Terminals lie. They claim 256-color but render extended underline shapes incorrectly; they support truecolor on some sessions and 16-color on others. The negotiator probes what's actually realized, and a `StyleQuantizer` adapts styles to fit:

- RGB → 256-color via xterm's 6×6×6 cube + grayscale ramp when truecolor isn't available.
- Palette > 15 → 16 via approximate channel-on thresholds.
- Drops attributes the terminal doesn't honor.
- Collapses extended underline shapes to `Single`.
- Drops underline color when unsupported.

When constructed with capabilities — `new FrameRenderer(session.Capabilities.Output)` — the renderer holds a quantizer and runs each cell's style through it before diffing or emission. The front-buffer snapshot stores the quantized form so a stable rendered frame produces an empty delta. The no-capabilities constructor preserves raw-style behavior for tests and for consumers that quantize upstream.

## Blending modes and alpha compositing

`CellBuffer.Set` and `Fill` compose new style colors against the existing cell's colors through the current blending mode — the top of an internal stack, or `BlendingModes.SourceOver` when the stack is empty.

```csharp
buffer.PushBlendingMode(BlendingModes.Multiply);
try
{
    for (int dx = 0; dx < width; dx++)
        buffer.Set(row, col + dx, " ",
                   Style.Default.WithBackground(Color.FromRgba(255, 80, 80, 128)));
}
finally
{
    buffer.PopBlendingMode();
}
```

Built-in modes: `SourceOver` (the `Default`), `Multiply`, `Screen`, `Overlay`, `Darken`, `Lighten`, `Plus`. Custom modes implement `IBlendingMode { Color Blend(Color source, Color backdrop); }` and slot in cleanly. Only color fields (foreground, background, underline color) blend; non-color style (attributes, underline shape) takes the source's value. Blending engages only for RGB-on-RGB pairs — palette / default colors short-circuit to "return source" because round-tripping through RGB would be lossy and surprising.

Alpha compositing runs in two steps: the active blending mode produces a *blended color* from the source and backdrop (treating both as opaque), then the buffer linearly mixes that blend with the backdrop using the source's alpha: `result = blended·α + backdrop·(1-α)`. With an empty blend stack (`SourceOver`), this collapses to the classic linear alpha blend.

## Inline rich text

For decorations the cell grid doesn't natively model, the byte writers can be invoked directly against an `IBufferWriter<byte>`:

- **`HyperlinkWriter`** — OSC 8 clickable hyperlinks. Open / close brackets, or a one-shot `WriteHyperlink(uri, text)`.
- **`TextSizingWriter`** — Kitty OSC 66 text sizing (scaled / wider / numerator-denominator / vertical offsets / horizontal offsets). Includes grapheme-cluster chunking past the 4096-byte payload cap.

These bypass the cell grid; render them at a fixed position alongside the buffered frame. The cell buffer doesn't yet model OSC 8 anchors or multi-row OSC 66 spans — that's planned for a higher layer.

## Text utilities (`Cursorial.Core.Text`)

- **`GraphemeWidth`** — `CodepointWidth(int)`, `ClusterWidth(ReadOnlySpan<char>)`, `StringWidth(string)`. Hand-coded table covering Hangul, CJK Unified Ideographs (Plane 0–3), Compatibility Ideographs, Fullwidth Forms, and the major emoji blocks. Cluster width handles VS16 (forces emoji presentation → bumps to 2), VS15 (forces text presentation → pins to 1), and ZWJ continuation.
- **`AnsiTextWrap`** — word-wrap that measures width via grapheme clusters and passes ANSI escape sequences through with zero column accounting. SGR state crosses wrap boundaries naturally; multi-line styled output preserves color across the split.

## Capability negotiation (`VtTerminalNegotiator`)

The negotiator runs an XTVERSION + DA1 sentinel handshake at session open, identifies the terminal family (Kitty, iTerm2, WezTerm, Ghostty, Windows Terminal, xterm, screen, tmux, Apple Terminal, …), and applies opt-in protocols gated on what the family actually supports:

- SGR mouse (DECSET 1006) + button-event tracking (1002) + optional any-event motion (1003).
- Focus events (1004).
- Bracketed paste (2004).
- Kitty keyboard with a configurable flag set.
- Win32 Input Mode on Windows-family terminals.
- Synchronized output (2026) on supporting families.

It returns a `TerminalCapabilities` aggregate reflecting *realized* capabilities (features the terminal claimed but did not honor are reported as unavailable). Disposal reverses every opt-in in LIFO order and is idempotent. The probe path is single-shot per instance — re-negotiating requires a new instance, which keeps "what to restore to" unambiguous.

## Layout

| Project | Contents |
| --- | --- |
| `Cursorial.Core` | Input parsing, output writers, terminal session, capability negotiation, text utilities. |
| `Cursorial.Rendering` | Cell buffer, frame renderer, blending mode integration. |
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
