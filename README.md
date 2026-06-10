![Cursorial](https://raw.githubusercontent.com/mstrobel/Cursorial/main/readme_title_banner.png)

A cross-platform .NET library for building visually rich terminal applications with first-class mouse and keyboard support.

Terminal development is deceptively hard. Capability detection, VT-vs-Win32 input, grapheme widths, wide-glyph
alignment, raw-mode and signal-handler safety, and a long tail of per-terminal quirks all stand between you and a
polished UI. Cursorial absorbs that complexity so you don't have to. Whether you're building a TUI framework or an
application, **use as much of the stack as you need** — take just the input handling, or opt all the way in to the
rendering layer with rich text formatting and image support.

Cursorial is **library-first**: designed to be consumed by app and framework authors, not used directly as an
end-user tool. It targets Windows, macOS, and Linux terminals as equal peers — design choices that would bake in
single-platform assumptions (VT-only, Win32-only) need an explicit story for the others before they land.

## Layered design

The library is a stack. Each layer builds on the one below it, and each is usable on its own — you only reference
what you intend to use.

| Layer | Package | Role | Status |
| --- | --- | --- | --- |
| **`Cursorial.Core`** | ✅ Available on NuGet | Input parsing & delivery, output sequence emission, capability negotiation, session orchestration, text utilities. | **Ready for integration.** The most relevant APIs are reasonably stable. |
| **`Cursorial.Rendering`** | ⏳ Not yet published | Cell buffer, diffing frame renderer, rich text formatting, images, blending & alpha compositing. | **Work in progress.** No package yet — feedback welcome. |
| **`Cursorial.Drawing`** | ⏳ Not yet published | Brushes & gradients, a pen/box engine with automatic junctions, charts, scene compositing with cached rasters, and UI drawing primitives (clip/translate, occluding & image fills, titled panels, shadows). | **Work in progress** (on a feature branch). |
| **`Cursorial.Animation`** | ⏳ Not yet published | A time-free animation engine — easings, keyframes, and value interpolators (`Cursorial.Core`-only; the consumer/UI owns the clock). | **Work in progress.** |
| **`Cursorial.UI`** | — Not started | Widget tree, layout, focus management, input routing. | Possible future layer on top of `Cursorial.Drawing`. Work has not yet commenced. |

```bash
dotnet add package Cursorial.Core
```

> The `Core` API is approaching stable, but the library as a whole is pre-1.0. Expect occasional breaking changes,
> especially in `Cursorial.Rendering`.

## A taste

```csharp
// Raw mode, capability handshake, opt-in protocols, signal-safe restore — all in one call.
await using var session = await TerminalSession.OpenAsync();
var caps = session.Capabilities;

var buffer = new CellBuffer(Console.WindowWidth, Console.WindowHeight, caps);
var renderer = new FrameRenderer(caps.Output);

buffer.Write(0, 0, "Hello, terminal 👋",
             Style.Default.WithForeground(Color.FromRgb(64, 224, 208))
                          .WithAttributes(TextAttributes.Bold));

var frame = new ArrayBufferWriter<byte>();
renderer.Render(buffer, frame);
await session.Output.Writer.WriteAsync(frame.WrittenMemory);
await session.Output.Writer.FlushAsync();

await foreach (var evt in session.Input.ReadAllAsync())
    if (evt is KeyEvent { Kind: KeyEventKind.Down, Text.Span: "q" }) break;
```

`TerminalSession.OpenAsync()` puts stdio into raw mode, runs the capability handshake, and applies the opt-in
protocols (mouse, focus, paste, Kitty keyboard, …) the negotiator's policy allows. Disposal restores everything it
touched — termios / Windows console state, every protocol opt-in — in LIFO order, and signal handlers (SIGINT /
SIGTERM / SIGHUP / SIGQUIT / `AppDomain.ProcessExit`) are registered automatically so the terminal isn't left in raw
mode on abnormal exit. A bring-your-own-transport overload — `TerminalSession.OpenAsync(source, sink)` — embeds the
pipeline inside a tool that already owns terminal state, or drives it from a recorded trace.

---

# `Cursorial.Core` — the foundation

Everything you need to read input, negotiate capabilities, and emit correct escape sequences. **Ready for
integration today.**

## Input

`InputEvent` is a sealed record hierarchy — pattern-match on the concrete type. Unrecognized CSI / OSC / DCS / SS3
sequences surface as `UnknownEvent` with the original wire bytes, so nothing is silently swallowed.

- **Keyboard** — `KeyEvent` with key-down / key-up distinction (release where the terminal reports it), an
  `IsRepeat` flag, the full modifier set, and a `Text` payload. Decodes legacy xterm, `modifyOtherKeys`, CSI u, and
  the Kitty keyboard protocol.
- **Mouse** — `MouseEvent` press / release / drag / motion / wheel, buttons X1–X4, SGR (1006) and SGR-Pixels (1016)
  encodings, with an accumulated held-button mask.
- **Other** — `FocusEvent`, `PasteEvent` (bracketed paste), `ResizeEvent` (SIGWINCH on POSIX, polled on Windows),
  and device responses (DA1/DA2/DA3, DSR-CPR, XTVERSION, color and cell-size queries).
- **Windows** — Win32 Input Mode console records decoded into the same `KeyEvent` shape.

Two delivery shapes, picked per device:

```csharp
// Pull (async): natural for code structured around `await foreach`.
await foreach (var evt in session.Input.ReadAllAsync(ct))
{
    switch (evt)
    {
        case KeyEvent { Kind: KeyEventKind.Down } k: HandleKey(k); break;
        case MouseEvent m:                           HandleMouse(m); break;
        case ResizeEvent r:                          HandleResize(r); break;
    }
}

// Push (event-driven): wrap any device for classic EventHandler<> events.
// On a UI thread, EventInputDevice.CapturingCurrentContext(session.Input) instead marshals
// raises onto the captured dispatcher; the default raises them on a background pump thread.
await using var events = new EventInputDevice(session.Input);
events.Input     += (_, e)  => HandleEvent(e);
events.Error     += (_, ex) => Log(ex);
events.Completed += (_, _)  => Log("input pipeline closed");
```

Both `IAsyncInputDevice` and `IEventInputDevice` extend `IInputDevice`, which carries the realized
`InputCapabilities` and the `IAsyncDisposable` contract. Devices chain via decoration (`IInputDeviceDecorator`);
the `InputEvent.Synthesized` flag distinguishes fabricated events from device-reported truth. With the pull surface,
your `await foreach` loop body resumes on whatever context you awaited from (e.g. a UI thread), so the input layer
plays nice without configuration.

### Transforms and synthesis

Stages that rewrite the event stream are `IInputTransformer`s; `device.Transform(transformer)` applies one
(`TransformingInputDevice` folds the transformer's capabilities into the device it returns).

- **Mouse clicks** — `device.WithClickSynthesis(options)` counts rapid same-cell clicks into
  `MouseEvent.ClickCount` (1 / 2 / 3 …, WPF-style press-determined timing) and can emit synthesized
  `MouseEventKind.Click` events. Configure the multi-click window, whether to emit `Click` events, and which event
  surfaces the count (`ButtonDown` / `ButtonUp` / `Click`).
- **Key releases & repeat** — `KeyReleaseSynthesizer` fabricates `KeyEventKind.Up` and `IsRepeat` for terminals that
  report neither (a timer-driven device decorator rather than a pure transform).

```csharp
var input = session.Input.WithClickSynthesis(
    new MouseClickOptions { SynthesizeClickEvents = true, ClickCount = ClickCountTarget.Click });

await foreach (var evt in input.ReadAllAsync(ct))
    if (evt is MouseEvent { Kind: MouseEventKind.Click, ClickCount: 2 } m)
        OnDoubleClick(m.Position);
```

## Capability negotiation

`VtTerminalNegotiator` runs an XTVERSION + DA1 sentinel handshake at session open, identifies the terminal family
(Kitty, iTerm2, WezTerm, Ghostty, Rio, Windows Terminal, xterm, screen, tmux, Apple Terminal, …), and enables only
the protocols that family actually honors:

- SGR mouse (1006) + button-event tracking (1002) + optional any-event motion (1003).
- Focus events (1004), bracketed paste (2004).
- Kitty keyboard with a configurable flag set.
- Win32 Input Mode on Windows-family terminals.
- Synchronized output (2026) on supporting families.

It returns a `TerminalCapabilities` aggregate reflecting **realized** capabilities — features a terminal claims but
doesn't honor are reported as unavailable, so you can branch on flags directly. Restore is idempotent and reverses
every opt-in in LIFO order.

## Output writers

Pure byte-emitting writers target `IBufferWriter<byte>`, so the same encoder feeds both live terminal output and an
in-memory scratch buffer. Use them directly when you want "move the cursor, print red text" without the rendering
layer.

- **`SgrEncoder`** — `Style` → SGR bytes, with full-style and minimal-delta modes.
- **`CursorWriter`** — absolute / relative moves, save / restore, hide / show, shape (DECSCUSR).
- **`ScreenWriter`** — clear screen / line variants, alternate screen buffer, scroll region.
- **`HyperlinkWriter`** — OSC 8 clickable hyperlinks.
- **`TextSizingWriter`** — Kitty OSC 66 text sizing, with grapheme-cluster chunking past the payload cap.

### Style and color

`Style` is a value type — foreground, background, text attributes, underline shape, and underline color — composed
with fluent `With…` helpers; `default(Style)` is "no styling."

```csharp
var s = Style.Default
    .WithForeground(Color.FromRgb(64, 224, 208))
    .WithBackground(Color.FromPalette(4))
    .WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
    .WithUnderlineStyle(UnderlineStyle.Curly)
    .WithUnderlineColor(Color.FromRgb(255, 80, 80));
```

- **`Color.Default`** — the terminal's default fg / bg (no SGR color parameters emitted).
- **`Color.FromPalette(index)`** — ANSI 0–15 plus the xterm 6×6×6 cube + grayscale ramp (16–255).
- **`Color.FromRgb(r, g, b)`** — 24-bit truecolor.
- **`Color.FromRgba(r, g, b, a)`** / `color.WithAlpha(a)` — RGB with an alpha channel for blending (consumed at
  composite time; stored cells are always opaque).

**Terminals lie** about what they render. `StyleQuantizer` adapts a style to realized capabilities — RGB → 256 or 16
colors, extended underline shapes → `Single`, dropping attributes and colored underlines the terminal won't honor.
The frame renderer applies it automatically when constructed with capabilities.

## Text utilities

- **`GraphemeWidth`** — `CodepointWidth`, `ClusterWidth`, `StringWidth`. Handles VS15/VS16 presentation selectors and
  ZWJ continuation; covers Hangul, CJK, fullwidth forms, and the major emoji blocks.
- **`AnsiTextWrap`** — word-wrap that measures by grapheme cluster and passes ANSI escapes through with zero column
  accounting, so SGR state survives the line split.

---

# `Cursorial.Rendering` — drawing

A cell-buffer + diffing renderer, plus the content types you draw into it: rich text, images, and sized text.
**Work in progress — no package yet, but feedback is very welcome.**

## Cell buffer + frame renderer

Write into a `CellBuffer`; hand successive buffers to a stateful `FrameRenderer` that diffs them and emits the
minimal byte stream to update the terminal.

```csharp
var buffer = new CellBuffer(columns: 80, rows: 24, caps);

buffer.Write(0, 0, "Total: 42 items", Style.Default);      // whole string — grapheme-segmented
buffer.Write(0, 1, "中文 and 👨‍👩‍👧 emoji", style);          // wide glyphs + clusters advance correctly
buffer.Set(0, 2, "中", style);                              // Set places exactly one grapheme cluster

buffer.CursorVisible = true;
buffer.CursorShape   = CursorShape.SteadyBar;

renderer.Render(buffer, frame);       // full redraw on first frame / resize; per-cell delta thereafter
```

- **Grapheme-aware** — `Write` segments a string into grapheme clusters and lays them out across a row, advancing
  by each cluster's width; `Set` places exactly one cluster. Both maintain wide-cell (`WideLeft` +
  `WideContinuation`) consistency and return the number of columns written.
- **Minimal diffs** — the renderer owns the previous frame plus the SGR and cursor state it believes the terminal
  is in, and emits only what changed. `Reset()` forces a clean redraw.
- **Capability-aware** — constructed with `OutputCapabilities`, it quantizes every cell before diffing, so a stable
  frame produces an empty delta even on a 16-color terminal.

### Sub-views

`buffer.View(column, row, columns, rows)` (or `View(in Rect)`) returns a `CellBufferView` — a window onto a
sub-rectangle that takes **view-local, 0-based coordinates**, translates them to the backing buffer, and **clips
writes to its bounds**. A widget can draw in its own coordinate space without knowing where it sits on the screen,
and can't scribble outside its box. Views nest, and expose the same drawing surface as the buffer itself — `Set`,
`Write`, `Fill`, fragments, dirty regions, cursor — so drawing code is identical whether handed a buffer or a view.

```csharp
var panel = buffer.View(column: 10, row: 2, columns: 30, rows: 8);

panel.Write(0, 0, "Settings", Style.Default);   // (0,0) is the panel's top-left
panel.Write(0, 1, "A very long line that runs off the edge", Style.Default);  // clipped to 30 cols
```

## Rich text formatting

Parse a compact markup string (or build it fluently), format it to a column width with wrapping / alignment /
trimming, and paint the result.

```csharp
var rich = TextMarkup.Parse(
    "[b]Build[/b] [fg=green]passed[/fg] in 4.2s — see [link=https://ci.example/42]run #42[/link].");

var formatted = new TextFormatter { Wrap = WrapMode.WordWrap, Alignment = TextAlignment.Left }
    .Format(rich, availableColumns: 50, capabilities: caps.Output);

formatted.Paint(buffer, column: 2, row: 2, style: Style.Default, capabilities: caps.Output);
```

- **Inline tags** — `[b]` `[i]` `[u]` `[s]`, `[fg=…]` / `[bg=…]`, `[link=…]`, `[font=…]`.
- **Blocks** — `[p align=… wrap=… trim=… maxlines=…]`, `[br]`, `[hr]`, inline `[content=…]` slots.
- **Programmatic** — `RichTextBuilder` builds the same model fluently (`.Run`, `.Hyperlink`, `.Paragraph`,
  `.HorizontalRule`, `.SizedText`, `.Figlet`, …) with disposable style scopes.
- **OSC 8 hyperlinks** survive wrapping; styled runs keep their color across the line split.

## Images

`Image` (and the `Icon` convenience subclass) pick a graphics protocol at paint time from the negotiated
capabilities, and fall back gracefully when none is available.

```csharp
// From a file or URI, sized to a cell footprint (0 = auto from aspect ratio).
var photo = new Image(new Uri("avatar.png", UriKind.Relative), renderSize: new Size(20, 10));
photo.Paint(buffer, column: 1, row: 1, style: Style.Default, capabilities: caps.Output);

// Icon: an embedded image with a markup glyph to fall back on where graphics aren't supported.
var logo = Icon.FromEmbedded(Assembly.GetExecutingAssembly(), "Assets/logo.png",
                             fallbackGlyph: "[b][fg=cyan]C>[/fg][/b]", renderSize: new Size(6, 0));
logo.Paint(buffer, column: 2, row: 12, style: Style.Default, capabilities: caps.Output);
```

- **Protocols** — Kitty graphics → iTerm2 inline images → Sixel (with a built-in PNG decoder + resampler), chosen
  by realized capability.
- **Fallback** — a cell-rectangle placeholder (or an `Icon`'s glyph) when no protocol is available.
- **Stable across frames** — reuse the same `Image`/`Icon` instance and the renderer diff-skips it, so an image
  transmits exactly once instead of churning the terminal's image store.

## Sized text

`ScaledText` is the capability-aware entry point for large text: a Kitty OSC 66 sized-text fragment where supported,
and a bundled FIGlet face everywhere else.

```csharp
var title = new ScaledText("Dashboard", new TextSizing(Scale: 3));
title.Paint(buffer, new Rect(2, 0, title.Measure(buffer.Size, caps.Output)), Style.Default, caps.Output);
```

## Blending and alpha compositing

`CellBuffer.Set` / `Fill` compose new colors against existing ones through the current blending mode — the top of an
internal stack, or `SourceOver` when empty.

```csharp
buffer.PushBlendingMode(BlendingModes.Multiply);
try
{
    for (int dx = 0; dx < width; dx++)
        buffer.Set(row, col + dx, " ",
                   Style.Default.WithBackground(Color.FromRgba(255, 80, 80, 128)));
}
finally { buffer.PopBlendingMode(); }
```

Built-in modes: `SourceOver` (the default), `Multiply`, `Screen`, `Overlay`, `Darken`, `Lighten`, `Plus`. Custom
modes implement `IBlendingMode`. Only color fields blend; blending engages for RGB-on-RGB pairs (palette / default
colors short-circuit to "return source"). Alpha is mixed after the blend: `result = blended·α + backdrop·(1-α)`.

---

## Project layout

| Project | Contents |
| --- | --- |
| `Cursorial.Core` | Input parsing & delivery, output writers, capability negotiation, terminal session, text utilities. |
| `Cursorial.Rendering` | Cell buffer, frame renderer, rich text, images, blending & alpha compositing. |
| `Cursorial.Core.Tests` / `Cursorial.Rendering.Tests` | xUnit test suites. |
| `Cursorial.Demo` | Interactive REPL that drives the library end-to-end. |

## Requirements

- .NET SDK **10.0.0** or later (pinned in `global.json` with `rollForward: latestMinor`).
- A terminal. The suite has been exercised against kitty, Ghostty, iTerm2, WezTerm, Rio, Apple Terminal, Alacritty,
  GNOME Terminal, ConEmu/Cmder, and Windows Terminal / Console Host.

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
| `raw` / `trace` | Raw stdin bytes verbatim, or a live side-by-side view of bytes and decoded events. |
| `render` | Diff-rendered showcase: ANSI palette, truecolor gradient, wide glyphs, attributes, alpha overlay, images, sized text. |
| `sizing` | Kitty OSC 66 text-sizing demonstration. |
| `probe` | XTVERSION + DA1 raw response capture. |
| `help` / `quit` | Self-explanatory. |

Each command opens its own raw-mode `TerminalSession` and restores cooked mode before the next prompt.

## License

Apache License 2.0. See [LICENSE](LICENSE) for the full text.
