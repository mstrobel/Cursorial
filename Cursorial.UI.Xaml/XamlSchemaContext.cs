using System.Reflection;

using Cursorial.Media;
using Cursorial.Rendering.Media;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The xmlns→CLR resolution context (matrix XD5): maps a URI xmlns (the default Cursorial map covering
/// <c>Cursorial.UI</c> + <c>Cursorial.UI.Controls</c> + <c>Cursorial.UI.Data</c>) or a
/// <c>using:</c>/<c>clr-namespace:</c> form to a set of <c>(assembly, CLR namespace)</c> probes, then
/// finds the CLR type for a local name within that set. Two matches is an ambiguity the provider
/// surfaces; no match is a miss. The context is shared across documents and immutable after construction.
/// </summary>
public sealed class XamlSchemaContext
{
    /// <summary>The default Cursorial UI xmlns URI.</summary>
    public const string CursorialUiNamespace = "https://cursorial.dev/ui";

    /// <summary>The intrinsics (<c>x:</c>) xmlns URI.</summary>
    public const string IntrinsicsNamespace = "https://cursorial.dev/xaml";

    // Guards every read/write of the mutable registration lists below. The Default instance is
    // process-wide and documented as shared across documents/hosts; both the loader (Resolve /
    // GetKnownTypeNames, on whatever thread parses a document) and registration paths (an app/host or
    // a parallel test fixture calling RegisterAssembly / RegisterDefaultNamespace) touch these lists,
    // so a List<T>.Add concurrent with an in-flight enumeration would corrupt resolution. The critical
    // sections are tiny and registration is rare, so a single monitor is the right tool.
    private readonly object _gate = new();
    // xmlns URI → the CLR namespaces mapped to it by [assembly: XmlnsDefinition] across the seed + registered
    // assemblies. The default https://cursorial.dev/ui URI is just one key; a library (or app) that declares its own
    // URI (e.g. https://myapp.dev/controls) resolves the same way (WPF/Avalonia xmlns-definition parity), not only
    // via clr-namespace:/using:.
    private readonly Dictionary<string, List<string>> _namespaceMap = new(StringComparer.Ordinal);
    private readonly List<Assembly> _defaultAssemblies;
    private readonly List<Assembly> _additionalAssemblies = [];

    /// <summary>The process-wide default context (the Cursorial UI/Controls/Data map).</summary>
    public static XamlSchemaContext Default { get; } = new();

    /// <summary>Creates a schema context seeded with the default Cursorial map.</summary>
    public XamlSchemaContext()
    {
        // The seed assemblies that form the default https://cursorial.dev/ui map. Their CLR namespaces are NOT
        // hardcoded here — each assembly DECLARES them via [assembly: XmlnsDefinition], discovered below (and an app
        // assembly registered later auto-extends the map the same way — #108). Cursorial.UI declares UI / Controls /
        // Data / Input / Themes (ThemeKeys, so {x:Static ThemeKeys.SurfaceBrush} resolves unprefixed); Cursorial.Drawing
        // declares Drawing.Media (the XD13 color mini-language + {x:Static Colors.Red}/{x:Static Brushes.Red}).
        _defaultAssemblies =
        [
            typeof(UIElement).Assembly,                     // Cursorial.UI (UI / Controls / Data / Input / Themes)
            typeof(Color).Assembly,                         // Cursorial.Media (Output), Cursorial.Text (Text)
            typeof(IBrush).Assembly,                        // Cursorial.Rendering (Rendering.Media)
            typeof(Drawing.Media.Pen).Assembly,             // Cursorial.Drawing (Drawing.Media)
            typeof(MarkupExtension).Assembly                // Cursorial.UI.Xaml — only its Markup namespace ({Icon …})
        ];

        foreach (var assembly in _defaultAssemblies)
            DiscoverNamespaces(assembly, _namespaceMap);
    }

    /// <summary>
    /// Adds to <paramref name="map"/> every <c>(xmlns URI → CLR namespace)</c> pair the assembly declares via
    /// <c>[assembly: XmlnsDefinition]</c> — for ANY URI, not just the default Cursorial one. Matched on the
    /// attribute's SIMPLE NAME (the <c>Cursorial.Shared</c> markup-attribute pattern — an app may use this attribute
    /// or any equivalently named one), read through <see cref="CustomAttributeData"/> so no compile-time reference to
    /// the attribute type is needed.
    /// </summary>
    private static void DiscoverNamespaces(Assembly assembly, Dictionary<string, List<string>> map)
    {
        foreach (var data in SafeGetCustomAttributesData(assembly))
        {
            if (data.AttributeType.Name != "XmlnsDefinitionAttribute" || data.ConstructorArguments.Count < 2)
                continue;
            if (data.ConstructorArguments[0].Value is not string uri || uri.Length == 0)
                continue;
            if (data.ConstructorArguments[1].Value is string clrNs && clrNs.Length > 0)
                AddNamespace(map, uri, clrNs);
        }
    }

    private static void AddNamespace(Dictionary<string, List<string>> map, string xmlNamespace, string clrNamespace)
    {
        if (!map.TryGetValue(xmlNamespace, out var list))
            map[xmlNamespace] = list = [];
        if (!list.Contains(clrNamespace))
            list.Add(clrNamespace);
    }

    private static IList<CustomAttributeData> SafeGetCustomAttributesData(Assembly assembly)
    {
        try
        {
            return assembly.GetCustomAttributesData();
        }
        catch (Exception ex) when (ex is NotSupportedException or TypeLoadException)
        {
            return [];
        }
    }

    /// <summary>
    /// Adds an assembly the <c>using:</c>/<c>clr-namespace:</c> forms (and unqualified default-namespace
    /// type names declared there) can resolve against. The loader registers code-behind / app assemblies
    /// here so a document can name app controls.
    /// </summary>
    public void RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (_gate)
        {
            if (!_additionalAssemblies.Contains(assembly))
                _additionalAssemblies.Add(assembly);

            // Auto-extend the map from the assembly's own [assembly: XmlnsDefinition] declarations (every URI it
            // declares), so an app assembly's unprefixed types resolve without a separate RegisterDefaultNamespace
            // call (#108), and a library that ships its own xmlns URI becomes resolvable once its assembly is registered.
            DiscoverNamespaces(assembly, _namespaceMap);
        }
    }

    /// <summary>
    /// Adds an assembly the <c>using:</c>/<c>clr-namespace:</c> forms (and unqualified default-namespace
    /// type names declared there) can resolve against. The loader registers code-behind / app assemblies
    /// here so a document can name app controls.
    /// </summary>
    public void RegisterAssembly(Type typeInAssembly)
    {
        ArgumentNullException.ThrowIfNull(typeInAssembly);
        RegisterAssembly(typeInAssembly.Assembly);
    }

    /// <summary>
    /// Adds a CLR namespace to the default xmlns map (so unqualified type names declared there resolve
    /// against the default <c>https://cursorial.dev/ui</c> xmlns). The app/host extends the default map
    /// with its own control namespaces; tests register their fixture namespace.
    /// </summary>
    public void RegisterDefaultNamespace(string clrNamespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(clrNamespace);
        lock (_gate)
            AddNamespace(_namespaceMap, CursorialUiNamespace, clrNamespace);
    }

    /// <summary>The CLR namespaces mapped to an xmlns URI (for did-you-mean enumeration).</summary>
    public IReadOnlyList<string> GetClrNamespaces(string xmlNamespace)
    {
        if (SnapshotNamespaces(xmlNamespace) is { Count: > 0 } mapped)
            return mapped;
        if (TryDecodeClrNamespace(xmlNamespace, out var clrNs, out _))
            return [clrNs];
        return [];
    }

    /// <summary>
    /// Resolves a CLR <see cref="Type"/> for <paramref name="localName"/> in <paramref name="xmlNamespace"/>.
    /// Multiple matches set <paramref name="ambiguous"/> to the candidate full names; a single match
    /// returns it; no match returns null with empty <paramref name="ambiguous"/>.
    /// </summary>
    public Type? Resolve(string xmlNamespace, string localName, out string[] ambiguous)
    {
        ambiguous = [];

        if (TryDecodeClrNamespace(xmlNamespace, out var clrNs, out var assemblyName))
            return ResolveInNamespaces(localName, [clrNs], AssembliesFor(assemblyName), out ambiguous);

        // XAML2009 built-in (CLR basic) types live in the x: (intrinsics) namespace: x:String → System.String,
        // x:Int32 → System.Int32, etc. (WPF/Avalonia parity) — used as type tokens ({x:Type x:String},
        // x:Array Type="x:Int32") and as element-valued literals (<x:Double>1.5</x:Double>).
        if (string.Equals(xmlNamespace, IntrinsicsNamespace, StringComparison.Ordinal) && BuiltInType(localName) is { } builtin)
            return builtin;

        // Any URI declared via [assembly: XmlnsDefinition] (the default Cursorial URI is just one such key).
        if (SnapshotNamespaces(xmlNamespace) is { Count: > 0 } mapped)
            return ResolveInNamespaces(localName, mapped, AllAssemblies(), out ambiguous);

        return null;
    }

    /// <summary>The XAML2009 built-in (CLR basic) type for an intrinsic-namespace local name, or <see langword="null"/>.</summary>
    public static Type? BuiltInType(string localName) => localName switch
    {
        "Object"   => typeof(object),
        "Boolean"  => typeof(bool),
        "Byte"     => typeof(byte),
        "SByte"    => typeof(sbyte),
        "Char"     => typeof(char),
        "Decimal"  => typeof(decimal),
        "Single"   => typeof(float),
        "Double"   => typeof(double),
        "Int16"    => typeof(short),
        "Int32"    => typeof(int),
        "Int64"    => typeof(long),
        "UInt16"   => typeof(ushort),
        "UInt32"   => typeof(uint),
        "UInt64"   => typeof(ulong),
        "String"   => typeof(string),
        "TimeSpan" => typeof(TimeSpan),
        "Uri"      => typeof(Uri),
        _          => null,
    };

    private static readonly HashSet<Type> BuiltInClrTypes =
    [
        typeof(object), typeof(bool), typeof(byte), typeof(sbyte), typeof(char), typeof(decimal),
        typeof(float), typeof(double), typeof(short), typeof(int), typeof(long), typeof(ushort),
        typeof(uint), typeof(ulong), typeof(string), typeof(TimeSpan), typeof(Uri),
    ];

    /// <summary>True for a XAML2009 built-in (CLR basic) type — the set a primitive element initializes from
    /// its content text (<c>&lt;x:Int32&gt;5&lt;/x:Int32&gt;</c>, XD28).</summary>
    public static bool IsBuiltInType(Type type) => BuiltInClrTypes.Contains(type);

    private static readonly string[] BuiltInTypeNames =
    [
        "Object", "Boolean", "Byte", "SByte", "Char", "Decimal", "Single", "Double",
        "Int16", "Int32", "Int64", "UInt16", "UInt32", "UInt64", "String", "TimeSpan", "Uri",
    ];

    /// <summary>The known XAML-visible type names in an xmlns URI (Levenshtein did-you-mean source).</summary>
    public string[] GetKnownTypeNames(string xmlNamespace)
    {
        // The intrinsics namespace carries the XAML2009 built-ins (x:String, x:Int32, …) —
        // resolved by name in ResolveType; enumerable here for did-you-mean and completion.
        if (string.Equals(xmlNamespace, IntrinsicsNamespace, StringComparison.Ordinal))
            return (string[])BuiltInTypeNames.Clone();

        var clrNamespaces = (IReadOnlyList<string>)SnapshotNamespaces(xmlNamespace);
        var assemblies = AllAssemblies();
        if (clrNamespaces.Count == 0)
        {
            // clr-namespace:/using: URIs are self-describing rather than registered, same as
            // GetClrNamespaces; honor an assembly= qualifier so the sweep stays scoped.
            if (!TryDecodeClrNamespace(xmlNamespace, out var clrNamespace, out var assemblyName))
                return [];
            clrNamespaces = [clrNamespace];
            assemblies = AssembliesFor(assemblyName);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asm in assemblies)
        {
            foreach (var type in SafeGetExportedTypes(asm))
            {
                if (type.Namespace is { } ns && clrNamespaces.Contains(ns) && !type.IsGenericTypeDefinition)
                    names.Add(type.Name);
            }
        }
        return names.ToArray();
    }

    // ── Resolution mechanics ─────────────────────────────────────────────────────────────────────

    private Type? ResolveInNamespaces(string localName, IReadOnlyList<string> clrNamespaces, IReadOnlyList<Assembly> assemblies, out string[] ambiguous)
    {
        ambiguous = [];
        var matches = new List<Type>();

        foreach (var clrNs in clrNamespaces)
        {
            foreach (var asm in assemblies)
            {
                var type = asm.GetType($"{clrNs}.{localName}", throwOnError: false, ignoreCase: false);
                if (type is not null && !matches.Contains(type))
                    matches.Add(type);
            }
        }

        if (matches.Count == 0)
            return null;
        if (matches.Count == 1)
            return matches[0];

        ambiguous = matches.Select(static t => t.FullName ?? t.Name).ToArray();
        return null;
    }

    // Snapshots the registration lists under the gate so callers iterate a stable copy after the
    // monitor is released — registration mutates these lists and reflection (the slow part) must not
    // hold the lock.
    private List<string> SnapshotNamespaces(string xmlNamespace)
    {
        lock (_gate)
            return _namespaceMap.TryGetValue(xmlNamespace, out var list) ? [..list] : [];
    }

    private IReadOnlyList<Assembly> AllAssemblies()
    {
        lock (_gate)
        {
            if (_additionalAssemblies.Count == 0)
                return _defaultAssemblies.ToArray();
            return _defaultAssemblies.Concat(_additionalAssemblies).Distinct().ToArray();
        }
    }

    private IReadOnlyList<Assembly> AssembliesFor(string? assemblyName)
    {
        if (assemblyName is null)
            return AllAssemblies();

        var match = AllAssemblies().FirstOrDefault(a =>
            string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return [match];

        // Try to load the named assembly by simple name from the load context.
        try
        {
            return [Assembly.Load(assemblyName)];
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or FileLoadException)
        {
            return AllAssemblies();
        }
    }

    private static IEnumerable<Type> SafeGetExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or NotSupportedException)
        {
            return [];
        }
    }

    internal static bool TryDecodeClrNamespace(string ns, out string clrNamespace, out string? assemblyName)
    {
        clrNamespace = string.Empty;
        assemblyName = null;

        const string usingPrefix = "using:";
        const string clrPrefix = "clr-namespace:";

        if (ns.StartsWith(usingPrefix, StringComparison.Ordinal))
        {
            clrNamespace = ns.Substring(usingPrefix.Length).Trim();
            return clrNamespace.Length > 0;
        }

        if (ns.StartsWith(clrPrefix, StringComparison.Ordinal))
        {
            var body = ns.Substring(clrPrefix.Length);
            int semi = body.IndexOf(';');
            if (semi < 0)
            {
                clrNamespace = body.Trim();
            }
            else
            {
                clrNamespace = body.Substring(0, semi).Trim();
                foreach (var part in body.Substring(semi + 1).Split(';'))
                {
                    var kv = part.Split(['='], 2);
                    if (kv.Length == 2 && string.Equals(kv[0].Trim(), "assembly", StringComparison.OrdinalIgnoreCase))
                        assemblyName = kv[1].Trim();
                }
            }
            return clrNamespace.Length > 0;
        }

        return false;
    }
}
