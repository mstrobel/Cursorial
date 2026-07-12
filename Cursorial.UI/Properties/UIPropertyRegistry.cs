// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The process-global property registry: an append-only list indexed by dense
/// <see cref="UIProperty.Id"/>, a <c>(Type, string)</c> lookup for XAML/name resolution (fed by
/// <c>Register*</c> and <c>AddOwner</c>), the short-name → owners index backing
/// <see cref="FindOwnersByShortName"/> (ledger A15 — <c>(Grid.Row)</c> binding-path resolution with
/// ambiguity detection), and the cached inheriting-property id set the reparent diff enumerates.
/// </summary>
/// <remarks>
/// Registration happens in static constructors (the XAML loader forces them before name lookup);
/// mutations are locked because static constructors may run on arbitrary threads, but the registry
/// is cold-path only — per-instance value access never touches it.
/// </remarks>
internal static class UIPropertyRegistry
{
    private static readonly Lock Gate = new();
    private static readonly List<UIProperty> ById = [];
    private static readonly Dictionary<(Type Owner, string Name), UIProperty> ByOwnerAndName = [];
    private static readonly Dictionary<string, List<Type>> OwnersByShortName = [];
    private static readonly Dictionary<string, List<Type>> OwnerTypesBySimpleName = [];
    private static int[]? _inheritingPropertyIds;
    private static UIProperty[]? _inheritingProperties;

    /// <summary>
    /// Assigns the next dense id, records the property, and registers its declaring
    /// <c>(OwnerType, Name)</c> lookup entry. Throws <see cref="ArgumentException"/> on a duplicate
    /// (name, owner) pair — duplicate detection runs before the id is consumed.
    /// </summary>
    internal static int Register(UIProperty property)
    {
        lock (Gate)
        {
            AddOwnerEntry(property, property.OwnerType);
            var id = ById.Count;
            ById.Add(property);
            _inheritingPropertyIds = null;
            _inheritingProperties = null;
            return id;
        }
    }

    /// <summary>
    /// Registers an additional owner type for an existing property (the <c>AddOwner</c> path).
    /// Throws <see cref="ArgumentException"/> when the owner already has a property of that name.
    /// </summary>
    internal static void AddOwner(UIProperty property, Type ownerType)
    {
        ArgumentNullException.ThrowIfNull(ownerType);
        lock (Gate)
        {
            AddOwnerEntry(property, ownerType);
        }
    }

    /// <summary>
    /// The <c>(Type, string)</c> lookup: finds the property registered under <paramref name="name"/>
    /// for <paramref name="ownerType"/> or the nearest base type with a registration (so XAML on a
    /// derived element finds its base's properties). Returns <see langword="null"/> when no type in
    /// the chain registered the name.
    /// </summary>
    internal static UIProperty? Find(Type ownerType, string name)
    {
        ArgumentNullException.ThrowIfNull(ownerType);
        ArgumentNullException.ThrowIfNull(name);
        lock (Gate)
        {
            for (var type = ownerType; type is not null; type = type.BaseType)
            {
                if (ByOwnerAndName.TryGetValue((type, name), out var property))
                    return property;
            }

            return null;
        }
    }

    /// <summary>A registration-ordered snapshot of every registered property (the <see cref="UIProperties.All"/> backing).</summary>
    internal static IReadOnlyList<UIProperty> Snapshot()
    {
        lock (Gate)
        {
            return [.. ById];
        }
    }

    /// <summary>
    /// The non-attached properties applicable to <paramref name="type"/> — registered (declared
    /// or via <c>AddOwner</c>) on the type or any base. Registration-ordered, deduplicated
    /// (an <c>AddOwner</c> entry does not repeat its property).
    /// </summary>
    internal static IReadOnlyList<UIProperty> PropertiesForType(Type type)
    {
        lock (Gate)
        {
            var applicable = new HashSet<UIProperty>();
            foreach (var ((owner, _), property) in ByOwnerAndName)
            {
                if (!owner.IsAssignableFrom(type))
                    continue;

                // Attached DECLARATIONS stay out (Grid.Row is a property of Grid's children, not
                // of Grid) — but an AddOwner onto an element type surfaces the property as a plain
                // member there (TextBlock.Foreground over the attached TextElement.Foreground).
                if (!property.IsAttached || owner != property.OwnerType)
                    applicable.Add(property);
            }

            return [.. ById.Where(applicable.Contains)];
        }
    }

