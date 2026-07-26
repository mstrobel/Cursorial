using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Imaging;

using ImageContent = Cursorial.Rendering.Content.Image;

namespace Cursorial.UI.Controls;

/// <summary>
/// A primitive (design doc §12 / CD-P2K-1) that hosts a graphics-protocol image, drawn through the
/// <see cref="Cursorial.Rendering.Content.Image"/> content (the Kitty/iTerm2/Sixel <c>IBufferFragment</c> path — NOT
/// the cell-sampling <c>ImageBrush</c>) via <see cref="RenderContext.DrawContent"/>, which auto-crops the fragment to
/// the active clip. The image source is either explicit bytes (<see cref="Source"/>) or a <see cref="SourceUri"/>
/// loaded through the resource loader (the XAML-friendly path — <c>embedded://</c>/<c>file://</c>/relative). The image
/// renders only when there is an effective source <b>and</b> the negotiated graphics protocols can carry its format;
/// otherwise the inherited placeholder shows. See <see cref="DrawnContentPresenter"/> for the placeholder plumbing,
/// <c>ClipToBounds</c>, and the <c>:placeholder</c> pseudo-class.
/// </summary>
/// <remarks>
/// Sizing is delegated to <see cref="Cursorial.Rendering.Content.Image"/> (a null / single-axis
/// <see cref="ImageData.RequestedSize"/> is sized from the decoded pixels). A <c>Stretch</c> property is a deferral.
/// A mid-session renegotiation that flips graphics support re-evaluates on the next layout pass (the presenter
/// subscribes to <see cref="UIApplication.EffectiveCapabilitiesChanged"/>). <see cref="SourceUri"/> loads synchronously via
/// <see cref="ResourceLoader.Default"/> — fine for embedded/file sources; an explicit <see cref="Source"/> wins.
/// </remarks>
public class ImagePresenter : DrawnContentPresenter
{
    /// <summary>The image as explicit decoded-or-encoded bytes (<see langword="null"/> ⇒ fall back to <see cref="SourceUri"/>).</summary>
    public static readonly StyledProperty<ImageData?> SourceProperty =
        UIProperty.Register<ImagePresenter, ImageData?>(nameof(Source), changed: OnSourceChanged);

    /// <summary>A URI the image is loaded from (the XAML-friendly declarative source — <c>embedded://</c>/<c>file://</c>/relative
    /// via <see cref="ResourceLoader.Default"/>; the format is inferred from the path extension). An explicit
    /// <see cref="Source"/> takes precedence.</summary>
    public static readonly StyledProperty<Uri?> SourceUriProperty =
        UIProperty.Register<ImagePresenter, Uri?>(nameof(SourceUri), changed: OnSourceUriChanged);

    /// <summary>Whether the image (not the placeholder) is currently shown — an effective source is set, has bytes,
    /// and the negotiated graphics protocols can carry its format.</summary>
    public static readonly DirectProperty<ImagePresenter, bool> IsImageVisibleProperty =
        UIProperty.RegisterDirect<ImagePresenter, bool>(nameof(IsImageVisible),
                                                        getter: static o => o.IsImageVisible);

    private bool _isImageVisibleCached;
    private ImageContent? _imageContent;  // cached per effective source — a fresh content per Render would churn a new image id every frame
    private ImageData? _uriImage;          // the ImageData loaded from SourceUri (null when unset / load failed)
    private UIApplication? _subscribedApp; // the app whose CapabilitiesChanged we're subscribed to (for symmetric unsubscribe)

    static ImagePresenter()
    {
        AffectsMeasure<ImagePresenter>(SourceProperty, SourceUriProperty);
    }

    /// <inheritdoc cref="SourceProperty"/>
    public ImageData? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <inheritdoc cref="SourceUriProperty"/>
    public Uri? SourceUri { get => GetValue(SourceUriProperty); set => SetValue(SourceUriProperty, value); }

    /// <summary>The image actually shown — the explicit <see cref="Source"/> if set, else the <see cref="SourceUri"/>-loaded image.</summary>
    public ImageData? EffectiveSource => Source ?? _uriImage;

    /// <inheritdoc cref="IsImageVisibleProperty"/>
    public bool IsImageVisible
    {
        get
        {
            // Mirrors Cursorial.Rendering.Content.Image.CreateFragment's producer rule (the single place the protocol/
            // format compatibility lives): iTerm2 carries any format; Kitty/Sixel carry PNG only. Gating on protocol
            // presence alone would collapse the placeholder on a Kitty-only terminal with a JPEG source, then show the
            // content's own "[image]" text instead (CD-P2K-1 audit).
            if (EffectiveSource is not { } s || s.Bytes.IsEmpty)
                return false;
            // The EFFECTIVE view (negotiated ∘ CapabilityOverrides — FB-5): a user forcing images off
            // degrades to the placeholder exactly as a terminal without graphics would.
            if (UIApplication.Current?.EffectiveCapabilities.Output.Graphics is not {} g)
                return false;
            return g.ITerm2InlineImages || (s.Format == ImageFormat.Png && (g.KittyGraphics || g.Sixel));
        }
    }

