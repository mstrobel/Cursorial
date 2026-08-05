using Cursorial.Drawing;
using Cursorial.Drawing.Charts;
using Cursorial.Markup;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A primitive (design doc §12 / CD-P2L-1) that hosts a cell-rendered <see cref="IChart"/>, painted via
/// <see cref="RenderContext.DrawChart"/> (the chart draws itself into the presenter's bounds and clips to them).
/// Unlike <see cref="ImagePresenter"/> there is <b>no capability gate</b> — charts are drawn with ordinary cells and
/// render on every terminal. When no <see cref="Source"/> is set the inherited placeholder shows. See
/// <see cref="DrawnContentPresenter"/> for the placeholder plumbing, <c>ClipToBounds</c>, and the <c>:placeholder</c>
/// pseudo-class.
/// </summary>
/// <remarks>
/// A chart has no intrinsic size — it fills the available (bounded) area; an unbounded axis (e.g. a vertical
/// <c>StackPanel</c>'s height) collapses to 0, so give the host a bounded size. A chart mutated in place is not
/// observed; assign a new <see cref="Source"/> (or re-set it) to repaint.
/// </remarks>
[ContentProperty(nameof(Source))]
public class ChartPresenter : DrawnContentPresenter
{
    /// <summary>The chart to render (<see langword="null"/> = none ⇒ the placeholder shows).</summary>
    public static readonly StyledProperty<IChart?> SourceProperty =
        UIProperty.Register<ChartPresenter, IChart?>(nameof(Source), changed: OnSourceChanged);

    static ChartPresenter()
    {
        AffectsMeasure<ChartPresenter>(SourceProperty);
    }

    /// <inheritdoc cref="SourceProperty"/>
    public IChart? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <inheritdoc/>
    protected override bool IsPrimaryContentVisible => Source is not null;

    /// <inheritdoc/>
    protected override Size MeasurePrimaryContent(Size availableSize)
    {
        // A chart fills its bounded area; an unbounded axis collapses (it has no natural size to report).
        static int Axis(int a) => LayoutMath.IsUnbounded(a) ? 0 : a;
        var desiredSize = new Size(Axis(availableSize.Columns), Axis(availableSize.Rows));
        if (desiredSize != DesiredSize)
            InvalidateVisual();
        return desiredSize;
    }

    private SceneCompositor? _compositor;
    private CellBuffer? _buffer;
    private Output.Capabilities.OutputCapabilities? _bufferCapabilities;

    /// <summary>Test seam: the scratch's current dimensions, or <see langword="null"/> when none is
    /// held. Nothing outside tests should depend on the cache's existence.</summary>
    internal Size? CompositingScratchSize => _buffer is { } b ? new Size(b.Columns, b.Rows) : null;

    /// <summary>Test seam: the scratch's blank style, which must be transparent — see
    /// <see cref="RentCompositingScratch"/>.</summary>
    internal Output.Style? CompositingScratchBlank => _buffer?.DefaultStyle;

    /// <summary>
    /// Drops the layered-compositing scratch. Nothing outside a render observes it, so releasing is
    /// always safe — the next layered render rebuilds it.
    /// </summary>
    private void ReleaseCompositingScratch()
    {
        _buffer = null;
        _compositor = null;
        _bufferCapabilities = null;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        base.OnDetachedFromTree(e);

        // A detached presenter would otherwise hold a buffer the size of its last layout for as long
        // as the page stays off-screen.
        ReleaseCompositingScratch();
    }

    /// <summary>
    /// The scratch surface the layered path composites into. Rebuilt when it is too small, when it is
    /// wastefully large (a presenter that shrank after a resize would otherwise keep its peak
    /// allocation resident), and when the terminal's capabilities change — the buffer bakes those in
    /// at construction, so a cached one would keep compositing against a stale colour tier.
    /// </summary>
    private CellBuffer RentCompositingScratch(RenderContext context)
    {
        var size = context.Bounds.Size;
        long needed = (long) size.Columns * size.Rows;

        if (_buffer is { } cached &&
            cached.Bounds.Contains(new Rect(size)) &&
            _bufferCapabilities == context.Capabilities &&
            (long) cached.Columns * cached.Rows <= needed * 2)
        {
            cached.Clear(Output.Style.Transparent);
            _compositor ??= new SceneCompositor(cached);
            return cached;
        }

        // An intermediate surface: what it does NOT paint is blitted onwards and must contribute
        // nothing, so its blank is transparent rather than the terminal's default. Declaring that at
        // construction (instead of filling over an opaque blank afterwards) is what makes
        // DefaultStyle true here — the answer everything that blanks a cell of its own accord reads.
        var buffer = new CellBuffer(size.Columns, size.Rows,
                                    TerminalCapabilities.None with { Output = context.Capabilities },
                                    defaultStyle: Output.Style.Transparent);

        _buffer = buffer;
        _bufferCapabilities = context.Capabilities;
        _compositor = new SceneCompositor(buffer);
        return buffer;
    }

    /// <inheritdoc/>
    protected override void RenderPrimaryContent(RenderContext context)
    {
        if (Source is {} chart)
        {
            if (chart is ILayeredChart lc)
            {
                var layers = lc.ToLayers(context.Bounds);
                try
                {
                    var buffer = RentCompositingScratch(context);
                    var sceneLayers = new SceneLayer[layers.Count];
                    for (int i = 0; i < layers.Count; i++)
                        sceneLayers[i] = new SceneLayer(layers[i]);

                    _compositor!.Composite(sceneLayers, buffer);
                    context.Blit(buffer.View(context.Bounds), context.Bounds);
                }
                finally
                {
                    // ToLayers hands ownership to the caller: one scene — each with its own cell
                    // buffer — is built per series per render, so dropping them unreleased churns
                    // that much every frame (and would strand pooled buffers outright if ToLayers
                    // ever rents them rather than allocating).
                    for (int i = 0; i < layers.Count; i++)
                        layers[i].Dispose();
                }
            }
            else
            {
                context.DrawChart(chart, context.Bounds);
            }
        }
    }

    private static void OnSourceChanged(UIObject sender, IChart? oldValue, IChart? newValue)
    {
        if (sender is not ChartPresenter chart) return;
        
        chart.InvalidateVisual(); // AffectsMeasure re-lays out; also repaint the chart

        // Only the layered path uses the scratch — a source that no longer needs it must not keep it.
        if (newValue is not ILayeredChart)
            chart.ReleaseCompositingScratch();

        // Drop OUR hover tip (the new source's geometry supersedes it) — but never a host-set one:
        // an unguarded clear would permanently suppress a tooltip the user assigned themselves.
        if (chart.GetValueSource(ToolTipService.TipProperty) is { Kind: ValueSourceKind.Default })
            chart.ClearValue(ToolTipService.TipProperty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (Source is {} chart && chart.HitTest(e.GetPosition(this), out var tip))
            UpdateTip(tip);
        else
            UpdateTip(null);
    }

    private void UpdateTip(object? tip)
    {
        if (GetValueSource(ToolTipService.TipProperty) is { Kind: ValueSourceKind.Default })
        {
            if (tip is not null)
                SetCurrentValue(ToolTipService.TipProperty, tip);
            else
                ClearValue(ToolTipService.TipProperty);
        }
    }
}