    /// <summary>The attached properties declared by <paramref name="ownerType"/>, registration-ordered.</summary>
    internal static IReadOnlyList<UIProperty> AttachedByOwner(Type ownerType)
    {
        lock (Gate)
        {
            return [.. ById.Where(p => p.IsAttached && p.OwnerType == ownerType)];
        }
    }

    /// <summary>The property with dense id <paramref name="id"/>, or <see langword="null"/> (the −1 sentinel included).</summary>
    internal static UIProperty? FindById(int id)
    {
        lock (Gate)
        {
            return (uint)id < (uint)ById.Count ? ById[id] : null;
        }
    }

    /// <summary>
    /// Every owner type that registered (declared or via <c>AddOwner</c>) a property under the short
    /// name <paramref name="name"/>, in registration order (ledger A15). More than one element means
    /// the short name is ambiguous — the caller owns the disambiguation/diagnostic. Returns a
    /// snapshot; empty when the name is unknown.
    /// </summary>
    internal static IReadOnlyList<Type> FindOwnersByShortName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (Gate)
        {
            return OwnersByShortName.TryGetValue(name, out var owners) ? [.. owners] : [];
        }
    }

    /// <summary>
    /// The dense ids of every registered inheriting property — the set the reparent diff enumerates.
    /// Cached; invalidated by registration. Callers must not mutate the returned array.
    /// </summary>
    internal static int[] InheritingPropertyIds
    {
        get
        {
            lock (Gate)
            {
                return _inheritingPropertyIds ??= [.. ById.Where(static p => p.Inherits).Select(static p => p.Id)];
            }
        }
    }

    /// <summary>
    /// Every registered inheriting property — the set <c>SetInheritanceParent</c>'s reparent
    /// re-pull diffs (the property-object companion of <see cref="InheritingPropertyIds"/>).
    /// Cached; invalidated by registration. Callers must not mutate the returned array.
    /// </summary>
    internal static UIProperty[] InheritingProperties
    {
        get
        {
            lock (Gate)
            {
                return _inheritingProperties ??= [.. ById.Where(static p => p.Inherits)];
            }
        }
    }

    /// <summary>
    /// Every type that has registered (declared or via <c>AddOwner</c>) at least one property and
    /// whose CLR simple name is <paramref name="simpleName"/> (ordinal) — the "registry-known types"
    /// half of Fork B's default selector type resolver (style matrix SD1). Returns a snapshot;
    /// empty when no registered type carries the name.
    /// </summary>
    internal static IReadOnlyList<Type> FindOwnerTypesBySimpleName(string simpleName)
    {
        ArgumentNullException.ThrowIfNull(simpleName);
        lock (Gate)
        {
            return OwnerTypesBySimpleName.TryGetValue(simpleName, out var types) ? [.. types] : [];
        }
    }

    /// <summary>The number of registered properties (the sentinel excluded). Diagnostics only.</summary>
    internal static int RegisteredCount
    {
        get
        {
            lock (Gate)
            {
                return ById.Count;
            }
        }
    }

    private static void AddOwnerEntry(UIProperty property, Type ownerType)
    {
        if (!ByOwnerAndName.TryAdd((ownerType, property.Name), property))
        {
            throw new ArgumentException(
                $"A property named '{property.Name}' is already registered for owner type '{ownerType.Name}'.");
        }

        if (!OwnersByShortName.TryGetValue(property.Name, out var owners))
            OwnersByShortName[property.Name] = owners = [];
        owners.Add(ownerType);

        if (!OwnerTypesBySimpleName.TryGetValue(ownerType.Name, out var types))
            OwnerTypesBySimpleName[ownerType.Name] = types = [];
        if (!types.Contains(ownerType))
            types.Add(ownerType);
    }
}
