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
    internal XamlDesignInfo(int? designWidth, int? designHeight, XamlType? dataContextType,
                            XamlDocument? dataContextContent = null, string? dataContextStaticPath = null)
    {
        DesignWidth = designWidth;
        DesignHeight = designHeight;
        DataContextType = dataContextType;
        DataContextContent = dataContextContent;
        DataContextStaticPath = dataContextStaticPath;
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

    /// <summary>
    /// The element-form design data context (<c>&lt;d:Owner.DataContext&gt;</c> on the root): its single
    /// object child, parsed as a DETACHED fragment document — never part of the runtime graph. A designer
    /// host materializes it with the ordinary loader (<c>XamlLoader.Load(document)</c>) and assigns the
    /// instance as the preview's DataContext. When both forms are declared, the element form wins.
    /// </summary>
    public XamlDocument?  DataContextContent { get; }

    /// <summary>
    /// The <c>{x:Static}</c> INSTANCE form (<c>d:DataContext="{x:Static vm:MyViewModel.DesignInstance}"</c>):
    /// the static member path, unresolved — the frontend never resolves statics. A designer host resolves
    /// it against its loaded assemblies (the loader's <c>IXamlStaticResolver</c> seam) and assigns the
    /// resulting instance. Precedence when multiple forms are declared: element form > static path > type.
    /// </summary>
    public string? DataContextStaticPath { get; }
}
