using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

namespace Cursorial.UI;

/// <summary>
/// The framework-owned wrapper the <see cref="WindowManager"/> hosts the application root element in
/// (§8.6 — the root band as the bottom "window" of the modal stack): the root is modal-gated like any
/// blocked window — presses swallowed with the attention pulse, anchored popups closed, captures released —
/// and this host carries the blocked-band <b>look</b>, the same <see cref="ThemeKeys.ObscuredOverlayBrush"/>
/// darkening a <see cref="Window"/>'s chrome applies through its <c>PART_ObscuredOverlay</c>, without
/// stamping classes or overlay elements onto USER content. <see cref="UIApplication.RootElement"/> keeps
/// reflecting the element the application assigned; the host is an implementation detail of the root
/// surface (one extra node above the user root in the visual chain).
/// </summary>
public sealed class RootElementHost : UIElement
{
    private readonly UIElement _content;
    private Border? _overlay; // created on the FIRST modal push — an app that never shows a modal never pays
                              // the overlay element or its theme-brush subscription (test-visible costs)

    internal RootElementHost(UIElement content)
    {
        _content = content;
        AdoptChild(content, index: -1);
    }

    /// <summary>The hosted application root element (the value <see cref="UIApplication.RootElement"/> reflects).</summary>
    public UIElement Content => _content;

    /// <summary>
    /// Applies/clears the modal-blocked look — the darkening overlay plus the <c>obscured</c> class (the
    /// same class a blocked <see cref="Window"/> carries, so application styles can target the state). The
    /// window manager calls this on a modal push and on the last modal's close.
    /// </summary>
    internal void SetObscured(bool obscured)
    {
        if (obscured && _overlay is null)
        {
            // Hit-test transparent: modal gating is FilterMouseEvent policy, never overlay interception.
            // Occludes=false is a LOCAL value: the low-color-tier occlusion theme rules (position-free
            // under Style.RequiresCapabilities) would otherwise flip the scrim to FillOpaque and ERASE the
            // root band's glyphs instead of dimming them — the same self-protection the WindowManager's
            // own chrome carries. The scrim's contract is readable-but-dimmed, on every tier.
            // IsRenderBoundary=true keeps the scrim's alpha blend compositing correctly (develop 9474a3f).
            _overlay = new Border
                       {
                           Name = "PART_ObscuredOverlay",
                           IsHitTestVisible = true,
                           Occludes = false,
                           IsRenderBoundary = true
                       };
            _overlay.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ObscuredOverlayBrush);
            AdoptChild(_overlay, index: -1); // adopted after the content ⇒ paints above it
            InvalidateMeasure();
        }

        if (_overlay is {} overlay)
            overlay.Visibility = obscured ? Visibility.Visible : Visibility.Collapsed;

        if (obscured)
            Classes.Add("obscured");
        else
            Classes.Remove("obscured");
    }

    /// <summary>Releases the hosted content for re-hosting (the window manager's root-swap path).</summary>
    internal void DetachContent()
    {
        DisownChild(_content);

        if (_overlay is { } overlay)
        {
            DisownChild(overlay);
            _overlay = null;
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        _content.Measure(availableSize);
        _overlay?.Measure(availableSize);
        return _content.DesiredSize;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var bounds = new Rect(0, 0, finalSize.Columns, finalSize.Rows);
        _content.Arrange(bounds);
        _overlay?.Arrange(bounds);
        return finalSize;
    }
}
