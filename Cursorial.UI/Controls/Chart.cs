using Cursorial.Drawing.Charts;

namespace Cursorial.UI.Controls;

/// <summary>
/// A control that displays a cell-rendered <see cref="IChart"/> (design doc §12 / CD-P2L-1 — the chart sibling of
/// <see cref="Image"/>). Its <see cref="Control.Template"/> hosts a <see cref="ChartPresenter"/>
/// (<c>PART_ChartPresenter</c>) to which <see cref="Source"/>/<see cref="PlaceholderContent"/>/
/// <see cref="PlaceholderTemplate"/> flow one-way via <c>TemplateBinding</c>. Charts are cell-rendered, so there is no
/// capability gate; when no chart is set the placeholder shows.
/// </summary>
[TemplatePart(PartChartPresenter, typeof(ChartPresenter))]
public class Chart : Control
{
    private const string PartChartPresenter = "PART_ChartPresenter";

    /// <summary>The chart to render (<see langword="null"/> = none ⇒ the placeholder shows).</summary>
    public static readonly StyledProperty<IChart?> SourceProperty =
        UIProperty.Register<Chart, IChart?>(nameof(Source));

    /// <summary>The placeholder content shown when no chart renders.</summary>
    public static readonly StyledProperty<object?> PlaceholderContentProperty =
        UIProperty.Register<Chart, object?>(nameof(PlaceholderContent));

    /// <summary>The template for <see cref="PlaceholderContent"/>.</summary>
    public static readonly StyledProperty<DataTemplate?> PlaceholderTemplateProperty =
        UIProperty.Register<Chart, DataTemplate?>(nameof(PlaceholderTemplate));

    /// <inheritdoc cref="SourceProperty"/>
    public IChart? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <inheritdoc cref="PlaceholderContentProperty"/>
    public object? PlaceholderContent { get => GetValue(PlaceholderContentProperty); set => SetValue(PlaceholderContentProperty, value); }

    /// <inheritdoc cref="PlaceholderTemplateProperty"/>
    public DataTemplate? PlaceholderTemplate { get => GetValue(PlaceholderTemplateProperty); set => SetValue(PlaceholderTemplateProperty, value); }

    /// <summary>The templated chart presenter (diagnostic; null before the template applies).</summary>
    internal ChartPresenter? PresenterPart => GetTemplatePart<ChartPresenter>(PartChartPresenter);
}
