using System.Reflection;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

// ReSharper disable CheckNamespace

// The rich-text formatter showcase. A composite RichText document (BBcode markup + builder) is laid
// out once into an off-screen buffer sized to the full document; per frame a sliding window of its
// cells is blitted into the visible buffer with a scroll indicator. Event-driven (not animated):
// repaints only when the scroll offset changes (↑/↓ + PgUp/PgDn + Home/End + mouse wheel) or on
// resize. Behavior is a verbatim migration of Program.cs's DemoFormatAsync.
internal sealed class FormatDemo : InteractiveDemo
{
    public override string Name => "format";
    public override IReadOnlyList<string> Aliases => ["richtext"];
    public override string Description =>
        "Showcase the rich-text formatter — wrap modes, alignment, inline styles, content, scrolling.";

    protected override string IntroMessage =>
        "Formatting demo. Opening alt screen — press q or Ctrl+C to exit; ↑/↓ + PgUp/PgDn + Home/End to scroll.";

    // Built once in Initialize and reused across resizes by re-formatting against the new column
    // budget. The off-screen buffer holds the whole document; per-frame we blit a sliding window of
    // its cells into the visible Buffer. Re-built on resize.
    private RichText _doc = null!;
    private TextFormatter _formatter = null!;
    private CellBuffer? _offscreen;
    private int _docRows;
    private int _scrollOffset;
    private int _viewportRows;

    protected override void Initialize()
    {
        _doc = BuildFormattingShowcase(Style);
        _formatter = new TextFormatter { Trim = TextTrimming.WordEllipsis };

        Reformat();
    }

    protected override void OnResize(int columns, int rows)
    {
        Buffer.Resize(columns, rows);
        Reformat();
    }

    protected override bool OnEvent(InputEvent evt)
    {
        switch (evt)
        {
            case KeyEvent { Kind: KeyEventKind.Down } k:
                int before = _scrollOffset;
                if (HandleScrollKey(k, _viewportRows, _docRows, ref _scrollOffset))
                {
                    ClampScroll();
                    return _scrollOffset != before;
                }
                return false;

            case MouseEvent { Kind: MouseEventKind.Wheel } m:
                int prev = _scrollOffset;
                // Positive WheelDeltaY conventionally means scroll up.
                _scrollOffset -= m.WheelDeltaY / 120;
                ClampScroll();
                return _scrollOffset != prev;

            default:
                return false;
        }
    }

    protected override void RenderFrame(long frame) =>
        PaintScrolledShowcase(Buffer, _offscreen!, _scrollOffset, _viewportRows, _docRows, Style);

    private void Reformat()
    {
        var margins = new Margins(2, 1);
        int viewWidth = Math.Max(20, Math.Min(100, Buffer.Columns) - margins.Horizontal);

        _viewportRows = Math.Max(4, Buffer.Rows - margins.Vertical);

        // Format with no row cap; render into an off-screen buffer sized to the full document.
        var ft = _formatter.Format(_doc, viewWidth, maxRows: null, Capabilities.Output);

        _docRows = Math.Max(1, ft.Size.Rows);
        _offscreen = new CellBuffer(viewWidth, _docRows, Capabilities);
        _offscreen.Clear(Style);

        ft.Paint(_offscreen,
                 _offscreen.Bounds.LayoutContent(Anchor.Top, ft.Size),
                 Capabilities.Output);

        ClampScroll();
    }

    private void ClampScroll()
    {
        int maxScroll = Math.Max(0, _docRows - _viewportRows);
        if (_scrollOffset < 0) _scrollOffset = 0;
        if (_scrollOffset > maxScroll) _scrollOffset = maxScroll;
    }

    private static bool HandleScrollKey(KeyEvent k, int viewportRows, int docRows, ref int scrollOffset)
    {
        switch (k.Key)
        {
            case Key.UpArrow:   scrollOffset -= 1; return true;
            case Key.DownArrow: scrollOffset += 1; return true;
            case Key.PageUp:    scrollOffset -= viewportRows - 1; return true;
            case Key.PageDown:  scrollOffset += viewportRows - 1; return true;
            case Key.Home:      scrollOffset = 0; return true;
            case Key.End:       scrollOffset = docRows; return true;
            default:            return false;
        }
    }

