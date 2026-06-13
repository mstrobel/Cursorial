using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Cursorial.UI.Controls;
using Cursorial.UI.Data;

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

    private readonly List<string> _defaultClrNamespaces;
    private readonly List<Assembly> _defaultAssemblies;
    private readonly List<Assembly> _additionalAssemblies = new();

    /// <summary>The process-wide default context (the Cursorial UI/Controls/Data map).</summary>
    public static XamlSchemaContext Default { get; } = new();

    /// <summary>Creates a schema context seeded with the default Cursorial map.</summary>
    public XamlSchemaContext()
    {
        // The default xmlns map covers UI/Controls/Data plus Drawing.Media (where brushes/colors/Colors/
        // Brushes live — the XD13 color mini-language and {x:Static Colors.Red}/{x:Static Brushes.Red}).
        _defaultClrNamespaces = new List<string> { "Cursorial.UI", "Cursorial.UI.Controls", "Cursorial.UI.Data", "Cursorial.Drawing.Media" };
        _defaultAssemblies = new List<Assembly>
        {
            typeof(UIElement).Assembly,                  // Cursorial.UI
            typeof(Control).Assembly,                    // Cursorial.UI.Controls (same assembly)
            typeof(Binding).Assembly,                    // Cursorial.UI.Data (same assembly)
            typeof(Drawing.Media.SolidColorBrush).Assembly, // Cursorial.Drawing (brushes / Colors / Brushes)
        };
    }

    /// <summary>
    /// Adds an assembly the <c>using:</c>/<c>clr-namespace:</c> forms (and unqualified default-namespace
    /// type names declared there) can resolve against. The loader registers code-behind / app assemblies
    /// here so a document can name app controls.
    /// </summary>
    public void RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (!_additionalAssemblies.Contains(assembly))
            _additionalAssemblies.Add(assembly);
    }

    /// <summary>
    /// Adds a CLR namespace to the default xmlns map (so unqualified type names declared there resolve
    /// against the default <c>https://cursorial.dev/ui</c> xmlns). The app/host extends the default map
    /// with its own control namespaces; tests register their fixture namespace.
    /// </summary>
    public void RegisterDefaultNamespace(string clrNamespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(clrNamespace);
        if (!_defaultClrNamespaces.Contains(clrNamespace))
            _defaultClrNamespaces.Add(clrNamespace);
    }

    /// <summary>The CLR namespaces mapped to an xmlns URI (for did-you-mean enumeration).</summary>
    public IReadOnlyList<string> GetClrNamespaces(string xmlNamespace)
    {
        if (string.Equals(xmlNamespace, CursorialUiNamespace, StringComparison.Ordinal))
            return _defaultClrNamespaces;
        if (TryDecodeClrNamespace(xmlNamespace, out var clrNs, out _))
            return new[] { clrNs };
        return Array.Empty<string>();
    }

    /// <summary>
    /// Resolves a CLR <see cref="Type"/> for <paramref name="localName"/> in <paramref name="xmlNamespace"/>.
    /// Multiple matches set <paramref name="ambiguous"/> to the candidate full names; a single match
    /// returns it; no match returns null with empty <paramref name="ambiguous"/>.
    /// </summary>
    public Type? Resolve(string xmlNamespace, string localName, out string[] ambiguous)
    {
        ambiguous = Array.Empty<string>();

        if (TryDecodeClrNamespace(xmlNamespace, out var clrNs, out var assemblyName))
            return ResolveInNamespaces(localName, new[] { clrNs }, AssembliesFor(assemblyName), out ambiguous);

        if (string.Equals(xmlNamespace, CursorialUiNamespace, StringComparison.Ordinal))
            return ResolveInNamespaces(localName, _defaultClrNamespaces, AllAssemblies(), out ambiguous);

        return null;
    }

    /// <summary>The known XAML-visible type names in an xmlns URI (Levenshtein did-you-mean source).</summary>
    public string[] GetKnownTypeNames(string xmlNamespace)
    {
        if (!string.Equals(xmlNamespace, CursorialUiNamespace, StringComparison.Ordinal))
            return Array.Empty<string>();

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asm in AllAssemblies())
        {
            foreach (var type in SafeGetExportedTypes(asm))
            {
                if (type.Namespace is { } ns && _defaultClrNamespaces.Contains(ns) && !type.IsGenericTypeDefinition)
                    names.Add(type.Name);
            }
        }
        return names.ToArray();
    }

    // ── Resolution mechanics ─────────────────────────────────────────────────────────────────────

    private Type? ResolveInNamespaces(string localName, IReadOnlyList<string> clrNamespaces, IReadOnlyList<Assembly> assemblies, out string[] ambiguous)
    {
        ambiguous = Array.Empty<string>();
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

    private IReadOnlyList<Assembly> AllAssemblies()
    {
        if (_additionalAssemblies.Count == 0)
            return _defaultAssemblies;
        return _defaultAssemblies.Concat(_additionalAssemblies).Distinct().ToList();
    }

    private IReadOnlyList<Assembly> AssembliesFor(string? assemblyName)
    {
        if (assemblyName is null)
            return AllAssemblies();

        var match = AllAssemblies().FirstOrDefault(a =>
            string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return new[] { match };

        // Try to load the named assembly by simple name from the load context.
        try
        {
            return new[] { Assembly.Load(assemblyName) };
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or BadImageFormatException or System.IO.FileLoadException)
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
            return Array.Empty<Type>();
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
                    var kv = part.Split(new[] { '=' }, 2);
                    if (kv.Length == 2 && string.Equals(kv[0].Trim(), "assembly", StringComparison.OrdinalIgnoreCase))
                        assemblyName = kv[1].Trim();
                }
            }
            return clrNamespace.Length > 0;
        }

        return false;
    }
}
