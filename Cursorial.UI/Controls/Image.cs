using Cursorial.Rendering.Imaging;

namespace Cursorial.UI.Controls;

/// <summary>
/// A control that displays a graphics-protocol image (design doc §12 / CD-P2K-1 — the WPF <c>Image</c> analog). Its
/// <see cref="Control.Template"/> hosts an <see cref="ImagePresenter"/> (<c>PART_ImagePresenter</c>) to which
/// <see cref="Source"/>/<see cref="PlaceholderContent"/>/<see cref="PlaceholderTemplate"/> flow one-way via
/// <c>TemplateBinding</c>. The image renders only when a graphics protocol is negotiated; otherwise the placeholder
/// shows (see <see cref="ImagePresenter"/>).
/// </summary>
[TemplatePart(PartImagePresenter, typeof(ImagePresenter))]
public class Image : Control
{
    private const string PartImagePresenter = "PART_ImagePresenter";

    /// <summary>The image to display (<see langword="null"/> = none ⇒ the placeholder shows).</summary>
    public static readonly StyledProperty<ImageData?> SourceProperty =
        UIProperty.Register<Image, ImageData?>(nameof(Source));

    /// <summary>The placeholder content shown when no image renders.</summary>
    public static readonly StyledProperty<object?> PlaceholderContentProperty =
        UIProperty.Register<Image, object?>(nameof(PlaceholderContent));

    /// <summary>The template for <see cref="PlaceholderContent"/>.</summary>
    public static readonly StyledProperty<DataTemplate?> PlaceholderTemplateProperty =
        UIProperty.Register<Image, DataTemplate?>(nameof(PlaceholderTemplate));

    /// <inheritdoc cref="SourceProperty"/>
    public ImageData? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <inheritdoc cref="PlaceholderContentProperty"/>
    public object? PlaceholderContent { get => GetValue(PlaceholderContentProperty); set => SetValue(PlaceholderContentProperty, value); }

    /// <inheritdoc cref="PlaceholderTemplateProperty"/>
    public DataTemplate? PlaceholderTemplate { get => GetValue(PlaceholderTemplateProperty); set => SetValue(PlaceholderTemplateProperty, value); }

    /// <summary>The templated image presenter (diagnostic; null before the template applies).</summary>
    internal ImagePresenter? PresenterPart => GetTemplatePart<ImagePresenter>(PartImagePresenter);
}