    // Blit the <paramref name="offscreen"/> document's rows [<paramref name="scrollOffset"/>,
    // scrollOffset + viewportRows) into the visible <paramref name="screen"/>, plus a scroll
    // indicator. Fragments whose anchor falls inside the visible window are re-attached at
    // translated coordinates so inline graphics (Kitty icons, Sixel) scroll with the cells.
    private static void PaintScrolledShowcase(
        CellBufferView screen, CellBufferView offscreen, int scrollOffset, int viewportRows, int docRows, in CellStyle style)
    {
        screen.CursorVisible = false;
        screen.Clear(style);

        int innerCols = Math.Min(offscreen.Columns, screen.Columns);
        int innerRows = Math.Min(viewportRows, screen.Rows);

        var rect = screen.Bounds.LayoutContent(Anchor.Top, offscreen.Dimensions);
        var screenView = screen.View(rect);

        // Cells.
        for (int dy = 0; dy < innerRows; dy++)
        {
            int srcRow = scrollOffset + dy;
            if (srcRow >= offscreen.Rows) break;
            for (int dx = 0; dx < innerCols; dx++)
            {
                var cell = offscreen[dx, srcRow];
                // Reuse the raw indexer rather than Set(); we already have the destination grapheme +
                // style and don't want grapheme-width recomputation to second-guess the off-screen.
                screenView[dx, dy] = cell;
            }
        }

        // Fragments anchored within the visible window.
        foreach (var (anchor, entry) in offscreen.Fragments)
        {
            int relRow = anchor.Row - scrollOffset;
            if (relRow < 0 || relRow >= innerRows) continue;
            if (anchor.Column < 0 || anchor.Column >= innerCols) continue;
            screen.AddFragment(rect.Column + anchor.Column, rect.Row + relRow, entry.Fragment, entry.AnchorStyle);
        }

        // Scroll indicator in the top-right gutter.
        if (docRows > viewportRows)
        {
            int percent = (int) (100.0 * scrollOffset / Math.Max(1, docRows - viewportRows));

            string indicator = $" ▲▼ {percent,3}% ";

            int x = Math.Max(0, screen.Columns - 1 - indicator.Length);
            int y = Math.Min(rect.RowEnd, screen.Rows - 1 - (screen.Rows - viewportRows));

            if (x > rect.ColumnEnd + 2)
                x = rect.ColumnEnd + 2;

            var indicatorStyle = CellStyle.Default
                                      .WithForeground(style.Background)
                                      .WithBackground(style.Foreground.WithAlpha(191));

            for (int i = 0; i < indicator.Length && x + i < screen.Columns; i++)
                screen.Set(x + i, y, indicator[i] == ' ' ? "" : indicator[i].ToString(), indicatorStyle);
        }
    }

