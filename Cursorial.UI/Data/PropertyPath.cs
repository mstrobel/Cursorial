using Cursorial.UI.Xaml;

namespace Cursorial.UI.Data;

/// <summary>
/// A binding path — WPF's <c>PropertyPath</c> analog (design doc §6.3). Carries the path TEXT and, when built with an
/// <see cref="IPathTypeResolver"/> (the XAML load-time "preprocessor") or a sequence of <see cref="UIProperty"/> steps,
/// the resolved owner types baked in: the path's TYPE QUALIFICATIONS — the <c>(prefix:Type.Member)</c> owners — resolve
/// ONCE at bind-creation against the document's xmlns context, while the property/leaf names stay resolved lazily
/// against the runtime source. A type qualification is NOT assumed to be an attached property — it may be a regular
/// property qualified by owner type for disambiguation/clarity; only the OWNER TYPE resolves early.
/// <para>
/// A bare string implicitly converts to a <see cref="PropertyPath"/> whose (unprefixed) owners resolve via the
/// registry default (<see cref="DefaultPathTypeResolver"/>) at bind time — the code-first form. So
/// <c>new Binding { Path = "Sub.Name" }</c> and <c>new Binding { Path = "(Grid.Row)" }</c> keep working unchanged; a
/// PREFIXED owner (<c>(prefix:Type.Member)</c>) needs the xmlns-aware form the XAML loader builds, or a resolver
/// supplied on the binding. The <see cref="PropertyPath(UIProperty, UIProperty[])"/> ctor is the compile-time-checked
/// form — <c>new PropertyPath(ContentControl.ContentProperty, Control.BackgroundProperty)</c>.
/// </para>
/// </summary>
[Cursorial.Markup.TypeConverter(typeof(PropertyPathConverter))]
public sealed class PropertyPath
{
    private readonly bool _preResolved;   // built with a resolver / properties → _parsed is authoritative, never re-parse
    private BindingPath? _parsed;         // the parsed form (preprocessed, or lazily cached for the string form)
    private IPathTypeResolver? _parsedWith; // the resolver the lazy cache was built with (string form only)

    /// <summary>The empty path (<c>""</c> / <c>"."</c>) — the source object itself.</summary>
    public static readonly PropertyPath Empty = new(string.Empty);

    /// <summary>The path text. <c>""</c> or <c>"."</c> = the source object itself.</summary>
    public string Path { get; }

    /// <summary>Creates a path from <paramref name="path"/>; its type-qualified owners resolve lazily at bind time
    /// (via the binding's resolver, or the registry default) — the code-first / unprefixed form.</summary>
    public PropertyPath(string? path) => Path = path ?? string.Empty;

    /// <summary>PREPROCESSES <paramref name="path"/> now with <paramref name="typeResolver"/>: the type-qualified
    /// owners — <c>(prefix:Type.Member)</c> — are resolved and baked in immediately (property/leaf names stay lazy).
    /// The XAML loader builds this form with an xmlns-aware resolver so a prefix binds the document's root namespaces.
    /// Throws <see cref="FormatException"/> (position-carrying) if a type qualification can't resolve.</summary>
    public PropertyPath(string? path, IPathTypeResolver typeResolver)
    {
        ArgumentNullException.ThrowIfNull(typeResolver);
        Path = path ?? string.Empty;
        _parsed = BindingPath.Parse(Path, typeResolver);
        _parsedWith = typeResolver;
        _preResolved = true;
    }

    /// <summary>The compile-time-checked form: builds a path from one or more resolved <see cref="UIProperty"/> steps —
    /// <c>new PropertyPath(ContentControl.ContentProperty, Control.BackgroundProperty)</c>. Each property is a fully
    /// resolved hop (a typo is a compile error — the property field must exist), so nothing parses or resolves lazily.
    /// A first property is REQUIRED (an empty property path is meaningless — use <see cref="Empty"/>); this also keeps
    /// <c>new PropertyPath(null)</c>/<c>new PropertyPath()</c> from silently resolving here (both are compile errors
    /// that steer to <see cref="Empty"/> / a string path).</summary>
    public PropertyPath(UIProperty property, params UIProperty[] additionalProperties)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(additionalProperties);

        var steps = new UIProperty[additionalProperties.Length + 1];
        steps[0] = property;
        additionalProperties.CopyTo(steps, 1);

        _parsed = BindingPath.FromProperties(steps);
        Path = _parsed.ToString();
        _preResolved = true;
    }

    /// <summary>The code-first ergonomic conversion: a bare string is a lazily-resolved path.</summary>
    public static implicit operator PropertyPath(string? path) => path is null ? Empty : new PropertyPath(path);

    /// <summary>Whether the type qualifications were resolved at construction (the XAML-preprocessed / properties form).</summary>
    public bool IsPreResolved => _preResolved;

    /// <inheritdoc/>
    public override string ToString() => Path;

    /// <summary>
    /// The parsed <see cref="BindingPath"/> — the preprocessed form when built with a resolver, else parsed (and
    /// cached per resolver) with <paramref name="fallbackResolver"/> (the binding's <c>TypeResolver</c>, or the
    /// registry default). Single-UI-thread by contract (invariant 6), so the lazy cache needs no lock.
    /// </summary>
    internal BindingPath ToBindingPath(IPathTypeResolver? fallbackResolver)
    {
        if (_preResolved)
            return _parsed!;

        var resolver = fallbackResolver ?? DefaultPathTypeResolver.Instance;
        if (_parsed is { } cached && ReferenceEquals(_parsedWith, resolver))
            return cached;

        _parsedWith = resolver;
        return _parsed = BindingPath.Parse(Path, resolver);
    }
}

/// <summary>
/// The <see cref="PropertyPath"/> string converter (WPF's <c>PropertyPathConverter</c> analog), declared on
/// <see cref="PropertyPath"/> via <c>[TypeConverter]</c>. NOT context-free: a <c>(prefix:Type.Member)</c> segment
/// needs the document's xmlns table, so a value is never constant-folded and the converter runs at load. It PULLS
/// the xmlns-aware <see cref="IPathTypeResolver"/> from <see cref="XamlValueContext.Services"/> (the loader supplies
/// one bound to the document's root namespaces) and PREPROCESSES the path — the "preprocessor" — so its type
/// qualifications resolve then. With no service present (code-first / no loader context) it produces the lazy form,
/// which resolves at bind time via the registry default. (The common <c>{Binding Path=…}</c> markup form is
/// preprocessed directly in the loader's markup handler, which also holds the resolver.)
/// </summary>
public sealed class PropertyPathConverter : ITypeConverter
{
    /// <inheritdoc/>
    public bool IsContextFree => false;

    /// <inheritdoc/>
    public object ConvertFromString(string text, in XamlValueContext context)
        => context.GetService(typeof(IPathTypeResolver)) is IPathTypeResolver resolver
            ? new PropertyPath(text, resolver)
            : new PropertyPath(text);
}
