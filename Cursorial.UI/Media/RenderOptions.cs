using Cursorial.Media;

namespace Cursorial.UI.Media;

public sealed class RenderOptions
{
    public static readonly StyledProperty<IBlendingMode?> BlendingModeProperty =
        UIProperty.RegisterAttached<RenderOptions, UIElement, IBlendingMode?>("BlendingMode");

    static RenderOptions()
    {
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender | PropertyEffects.AffectsComposite,
                                  BlendingModeProperty);
    }
}