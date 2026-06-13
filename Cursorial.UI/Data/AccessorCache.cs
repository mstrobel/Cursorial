using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

namespace Cursorial.UI.Data;

/// <summary>
/// The global, copy-on-write per-hop accessor cache (design doc §6.3 / spec §3.2), keyed on
/// <c>(runtime type, member, indexKind)</c>. Reads are lock-free (volatile snapshot of an immutable
/// dictionary); writes copy-and-publish under a lock. Populated on the UI thread; the cache is a
/// pure function of the runtime type + member, so cross-instance sharing is safe.
/// </summary>
internal static class AccessorCache
{
    private static readonly Lock Gate = new();
    private static volatile Dictionary<CacheKey, PropertyAccessor> _propertyAccessors = new();
    private static volatile Dictionary<CacheKey, PropertyAccessor> _intIndexers = new();
    private static int _resolveCount;

    /// <summary>Diagnostics probe: the number of cold accessor resolutions (matrix B18).</summary>
    internal static int ResolveCount => Volatile.Read(ref _resolveCount);

    /// <summary>Resolves a property/attached segment accessor for <paramref name="instance"/>'s runtime type.</summary>
    public static PropertyAccessor ResolveProperty(object instance, in PathSegment segment)
    {
        var instanceType = instance.GetType();
        var memberName = segment.Kind == PathSegmentKind.Attached ? $"({segment.AttachedOwner!.Name}.{segment.Name})" : segment.Name!;
        var key = new CacheKey(instanceType, memberName);

        var snapshot = _propertyAccessors;
        if (snapshot.TryGetValue(key, out var cached))
            return cached;

        lock (Gate)
        {
            if (_propertyAccessors.TryGetValue(key, out cached))
                return cached;

            var accessor = ResolvePropertyCore(instance, instanceType, in segment);
            var grown = new Dictionary<CacheKey, PropertyAccessor>(_propertyAccessors) { [key] = accessor };
            _propertyAccessors = grown;
            Interlocked.Increment(ref _resolveCount);
            return accessor;
        }
    }

    /// <summary>Resolves an integer-indexer accessor for <paramref name="instance"/>'s runtime type.</summary>
    public static PropertyAccessor ResolveIntIndexer(object instance, int index)
    {
        var instanceType = instance.GetType();
        var key = new CacheKey(instanceType, $"[{index}]");

        var snapshot = _intIndexers;
        if (snapshot.TryGetValue(key, out var cached))
            return cached;

        lock (Gate)
        {
            if (_intIndexers.TryGetValue(key, out cached))
                return cached;

            var accessor = ResolveIntIndexerCore(instance, instanceType, index);
            var grown = new Dictionary<CacheKey, PropertyAccessor>(_intIndexers) { [key] = accessor };
            _intIndexers = grown;
            Interlocked.Increment(ref _resolveCount);
            return accessor;
        }
    }

    /// <summary>Resolves a string-indexer accessor (dictionary / general <c>Item[string]</c>) — not type-cached (key varies).</summary>
    public static PropertyAccessor? ResolveStringIndexer(object instance, string key)
    {
        if (instance is IDictionary dictionary)
        {
            var indexer = instance.GetType().GetProperty("Item", [typeof(string)])
                          ?? instance.GetType().GetProperty("Item", [typeof(object)]);
            if (indexer is not null)
                return new ReflectionIndexerAccessor(indexer, key);

            // Non-generic IDictionary without a typed indexer.
            return new DictionaryAccessor(key);
        }

        var stringIndexer = instance.GetType().GetProperty("Item", [typeof(string)]);
        return stringIndexer is not null ? new ReflectionIndexerAccessor(stringIndexer, key) : null;
    }

