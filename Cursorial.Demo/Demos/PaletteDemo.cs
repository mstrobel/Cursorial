using Cursorial.Input.Events;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

// ReSharper disable CheckNamespace

// OSC 4 palette showcase. Queries the terminal's full extended 256-color palette via round-trip
// OSC 4 color queries, then renders the resolved entries as a labeled grid of swatches (hex code +
// index). Event-driven (~50ms): the screen stays blank while the query is in flight (matching the
// original, which blocked before painting anything), repaints once the palette resolves, and
// repaints again only on resize. Behavior is a verbatim migration of Program.cs's DemoPaletteAsync.
//
// The palette query is resolved by feeding every observed input event back into the
// TerminalPalette (OnEvent → Palette.OnInputEvent): the OSC 4 responses arrive as
// DeviceResponseEvents and complete the pending per-index query tasks. The InteractiveDemo harness
// owns the single input pump; this demo only tees its events to the palette.
internal sealed class PaletteDemo : InteractiveDemo
{
    public override string Name => "palette";
    public override string Description =>
        "Query and display the terminal's extended 256-color palette as a labeled swatch grid.";

    protected override string IntroMessage =>
        "Palette demo. Opening alt screen — press q or Ctrl+C to exit.";

    // The in-flight palette query (started in Initialize, resolved by the teed OnInputEvent calls)
    // and its harvested result. _queryHarvested guards the one-time transfer of the completed
    // query's outcome into the _colors / _status fields painted by PaintPaletteShowcase.
    private Task<IColorPalette?>? _queryTask;
    private bool _queryHarvested;
    private IColorPalette? _colors;
    private string? _status;

    protected override void Initialize()
    {
        if (!Palette.IsSupported)
        {
            _status = "Terminal doesn't advertise OSC 4 palette-query support.";
            _queryHarvested = true;
            return;
        }

        // Generous timeout — 256 round-trips at a few ms each on a fast terminal, much slower over SSH.
        // The query awaits OSC 4 responses; those flow through the harness pump and are routed to the
        // palette via OnEvent → Palette.OnInputEvent, which completes the per-index query tasks.
        _queryTask = Palette.QueryExtendedPaletteAsync(TimeSpan.FromSeconds(3));

        // The aggregate query completes on an async continuation AFTER the OnEvent that fed the final
        // response — so OnEvent can't observe IsCompleted in time, and no further input arrives to
        // trigger a repaint. Request one when the task actually completes (or times out); the next
        // render-loop tick harvests the result and paints the grid. Runs on the thread pool — Invalidate
        // is thread-safe.
        _queryTask.ContinueWith(_ => Invalidate(), TaskScheduler.Default);
    }

    // Tee every observed event to the palette so OSC 4 color replies resolve the pending query. The
    // repaint on completion is driven by the Invalidate continuation wired in Initialize (the aggregate
    // task completes on an async hop AFTER this OnEvent, so observing IsCompleted here is unreliable).
    // The IsCompleted check below is a harmless safety net for a late event arriving after completion.
    protected override bool OnEvent(InputEvent evt)
    {
        Palette.OnInputEvent(evt);
        return _queryTask is { IsCompleted: true } && !_queryHarvested;
    }

    protected override void RenderFrame(long frame)
    {
        // While the query is in flight, paint nothing — the harness has already entered the alt
        // screen and reset SGR, so the screen reads blank, exactly as the original did while it
        // blocked on the query before its first paint.
        if (!_queryHarvested)
        {
            if (_queryTask is { IsCompleted: true } query)
            {
                _colors = query.GetAwaiter().GetResult();
                if (_colors is null || _colors.Count == 0)
                {
                    _status = """
                              [p align=center]No palette response within the timeout.
                              Press [b][fg=brightcyan]q[/fg][/b] or [b][fg=brightcyan]Esc[/fg][/b] to exit.[/p]
                              """;
                }
                _queryHarvested = true;
            }
            else
            {
                Buffer.CursorVisible = false;
                Buffer.Clear();
                return;
            }
        }

        PaintPaletteShowcase(Buffer, _colors, Style, _status);
    }

