// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// A name → element registry (design doc §6.3). The shape is shared with Fork C; the XAML loader
/// and template engine populate scopes, while this subsystem defines how <c>ElementName</c> finds
/// them.
/// </summary>
public interface INameScope
{
    /// <summary>Registers <paramref name="element"/> under <paramref name="name"/>.</summary>
    void Register(string name, object element);

    /// <summary>The element registered under <paramref name="name"/>, or <see langword="null"/>.</summary>
    object? Find(string name);
}

/// <summary>
/// A simple dictionary-backed <see cref="INameScope"/> — the code-first default scope implementation.
/// </summary>
public sealed class NameScopeDictionary : INameScope
{
    private readonly Dictionary<string, object> _names = [];
    private readonly UIElement? _chainHost;

    /// <summary>An isolated scope (the control-template contract — part names never leak, BD21).</summary>
    public NameScopeDictionary() {}

    /// <summary>
    /// A CHAINING scope: a local miss continues through <paramref name="chainHost"/>'s enclosing
    /// scope chain, resolved LAZILY at <see cref="Find"/> time (robust to attach order — the host's
    /// ancestry may complete after this scope is created). This is the data-template doctrine (PD24):
    /// template content is authoring-equivalent to inline elements at the use site, so enclosing
    /// names stay visible; nearest scope wins because the local dictionary is consulted first, and
    /// nesting chains naturally (the enclosing scope may itself chain).
    /// </summary>
    /// <param name="chainHost">The element hosting this scope's content (the presenter).</param>
    public NameScopeDictionary(UIElement chainHost)
    {
        ArgumentNullException.ThrowIfNull(chainHost);
        _chainHost = chainHost;
    }

    /// <inheritdoc/>
    public void Register(string name, object element)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(element);
        _names[name] = element;
    }

    /// <inheritdoc/>
    public object? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_names.GetValueOrDefault(name) is { } local)
            return local;

        return _chainHost is not null ? NameScope.FindEnclosing(_chainHost)?.Find(name) : null;
    }
}

/// <summary>
/// The name-scope attachment points and the guarded nearest-scope walk (design doc §6.3). The
/// document scope is set on document roots; the template scope is set on the TEMPLATED PARENT (Fork
/// C §3.5). The guard is what seals part names: a template scope at ancestor A is consulted only
/// when <c>element.TemplatedParent == A</c>; a document content child of a templated control fails
/// the guard and resolves document names, never part names (BD21).
/// </summary>
public static class NameScope
{
    /// <summary>The document-scope attached property (set by the XAML loader / code-first / DataTemplate item roots).</summary>
    public static readonly AttachedProperty<INameScope?> NameScopeProperty =
        UIProperty.RegisterAttached<UIElement, UIElement, INameScope?>("NameScope");

    /// <summary>The template-scope attached property — set by <c>ApplyTemplate</c> on the templated parent (Fork C §3.5).</summary>
    internal static readonly AttachedProperty<INameScope?> TemplateNameScopeProperty =
        UIProperty.RegisterAttached<UIElement, UIElement, INameScope?>("TemplateNameScope");

    /// <summary>The document scope attached to <paramref name="element"/>, or <see langword="null"/>.</summary>
    public static INameScope? GetNameScope(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(NameScopeProperty);
    }

    /// <summary>Attaches (or clears) the document scope on <paramref name="element"/>.</summary>
    public static void SetNameScope(UIElement element, INameScope? scope)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(NameScopeProperty, scope);
    }

    /// <summary>The template scope attached to <paramref name="element"/> (the templated parent), or <see langword="null"/>.</summary>
    internal static INameScope? GetTemplateNameScope(UIElement element)
        => element.GetValue(TemplateNameScopeProperty);

    /// <summary>Attaches (or clears) the template scope on <paramref name="element"/> (the templated parent).</summary>
    internal static void SetTemplateNameScope(UIElement element, INameScope? scope)
        => element.SetValue(TemplateNameScopeProperty, scope);

    /// <summary>
    /// The guarded nearest-scope walk (design doc §6.3): for each ancestor A from
    /// <paramref name="element"/> up the logical tree, a template scope is consulted only when
    /// <c>element.TemplatedParent == A</c> (template parts see their template's names first); else a
    /// document scope on A wins. Returns the first matching scope, or <see langword="null"/>.
    /// </summary>
    public static INameScope? FindEnclosing(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        for (UIElement? a = element; a is not null; a = a.LogicalParent)
        {
            if (ReferenceEquals(element.TemplatedParent, a) && GetTemplateNameScope(a) is {} templateScope)
                return templateScope;

            if (GetNameScope(a) is { } documentScope)
                return documentScope;
        }

        return null;
    }
}
