using Cursorial.Drawing.Charts;
using Cursorial.Rendering;

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
        return new Size(Axis(availableSize.Columns), Axis(availableSize.Rows));
    }

    /// <inheritdoc/>
    protected override void RenderPrimaryContent(RenderContext context)
    {
        if (Source is { } chart)
            context.DrawChart(chart, context.Bounds);
    }

    private static void OnSourceChanged(UIObject sender, IChart? oldValue, IChart? newValue)
        => (sender as ChartPresenter)?.InvalidateVisual(); // AffectsMeasure re-lays out; also repaint the chart
}
