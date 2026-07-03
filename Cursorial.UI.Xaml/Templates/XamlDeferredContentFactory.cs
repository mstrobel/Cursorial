using Cursorial.UI.Controls;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The X3 deferred-content factory (matrix §13): wraps a node-graph slice as a
/// <see cref="XamlTemplateContent"/>. Type-contract deferral (matrix XD6) — a member typed
/// <c>ITemplateContent</c> captures its child slice (no instantiation at load); the existing
/// <see cref="ControlTemplate.Instantiate"/> / <see cref="DataTemplate.Build"/> expansion drives the
/// per-target instantiation through <see cref="ITemplateContent.Build"/>.
/// </summary>
internal sealed class XamlDeferredContentFactory(XamlLoaderOptions defaultOptions) : IXamlDeferredContentFactory
{
    private readonly XamlLoaderOptions _defaultOptions = defaultOptions;

    public object Create(XamlDocument doc, int sliceHead, XamlLoaderOptions? options, Uri? source, CapturedScopeChain capturedScope)
        => new XamlTemplateContent(doc, sliceHead, options ?? _defaultOptions, source, capturedScope);
}

/// <summary>
/// A node-graph-backed <see cref="ITemplateContent"/> (matrix X153/X154): each <see cref="Build"/>
/// instantiates a FRESH element subtree from the captured slice (distinct instances; shared folded
/// constants — XD20), registering <c>x:Name</c> parts into the per-build template name scope (matrix
/// X155) and resolving <c>{StaticResource}</c> against the captured definition-site lexical chain plus
/// the template's own resources (lexical capture — matrix X162/X164). <c>{TemplateBinding}</c> inside
/// resolves against <see cref="TemplateBuildContext.TemplatedParent"/> (matrix X160).
/// </summary>
internal sealed class XamlTemplateContent : ITemplateContent
{
    private readonly XamlDocument _doc;
    private readonly int _sliceHead;
    private readonly XamlLoaderOptions _options;
    private readonly Uri? _source;
    private readonly CapturedScopeChain _capturedScope;

    internal XamlTemplateContent(XamlDocument doc, int sliceHead, XamlLoaderOptions options, Uri? source, CapturedScopeChain capturedScope)
    {
        _doc = doc;
        _sliceHead = sliceHead;
        _options = options;
        _source = source;
        _capturedScope = capturedScope;
    }

    /// <inheritdoc/>
    public UIElement Build(TemplateBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var builder = new XamlObjectGraphBuilder(_doc, _options, _source)
        {
            TemplateContext = context,
            CapturedTemplateScope = _capturedScope,
        };

        object root = builder.BuildTemplateSlice(_sliceHead);
        if (root is not UIElement element)
            throw new InvalidOperationException(
                $"The template's root element instantiated to a '{root.GetType().Name}', not a UIElement.");
        return element;
    }
}
