using Cursorial.Rendering;
using Cursorial.Rendering.Imaging;

using ImageContent = Cursorial.Rendering.Content.Image;

namespace Cursorial.UI.Controls;

/// <summary>
/// A primitive (design doc §12 / CD-P2K-1) that hosts a graphics-protocol image, drawn through the
/// <see cref="Cursorial.Rendering.Content.Image"/> content (the Kitty/iTerm2/Sixel <c>IBufferFragment</c> path — NOT
/// the cell-sampling <c>ImageBrush</c>) via <see cref="RenderContext.DrawContent"/>, which auto-crops the fragment to
/// the active clip. The image renders only when a <see cref="Source"/> is set <b>and</b> the negotiated graphics
/// protocols can actually carry its format; otherwise a placeholder (<see cref="PlaceholderContent"/> +
/// <see cref="PlaceholderTemplate"/>, realized through an internal <see cref="ContentPresenter"/>) shows. The presenter
/// is <see cref="UIElement.ClipToBounds"/> so its image is clipped to its layout rect. The <c>:placeholder</c>
/// pseudo-class marks the placeholder state.
/// </summary>
/// <remarks>
/// v1 uses the content's native aspect-preserving fit-within-bounds (sizing is delegated to
/// <see cref="Cursorial.Rendering.Content.Image"/>, so a null / single-axis <see cref="ImageData.RequestedSize"/> is
/// sized from the decoded pixels rather than collapsing to nothing); a <c>Stretch</c> property and a UI-level
/// <c>ImageSource</c>/URI abstraction are deferrals (v1 takes <see cref="ImageData"/> directly). A mid-session
/// renegotiation that flips graphics support re-evaluates on the next layout pass (the presenter subscribes to
/// <see cref="UIApplication.CapabilitiesChanged"/>).
/// </remarks>
public class ImagePresenter : UIElement
{
    /// <summary>The image to display (<see langword="null"/> = none ⇒ the placeholder shows).</summary>
    public static readonly StyledProperty<ImageData?> SourceProperty =
        UIProperty.Register<ImagePresenter, ImageData?>(nameof(Source), changed: OnSourceChanged);

    /// <summary>The placeholder content shown when no image renders (no <see cref="Source"/>, an incompatible format, or no graphics protocol).</summary>
    public static readonly StyledProperty<object?> PlaceholderContentProperty =
        UIProperty.Register<ImagePresenter, object?>(nameof(PlaceholderContent), changed: OnPlaceholderContentChanged);

    /// <summary>The template for <see cref="PlaceholderContent"/>.</summary>
    public static readonly StyledProperty<DataTemplate?> PlaceholderTemplateProperty =
        UIProperty.Register<ImagePresenter, DataTemplate?>(nameof(PlaceholderTemplate), changed: OnPlaceholderTemplateChanged);

    private readonly ContentPresenter _placeholder = new();
    private ImageContent? _imageContent;  // cached per Source — a fresh content per Render would churn a new image id every frame
    private UIApplication? _subscribedApp; // the app whose CapabilitiesChanged we're subscribed to (for symmetric unsubscribe)

    static ImagePresenter()
    {
        AffectsMeasure<ImagePresenter>(SourceProperty, PlaceholderContentProperty, PlaceholderTemplateProperty);
    }

    /// <summary>Creates an image presenter (clip-bounded so its graphics content never bleeds past its layout rect).</summary>
    public ImagePresenter()
    {
        ClipToBounds = true;
        AdoptChild(_placeholder, index: -1);
    }

    /// <inheritdoc cref="SourceProperty"/>
    public ImageData? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <inheritdoc cref="PlaceholderContentProperty"/>
    public object? PlaceholderContent { get => GetValue(PlaceholderContentProperty); set => SetValue(PlaceholderContentProperty, value); }

    /// <inheritdoc cref="PlaceholderTemplateProperty"/>
    public DataTemplate? PlaceholderTemplate { get => GetValue(PlaceholderTemplateProperty); set => SetValue(PlaceholderTemplateProperty, value); }

    /// <summary>The internal placeholder host (diagnostic).</summary>
    internal ContentPresenter PlaceholderPresenter => _placeholder;

