using System.Reflection;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;
using Cursorial.Text;

// ReSharper disable CheckNamespace

// The interactive reference demo. Cursorial.Rendering showcase — colors, wide glyphs, attributes,
// an alpha-blended overlay, sized title, PNG icons, and a clock ticking in the corner. Animated at
// ~3 fps so the clock visibly advances and the diff renderer's per-cell deltas are observable.
internal sealed class RenderDemo : InteractiveDemo
{
    public override string Name => "render";
    public override IReadOnlyList<string> Aliases => ["showcase"];
    public override string Description =>
        "Cursorial.Rendering showcase — colors, wide glyphs, attributes, alpha overlay, clock.";

    protected override string IntroMessage =>
        "Render demo. Opening alt screen — press q or Ctrl+C to exit.";

    protected override TimeSpan FrameInterval => TimeSpan.FromMilliseconds(333); // ~3 fps
    protected override bool Animated => true;

    // Stable fragment instances, built ONCE in Initialize and reused across every frame. Hoisting
    // these out of the per-frame paint loop lets the FrameRenderer's fragment diff (Phase 6.8) skip
    // re-emission when nothing meaningful has changed — the reference-equality contract means the
    // same instance across frames is enough to take the skip path. Recreating them per frame churns
    // Kitty image IDs and exhausts the terminal's image store on a long session.
    private Icon _icon = null!;
    private Icon[] _icons = null!;
    private ScaledText _title = null!;
    private RichText _githubLink = null!;

    private static readonly (string Resource, string Fallback)[] IconSpecs =
    [
        ("Icons/settings.png", "⚙️"),
        ("Icons/download.png", "⬇️"),
        ("Icons/calendar.png", "📆"),
        ("Icons/power.png", "⚡️"),
    ];

    protected override void Initialize()
    {
        var assembly = Assembly.GetExecutingAssembly();

        _icon = Icon.FromEmbedded(
            assembly,
            "Icons/cursorial_icon.png",
            "\\[[b][fg=cyan]C[/fg] [fg=white][u]>[/u][/fg][/b]\\]",
            renderSize: new Size(6, 0));

        _icons = BuildIcons(assembly);

        _title = new ScaledText(
            "Cursorial Rendering Demo",
            new TextSizing(Scale: 2),
            fallbackFont: DecoratedFont.QuarterBlockUnderline);

        _githubLink =
            TextMarkup.Parse("[i]Cursorial[/i] is hosted at [link=https://github.com/mstrobel/cursorial][fg=blue]GitHub[/fg][/link].");
    }

    private static Icon[] BuildIcons(Assembly assembly)
    {
        var iconStyle = new CellStyle(Color.Default, Color.FromRgb(40, 52, 87), default, default, default);
        var result = new Icon[IconSpecs.Length];
        for (int i = 0; i < IconSpecs.Length; i++)
            result[i] = Icon.FromEmbedded(
                assembly,
                IconSpecs[i].Resource,
                IconSpecs[i].Fallback,
                fallbackStyle: iconStyle,
                renderSize: new Size(2, Math.Max(2 / 2, 1)));
        return result;
    }

    protected override void RenderFrame(long frame) => PaintRenderShowcase(Buffer, Style, Capabilities.Output);

