namespace Cursorial.UI.Data;

/// <summary>
/// Internal access to the <c>DataContextProperty</c> from the binding engine (the property is
/// declared on <see cref="UIElement"/> by the S2 partial). Lets the engine reference the inherited
/// styled property without a public type dependency cycle.
/// </summary>
internal static class DataContextSupport
{
    /// <summary>The inherited <c>DataContext</c> styled property — binding's anchoring backbone.</summary>
    public static StyledProperty<object?> DataContextProperty => UIElement.DataContextProperty;
}
