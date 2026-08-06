using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

using Size = Cursorial.Rendering.Size;
using CellStyle = Cursorial.Output.Style;

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

        MeasureGraphemes(availableSize, text, out int width, out int height, out _);
        
        var maxSize = new Size(width, height);
        return maxSize.ClampTo(availableSize);
    }

    private void MeasureGraphemes(Size size, string text, out int maxWidth, out int maxHeight, out Rect sampleBounds)
    {
        var width = size.Columns;
        var height = size.Rows;

        var hUnit = GraphemeWidth.StringWidth(text);
        
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
        
        var fg = Foreground ?? Brushes.Default;
        var bg = Background;
        var style = TextElement.ComposeAttributes(this).Apply(CellStyle.Default);

        var fgSample = fg is not SolidColorBrush;
        var bgSample = bg is IBrush and not SolidColorBrush;

        if (fgSample is false)
            style = style with { Foreground = fg.ColorAt(0, 0, bounds) };

        if (bgSample is false)
            style = style with { Background = bg?.ColorAt(0, 0, bounds) ?? Colors.Transparent }; 

        MeasureGraphemes(bounds.Size, text, out int maxWidth, out int maxHeight, out Rect sampleBounds);

        if (maxWidth > bounds.Columns || maxHeight > bounds.Rows) return;

        var colStart = bounds.Column;
        var rowStart = bounds.Row;

        for (var r = 0; r < maxHeight; r++)
        {
            int c;

            for (c = 0; c < maxWidth;)
            {
                var g = text.GetGraphemeEnumerator();
                var widthWritten = 0;
                var cEff = colStart + c;

                // TODO: Optimize this to write repeating blocks when 'Fill=True'. Simple `stackalloc` win?
                while (g.MoveNext())
                {
                    if (fgSample)
                        style = style with { Foreground = fg.ColorAt(cEff + widthWritten, r, sampleBounds) };

                    if (bgSample)
                        style = style with { Background = bg!.ColorAt(cEff + widthWritten, r, sampleBounds) };

                    var clusterWidth = GraphemeWidth.ClusterWidth(g.Current);

                    if (widthWritten + clusterWidth > maxWidth)
                        break;

                    context.DrawText(cEff + widthWritten, rowStart + r, g.Current, style.Foreground, style.Background, style);
                    widthWritten += clusterWidth;
                }

                c += widthWritten;
            }
        }
    }
}