    // Paint the render-demo content into <paramref name="buf"/>. Uses every piece of the rendering
    // surface we've built: SGR styles via <c>buffer.Set</c>, wide glyphs that auto-pair into
    // wide-left+continuation, an alpha-blended overlay with a pushed blending mode, and a clock
    // that changes once per second — the clock is how you tell the diff renderer is doing per-cell
    // deltas instead of repainting the whole screen each frame.
    private void PaintRenderShowcase(CellBufferView buf, in CellStyle style, OutputCapabilities outputCaps)
    {
        // The sized title flows through a ScaledText content (Phase 3) — on terminals that honor
        // OSC 66 it attaches a SizedTextFragment; on the rest it falls back to a bundled FIGlet
        // face. The cell buffer + FrameRenderer take care of the rest (capability gating,
        // DECSC/DECRC bracketing, diff rendering).
        buf.CursorVisible = false;
        // Immediate-mode clear, intentionally disabled to exercise retained-mode rendering: the
        // showcase re-Sets every cell each frame, so the diff renderer needs no blank-first pass and
        // the FrameRenderer's delta is identical either way for all-cell content. Re-enable if
        // investigating retained-mode artifacts. See FragmentContent.IsFragmentNeeded (fragment
        // caching) and FrameRendererOptions.RestrictToDirtyRegions (dirty-region opt-in).
        // buf.Clear(style);

        int cols = buf.Columns;
        int rows = buf.Rows;

        // ---- Title bar ----
        var titleStyle = new CellStyle(
            Foreground: Colors.Black,
            Background: Colors.LightBlue,
            Attributes: TextAttributes.Bold,
            UnderlineStyle: default,
            UnderlineColor: default);

        DemoSupport.PaintLine(buf, 0, 0, "  Cursorial render demo — press q or Ctrl+C to exit  ".PadRight(cols), titleStyle);

        // ---- 16-color ANSI palette ----
        int row = 5;
        if (row < rows) DemoSupport.PaintLine(buf, 1, row, "ANSI 16-color palette:", style);
        if (row + 1 < rows)
        {
            for (int i = 0; i < 16 && (1 + i * 3 + 2) < cols; i++)
            {
                var bg = Color.FromPalette((byte)i);
                var swatch = new CellStyle(
                    Foreground: Color.Default,
                    Background: bg,
                    Attributes: default,
                    UnderlineStyle: default,
                    UnderlineColor: default);
                int x = 1 + i * 3;
                buf.Set(x,     row + 1, " ", swatch);
                buf.Set(x + 1, row + 1, " ", swatch);
            }
        }

        // ---- Truecolor gradient ----
        row += 2;
        if (row < rows) DemoSupport.PaintLine(buf, 1, row, "24-bit truecolor gradient:", style);
        if (row + 1 < rows)
        {
            int width = Math.Min(cols - 2, 60);
            for (int i = 0; i < width; i++)
            {
                // Hue-like sweep across red/green/blue.
                byte r = (byte)(255 - (i * 255 / width));
                byte g = (byte)(i * 255 / width);
                byte b = (byte)(128 + (i * 64 / width) % 128);
                var swatch = new CellStyle(
                    Foreground: Color.Default,
                    Background: Color.FromRgb(r, g, b),
                    Attributes: default,
                    UnderlineStyle: default,
                    UnderlineColor: default);
                buf.Set(1 + i, row + 1, " ", swatch);
            }
        }

        // ---- Wide glyphs ----
        row += 3;
        if (row < rows) DemoSupport.PaintLine(buf, 1, row, "Wide glyphs (emoji + CJK, each occupies 2 cells):", style);

        if (row + 1 < rows)
        {
            int x = 1;
            foreach (var g in new[] { "🚀", "🌍", "🎨", "🐈", "中", "日", "本", "文" })
            {
                if (x + 2 >= cols) break;
                buf.Set(x, row + 1, g, style);
                x += 3; // 2 cells for the glyph + 1 space
            }
        }

        // ---- Attribute showcase ----
        row += 2;
        if (row < rows) DemoSupport.PaintLine(buf, 1, row, "Text attributes:", style);
        if (row + 1 < rows)
        {
            int x = 1;
            x += DemoSupport.PaintWord(buf, x, row + 1,
                "Bold ", style.WithAttributes(TextAttributes.Bold));
            x += DemoSupport.PaintWord(buf, x, row + 1,
                "Italic ", style.WithAttributes(TextAttributes.Italic));
            x += DemoSupport.PaintWord(buf, x, row + 1,
                "Underline ", style.WithAttributes(TextAttributes.Underline));
            x += DemoSupport.PaintWord(buf, x, row + 1,
                "Curly ", style
                          .WithAttributes(TextAttributes.Underline)
                          .WithUnderlineStyle(UnderlineStyle.Curly)
                          .WithUnderlineColor(Color.FromRgb(255, 80, 80)));
            x += DemoSupport.PaintWord(buf, x, row + 1,
                "Strike ", style.WithAttributes(TextAttributes.Strikethrough));
            DemoSupport.PaintWord(buf, x, row + 1,
                "Inverse", style.WithAttributes(TextAttributes.Inverse));
        }

        // ---- Alpha-blended overlay ----
        row += 2;
        if (row < rows) DemoSupport.PaintLine(buf, 1, row, "Alpha-blended overlay (Multiply mode, α=128):", style);
        if (row + 1 < rows && row + 4 < rows)
        {
            // Backdrop: solid color stripes.
            var stripes = new[]
                          {
                              Color.FromRgb(220, 60, 60),
                              Color.FromRgb(60, 220, 60),
                              Color.FromRgb(60, 60, 220),
                              Color.FromRgb(220, 220, 60)
                          };

            int barWidth = Math.Min(cols - 2, 60);

            for (int dy = 0; dy < 3; dy++)
            {
                if (row + 1 + dy >= rows) break;

                for (int x = 0; x < barWidth; x++)
                {
                    var bg = stripes[(x * stripes.Length) / barWidth];

                    buf.Set(1 + x, row + 1 + dy,
                            " ", new CellStyle(Color.Default, bg, default, default, default));
                }
            }

            // Translucent overlay in Multiply mode. The mid-gray + Multiply darkens each stripe
            // toward its own color * gray, and the α=128 means we mix 50/50 with the original.
            buf.PushBlendingMode(BlendingModes.Multiply);
            try
            {
                int overlayStart = Math.Min(barWidth - 20, 10);
                int overlayWidth = Math.Min(20, barWidth - overlayStart - 1);
                for (int dy = 0; dy < 3; dy++)
                {
                    if (row + 1 + dy >= rows) break;
                    for (int dx = 0; dx < overlayWidth; dx++)
                    {
                        buf.Set(1 + overlayStart + dx, row + 1 + dy,
                                " ", new CellStyle(
                                    Color.Default,
                                    Color.FromRgba(128, 128, 128, 128),
                                    default, default, default));
                    }
                }
            }
            finally
            {
                buf.PopBlendingMode();
            }
        }

        row += 5;

        var tf = new TextFormatter { Trim = TextTrimming.CharacterEllipsis };
        var ft = tf.Format(_githubLink, cols, capabilities: outputCaps);

        ft.Paint(buf, buf.Bounds.Translate(1, row).WithSize(ft.Size), outputCaps);

        // ---- Sized Text below Title Bar ----
        // ScaledText is the capability-aware entry point: when the terminal honors OSC 66, it
        // attaches a SizedTextFragment (Kitty / Ghostty / etc.); otherwise it falls back to a
        // bundled FIGlet face. The styled title here uses italic + curly underline, so the OSC 66
        // path picks up the SGR backdrop visibly when supported.
        var sizedTitleStyle = style
            .WithForeground(Color.FromRgb(192, 202, 245))
            .WithBackground(Color.Transparent)
            .WithAttributes(TextAttributes.Italic | TextAttributes.Underline)
            .WithUnderlineStyle(UnderlineStyle.Curly)
            .WithUnderlineColor(Color.FromPalette(5));

        var desiredSize = _title.Measure(buf.Size, outputCaps);

        // Reuse a single ScaledText instance across frames — Phase 6.8's fragment diff uses
        // reference equality on the underlying IBufferFragment, so a stable instance lets the
        // renderer skip re-emission when the title and sizing haven't changed.
        _title.Paint(buf, new Rect(1, 2, desiredSize), style: sizedTitleStyle, capabilities: outputCaps);

        var iconMargin = new Margins(2, 1);

        desiredSize = _icon.Measure(buf.Size, outputCaps);

        _icon.Paint(buf,
                    buf.Bounds.LayoutContent(Anchor.BottomRight, desiredSize, iconMargin),
                    in style,
                    outputCaps);

        const int iconY = 5;
        const int iconCount = 4;
        const int iconColumns = 2;

        var iconStyle = style.WithBackground(Color.FromRgb(40, 52, 87));

        // Reuse the four icon instances (see _icons) so their Kitty image fragments stay stable
        // across frames and the renderer diff-skips them — recreating them per frame would churn
        // image IDs and exhaust the terminal's image store on a long session.
        var icons = _icons;

        for (int i = 0; i < iconCount; i++)
        {
            var d = icons.Length - i;
            var x = buf.Columns - ((d + 1) * (iconColumns + 1) + 1) - 8;
            var icon = icons[i];
            buf.Set(x, iconY, " ", iconStyle);
            buf.Set(x + 1, iconY, " ", iconStyle);
            icon.Measure(icon.RenderSize, outputCaps);
            icon.Paint(buf, column: x, row: iconY, style: iconStyle, capabilities: outputCaps);
            buf.Set(x, iconY + 2, icon.FallbackGlyph, iconStyle);

            if (i == 0)
            {
                var labelStyle = (style with { Foreground = style.Foreground.WithAlpha(0xC0) }).BlendOver(style);

                DemoSupport.PaintWord(buf, x, iconY - 3, "PNG Icons w/", labelStyle);
                DemoSupport.PaintWord(buf, x, iconY - 2, "Emoji Fallback", labelStyle);
            }
        }

        // ---- Clock in top-right corner ----
        // The right margin pad (` ` after the clock plus a single empty cell at the rightmost
        // column) is a deliberate workaround for ConEmu / Cmder, whose DECAWM-off behavior isn't
        // honored across frames — writing the last clock digit at the rightmost column lets a
        // residual deferred-wrap state trip later ticks and the digit wraps to the next line.
        // Backing off one cell keeps the digit on-screen on every terminal we test.
        string clock = DateTime.Now.ToString("HH:mm:ss");
        if (clock.Length + 2 < cols)
        {
            var clockStyle = style
                .WithForeground(Colors.TrueWhite)
                .WithBackground(Colors.Extended1)
                .WithAttributes(TextAttributes.Bold);
            int x = cols - clock.Length - 2;
            DemoSupport.PaintLine(buf, x, 0, " " + clock + " ", clockStyle);
        }
    }
}
