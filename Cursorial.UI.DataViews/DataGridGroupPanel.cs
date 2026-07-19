using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Input;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The direct-draw group panel band (design doc §3.1; the mockup's <c>grouppanel</c>): one cell row
/// of grouping-level chips — each chip carries the level's sort glyph (<c>▲/▼</c> per its
/// <see cref="GroupDescription.Direction"/>), the column caption, and an <c>✕</c> remove zone,
/// levels separated by a muted <c>▸</c>. Empty shows the ghosted drag prompt (drawn only when it
/// fits). Its own render boundary (ClipToBounds — a 1-row band, §3.1). Chips are live: a click on
/// the <c>✕</c> ungroups the level; a click elsewhere toggles its direction IN PLACE
/// (<c>GroupDescriptions[i]</c> replace — the observable collection is the one source of truth, so
/// the gesture edit reshapes and re-inks through the ordinary pipeline). The band collapses to zero
/// rows while <see cref="DataGrid.ShowGroupPanel"/> is off (the Visibility lane, kept in-measure so
/// the template needs no binding).
/// </summary>
public sealed class DataGridGroupPanel : UIElement
{
    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> BackgroundProperty =
        UIProperty.Register<DataGridGroupPanel, Cursorial.Drawing.Media.IBrush?>(nameof(Background));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> ChipBackgroundProperty =
        UIProperty.Register<DataGridGroupPanel, Cursorial.Drawing.Media.IBrush?>(nameof(ChipBackground));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> TextBrushProperty =
        UIProperty.Register<DataGridGroupPanel, Cursorial.Drawing.Media.IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> GlyphBrushProperty =
        UIProperty.Register<DataGridGroupPanel, Cursorial.Drawing.Media.IBrush?>(nameof(GlyphBrush));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> PromptBrushProperty =
        UIProperty.Register<DataGridGroupPanel, Cursorial.Drawing.Media.IBrush?>(nameof(PromptBrush));

    static DataGridGroupPanel()
    {
        AffectsRender<DataGridGroupPanel>(
            BackgroundProperty, ChipBackgroundProperty, TextBrushProperty, GlyphBrushProperty, PromptBrushProperty);
    }

    public Cursorial.Drawing.Media.IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? ChipBackground { get => GetValue(ChipBackgroundProperty); set => SetValue(ChipBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? GlyphBrush { get => GetValue(GlyphBrushProperty); set => SetValue(GlyphBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? PromptBrush { get => GetValue(PromptBrushProperty); set => SetValue(PromptBrushProperty, value); }

    private const string EmptyPrompt = "— drag a column header here to add a grouping level —";

    private DataGrid? _owner;

    /// <summary>The owning grid (stamped when the template applies).</summary>
    internal DataGrid? Owner
    {
        get => _owner;
        set
        {
            if (ReferenceEquals(_owner, value))
                return;
            if (_owner is not null)
                _owner.SnapshotChanged -= OnSnapshotChanged;
            _owner = value;
            if (_owner is not null)
                _owner.SnapshotChanged += OnSnapshotChanged;
            ClipToBounds = true; // own boundary — band-local re-ink (§3.1; safe: a 1-row band)
            InvalidateMeasure();
        }
    }

    // Every publish re-inks the band: chip glyphs render live GroupDescriptions state, and every
    // grouping edit funnels through a reshape (the observable-collections truth).
    private void OnSnapshotChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
        => _owner is { ShowGroupPanel: true }
            ? new Size(availableSize.Columns, 1)
            : new Size(availableSize.Columns, 0); // collapsed — the band spends no row

    /// <summary>
    /// One chip's geometry: <c>[pad][▲][sp]Header[sp][✕][pad]</c> — the ✕ zone is the trailing 2
    /// cells. Deterministic from the descriptions alone, so hit testing never depends on a render
    /// having run (the header presenter's EntryAt posture).
    /// </summary>
    private readonly record struct Chip(int Index, DataGridColumn Column, int X, int Width)
    {
        public int RemoveZoneStart => X + Width - 2;
    }

    private IEnumerable<Chip> Chips()
    {
        var owner = _owner;
        if (owner is null)
            yield break;

        int x = 1;
        for (int i = 0; i < owner.GroupDescriptions.Count; i++)
        {
            if (owner.GroupDescriptions[i].ColumnKey is not DataGridColumn column)
                continue;
            int width = GraphemeWidth.StringWidth(column.EffectiveHeader) + 6; // pad+glyph+sp … sp+✕+pad
            yield return new Chip(i, column, x, width);
            x += width + 3; // " ▸ " separator
        }
    }

    protected override void Render(RenderContext context)
    {
        base.Render(context);
        var owner = _owner;
        if (owner is null || Bounds.Rows < 1)
            return;

        if (Background is not null)
            context.FillOpaque(new Rect(0, 0, Bounds.Columns, 1), Background);

        bool any = false;
        foreach (var chip in Chips())
        {
            if (any) // separator before every chip but the first
                context.DrawText(chip.X - 2, 0, "▸", PromptBrush ?? TextBrush);
            any = true;

            if (ChipBackground is not null)
                context.FillOpaque(new Rect(chip.X, 0, chip.Width, 1), ChipBackground);

            // The §3.3 virtual band focus: the focused chip wears the glyph accent as a frame
            // (⟨…⟩ corners drawn in the padding cells — no new theme key for a keyboard cue).
            if (chip.Index == owner.GroupChipFocusIndex)
            {
                context.DrawText(chip.X, 0, "⟨", GlyphBrush ?? TextBrush);
                context.DrawText(chip.X + chip.Width - 1, 0, "⟩", GlyphBrush ?? TextBrush);
            }

            var direction = owner.GroupDescriptions[chip.Index].Direction;
            context.DrawText(chip.X + 1, 0, direction == SortDirection.Ascending ? "▲" : "▼", GlyphBrush ?? TextBrush);
            context.DrawText(chip.X + 3, 0, chip.Column.EffectiveHeader, TextBrush);
            context.DrawText(chip.RemoveZoneStart, 0, "✕", PromptBrush ?? TextBrush);
        }

        if (!any && GraphemeWidth.StringWidth(EmptyPrompt) <= Bounds.Columns - 2)
            context.DrawText(1, 0, EmptyPrompt, PromptBrush); // drawn only when it fits (no truncated prompt)
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left || _owner is null)
            return;

        var position = e.GetPosition(this);
        foreach (var chip in Chips())
        {
            if (position.Column < chip.X || position.Column >= chip.X + chip.Width)
                continue;

            if (position.Column >= chip.RemoveZoneStart)
            {
                _owner.Ungroup(chip.Column); // the ✕ zone removes the level
            }
            else
            {
                // Elsewhere on the chip toggles the level's direction IN PLACE (a Replace edit —
                // the collection's CollectionChanged funnels the reshape; §3.3's chip Enter analog).
                var description = _owner.GroupDescriptions[chip.Index];
                _owner.GroupDescriptions[chip.Index] = description with
                {
                    Direction = description.Direction == SortDirection.Ascending
                        ? SortDirection.Descending
                        : SortDirection.Ascending,
                };
            }

            e.Handled = true;
            return;
        }
    }
}