    private static void PaintPaletteShowcase(CellBufferView buffer, IColorPalette? palette, in CellStyle style, string? statusMessage)
    {
        buffer.CursorVisible = false;
        buffer.Clear();

        if (palette is null || palette.Count == 0)
        {
            var msg = statusMessage ?? "[p align=center][fg=#dcdcff]No palette data.[/fg][/p]";
            var rtb = new RichTextBuilder(PartialStyle.From(style)).Paragraph(alignment: TextAlignment.Center);
            TextMarkup.Parse(msg, rtb);
            var msgFormatted = new TextFormatter().Format(rtb.Build(), buffer.Columns, buffer.Rows);

            msgFormatted.Paint(buffer, buffer.Bounds, OutputCapabilities.None);

            return;
        }

        // Header.
        string header = $"OSC 4 palette — {palette.Count} entries (press q to exit)";
        var headerStyle = style.WithForeground(Color.FromRgb(20, 20, 30))
                               .WithBackground(Color.FromRgb(180, 220, 255))
                               .WithAttributes(TextAttributes.Bold);
        DemoSupport.PaintTextRow(buffer, 0, 0, header.PadRight(buffer.Columns), headerStyle);

        // Grid layout. 16 columns; each cell wide enough to display "#RRGGBB" (7 chars) with one
        // cell of horizontal breathing room. Adapts to narrow terminals by shrinking the cell width.
        const int desiredGridCols = 16;
        const int desiredCellWidth = 7 + /* side margins */ 2;

        int cellWidth = Math.Max(desiredCellWidth, (buffer.Columns - 2) / desiredGridCols);
        int cellHeight = 2;

        int colsAvailable = Math.Min(16, buffer.Columns / cellWidth);

        int gridLeft = Math.Max(0, (buffer.Columns - cellWidth * colsAvailable) / 2);
        int gridTop = 2;

        int rowsAvailable = Math.Max(0, buffer.Rows - gridTop - 1);
        int gridRows = (palette.Count + colsAvailable - 1) / colsAvailable;

        if (gridRows * cellHeight > rowsAvailable)
        {
            cellHeight = 1;
            var maxGridRows = Math.Max(1, rowsAvailable);
            gridRows = Math.Min((palette.Count + colsAvailable - 1) / colsAvailable, maxGridRows);
        }

        int idx = 0;

        for (int gy = 0; gy < gridRows; gy++)
        {
            for (int gx = 0; gx < colsAvailable; gx++)
            {
                if (idx >= palette.Count) break;

                var color = palette[idx];
                // Only paint hex when we have a real RGB color (Color.Default sentinel means the
                // entry didn't respond — show as a dimly-marked tile so missing entries are obvious).
                bool hasColor = color.Kind == ColorKind.Rgb;
                var bg = hasColor ? color : Color.FromRgb(30, 30, 30);
                var fg = hasColor ? PickReadableForeground(color) : Color.FromRgb(120, 120, 120);
                var cellStyle = style.WithForeground(fg).WithBackground(bg);

                int cellX = gridLeft + gx * cellWidth;
                int cellY = gridTop + gy * cellHeight;

                // Fill the cell with the color.
                for (int dy = 0; dy < cellHeight && cellY + dy < buffer.Rows; dy++)
                {
                    for (int dx = 0; dx < cellWidth && cellX + dx < buffer.Columns; dx++)
                        buffer.Set(cellX + dx, cellY + dy, " ", cellStyle);
                }

                // Overlay: hex code on the first row of the cell, index on the second.
                if (cellY < buffer.Rows)
                {
                    string hex = hasColor ? $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}" : "—";
                    int hx = cellX + Math.Max(0, (cellWidth - hex.Length) / 2);
                    for (int i = 0; i < hex.Length && hx + i < buffer.Columns; i++)
                        buffer.Set(hx + i, cellY, hex[i].ToString(), cellStyle);
                }

                if (cellHeight >= 2 && cellY + 1 < buffer.Rows)
                {
                    string label = idx.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    int lx = cellX + Math.Max(0, (cellWidth - label.Length) / 2);
                    for (int i = 0; i < label.Length && lx + i < buffer.Columns; i++)
                        buffer.Set(lx + i, cellY + 1, label[i].ToString(), cellStyle);
                }

                idx++;
            }
        }
    }

    private static Color PickReadableForeground(in Color bg)
    {
        // Relative luminance (Rec. 601) is plenty for picking light-vs.-dark text on a solid block.
        int luminance = (bg.Red * 299 + bg.Green * 587 + bg.Blue * 114) / 1000;
        return luminance > 140
            ? Color.FromRgb(0, 0, 0)
            : Color.FromRgb(255, 255, 255);
    }
}
