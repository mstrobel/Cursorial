namespace Cursorial.UI.Controls;

public sealed class DeferredContent(Func<UIElement> factory)
{
    private UIElement? _cachedRealization;

    public UIElement Realize() => _cachedRealization ??= factory();
}