using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// The shared base for the framework-drawn content presenters (design doc §12 / CD-P2K-1): a primitive that paints
/// primary content directly in <see cref="UIElement.Render"/> (a graphics-protocol image, a cell-rendered chart) and
/// falls back to a <b>placeholder</b> (<see cref="PlaceholderContent"/> + <see cref="PlaceholderTemplate"/>, realized
/// through an internal <see cref="ContentPresenter"/>) when there is nothing to draw. It owns the placeholder
/// plumbing, the <c>:placeholder</c> pseudo-class, and <see cref="UIElement.ClipToBounds"/> (so the drawn content is
/// clipped to the layout rect); subclasses supply the primary-content visibility, measure, and paint.
/// </summary>
public abstract class DrawnContentPresenter : UIElement
{
    /// <summary>The placeholder content shown when no primary content is drawn.</summary>
    public static readonly StyledProperty<object?> PlaceholderContentProperty =
        UIProperty.Register<DrawnContentPresenter, object?>(nameof(PlaceholderContent), changed: OnPlaceholderContentChanged);

    /// <summary>The template for <see cref="PlaceholderContent"/>.</summary>
    public static readonly StyledProperty<DataTemplate?> PlaceholderTemplateProperty =
        UIProperty.Register<DrawnContentPresenter, DataTemplate?>(nameof(PlaceholderTemplate), changed: OnPlaceholderTemplateChanged);

    private readonly ContentPresenter _placeholder = new();

    static DrawnContentPresenter()
    {
        AffectsMeasure<DrawnContentPresenter>(PlaceholderContentProperty, PlaceholderTemplateProperty);
    }

    /// <summary>Creates the presenter (clip-bounded so its drawn content never bleeds past its layout rect).</summary>
    protected DrawnContentPresenter()
    {
        ClipToBounds = true;
        AdoptChild(_placeholder, index: -1);
    }

    /// <inheritdoc cref="PlaceholderContentProperty"/>
    public object? PlaceholderContent { get => GetValue(PlaceholderContentProperty); set => SetValue(PlaceholderContentProperty, value); }

    /// <inheritdoc cref="PlaceholderTemplateProperty"/>
    public DataTemplate? PlaceholderTemplate { get => GetValue(PlaceholderTemplateProperty); set => SetValue(PlaceholderTemplateProperty, value); }

    /// <summary>The internal placeholder host (diagnostic).</summary>
    internal ContentPresenter PlaceholderPresenter => _placeholder;

    /// <summary>Whether the primary (drawn) content is currently shown — false ⇒ the placeholder shows.</summary>
    protected abstract bool IsPrimaryContentVisible { get; }

    /// <summary>Measures the primary content within <paramref name="availableSize"/> (called only when it is visible).</summary>
    protected abstract Size MeasurePrimaryContent(Size availableSize);

    /// <summary>Paints the primary content (called from <see cref="UIElement.Render"/> only when it is visible and the bounds are non-empty).</summary>
    protected abstract void RenderPrimaryContent(RenderContext context);

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (UpdatePlaceholderState())
        {
            _placeholder.Measure(Size.Empty); // collapsed — measures to nothing
            return MeasurePrimaryContent(availableSize);
        }

        _placeholder.Measure(availableSize);
        return _placeholder.DesiredSize;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        // The placeholder fills the rect when visible; when primary content shows it's Collapsed (arrange is a no-op).
        _placeholder.Arrange(new Rect(0, 0, finalSize.Columns, finalSize.Rows));
        return finalSize;
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        if (IsPrimaryContentVisible && !context.Bounds.IsEmpty)
            RenderPrimaryContent(context);
    }

    /// <summary>Flips the placeholder child's visibility + the <c>:placeholder</c> pseudo-class to the current
    /// primary-content-visible state, and returns whether the primary content is visible. Subclasses call this when an
    /// out-of-band signal (e.g. a capability flip) may have changed visibility without a re-measure.</summary>
    protected bool UpdatePlaceholderState()
    {
        var visible = IsPrimaryContentVisible;
        _placeholder.SetCurrentValue(VisibilityProperty, visible ? Visibility.Collapsed : Visibility.Visible);
        PseudoClasses.Set(":placeholder", !visible);
        return visible;
    }

    private static void OnPlaceholderContentChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is DrawnContentPresenter p)
            p._placeholder.SetCurrentValue(ContentPresenter.ContentProperty, newValue);
    }

    private static void OnPlaceholderTemplateChanged(UIObject sender, DataTemplate? oldValue, DataTemplate? newValue)
    {
        if (sender is DrawnContentPresenter p)
            p._placeholder.SetCurrentValue(ContentPresenter.ContentTemplateProperty, newValue);
    }
}
