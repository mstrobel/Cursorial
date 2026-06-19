using Cursorial.Rendering;
using Cursorial.Rendering.Imaging;

using ImageContent = Cursorial.Rendering.Content.Image;

namespace Cursorial.UI.Controls;

/// <summary>
/// A primitive (design doc §12 / CD-P2K-1) that hosts a graphics-protocol image, drawn through the
/// <see cref="Cursorial.Rendering.Content.Image"/> content (the Kitty/iTerm2/Sixel <c>IBufferFragment</c> path — NOT
/// the cell-sampling <c>ImageBrush</c>) via <see cref="RenderContext.DrawContent"/>, which auto-crops the fragment to
/// the active clip. The image renders only when a <see cref="Source"/> is set <b>and</b> the negotiated graphics
/// protocols can actually carry its format; otherwise the inherited placeholder shows. See
/// <see cref="DrawnContentPresenter"/> for the placeholder plumbing, <c>ClipToBounds</c>, and the <c>:placeholder</c>
/// pseudo-class.
/// </summary>
/// <remarks>
/// v1 uses the content's native aspect-preserving fit-within-bounds (sizing is delegated to
/// <see cref="Cursorial.Rendering.Content.Image"/>, so a null / single-axis <see cref="ImageData.RequestedSize"/> is
/// sized from the decoded pixels rather than collapsing to nothing); a <c>Stretch</c> property and a UI-level
/// <c>ImageSource</c>/URI abstraction are deferrals (v1 takes <see cref="ImageData"/> directly). A mid-session
/// renegotiation that flips graphics support re-evaluates on the next layout pass (the presenter subscribes to
/// <see cref="UIApplication.CapabilitiesChanged"/>).
/// </remarks>
public class ImagePresenter : DrawnContentPresenter
{
    /// <summary>The image to display (<see langword="null"/> = none ⇒ the placeholder shows).</summary>
    public static readonly StyledProperty<ImageData?> SourceProperty =
        UIProperty.Register<ImagePresenter, ImageData?>(nameof(Source), changed: OnSourceChanged);

    private ImageContent? _imageContent;  // cached per Source — a fresh content per Render would churn a new image id every frame
    private UIApplication? _subscribedApp; // the app whose CapabilitiesChanged we're subscribed to (for symmetric unsubscribe)

    static ImagePresenter()
    {
        AffectsMeasure<ImagePresenter>(SourceProperty);
    }

    /// <inheritdoc cref="SourceProperty"/>
    public ImageData? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>Whether the image (not the placeholder) is currently shown — a <see cref="Source"/> is set, has bytes,
    /// and the negotiated graphics protocols can carry its format.</summary>
    public bool IsImageVisible
    {
        get
        {
            // Mirrors Cursorial.Rendering.Content.Image.CreateFragment's producer rule (the single place the protocol/
            // format compatibility lives): iTerm2 carries any format; Kitty/Sixel carry PNG only. Gating on protocol
            // presence alone would collapse the placeholder on a Kitty-only terminal with a JPEG source, then show the
            // content's own "[image]" text instead (CD-P2K-1 audit).
            if (Source is not { } s || s.Bytes.IsEmpty)
                return false;
            if (UIApplication.Current?.Capabilities.Output.Graphics is not { } g)
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
    protected override Size MeasurePrimaryContent(Size availableSize)
    {
        var content = _imageContent ??= BuildContent();
        if (content is null)
            return Size.Empty;

        var size = content.Measure(availableSize, UIApplication.Current!.Capabilities.Output); // delegated sizing (handles null/single-axis)
        return FloorVisibleImageSize(size, availableSize); // a visible image must never collapse to a 0 extent on either axis
    }

    /// <inheritdoc/>
    protected override void RenderPrimaryContent(RenderContext context)
    {
        if ((_imageContent ??= BuildContent()) is { } content)
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

    private static void OnSourceChanged(UIObject sender, ImageData? oldValue, ImageData? newValue)
    {
        if (sender is not ImagePresenter p)
            return;
        p._imageContent = newValue is { } src ? new ImageContent(src) : null; // rebuild the cached content (id/diff stability)
        p.InvalidateVisual();
    }
}
