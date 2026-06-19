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

    /// <summary>The image as explicit bytes (<see langword="null"/> ⇒ fall back to <see cref="SourceUri"/>, else the placeholder).</summary>
    public static readonly StyledProperty<ImageData?> SourceProperty =
        UIProperty.Register<Image, ImageData?>(nameof(Source));

    /// <summary>A URI the image is loaded from (the XAML-friendly declarative source — see <see cref="ImagePresenter.SourceUri"/>).</summary>
    public static readonly StyledProperty<Uri?> SourceUriProperty =
        UIProperty.Register<Image, Uri?>(nameof(SourceUri));

    /// <summary>The placeholder content shown when no image renders.</summary>
    public static readonly StyledProperty<object?> PlaceholderContentProperty =
        UIProperty.Register<Image, object?>(nameof(PlaceholderContent));

    /// <summary>The template for <see cref="PlaceholderContent"/>.</summary>
    public static readonly StyledProperty<DataTemplate?> PlaceholderTemplateProperty =
        UIProperty.Register<Image, DataTemplate?>(nameof(PlaceholderTemplate));

    /// <inheritdoc cref="SourceProperty"/>
    public ImageData? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <inheritdoc cref="SourceUriProperty"/>
    public Uri? SourceUri { get => GetValue(SourceUriProperty); set => SetValue(SourceUriProperty, value); }

    /// <inheritdoc cref="PlaceholderContentProperty"/>
    public object? PlaceholderContent { get => GetValue(PlaceholderContentProperty); set => SetValue(PlaceholderContentProperty, value); }

    /// <inheritdoc cref="PlaceholderTemplateProperty"/>
    public DataTemplate? PlaceholderTemplate { get => GetValue(PlaceholderTemplateProperty); set => SetValue(PlaceholderTemplateProperty, value); }

    /// <summary>The templated image presenter (diagnostic; null before the template applies).</summary>
    internal ImagePresenter? PresenterPart => GetTemplatePart<ImagePresenter>(PartImagePresenter);
}
