using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The edit action bar (design doc §3.2; the mockup's <c>editbar</c>): a one-row band docked at the
/// very bottom (below the summary footer) that renders ONLY while the rows presenter hosts an
/// editor — <c>✎ editing SO-1044 · Enter commit · Esc cancel · Tab next cell</c>, with the key
/// names accented and the ✎ indicator in the pending-edit tint. The caption is the edit row's
/// first visible column value ("(new)" on the new-row template). Collapses to zero rows otherwise,
/// so a grid that never edits spends nothing on it. Its own render boundary (ClipToBounds — the
/// band-local re-ink rule, §3.1); the grid pings it via <c>NotifyEditingChanged</c> on every
/// editor begin/end (editing state is presenter state, not a property — nothing to bind).
/// </summary>
public sealed class DataGridEditBar : UIElement
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        UIProperty.Register<DataGridEditBar, IBrush?>(nameof(Background));

    /// <summary>The muted body ink ("editing …", "commit", the · separators).</summary>
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        UIProperty.Register<DataGridEditBar, IBrush?>(nameof(TextBrush));

    /// <summary>The accented key-name ink (Enter / Esc / Tab — the mockup's <c>.k</c>).</summary>
    public static readonly StyledProperty<IBrush?> KeyBrushProperty =
        UIProperty.Register<DataGridEditBar, IBrush?>(nameof(KeyBrush));

    /// <summary>The ✎ pending-edit indicator ink (the mockup's amber <c>ind-edit</c>).</summary>
    public static readonly StyledProperty<IBrush?> IndicatorBrushProperty =
        UIProperty.Register<DataGridEditBar, IBrush?>(nameof(IndicatorBrush));

    /// <summary>The ✕ validation-error ink (§10.2 — the danger tint the commit veto message wears).</summary>
    public static readonly StyledProperty<IBrush?> ErrorBrushProperty =
        UIProperty.Register<DataGridEditBar, IBrush?>(nameof(ErrorBrush));

    static DataGridEditBar()
    {
        AffectsRender<DataGridEditBar>(BackgroundProperty, TextBrushProperty, KeyBrushProperty,
                                       IndicatorBrushProperty, ErrorBrushProperty);
    }

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? KeyBrush { get => GetValue(KeyBrushProperty); set => SetValue(KeyBrushProperty, value); }
    public IBrush? IndicatorBrush { get => GetValue(IndicatorBrushProperty); set => SetValue(IndicatorBrushProperty, value); }
    public IBrush? ErrorBrush { get => GetValue(ErrorBrushProperty); set => SetValue(ErrorBrushProperty, value); }

    private DataGrid? _owner;

    /// <summary>The owning grid (stamped when the template applies).</summary>
    internal DataGrid? Owner
    {
        get => _owner;
        set
        {
            if (ReferenceEquals(_owner, value))
                return;
            _owner = value;
            ClipToBounds = true; // own boundary — band-local re-ink (§3.1; safe: a 1-row band)
            InvalidateMeasure();
        }
    }

    /// <summary>Whether the band is live (the rows presenter hosts an editor).</summary>
    private bool IsActive => _owner?.RowsPresenter is { IsEditing: true };

    protected override Size MeasureOverride(Size availableSize)
        => new(availableSize.Columns, IsActive ? 1 : 0); // collapsed while idle — no row spent

    protected override void Render(RenderContext context)
    {
        base.Render(context);
        var owner = _owner;
        if (owner is null || !IsActive || Bounds.Rows < 1)
            return;

        if (Background is not null)
            context.FillOpaque(new Rect(0, 0, Bounds.Columns, 1), new BrushedStyle { Background = Background });

        // "✎ editing SO-1044 · Enter commit · Esc cancel · Tab next cell" — segment by segment,
        // walking x by measured width (the ✎ can render wide on some terminals; measure, don't count).
        int x = 1;
        x = Draw(context, x, "✎", IndicatorBrush ?? KeyBrush);
        x = Draw(context, x, " editing", TextBrush);
        string caption = owner.EditCaption();
        if (caption.Length > 0)
            x = Draw(context, x, $" {caption}", TextBrush);

        // §10.2: a rejected commit shows the validator's message in the danger ink, in place of the
        // key hints (prominent, and it can't get clipped off the right past a long hint run).
        if (owner.EditValidationError is { Length: > 0 } error)
        {
            Draw(context, x, $"  ✕ {error}", ErrorBrush ?? KeyBrush);
            return;
        }

        x = Draw(context, x, " · ", TextBrush);
        x = Draw(context, x, "Enter", KeyBrush);
        x = Draw(context, x, " commit · ", TextBrush);
        x = Draw(context, x, "Esc", KeyBrush);
        x = Draw(context, x, " cancel · ", TextBrush);
        x = Draw(context, x, "Tab", KeyBrush);
        // The new-row session is one-cell-at-a-time (§3.2): Tab COMMITS the pending row there —
        // the hint says what the key does (sweep [20]; a hint must never advertise the wrong verb).
        Draw(context, x, owner.IsNewRowSession ? " commit row" : " next cell", TextBrush);
    }

    /// <summary>Draws one segment and returns the advanced x (clipped by the band's own boundary).</summary>
    private static int Draw(RenderContext context, int x, string text, IBrush? brush)
    {
        if (brush is not null)
            context.DrawText(x, 0, text, brush);
        return x + GraphemeWidth.StringWidth(text);
    }
}
