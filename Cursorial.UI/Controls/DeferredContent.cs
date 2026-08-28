namespace Cursorial.UI.Controls;

public sealed class DeferredContent(Func<object?> factory, bool cache = true)
{
    private object? _cachedRealization;

    public object? Realize()
    {
        if (cache is false) return factory();
        return _cachedRealization ??= factory();
    }
}