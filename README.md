![Cursorial](https://raw.githubusercontent.com/mstrobel/Cursorial/main/readme_title_banner.png)

A cross-platform .NET library for building visually rich terminal applications with first-class mouse and keyboard support.

Terminal development is deceptively hard. Capability detection, VT-vs-Win32 input, grapheme widths, wide-glyph
alignment, raw-mode and signal-handler safety, and a long tail of per-terminal quirks all stand between you and a
polished UI. Cursorial absorbs that complexity so you don't have to. Whether you're building a TUI framework or an
application, **use as much of the stack as you need** — take just the input handling, opt in to the rendering and
drawing layers, or build a declarative, data-bound UI in XAML.

Cursorial is **library-first**: designed to be consumed by app and framework authors, not used directly as an
end-user tool. It targets Windows, macOS, and Linux terminals as equal peers — design choices that would bake in
single-platform assumptions (VT-only, Win32-only) need an explicit story for the others before they land.

> **📖 Documentation lives in the [Wiki](https://github.com/mstrobel/Cursorial/wiki).** This README is the tour; the
> wiki has the deep per-layer guides, tutorials, and API walkthroughs.

## Layered design

The library is a stack. Each layer builds on the one below it, and each is usable on its own — you only reference
what you intend to use.

| Layer | Package | Role |
| --- | --- | --- |
| **`Cursorial.Core`** | ✅ On NuGet | Input parsing & delivery, output sequence emission, capability negotiation, session orchestration, text utilities. |
| **`Cursorial.Rendering`** | ✅ On NuGet | Cell buffer, diffing frame renderer, rich-text formatting, images, blending & alpha compositing. |
| **`Cursorial.Drawing`** | ✅ On NuGet | Scenes & cached-raster compositor, brushes & gradients, a pen/box engine with automatic junctions, charts, and UI drawing primitives (clip/translate, occluding & image fills, titled panels, shadows). |
| **`Cursorial.Animation`** | ✅ On NuGet | A time-free animation engine — easings, keyframes, and value interpolators (the consumer owns the clock). |
| **`Cursorial.UI`** | ✅ On NuGet | A WPF/Avalonia-style UI framework: dependency-property system, element tree, layout, render zones, routed input & focus, styling with CSS-like selectors, data binding, resources & theming, windowing, storyboards & transitions, and a full control catalog. |
| **`Cursorial.UI.Xaml`** | ✅ On NuGet | A runtime XAML loader **and** a Roslyn source generator — declarative markup, `{Binding}`/`{StaticResource}`/`ControlTemplate`, typed code-behind, compiled bindings, and an AOT-clean metadata provider. |
| **`Cursorial.UI.Bars`** | ✅ On NuGet | Command surfaces over one shared `BarCommand` set: a `Toolbar` with discrete overflow, a `Ribbon` (tabs/groups, density collapse, contextual tabs, Backstage, Quick Access Toolbar, minimize), KeyTips (Alt-overlay accelerators), and SuperTips. |

```bash
dotnet add package Cursorial.Core
```

> Every layer above is published on NuGet. `Cursorial.Core` is approaching stable; the layers on top of it are
> **pre-release** — usable today (the `Cursorial.Demo` REPL and `Cursorial.Gallery` app drive them end-to-end), but
> expect breaking changes as their surfaces settle. `Cursorial.UI.Testing` is a test-only harness and is not
> published. Feedback very welcome.

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

Everything you need to read input, negotiate capabilities, and emit correct escape sequences. **The most stable,
integration-ready layer.** &nbsp;→ Wiki: [Input](https://github.com/mstrobel/Cursorial/wiki/Core-Input) ·
[Capabilities](https://github.com/mstrobel/Cursorial/wiki/Core-Capability-Negotiation) ·
[Output](https://github.com/mstrobel/Cursorial/wiki/Core-Output) ·
[Text](https://github.com/mstrobel/Cursorial/wiki/Core-Text-Utilities)

**Input.** `InputEvent` is a sealed record hierarchy — pattern-match on the concrete type. `KeyEvent`
(down/up/repeat, full modifiers, `Text` payload; legacy xterm, `modifyOtherKeys`, CSI u, Kitty keyboard, and Win32
Input Mode), `MouseEvent` (press/release/drag/motion/wheel, buttons X1–X4, SGR + SGR-Pixels), plus `FocusEvent`,
`PasteEvent`, `ResizeEvent`, and device responses. Unrecognized sequences surface as `UnknownEvent` with the wire
bytes — nothing is silently swallowed. Pick a delivery shape per device: pull (`IAsyncInputDevice.ReadAllAsync`,
`await foreach`) or push (`IEventInputDevice`, classic events). Transforms (`device.WithClickSynthesis(…)`,
`KeyReleaseSynthesizer`) layer click-counting and synthetic key-up/repeat on top.

```csharp
await foreach (var evt in session.Input.ReadAllAsync(ct))
    switch (evt)
    {
        case KeyEvent { Kind: KeyEventKind.Down } k: HandleKey(k); break;
        case MouseEvent m:                           HandleMouse(m); break;
        case ResizeEvent r:                          HandleResize(r); break;
    }
```

**Capability negotiation.** `VtTerminalNegotiator` runs an XTVERSION + DA1 sentinel handshake, identifies the
terminal family (Kitty, iTerm2, WezTerm, Ghostty, Rio, Windows Terminal, xterm, tmux, Apple Terminal, …), and
enables only the protocols that family honors (SGR mouse, focus, bracketed paste, Kitty keyboard, Win32 Input Mode,
synchronized output). It returns a `TerminalCapabilities` aggregate of **realized** capabilities — features a
terminal claims but doesn't honor are reported unavailable — and restore is idempotent, reversing every opt-in.

**Output writers.** Pure byte-emitters targeting `IBufferWriter<byte>`: `SgrEncoder` (full + minimal-delta),
`CursorWriter`, `ScreenWriter`, `HyperlinkWriter` (OSC 8), `TextSizingWriter` (Kitty OSC 66). The `Style` value type
composes foreground/background/attributes/underline; `Color` spans `Default`, `FromPalette(0–255)`, `FromRgb`, and
`FromRgba` (alpha for blending). `StyleQuantizer` adapts a style down to what the terminal can actually render.

**Text utilities.** `GraphemeWidth` (cluster-accurate widths, VS15/VS16, ZWJ, CJK & emoji) and `AnsiTextWrap`
(word-wrap that measures by grapheme cluster and passes ANSI escapes through untouched).

---

# `Cursorial.Rendering` — drawing to a cell buffer

A cell-buffer + diffing renderer, plus the content types you draw into it: rich text, images, and sized text.
&nbsp;→ Wiki: [Rendering](https://github.com/mstrobel/Cursorial/wiki/Rendering-Cell-Buffer-and-Renderer) ·
[Rich Text & Images](https://github.com/mstrobel/Cursorial/wiki/Rendering-Rich-Text-and-Images)

Write into a `CellBuffer`; hand successive buffers to a stateful `FrameRenderer` that diffs them and emits the
minimal byte stream to update the terminal (including scroll-region detection). Writes are grapheme-aware, wide-cell
consistent, and capability-quantized before diffing, so a stable frame produces an empty delta even on a 16-color
terminal. `buffer.View(…)` returns a clipped, view-local `CellBufferView` so a widget draws in its own coordinate
space without knowing where it sits.

```csharp
var buffer = new CellBuffer(columns: 80, rows: 24, caps);
buffer.Write(0, 0, "中文 and 👨‍👩‍👧 emoji", style);   // wide glyphs + clusters advance correctly
renderer.Render(buffer, frame);                       // full redraw on frame 1 / resize; per-cell delta after
```

On top of the buffer sit **rich text** (`TextMarkup.Parse` / `RichTextBuilder` → `TextFormatter` → paint, with
inline tags, wrapping/alignment, and OSC 8 hyperlinks that survive a line split), **images** (`Image` / `Icon` pick
Kitty graphics → iTerm2 → Sixel at paint time, with glyph fallback and diff-skip across frames), **sized text**
(`ScaledText` — Kitty OSC 66 where supported, a bundled FIGlet face elsewhere), and **blending & alpha
compositing** (`PushBlendingMode` with `Multiply`/`Screen`/`Overlay`/… over RGB pairs).

---

# `Cursorial.Drawing` — scenes, brushes, charts

A retained-scene drawing layer over the cell buffer: the unit of work is a `Scene` (a cached raster) composited by a
`SceneCompositor` that only repaints what changed. &nbsp;→ Wiki:
[Drawing](https://github.com/mstrobel/Cursorial/wiki/Drawing-Scenes-Brushes-and-Charts)

```csharp
scene.Draw(ctx =>
{
    ctx.FillRectangle(new Rect(0, 0, 20, 6), new LinearGradientBrush(Colors.Cyan, Colors.Blue));
    ctx.DrawBox(new Rect(0, 0, 20, 6), Pens.Light);            // junctions auto-resolve where strokes meet
    ctx.DrawText(2, 2, "Cached & composited", Colors.White);
});
```

- **Brushes & pens** — `SolidColorBrush`, `LinearGradientBrush`/`RadialGradientBrush` (aspect-corrected),
  `TileBrush`/`Image`; a `Pen` engine that forms box-drawing junctions automatically across separate strokes within
  a figure.
- **Charts** — line / scatter / bar (signed, NaN-gap), category axes, fill areas — in `Cursorial.Drawing.Charts`.
- **Compositor** — `Scene` + `SceneCompositor` maintain the compositing invariant (always composite onto base, so
  retained translucent scenes don't drift) with cached rasters and a pooled `ScenePool`.
- **UI primitives** — an intra-scene clip/translate stack honored by every draw path, opaque/occluding fills,
  dither, titled panels, and drop/inner shadows; brush-aware formatted text and images.

---

# `Cursorial.Animation` — a time-free animation engine

Pure animation primitives with **no ambient clock**: `IAnimation<T>` maps elapsed time → value, so the consumer (or
`Cursorial.UI`) owns the clock and the layer stays deterministic and testable. &nbsp;→ Wiki:
[Animation](https://github.com/mstrobel/Cursorial/wiki/Animation)

```csharp
IAnimation<double> fade = new Animation<double>(from: 0.0, to: 1.0, duration: TimeSpan.FromMilliseconds(250),
                                                Interpolator.For<double>(), Easings.QuadOut);
double v = fade.ValueAt(elapsed);   // sample wherever your clock is
```

Easings (cubic/quad/elastic/bounce/`cubic-bezier(…)`), a process-global `Interpolator` registry (double, int,
`Color`, `Point`/`Size`/`Rect`, `Margins`, `IBrush`, `Pen`, …), and combinators (`Delay`, `Then`, repeat). The UI
layer drives these through a frame-aligned scheduler with storyboards and implicit transitions (below).

---

# `Cursorial.UI` — a terminal UI framework

A WPF/Avalonia-style framework on top of `Cursorial.Drawing`. If you've used either, the shapes will feel familiar:
a dependency-property system, an element tree with measure/arrange layout, retained render zones, routed input,
CSS-like styling, data binding, and a control catalog. &nbsp;→ Wiki:
[UI Overview](https://github.com/mstrobel/Cursorial/wiki/UI-Overview) ·
[Layout & Panels](https://github.com/mstrobel/Cursorial/wiki/UI-Layout-and-Panels) ·
[Controls](https://github.com/mstrobel/Cursorial/wiki/UI-Controls) ·
[Styling & Themes](https://github.com/mstrobel/Cursorial/wiki/UI-Styling-and-Themes) ·
[Data Binding](https://github.com/mstrobel/Cursorial/wiki/UI-Data-Binding) ·
[Input & Focus](https://github.com/mstrobel/Cursorial/wiki/UI-Input-and-Focus) ·
[Windowing](https://github.com/mstrobel/Cursorial/wiki/UI-Windowing) ·
[Animation & Transitions](https://github.com/mstrobel/Cursorial/wiki/UI-Animation-and-Transitions)

```csharp
var app = UIApplication.CreateBuilder().WithFrameRate(60).Build();

return await app.RunAsync(() =>
{
    var stack = new StackPanel { Spacing = 1, Margin = new Margins(2, 1) };
    var button = new Button { Content = "_Click me" };          // _ = Alt access key
    button.Click += (_, _) => button.Content = "Clicked!";

    stack.Children.Add(new TextBlock { Text = "Hello, Cursorial.UI 👋" });
    stack.Children.Add(button);
    return stack;
});
```

- **Property system & layout** — styled / attached / direct dependency properties with a priority-ordered value
  store; `Measure`/`Arrange`; panels: `StackPanel`, `DockPanel`, `Grid`, `Canvas`, `WrapPanel`,
  `VirtualizingStackPanel`.
- **Controls** — `Button`/`ToggleButton`/`CheckBox`/`RadioButton`, `TextBox`/`PasswordBox`, `ScrollViewer`,
  `ListBox`/`ComboBox`/`TreeView`, `TabControl`, `Menu`/`ContextMenu`/`ToolTip`, `Slider`/`ProgressBar`,
  `Expander`/`GridSplitter`/`StatusBar`, `Calendar`/`DatePicker`, and `ItemsControl` with container virtualization.
- **Styling** — `Style`/`Setter` with a CSS-like selector grammar (type / `.class` / `#name` / `:pseudo-class` /
  descendant & child / `:is()`), pseudo-classes driven by interaction state (`:pointerover`, `:focus`, `:checked`),
  `When` data conditions, resources (`{StaticResource}`/`{DynamicResource}`), and theme variants (light/dark, color
  tiers).
- **Data binding** — `{Binding}` with paths, converters, modes, `RelativeSource`, `ElementName`/`x:Reference`,
  `INotifyPropertyChanged`, plus typed zero-allocation **compiled bindings**.
- **Input & focus** — bubbling/tunneling routed events, focus scopes & directional navigation, `KeyGesture`
  commands & `InputBinding`s, access keys, and element-level mouse cursors.
- **Windowing** — `Window` (draggable/resizable/maximizable chrome, modal `ShowDialogAsync`), light-dismiss
  `Popup`s, drop shadows, and a window manager that composites a surface stack — all without OS windows.
- **Animation** — storyboards and implicit `Transition`s over the property system (e.g. fade `Opacity` on
  `:pointerover`), on a frame-aligned scheduler with reduced-motion support.
- **Command surfaces** (`Cursorial.UI.Bars`) — a `Toolbar` with discrete overflow and a `Ribbon` (tabs, groups with
  a density-collapse tier, contextual tabs, Backstage, a Quick Access Toolbar, and a minimizable band), plus KeyTips
  (Alt-overlay accelerator badges) and SuperTips — all bound to one shared `BarCommand` set (define once, surface
  anywhere).

`Cursorial.UI.Testing` provides a headless `UITestHost` (a synthetic terminal on a fake clock) so the whole
framework — layout, input, rendering — is unit-testable without a TTY.

---

# `Cursorial.UI.Xaml` — declarative markup

Define your UI in XAML, loaded at runtime **or** compiled by a Roslyn source generator. &nbsp;→ Wiki:
[XAML](https://github.com/mstrobel/Cursorial/wiki/XAML)

```xml
<StackPanel xmlns="https://cursorial.dev/ui" Spacing="1" Margin="2,1">
  <TextBlock Text="{Binding Title}" />
  <Button Content="_Save" Command="{Binding SaveCommand}" Background="{StaticResource AccentBrush}" />
</StackPanel>
```

```csharp
var root = XamlLoader.Shared.Load<StackPanel>(xaml);
root.DataContext = viewModel;
```

- **Runtime loader** — `{Binding}` / `{StaticResource}` / `{DynamicResource}` / `{TemplateBinding}`,
  `ControlTemplate`s, `ResourceDictionary` with merged & theme dictionaries, styles with the full selector grammar,
  access-key folding, and line+column diagnostics on parse/semantic errors.
- **Source generator** — build-time XAML validation as compiler diagnostics, typed `x:Name` fields +
  `InitializeComponent()` code-behind (WPF-style `x:Class`), generator-emitted **compiled bindings** with
  `x:DataType` path checking, and an AOT-clean metadata provider (no reflection at runtime). The generator runs the
  *same* parser as the loader over Roslyn symbols, and a dual-run gate asserts the two produce byte-identical trees.
- **Theming** — built-in control themes authored in box-drawing idiom, overridable per-app; ship themes as code or
  as embedded XAML.

---

## Project layout

| Project | Contents |
| --- | --- |
| `Cursorial.Core` | Input parsing & delivery, output writers, capability negotiation, terminal session, text utilities. |
| `Cursorial.Rendering` | Cell buffer, frame renderer, rich text, images, blending & alpha compositing. |
| `Cursorial.Drawing` | Scenes & compositor, brushes/gradients, pen/junction engine, charts, UI drawing primitives. |
| `Cursorial.Animation` | Time-free `IAnimation<T>`, easings, interpolator registry. |
| `Cursorial.UI` | The UI framework: property system, layout, controls, styling, binding, input/focus, windowing, animation. |
| `Cursorial.UI.Xaml.Frontend` | The shared **netstandard2.0** XAML parser, node model, and type-system seams. |
| `Cursorial.UI.Xaml` | The net10.0 runtime XAML loader (markup extensions, resources, templates). |
| `Cursorial.UI.Xaml.Generator` | The Roslyn source generator (compiled bindings, code-behind, AOT-clean provider). |
| `Cursorial.UI.Bars` | Command surfaces: `Toolbar` with overflow, `Ribbon` (density collapse, contextual tabs, Backstage, QAT, minimize), KeyTips, SuperTips. |
| `Cursorial.UI.Testing` | Headless `UITestHost` + synthetic terminal for testing UI without a TTY. *Not published — test-only.* |
| `Cursorial.Shared` | Markup attributes shared by the loader and generator. |
| `Cursorial.Demo` | Interactive REPL that drives every layer end-to-end (see below). |
| `Cursorial.Demo.XamlAot` / `.XamlAotStrict` | NativeAOT publish demos — the reflection loader, and the reflection-free build on the generated metadata provider (the AOT-clean exit gate). |
| `Cursorial.Gallery` | A standalone XAML-first MVVM control gallery — a full app, not a demo command. |
| `*.Tests` | xUnit suites per project. |

## Requirements

- .NET SDK **10.0.100** or later (pinned in `global.json` with `rollForward: latestMinor`).
- A terminal. The suite has been exercised against kitty, Ghostty, iTerm2, WezTerm, Rio, Apple Terminal, Alacritty,
  GNOME Terminal, ConEmu/Cmder, and Windows Terminal / Console Host.

## Building and testing

```bash
dotnet build
dotnet test
```

## Trying the demos

```bash
dotnet run --project Cursorial.Demo     # the interactive REPL
dotnet run --project Cursorial.Gallery  # the standalone control gallery
```

The demo opens an interactive prompt. Useful commands:

| Command | What it does |
| --- | --- |
| `negotiate` | Run the negotiator and dump the realized capabilities. |
| `read` / `raw` / `trace` | Decoded input events, raw stdin bytes, or a live side-by-side of both. |
| `render` | Diff-rendered showcase: palette, truecolor gradient, wide glyphs, attributes, alpha, images, sized text. |
| `draw` / `charts` / `brushtext` | `Cursorial.Drawing` scenes, charts, and brush-aware text. |
| `imagescene` / `imageclip` / `sizing` | Image compositing, clipping, and Kitty OSC 66 sized text. |
| `animate` | `Cursorial.Animation` showcase. |
| `ui` | `Cursorial.Drawing` UI primitives — gradient FIGlet, titled panels + shadows, an occluding opaque modal. |
| `uipanels` / `uicontrols` / `gallery` | The `Cursorial.UI` panel tree, control set, and full control gallery on the real frame loop. |
| `uixaml` | A control tree loaded at runtime from an embedded `.xaml` resource. |
| `motion` | The `Cursorial.UI` animation showcase — storyboards, implicit transitions, edge-action pulse. |
| `windows` | The S4 windowing showcase — draggable/resizable windows, modal dialogs, light-dismiss popups. |
| `inspect` | A live XAML / element-tree inspector. |
| `probe` / `accesskeys` / `rasterbench` | Protocol probes, the access-key capability gate, and a raster benchmark. |
| `help` / `quit` | Self-explanatory. |

Each command opens its own raw-mode `TerminalSession` and restores cooked mode before the next prompt.

## License

Apache License 2.0. See [LICENSE](LICENSE) for the full text.
