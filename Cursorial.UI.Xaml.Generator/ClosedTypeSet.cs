using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

namespace Cursorial.UI.Xaml.Generator;

/// <summary>
/// Discovers the set of CLR types a XAML document references, by parsing with a <em>recording</em>
/// metadata provider: the frontend parser calls <see cref="IXamlTypeMetadataProvider.TryGetType"/> for
/// every element it encounters (children too, under error recovery), so recording each query captures
/// the document's full element-name set without needing positions or runtime <c>System.Type</c>s. The
/// recorded <c>(xmlns, localName)</c> pairs are then resolved to <see cref="INamedTypeSymbol"/>s by
/// <see cref="XamlSymbolResolver"/> — the closed type set the generated metadata provider covers.
/// </summary>
internal static class ClosedTypeSet
{
    /// <summary>The distinct element <c>(xmlns, localName)</c> names a document references (parse order).</summary>
    public static IReadOnlyList<(string Namespace, string LocalName)> CollectElementNames(string xaml)
    {
        var recorder = new RecordingProvider();

        try
        {
            XamlFrontend.Parse(xaml,
                               new XamlParseOptions
                               {
                                   MetadataProvider = recorder,
                                   DiagnosticMode = XamlDiagnosticMode.CollectAll,
                               });
        }
        catch
        {
            // The recording provider returns NotFound for everything, so the parser never instantiates;
            // any escape is ignored — we keep whatever names were recorded before it.
        }

        return recorder.Names;
    }