    /// <summary>Test-only cache reset (registrations are process-global; the cache is a pure function so reset is harmless).</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _propertyAccessors = new Dictionary<CacheKey, PropertyAccessor>();
            _intIndexers = new Dictionary<CacheKey, PropertyAccessor>();
            Volatile.Write(ref _resolveCount, 0);
        }
    }

    private static PropertyAccessor ResolvePropertyCore(object instance, Type instanceType, in PathSegment segment)
    {
        // Attached segment: resolve the registered UIProperty (rule 1) when present.
        if (segment.Kind == PathSegmentKind.Attached)
        {
            if (segment.AttachedProperty is { } attachedProperty && instance is UIObject)
                return new UIPropertyAccessor(attachedProperty);
            // Fall through: reflective access on a CLR member named identically is not supported for
            // attached segments; surface as a CLR property lookup by the member name.
            var attachedClr = instanceType.GetProperty(segment.Name!, BindingFlags.Public | BindingFlags.Instance);
            if (attachedClr is not null)
                return BuildClrAccessor(instanceType, attachedClr);
            return new UnresolvableAccessor(segment.Name!);
        }

        // Rule 1: a registered UIProperty on a UIObject hop (by runtime type + name).
        if (instance is UIObject && UIPropertyRegistry.Find(instanceType, segment.Name!) is { } uiProperty)
            return new UIPropertyAccessor(uiProperty);

        // Rule 2: CLR property via reflection (compiled delegate when dynamic code is supported).
        var property = instanceType.GetProperty(segment.Name!, BindingFlags.Public | BindingFlags.Instance);
        if (property is not null)
            return BuildClrAccessor(instanceType, property);

        return new UnresolvableAccessor(segment.Name!);
    }

    private static PropertyAccessor BuildClrAccessor(Type instanceType, PropertyInfo property)
    {
        var rung = NotificationRung.Inpc;
        EventInfo? changedEvent = null;

        // Rung 1: INPC subscribes the whole instance — no per-property discovery needed; mark Inpc.
        if (typeof(INotifyPropertyChanged).IsAssignableFrom(instanceType))
        {
            rung = NotificationRung.Inpc;
        }
        else
        {
            // Rung 2: a convention-matched [PropertyName]Changed CLR event.
            changedEvent = FindChangedEvent(instanceType, property.Name);
            rung = changedEvent is not null ? NotificationRung.ChangedEvent : NotificationRung.ParentChangeOnly;
        }

        return new ClrPropertyAccessor(property, rung, changedEvent);
    }

    private static EventInfo? FindChangedEvent(Type type, string propertyName)
    {
        var changed = type.GetEvent(propertyName + "Changed", BindingFlags.Public | BindingFlags.Instance);
        if (changed?.EventHandlerType is not { } handlerType)
            return null;

        // Accept EventHandler and EventHandler<EventArgs>-compatible delegates (an Invoke taking
        // (object, EventArgs-derived)).
        var invoke = handlerType.GetMethod("Invoke");
        if (invoke is null)
            return null;

        var parameters = invoke.GetParameters();
        if (parameters.Length != 2)
            return null;
        if (!typeof(EventArgs).IsAssignableFrom(parameters[1].ParameterType))
            return null;

        return changed;
    }

    private static PropertyAccessor ResolveIntIndexerCore(object instance, Type instanceType, int index)
    {
        // Fast path: IList / IReadOnlyList<T>.
        if (instance is IList)
        {
            var elementType = FindEnumerableElementType(instanceType);
            return new ListIndexerAccessor(index, elementType);
        }

        // General int indexer via reflection.
        var indexer = instanceType.GetProperty("Item", [typeof(int)]);
        if (indexer is not null)
            return new ReflectionIndexerAccessor(indexer, index);

        return new UnresolvableAccessor($"[{index}]");
    }

    private static Type? FindEnumerableElementType(Type type)
    {
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                return iface.GetGenericArguments()[0];
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IList<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    private readonly record struct CacheKey(Type Type, string Member);
}

/// <summary>A non-generic <c>IDictionary</c> string-key accessor (no typed indexer present).</summary>
internal sealed class DictionaryAccessor(string key) : PropertyAccessor
{
    public override AccessorKind Kind => AccessorKind.ReflectionIndexer;

    public override string MemberName => "Item[]";

    public override Type? ValueType => typeof(object);

    public override bool CanWrite => true;

    public override NotificationRung Rung => NotificationRung.Inpc;

    public string Key { get; } = key;

    public override object? GetValue(object instance)
        => instance is IDictionary dictionary && dictionary.Contains(Key) ? dictionary[Key] : null;

    public override void SetValue(object instance, object? value)
    {
        if (instance is IDictionary { IsReadOnly: false } dictionary)
            dictionary[Key] = value;
    }
}

/// <summary>A dead-end accessor — the member could not be resolved on the runtime type (a path error).</summary>
internal sealed class UnresolvableAccessor(string memberName) : PropertyAccessor
{
    public override AccessorKind Kind => AccessorKind.ClrReflection;

    public override string MemberName { get; } = memberName;

    public override Type? ValueType => null;

    public override bool CanWrite => false;

    public override NotificationRung Rung => NotificationRung.ParentChangeOnly;

    public bool IsUnresolvable => true;

    public override object? GetValue(object instance) => UIProperty.UnsetValue;

    public override void SetValue(object instance, object? value)
    {
    }
}