    /// <inheritdoc/>
    protected override bool IsPrimaryContentVisible => IsImageVisible;

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        // Re-evaluate image-vs-placeholder when the terminal renegotiates graphics support (the measure cache would
        // otherwise leave the placeholder visibility / :placeholder stale on a caps flip — CD-P2K-1 audit).
        if (UIApplication.Current is { } app)
        {
            app.EffectiveCapabilitiesChanged += OnCapabilitiesChanged;
            app.CapabilityOverridesChanged += OnCapabilityOverridesChanged; // FB-5: forced-off images collapse to the placeholder live
            _subscribedApp = app;
        }
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (_subscribedApp is { } app)
        {
            app.EffectiveCapabilitiesChanged -= OnCapabilitiesChanged;
            app.CapabilityOverridesChanged -= OnCapabilityOverridesChanged;
            _subscribedApp = null;
        }

        base.OnDetachedFromTree(in e);
    }

    private void OnCapabilitiesChanged(object? sender, CapabilitiesChangedEventArgs e)
    {
        InvalidateContent(invalidateMeasure: true);  // defeats the measure-cache early-out so MeasureOverride
                                                     // re-runs UpdatePlaceholderState
    }

    private void OnCapabilityOverridesChanged(object? sender, EventArgs e)
    {
        InvalidateContent(invalidateMeasure: true);  // defeats the measure-cache early-out so MeasureOverride
                                                     // re-runs UpdatePlaceholderState
    }

    /// <inheritdoc/>
    protected override Size MeasurePrimaryContent(Size availableSize)
    {
        var content = _imageContent ??= BuildContent();
        if (content is null)
            return Size.Empty;
        
        var size = content.Measure(availableSize, UIApplication.Current!.EffectiveCapabilities.Output); // delegated sizing (handles null/single-axis)
        return FloorVisibleImageSize(size, availableSize); // a visible image must never collapse to a 0 extent on either axis
    }

    /// <inheritdoc/>
    protected override void RenderPrimaryContent(RenderContext context)
    {
        if ((_imageContent ??= BuildContent()) is { } content)
            context.DrawContent(context.Bounds, content); // the cached content fits within bounds + selects the protocol
    }

    private ImageContent? BuildContent() => EffectiveSource is { } src ? new ImageContent(src) : null;

    // A visible image must never measure to a 0 extent on either axis (Rect.IsEmpty is "any axis 0" — Render would skip
    // it and it would silently vanish). The content's null/single-axis sizing can round an axis to 0 (tiny image, or a
    // terminal that didn't report a cell-pixel size); floor any zero axis to the available extent (≥ 1, unbounded ⇒ 1).
    private static Size FloorVisibleImageSize(Size measured, Size available)
    {
        static int Axis(int m, int avail) => m > 0 ? m : LayoutMath.IsUnbounded(avail) ? 1 : Math.Max(1, avail);
        return new Size(Axis(measured.Columns, available.Columns), Axis(measured.Rows, available.Rows));
    }

    private static void OnSourceChanged(UIObject sender, ImageData? oldValue, ImageData? newValue)
        => (sender as ImagePresenter)?.InvalidateContent(invalidateMeasure: true);

    private static void OnSourceUriChanged(UIObject sender, Uri? oldValue, Uri? newValue)
    {
        if (sender is not ImagePresenter p)
            return;
        p._uriImage = LoadFromUri(newValue); // synchronous load via the resource loader (embedded/file/relative)
        p.InvalidateContent(invalidateMeasure: true);
    }

    private void InvalidateContent(bool invalidateMeasure = false)
    {   
        var wasImageVisible = _isImageVisibleCached;

        _imageContent = EffectiveSource is {} src ? new ImageContent(src) : null; // rebuild the cached content (id/diff stability)

        var isImageVisible = _isImageVisibleCached = IsImageVisible;

        if (invalidateMeasure || isImageVisible != wasImageVisible)
            InvalidateMeasure();

        InvalidateVisual();
        InvalidateComposite();
        UpdatePlaceholderState();
        
        if (wasImageVisible == isImageVisible) return;

        DispatchPropertyChanged(IsImageVisibleProperty,
                                null,
                                wasImageVisible,
                                isImageVisible,
                                BindingPriority.LocalValue);
    }

    // Load + decode-format an image from a URI via the default resource loader; null on a missing source / failed load.
    // The load is defended even though TryLoadBytes is documented to return null for unresolvable URIs: the default
    // loader's file/relative paths still throw on a malformed path (NUL char ⇒ ArgumentException, over-long ⇒
    // PathTooLongException) — that must degrade to the placeholder, not escape the property-changed callback and crash
    // the setter/binding (CD-P2K-2 audit). The loader itself is also hardened, so this is defense in depth.
    private static ImageData? LoadFromUri(Uri? uri)
    {
        if (uri is null)
            return null;
        try
        {
            if (ResourceLoader.Default.TryLoadBytes(uri) is not { } bytes)
                return null;
            return new ImageData(bytes, FormatFromUri(uri)); // null RequestedSize ⇒ sized from the decoded pixels
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return null; // a malformed/unloadable source degrades to the placeholder
        }
    }

    private static ImageFormat FormatFromUri(Uri uri)
    {
        // For a relative URI strip any query/fragment before the extension (an absolute URI's AbsolutePath already has).
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        var cut = path.AsSpan().IndexOfAny('?', '#');
        if (cut >= 0)
            path = path[..cut];
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".gif" => ImageFormat.Gif,
            _ => ImageFormat.Png, // best-guess default (Kitty refuses non-PNG, iTerm2 accepts most)
        };
    }
}
