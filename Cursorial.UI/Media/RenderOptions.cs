using Cursorial.Drawing;
using Cursorial.Media;

namespace Cursorial.UI.Media;

public sealed class RenderOptions
{
    /// <summary>
    /// The blending mode to be used when compositing an element. Propagates to <see cref="CompositeParameters.Mode"/>.
    /// </summary>
    public static readonly StyledProperty<IBlendingMode?> BlendingModeProperty =
        UIProperty.RegisterAttached<RenderOptions, UIElement, IBlendingMode?>("BlendingMode");

    /// <summary>
    /// Gets the blending mode to be used when compositing the specified <paramref name="element"/>.
    /// </summary>
    public static IBlendingMode? GetBlendingMode(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(BlendingModeProperty);
    }

    /// <summary>
    /// Sets the blending mode to be used when compositing the specified <paramref name="element"/>.
    /// </summary>
    public static void SetBlendingMode(UIElement element, IBlendingMode? mode)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(BlendingModeProperty, mode);
    }

    static RenderOptions()
    {
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender | PropertyEffects.AffectsComposite,
                                  BlendingModeProperty);
    }
}