    private static RichText BuildFormattingShowcase(in CellStyle defaultStyle = default)
    {
        // A composite document mixing BBcode markup (for the prose-heavy sections) and the builder
        // (for FIGlet + HR blocks that don't have terse markup equivalents). Builds once and is
        // reused across resizes by re-formatting against the new column budget.
        var builder = new RichTextBuilder(defaultStyle);

        // Inline content registry: makes embedded icons / badges reachable from markup via
        // [content=name/]. The PNG paths use embedded resources; Icon falls back to its glyph
        // when the negotiated terminal can't render images, so the markup is portable.
        var assembly = Assembly.GetExecutingAssembly();

        var contentRegistry = new Dictionary<string, IContent>(StringComparer.OrdinalIgnoreCase)
                              {
                                  ["settings"] = Icon.FromEmbedded(assembly, "Icons/settings.png", "⚙️ ",
                                                                   renderSize: new Size(2, 0)),
                                  ["download"] = Icon.FromEmbedded(assembly, "Icons/download.png", "⬇️ ",
                                                                   renderSize: new Size(2, 0)),
                                  ["calendar"] = Icon.FromEmbedded(assembly, "Icons/calendar.png", "📆 ",
                                                                   renderSize: new Size(2, 0)),
                                  ["power"] = Icon.FromEmbedded(assembly, "Icons/power.png", "⚡️",
                                                                renderSize: new Size(2, 0))
                              };

        var markupOptions = new TextMarkupOptions { Content = contentRegistry, DefaultStyle = BrushedStyle.FromStated(defaultStyle) };

        // Title.
        builder.Figlet("Rich Text",
                       FigletFonts.Standard,
                       defaultStyle.WithForeground(Color.FromHex("#f92572"))
                                   .WithAttributes(TextAttributes.Bold),
                       alignment: TextAlignment.Center);

        builder.HorizontalRule(HorizontalRule.Double);

        // BBcode-driven intro.
        TextMarkup.Parse(
            "Cursorial's [b]TextFormatter[/b] lays out [fg=brightcyan]styled rich text[/fg] " +
            "into cell-grid lines. Hand it a [link=https://en.wikipedia.org/wiki/BBCode]BBcode[/link]-flavored " +
            "markup string or build it programmatically; the result is the same: an [i]immutable[/i] " +
            "[fg=brightgreen]FormattedText[/fg] you can paint once or many times.",
            builder,
            markupOptions);
        builder.EndParagraph();

        builder.HorizontalRule(margin: new Margins(0, 1));

        TextMarkup.Parse(
            "[b][fg=brightyellow]Wrap modes.[/fg][/b]  [u]WordWrap[/u] breaks at whitespace and word " +
            "boundaries; long words split mid-character. [u]WordWrapOverflow[/u] lets them overflow " +
            "past the right edge. [u]CharacterWrap[/u] is CJK-friendly and breaks at any grapheme. " +
            "[u]NoWrap[/u] keeps everything on a single line, relying on the active trim mode to clip.",
            builder,
            markupOptions);
        builder.EndParagraph();

        builder.HorizontalRule(margin: new Margins(0, 1));

        // Justify demonstration — paragraph with align=justify.
        TextMarkup.Parse(
            "[p align=justify][b][fg=brightyellow]Justify alignment.[/fg][/b]  Slack cells are " +
            "distributed across inter-word gaps so every line except the last fills the column budget " +
            "exactly. The last line of a paragraph stays in its natural alignment — it would look " +
            "stretched if it were justified to a half-empty row. Cell-aware width accounting means " +
            "this also works for mixed-script text without surprises.[/p]",
            builder,
            markupOptions);

        builder.HorizontalRule(margin: new Margins(0, 1));

        TextMarkup.Parse(
            "[b][fg=brightyellow]Soft hyphens.[/fg][/b]  Insert U+00AD where a long word can split. " +
            "The hyphen is invisible until the formatter wraps at it, in which case a literal " +
            "[fg=brightred]-[/fg] is appended to the previous line. Example: " +
            "interna­tionali­zation, hyper­exten­sibility, " +
            "contra­distinc­tion. Narrow the terminal to watch them activate.",
            builder,
            markupOptions);
        builder.EndParagraph();

        builder.HorizontalRule(margin: new Margins(0, 1));

        TextMarkup.Parse(
            "[b][fg=brightyellow]Grapheme maps.[/fg][/b]  Per-run substitution preserves cell-stream " +
            "semantics. [font=fullwidth]Fullwidth[/font] · [font=doublestruck]DoubleStruck[/font] · " +
            "[font=smallcaps]small caps[/font] · [font=superscript]012345[/font] · [font=subscript]012345[/font].",
            builder,
            markupOptions);
        builder.EndParagraph();

        builder.HorizontalRule(margin: new Margins(0, 1));

        TextMarkup.Parse(
            "[b][fg=brightyellow]Inline styles.[/fg][/b]  " +
            "[b]bold[/b] · [i]italic[/i] · [u]underline[/u] · [s]strike[/s] · " +
            "[fg=red]red[/fg] · [fg=#ffa500]hex orange[/fg] · [fg=42]palette 42[/fg] · " +
            "[bg=blue][fg=brightwhite] on blue [/fg][/bg] · " +
            "[link=https://github.com/anthropics/claude-code]Ctrl+click hyperlink[/link]",
            builder,
            markupOptions);
        builder.EndParagraph();

        builder.HorizontalRule(margin: new Margins(0, 1));

        TextMarkup.Parse(
            "[b][fg=brightyellow]Inline content.[/fg][/b]  Register an [i]IContent[/i] under a name " +
            "and reference it from markup with [fg=brightcyan]\\[content=name/\\][/fg]. The formatter " +
            "places it atomically in the paragraph flow at its measured width: " +
            "[content=settings/] settings · " +
            "[content=download/] download · " +
            "[content=calendar/] calendar · " +
            "[content=power/] power.",
            builder,
            markupOptions);
        builder.EndParagraph();

        builder.HorizontalRule(margin: new Margins(0, 1));

        TextMarkup.Parse(
            "Press [b][fg=brightcyan]q[/fg][/b] or [b][fg=brightcyan]Ctrl+C[/fg][/b] to exit.",
            builder,
            markupOptions);

        return builder.Build();
    }
}
