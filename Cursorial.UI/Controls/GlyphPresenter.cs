using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

using Size = Cursorial.Rendering.Size;

namespace Cursorial.UI.Controls;

public sealed class GlyphPresenter : UIElement
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<GlyphPresenter>();

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<GlyphPresenter>();

    public static readonly StyledProperty<Orientation> OrientationProperty =
        UIProperty.Register<GlyphPresenter, Orientation>(nameof(Orientation), defaultValue: Orientation.Horizontal);

    public static readonly StyledProperty<string?> GlyphProperty =
        UIProperty.Register<GlyphPresenter, string?>(nameof(Glyph), defaultValue: null);

    public static readonly StyledProperty<bool> FillProperty =
        UIProperty.Register<GlyphPresenter, bool>(nameof(Fill), defaultValue: false);

    public static readonly StyledProperty<bool> TruncateProperty =
        UIProperty.Register<GlyphPresenter, bool>(nameof(Truncate), defaultValue: true);

    static GlyphPresenter()
    {
        AffectsRender<GlyphPresenter>(OrientationProperty, GlyphProperty, FillProperty, TruncateProperty);
        AffectsMeasure<GlyphPresenter>(GlyphProperty, FillProperty, OrientationProperty);
    }
    
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }
    public bool Truncate
    {
        get => GetValue(TruncateProperty);
        set => SetValue(TruncateProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Glyph is not { Length: > 0 } text) return Size.Empty;
        
        availableSize = new(GraphemeWidth.StringWidth(text), 1);

        MeasureGraphemes(availableSize, text, out int width, out int height, out _, out _);
        
        var maxSize = new Size(width, height);
        return maxSize.ClampTo(availableSize);
    }

    private void MeasureGraphemes(Size size, string text, out int maxWidth, out int maxHeight, out Rect sampleBounds, out int hUnit)
    {
        var width = size.Columns;
        var height = size.Rows;

        hUnit = GraphemeWidth.StringWidth(text);
        
        if (Orientation is Orientation.Horizontal)
        {
            if (Fill)
                maxWidth = Truncate ? width : width / hUnit * hUnit;
            else
                maxWidth = Math.Min(width, hUnit);

            maxHeight = 1;
            sampleBounds = new Rect(0, 0, width, 1);
        }
        else
        {
            maxWidth = Math.Min(width, hUnit);
            maxHeight = height;
            sampleBounds = new Rect(0, 0, maxWidth, height);
        }
    }

    protected override void Render(RenderContext context)
    {
        if (Glyph is not { Length: > 0 } text) return;

        var bounds = context.Bounds;
        if (bounds.IsEffectivelyEmpty) return;
        
        var style = BrushedStyle.FromElement(this);

        if (style.Foreground is null)
            style = style with { Foreground = Brushes.Default };

        MeasureGraphemes(bounds.Size, text, out int maxWidth, out int maxHeight, out Rect sampleBounds, out int hUnit);

        if (maxWidth > bounds.Columns || maxHeight > bounds.Rows) return;

        var colStart = bounds.Column;
        var rowStart = bounds.Row;

        Span<char> graphemes = stackalloc char[maxWidth];

        if (hUnit > 1 || text.Length > 1)
        {
            var length = 0;

            int gw;
            var e = text.GetGraphemeEnumerator();
            var unit = text.AsSpan();
            
            for (int i = 0; i + hUnit <= maxWidth; i += hUnit)
            {
                unit.CopyTo(graphemes.Slice(length));
                length += hUnit;
            }

            while (e.MoveNext() && length + (gw = GraphemeWidth.ClusterWidth(e.Current)) <= maxWidth)
            {
                e.Current.CopyTo(graphemes.Slice(length));
                length += gw;
            }

            if (length < 1) return;

            graphemes = graphemes.Slice(0, length);
        }
        else
        {
            graphemes.Fill(text[0]);
        }

        for (var r = 0; r < maxHeight; r++)
            context.DrawText(colStart, rowStart + r, graphemes, style, sampleBounds);
    }
}