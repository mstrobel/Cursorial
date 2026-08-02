// ReSharper disable CheckNamespace

using System.Globalization;

using Cursorial.UI.Data;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The ambient services handed to a custom <see cref="MarkupExtension.ProvideValue"/> call (matrix
/// X125): the provide-value target, the document root, the source line/column, the lexical resource
/// stack, and the enclosing name scope. Implements <see cref="IServiceProvider"/> over the five seam
/// interfaces.
/// </summary>
internal sealed class XamlServiceProvider :
    IServiceProvider,
    IProvideValueTarget,
    IRootObjectProvider,
    IXamlLineInfo,
    IAmbientResources,
    INameScopeProvider,
    IXamlValueContextProvider
{
    private readonly XamlObjectGraphBuilder _builder;
    private readonly XamlResourceScopeStack _scopes;
    private readonly object? _targetObject;
    private readonly XamlMember? _targetMember;

    // A standalone element-form extension (a dictionary/collection entry) has no target object/member.
    internal XamlServiceProvider(
        XamlObjectGraphBuilder builder,
        object? targetObject,
        XamlMember? targetMember,
        XamlResourceScopeStack scopes,
        int line,
        int column)
    {
        _builder = builder;
        _targetObject = targetObject;
        _targetMember = targetMember;
        _scopes = scopes;
        LineNumber = line;
        LinePosition = column;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IProvideValueTarget)) return this;
        if (serviceType == typeof(IRootObjectProvider)) return this;
        if (serviceType == typeof(IXamlLineInfo)) return this;
        if (serviceType == typeof(IAmbientResources)) return this;
        if (serviceType == typeof(INameScopeProvider)) return this;
        if (serviceType == typeof(IPathTypeResolver)) return _builder.PathTypeResolver;
        return null;
    }

    // IProvideValueTarget
    public object? TargetObject => _targetObject;
    public object? TargetProperty => _targetMember?.Property ?? _targetMember;

    // IRootObjectProvider
    public object? RootObject => _builder.RootObject;

    // IXamlValueContextProvider
    public XamlValueContext Context => CreateXamlValueContext();

    // IXamlLineInfo
    public int LineNumber { get; }

    public int LinePosition { get; }

    // IAmbientResources

    public bool TryFindResource(object key, out object? value)
        => _scopes.TryResolve(key, out value);

    // INameScopeProvider
    public INameScope NameScope => _builder.DocumentScope;

    private XamlValueContext CreateXamlValueContext()
    {
        return new XamlValueContext(culture: XamlLoader.Current?.Options.ConverterCulture ??
                                             CultureInfo.InvariantCulture,
                                    targetMember: _targetMember,
                                    targetType: _targetMember?.SystemType()!,
                                    source: _builder.Source,
                                    line: LineNumber,
                                    column: LinePosition);
    }
}
