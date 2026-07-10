// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// Design-time metadata captured from the document root's <c>d:</c> attributes
/// (<see cref="XmlnsNamespaces.DesignTime"/> / <see cref="XmlnsNamespaces.DesignTimeCursorial"/>).
/// Runtime loading ignores it entirely; designer hosts read it from
/// <see cref="XamlDocument.DesignInfo"/> to size the preview surface and to construct a
/// design-time data context. Immutable, like the document that carries it.
/// </summary>
public sealed class XamlDesignInfo
{
    internal XamlDesignInfo(int? designWidth, int? designHeight, XamlType? dataContextType)
    {
        DesignWidth = designWidth;
        DesignHeight = designHeight;
        DataContextType = dataContextType;
    }

    /// <summary>The preview surface width in cells (<c>d:DesignWidth</c>), when declared.</summary>
    public int? DesignWidth { get; }

    /// <summary>The preview surface height in cells (<c>d:DesignHeight</c>), when declared.</summary>
    public int? DesignHeight { get; }

    /// <summary>
    /// The resolved design-time data-context type (<c>d:DataContext="vm:SomeViewModel"</c>), when
    /// declared and resolvable. Under the reflection provider the type is activatable; under the
    /// Roslyn provider it exists symbolically (build-time validation without executing user code).
    /// </summary>
    public XamlType? DataContextType { get; }
}