    /// <summary>
    /// Type names a document references OUTSIDE element position — the load-time resolutions:
    /// <c>{x:Type …}</c> arguments, <c>TargetType</c>/<c>DataType</c>/<c>AncestorType</c> attributes (and
    /// the in-extension <c>AncestorType=</c> form), and selector type tokens. The recording parse never
    /// sees these (the frontend defers them to the loader), so without this sweep the generated provider
    /// knows a document's ELEMENTS but not its type REFERENCES — <c>{x:Type vm:LayerModel}</c> and
    /// <c>TargetType="ListBoxItem"</c> resolved under reflection and died under the closed set at
    /// runtime. Text-scan like <see cref="CollectStaticPaths"/>: over-collection (a reference inside a
    /// string, say) resolves-or-drops — never a wrong bake.
    /// </summary>
    public static IReadOnlyList<(string Namespace, string LocalName)> CollectTypeReferenceNames(string xaml)
    {
        var text = Regex.Replace(xaml, "<!--.*?-->", " ", RegexOptions.Singleline);

        var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, "xmlns(?::([A-Za-z_][\\w.-]*))?\\s*=\\s*\\\"([^\\\"]*)\\\""))
            map[m.Groups[1].Success ? m.Groups[1].Value : string.Empty] = m.Groups[2].Value;

        var seen = new HashSet<(string, string)>();
        var names = new List<(string Namespace, string LocalName)>();

        void Add(string reference)
        {
            reference = reference.Trim();
            if (reference.Length == 0 || reference[0] == '{')
                return;

            var colon = reference.IndexOf(':');
            var prefix = colon > 0 ? reference.Substring(0, colon) : string.Empty;
            var local = colon > 0 ? reference.Substring(colon + 1) : reference;
            if (local.Length == 0 || !map.TryGetValue(prefix, out var ns))
                return;

            if (seen.Add((ns, local)))
                names.Add((ns, local));
        }

        foreach (Match m in Regex.Matches(text, "\\{x:Type\\s+([^\\s,}]+)"))
            Add(m.Groups[1].Value);

        // Bare attribute forms ({x:Type …} values are caught above). Type= is x:Array's.
        foreach (Match m in Regex.Matches(text, "\\b(?:TargetType|DataType|AncestorType|Type)\\s*=\\s*\\\"([^\\\"{][^\\\"]*)\\\""))
            Add(m.Groups[1].Value);

        // The in-extension form: {Binding …, AncestorType=Name, …}.
        foreach (Match m in Regex.Matches(text, "\\bAncestorType\\s*=\\s*([A-Za-z_][\\w:.]*)"))
            Add(m.Groups[1].Value);

        // d:DataContext="vm:Type" — the design-time data-context TYPE reference (any design prefix; the
        // sweep is prefix-blind and over-collection resolves-or-drops). Without this, a type referenced
        // ONLY as a d:DataContext misses the closed set and the loader emits CUR2006 (context ignored)
        // under the generated provider while reflection resolves it — designer/runtime drift.
        foreach (Match m in Regex.Matches(text, "[A-Za-z_]\\w*:DataContext\\s*=\\s*\\\"([^\\\"{][^\\\"]*)\\\""))
            Add(m.Groups[1].Value);

        // Dotted setter-property owners: <Setter Property="Control.Template"/> resolves 'Control'
        // at load to find the member.
        foreach (Match m in Regex.Matches(text, "\\bProperty\\s*=\\s*\\\"([A-Za-z_][\\w:]*)\\.[A-Za-z_]"))
            Add(m.Groups[1].Value);

        // Attached-attribute owners (Grid.Row=, bars:Ribbon.ButtonSize=) and dotted
        // property-element owners (<Grid.RowDefinitions>): the owner resolves at load for the
        // member lookup. Binding-path fragments over-match here and resolve-or-drop.
        foreach (Match m in Regex.Matches(text, "[<\\s]([A-Za-z_][\\w:]*)\\.[A-Za-z_]\\w*\\s*="))
            Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(text, "<([A-Za-z_][\\w:]*)\\.[A-Za-z_]"))
            Add(m.Groups[1].Value);

        // Selector type tokens: leading compound identifiers, ns|Type forms, :is() arguments.
        foreach (Match sel in Regex.Matches(text, "\\bSelector\\s*=\\s*\\\"([^\\\"]*)\\\""))
        {
            var selector = sel.Groups[1].Value;
            foreach (Match m in Regex.Matches(selector, "(?:^|[\\s>+~,^(])([A-Za-z_]\\w*)(?:\\|([A-Za-z_]\\w*))?"))
            {
                if (m.Groups[2].Success)
                    Add(m.Groups[1].Value + ":" + m.Groups[2].Value);
                else
                    Add(m.Groups[1].Value);
            }

            foreach (Match m in Regex.Matches(selector, ":is\\(\\s*([^)\\s]+)\\s*\\)"))
                Add(m.Groups[1].Value.Replace('|', ':'));
        }

        return names;
    }

    /// <summary>
    /// The markup-extension NAMES a document uses in extension position (<c>{Icon …}</c>, <c>{my:Foo …}</c>) —
    /// raw, WITHOUT the "Extension" suffix. The recording parse cannot see these (an unresolved element type
    /// never reaches attribute-value extension parsing), so they are swept textually. The consumer must resolve
    /// them exactly like the parser probes at runtime: the "Extension"-suffixed form FIRST, the bare name as the
    /// fallback — so an extension's sister class (<c>Icon</c> beside <c>IconExtension</c>) is never baked from
    /// an extension usage. Over-collection ("{Binding" inside prose, intrinsics) resolves-or-drops.
    /// </summary>
    public static IReadOnlyList<(string Namespace, string LocalName)> CollectMarkupExtensionNames(string xaml)
    {
        var text = Regex.Replace(xaml, "<!--.*?-->", " ", RegexOptions.Singleline);

        var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, "xmlns(?::([A-Za-z_][\\w.-]*))?\\s*=\\s*\\\"([^\\\"]*)\\\""))
            map[m.Groups[1].Success ? m.Groups[1].Value : string.Empty] = m.Groups[2].Value;

        var seen = new HashSet<(string, string)>();
        var names = new List<(string Namespace, string LocalName)>();

        // Every "{Name" opener — "{}" (the escape) and "{{" never name an extension.
        foreach (Match m in Regex.Matches(text, "\\{\\s*([A-Za-z_][\\w:.]*)"))
        {
            var reference = m.Groups[1].Value;
            var colon = reference.IndexOf(':');
            var prefix = colon > 0 ? reference.Substring(0, colon) : string.Empty;
            var local = colon > 0 ? reference.Substring(colon + 1) : reference;
            if (local.Length == 0 || local.Contains('.') || !map.TryGetValue(prefix, out var ns))
                continue;

            if (seen.Add((ns, local)))
                names.Add((ns, local));
        }

        return names;
    }

    /// <summary>
    /// The distinct <c>{x:Static Type.Member}</c> member-path tokens a document references (a text scan, robust
    /// to parse failures + nesting — <c>{Binding …, Converter={x:Static C.D}}</c> yields <c>C.D</c>). The
    /// generated provider bakes a <c>TryResolveStatic</c> switch over these. Over-collection (a path inside a
    /// comment, say) is harmless — an extra unreachable switch case. The token after <c>{x:Static</c> runs to
    /// whitespace, <c>,</c> (the argument separator), or <c>}</c>; a surrounding single-quote pair and <c>\</c>
    /// escapes are then unwrapped, so the result matches the <see cref="XamlStaticReference"/> path the
    /// markup-extension parser hands the loader (<c>{x:Static 'Brushes.Red'}</c> → <c>Brushes.Red</c>).
    /// </summary>
    public static IReadOnlyList<string> CollectStaticPaths(string xaml)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        const string marker = "{x:Static";

        int i = 0;
        while ((i = xaml.IndexOf(marker, i, System.StringComparison.Ordinal)) >= 0)
        {
            int p = i + marker.Length;
            i = p;

            // Require whitespace after the marker so "{x:StaticFoo" / "{x:StaticResource …}" don't match.
            if (p >= xaml.Length || !char.IsWhiteSpace(xaml[p]))
                continue;

            while (p < xaml.Length && char.IsWhiteSpace(xaml[p]))
                p++;

            int start = p;
            // Stop at the argument separator (',') as well as '}' / whitespace — matching the runtime grammar
            // (a bare positional ends at ',' / '}'), so a single-positional x:Static with extra args isn't captured
            // with a trailing comma.
            while (p < xaml.Length && xaml[p] != '}' && xaml[p] != ',' && !char.IsWhiteSpace(xaml[p]))
                p++;

            if (p > start)
            {
                var path = CleanStaticArg(xaml.Substring(start, p - start));
                if (path.Length > 0 && seen.Add(path))
                    paths.Add(path);
            }

            i = p;
        }

        return paths;
    }

    // Unwraps a scanned positional token the way the markup-extension parser does: strips a surrounding single-quote
    // pair (the quoted-positional form) then resolves '\' escapes — so {x:Static 'Brushes.Red'} / {x:Static Foo\.Bar}
    // yield the same Brushes.Red / Foo.Bar the loader resolves at runtime (no generated-vs-reflection drift).
    private static string CleanStaticArg(string token)
    {
        if (token.Length >= 2 && token[0] == '\'' && token[token.Length - 1] == '\'')
            token = token.Substring(1, token.Length - 2);

        if (token.IndexOf('\\') < 0)
            return token;

        var sb = new System.Text.StringBuilder(token.Length);
        for (int k = 0; k < token.Length; k++)
        {
            if (token[k] == '\\' && k + 1 < token.Length)
                k++;
            sb.Append(token[k]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The distinct <c>{x:Static}</c> references across <paramref name="texts"/>, each resolved to a baked
    /// <c>global::FullType.Member</c> C# expression for the generated provider's <c>TryResolveStatic</c> switch.
    /// Unresolvable paths are dropped (the runtime then misses identically — no drift). The single source both
    /// <c>XamlSourceGenerator.EmitProvider</c> and the dual-run test use, so they can't diverge.
    /// </summary>
    public static List<(string Path, string Expr)> CollectStatics(XamlSymbolResolver resolver, IEnumerable<string> texts)
    {
        var statics = new List<(string Path, string Expr)>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var text in texts)
            foreach (var path in CollectStaticPaths(text))
                if (seen.Add(path) && ResolveStaticExpr(resolver, path) is { } expr)
                    statics.Add((path, expr));

        return statics;
    }

    /// <summary>
    /// Resolves an <c>{x:Static "Type.Member"}</c> path to a baked <c>global::FullType.Member</c> expression, or
    /// null when it can't resolve through the default UI xmlns. Mirrors <c>ReflectionXamlMetadata.TryResolveStatic</c>
    /// EXACTLY (default-xmlns scope; the xmlns-prefixed widening is P1C): a public static field / readable property
    /// declared DIRECTLY on the type — NOT inherited, because the reflection side uses
    /// <c>GetField/GetProperty(Public|Static)</c> with no <c>FlattenHierarchy</c>, so baking an inherited static
    /// would resolve under the generated provider but throw <c>MemberNotFound</c> under reflection (X174 drift).
    /// </summary>
    public static string? ResolveStaticExpr(XamlSymbolResolver resolver, string memberPath)
    {
        int dot = memberPath.LastIndexOf('.');
        if (dot <= 0)
            return null;

        var typeName = memberPath.Substring(0, dot);
        var memberName = memberPath.Substring(dot + 1);

        var type = resolver.Resolve(XamlSymbolResolver.CursorialUiNamespace, typeName, out _);
        if (type is null)
            return null;

        // Directly-declared members only (GetMembers does not include inherited) — matches reflection's no-FlattenHierarchy.
        foreach (var m in type.GetMembers(memberName))
        {
            var ok = m switch
            {
                IFieldSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public } => true,
                IPropertySymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public, GetMethod: not null } => true,
                _ => false,
            };

            if (ok)
                return $"{type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{memberName}";
        }

        return null;
    }

    /// <summary>
    /// Records every <c>(xmlns, localName)</c> the parser asks about and always reports a miss — so the
    /// parser keeps descending (recording children) without ever building a tree.
    /// </summary>
    private sealed class RecordingProvider : IXamlTypeMetadataProvider
    {
        private readonly HashSet<(string, string)> _seen = new();
        private readonly List<(string, string)> _names = new();

        public IReadOnlyList<(string Namespace, string LocalName)> Names => _names;

        public XamlTypeResolution TryGetType(string xmlNamespace, string localName)
        {
            if (_seen.Add((xmlNamespace, localName)))
                _names.Add((xmlNamespace, localName));

            return XamlTypeResolution.NotFound();
        }

        public string[] GetClrNamespaces(string xmlNamespace) => System.Array.Empty<string>();

        public string[] GetKnownTypeNames(string xmlNamespace) => System.Array.Empty<string>();

        public string[] GetKnownMemberNames(IXamlType type) => System.Array.Empty<string>();
    }
}