    /// <summary>Whether the image (not the placeholder) is currently shown — a <see cref="Source"/> is set, has bytes,
    /// and the negotiated graphics protocols can carry its format.</summary>
    public bool IsImageVisible
    {
        get
        {
            // Mirrors Cursorial.Rendering.Content.Image.CreateFragment's producer rule (the single place the protocol/
            // format compatibility lives): iTerm2 carries any format; Kitty/Sixel carry PNG only. Gating on protocol
            // presence alone would collapse this placeholder on a Kitty-only terminal with a JPEG source, then show the
            // content's own "[image]" text instead (CD-P2K-1 audit).
            if (Source is not { } s || s.Bytes.IsEmpty)
                return false;
            if (UIApplication.Current?.Capabilities.Output.Graphics is not { } g)
                return false;
            return g.ITerm2InlineImages || (s.Format == ImageFormat.Png && (g.KittyGraphics || g.Sixel));
        }
    }

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        // Re-evaluate image-vs-placeholder when the terminal renegotiates graphics support (the measure cache would
        // otherwise leave the placeholder visibility / :placeholder stale on a caps flip — CD-P2K-1 audit).
        if (UIApplication.Current is { } app)
        {
            app.CapabilitiesChanged += OnCapabilitiesChanged;
            _subscribedApp = app;
        }
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (_subscribedApp is { } app)
        {
            app.CapabilitiesChanged -= OnCapabilitiesChanged;
            _subscribedApp = null;
        }

        base.OnDetachedFromTree(in e);
    }

    private void OnCapabilitiesChanged(object? sender, CapabilitiesChangedEventArgs e)
    {
        InvalidateMeasure(); // defeats the measure-cache early-out so MeasureOverride re-runs UpdatePlaceholderState
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (UpdatePlaceholderState() && (_imageContent ??= BuildContent()) is { } content)
        {
            _placeholder.Measure(Size.Empty); // collapsed — measures to nothing
            var size = content.Measure(availableSize, UIApplication.Current!.Capabilities.Output); // delegated sizing (handles null/single-axis)
            return FloorVisibleImageSize(size, availableSize); // a visible image must never collapse to a 0 extent on either axis
        }

        _placeholder.Measure(availableSize);
        return _placeholder.DesiredSize;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        // The placeholder fills the rect when visible; when the image shows it's Collapsed (arrange is a harmless no-op).
        _placeholder.Arrange(new Rect(0, 0, finalSize.Columns, finalSize.Rows));
        return finalSize;
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        if (IsImageVisible && (_imageContent ??= BuildContent()) is { } content && !context.Bounds.IsEmpty)
            context.DrawContent(context.Bounds, content); // the cached content fits within bounds + selects the protocol
    }

    private ImageContent? BuildContent() => Source is { } src ? new ImageContent(src) : null;

    // A visible image must never measure to a 0 extent on either axis (Rect.IsEmpty is "any axis 0" — Render would skip
    // it and it would silently vanish). The content's null/single-axis sizing can round an axis to 0 (tiny image, or a
    // terminal that didn't report a cell-pixel size); floor any zero axis to the available extent (≥ 1, unbounded ⇒ 1).
    private static Size FloorVisibleImageSize(Size measured, Size available)
    {
        static int Axis(int m, int avail) => m > 0 ? m : LayoutMath.IsUnbounded(avail) ? 1 : Math.Max(1, avail);
        return new Size(Axis(measured.Columns, available.Columns), Axis(measured.Rows, available.Rows));
    }

    // Flip the placeholder child's visibility + the :placeholder pseudo-class to the current image-visible state;
    // returns whether the image is visible.
    private bool UpdatePlaceholderState()
    {
        var imageVisible = IsImageVisible;
        _placeholder.SetCurrentValue(VisibilityProperty, imageVisible ? Visibility.Collapsed : Visibility.Visible);
        PseudoClasses.Set(":placeholder", !imageVisible);
        return imageVisible;
    }

    private static void OnSourceChanged(UIObject sender, ImageData? oldValue, ImageData? newValue)
    {
        if (sender is not ImagePresenter p)
            return;
        p._imageContent = newValue is { } src ? new ImageContent(src) : null; // rebuild the cached content (id/diff stability)
        p.InvalidateVisual();
    }

    private static void OnPlaceholderContentChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is ImagePresenter p)
            p._placeholder.SetCurrentValue(ContentPresenter.ContentProperty, newValue);
    }

    private static void OnPlaceholderTemplateChanged(UIObject sender, DataTemplate? oldValue, DataTemplate? newValue)
    {
        if (sender is ImagePresenter p)
            p._placeholder.SetCurrentValue(ContentPresenter.ContentTemplateProperty, newValue);
    }
}
