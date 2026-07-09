using Cursorial.Markup;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Bars;

/// <inheritdoc/>
internal class BarsSelectorTypeResolver : ISelectorTypeResolver
{
    private static readonly ISelectorTypeResolver FallbackResolver = DefaultSelectorTypeResolver.Instance;
    private readonly Dictionary<string, Type> _typeMap = new();

    public BarsSelectorTypeResolver()
    {
        var typeInAssembly = typeof(BarButton);

        IEnumerable<XmlnsDefinitionAttribute> attributes =
            typeInAssembly.Assembly
                 .GetCustomAttributes(typeof(XmlnsDefinitionAttribute), false)
                 .OfType<XmlnsDefinitionAttribute>();

        var namespaces = new HashSet<string>();

        foreach (var attr in attributes)
        {
            if (attr.AssemblyName is null or "" || attr.AssemblyName == typeInAssembly.Assembly.GetName().Name)
                namespaces.Add(attr.ClrNamespace);
        }

        foreach (var type in typeInAssembly.Assembly.GetExportedTypes())
        {
            if (type.Namespace is {} ns && namespaces.Contains(ns))
                _typeMap[type.Name] = type;
        }
    }

    /// <inheritdoc/>
    public Type? Resolve(string typeName, out IReadOnlyList<Type>? ambiguousCandidates)
    { 
        ambiguousCandidates = null;
        
        if (_typeMap.TryGetValue(typeName, out var type))
            return type;
        
        return FallbackResolver.Resolve(typeName, out ambiguousCandidates);
    }
}