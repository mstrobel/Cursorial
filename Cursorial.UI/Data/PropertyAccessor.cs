using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Cursorial.UI.Data;

/// <summary>The change-notification rung resolved for a CLR hop (design doc §6.3 source ladder).</summary>
internal enum NotificationRung : byte
{
    /// <summary>The hop's instance carries <c>INotifyPropertyChanged</c> (rung 1) — or no notification applies (UIObject hop).</summary>
    Inpc,

    /// <summary>A convention-matched <c>[PropertyName]Changed</c> CLR event (rung 2).</summary>
    ChangedEvent,

    /// <summary>Observed-on-parent-change only (rung 3) — a one-time Info is recorded.</summary>
    ParentChangeOnly
}

/// <summary>
/// A resolved per-hop accessor (design doc §6.3 / spec §3.2) — reads (and optionally writes) a
/// value, and reports the source-ladder notification rung for the CLR lane. Accessors are cached in
/// the copy-on-write <see cref="AccessorCache"/>, keyed on <c>(runtime type, member)</c>.
/// </summary>
internal abstract class PropertyAccessor
{
    /// <summary>The lane this accessor resolved through (for diagnostics/tests).</summary>
    public abstract AccessorKind Kind { get; }

    /// <summary>The member/segment name (or <c>"Item[]"</c> for indexers).</summary>
    public abstract string MemberName { get; }

    /// <summary>The CLR type the value reads as (the leaf type for write-back conversion); <see langword="null"/> if unknown.</summary>
    public abstract Type? ValueType { get; }

    /// <summary>Whether a setter exists (write-back possible).</summary>
    public abstract bool CanWrite { get; }

    /// <summary>The source-ladder rung for a CLR hop; <see cref="NotificationRung.Inpc"/> for the UIObject lane.</summary>
    public abstract NotificationRung Rung { get; }

    /// <summary>The discovered <c>[PropertyName]Changed</c> event for <see cref="NotificationRung.ChangedEvent"/> (else <see langword="null"/>).</summary>
    public virtual EventInfo? ChangedEvent => null;

    /// <summary>Reads the value off <paramref name="instance"/> (already non-null at the call site).</summary>
    public abstract object? GetValue(object instance);

    /// <summary>Writes <paramref name="value"/> to <paramref name="instance"/>; throws if read-only.</summary>
    public abstract void SetValue(object instance, object? value);
}

/// <summary>The lane an accessor resolved through (diagnostics/test probe).</summary>
internal enum AccessorKind : byte
{
    /// <summary>A registered <c>UIProperty</c> on a <c>UIObject</c> hop.</summary>
    UIProperty,

    /// <summary>A CLR property via a compiled delegate.</summary>
    ClrDelegate,

    /// <summary>A CLR property via raw <c>PropertyInfo</c> (AOT-without-codegen fallback).</summary>
    ClrReflection,

    /// <summary>An <c>IList</c>/<c>IReadOnlyList&lt;T&gt;</c> integer indexer fast path.</summary>
    ListIndexer,

    /// <summary>An <c>IDictionary</c>/general <c>Item[...]</c> reflection indexer.</summary>
    ReflectionIndexer
}

/// <summary>A registered <c>UIProperty</c> accessor (rule 1) — no reflection, no INPC.</summary>
internal sealed class UIPropertyAccessor(UIProperty property) : PropertyAccessor
{
    public UIProperty Property { get; } = property;

    public override AccessorKind Kind => AccessorKind.UIProperty;

    public override string MemberName => Property.Name;

    public override Type ValueType => Property.PropertyType;

    public override bool CanWrite => !Property.IsReadOnly;

    public override NotificationRung Rung => NotificationRung.Inpc; // observed via AddObserver, not the ladder

    public override object? GetValue(object instance) => ((UIObject)instance).GetValue(Property);

    public override void SetValue(object instance, object? value) => ((UIObject)instance).SetValue(Property, value);
}

/// <summary>A CLR property accessor (rule 2) — compiled delegate or raw <c>PropertyInfo</c>.</summary>
internal sealed class ClrPropertyAccessor : PropertyAccessor
{
    private readonly PropertyInfo _property;
    private readonly Func<object, object?>? _getter;
    private readonly Action<object, object?>? _setter;
    private readonly bool _useDelegate;

    public ClrPropertyAccessor(PropertyInfo property, NotificationRung rung, EventInfo? changedEvent)
    {
        _property = property;
        Rung = rung;
        ChangedEvent = changedEvent;

        _useDelegate = RuntimeFeature.IsDynamicCodeSupported;
        if (_useDelegate)
        {
            // Explicit lambdas, not method-group captures: PropertyInfo.GetValue/SetValue are
            // overloaded (the indexed (object?, object?[]) forms), and a method-group bound to
            // Func<object,object?>/Action<object,object?> resolves to the wrong overload on unusual
            // PropertyInfo subclasses. The single-arg lambdas are unambiguous.
            _getter = property.CanRead ? obj => property.GetValue(obj) : null;
            _setter = property.CanWrite ? (obj, val) => property.SetValue(obj, val) : null;
        }
    }

    public override AccessorKind Kind => _useDelegate ? AccessorKind.ClrDelegate : AccessorKind.ClrReflection;

    public override string MemberName => _property.Name;

    public override Type ValueType => _property.PropertyType;

    public override bool CanWrite => _property.CanWrite;

    public override NotificationRung Rung { get; }

    public override EventInfo? ChangedEvent { get; }

    public override object? GetValue(object instance)
        => _useDelegate ? _getter?.Invoke(instance) : (_property.CanRead ? _property.GetValue(instance) : null);

    public override void SetValue(object instance, object? value)
    {
        if (!_property.CanWrite)
            throw new InvalidOperationException($"Property '{_property.Name}' has no setter.");
        if (_useDelegate)
            _setter!(instance, value);
        else
            _property.SetValue(instance, value);
    }
}

/// <summary>An <c>IList</c>/<c>IReadOnlyList&lt;T&gt;</c> integer-indexer accessor (rule 3, fast path).</summary>
internal sealed class ListIndexerAccessor(int index, Type? elementType) : PropertyAccessor
{
    public override AccessorKind Kind => AccessorKind.ListIndexer;

    public override string MemberName => "Item[]";

    public override Type? ValueType { get; } = elementType;

    public override bool CanWrite => true;

    public override NotificationRung Rung => NotificationRung.Inpc; // INCC / "Item[]" handled by the node

    public int Index { get; } = index;

    public override object? GetValue(object instance)
    {
        if (instance is IList list)
            return (uint)Index < (uint)list.Count ? list[Index] : null;
        return null;
    }

    public override void SetValue(object instance, object? value)
    {
        if (instance is IList { IsReadOnly: false } list && (uint)Index < (uint)list.Count)
            list[Index] = value;
    }
}

/// <summary>A general <c>Item[...]</c> reflection indexer (dictionaries, custom indexers).</summary>
internal sealed class ReflectionIndexerAccessor(PropertyInfo indexer, object key) : PropertyAccessor
{
    public override AccessorKind Kind => AccessorKind.ReflectionIndexer;

    public override string MemberName => "Item[]";

    public override Type ValueType => indexer.PropertyType;

    public override bool CanWrite => indexer.CanWrite;

    public override NotificationRung Rung => NotificationRung.Inpc;

    public object Key { get; } = key;

    public override object? GetValue(object instance)
    {
        try
        {
            return indexer.GetValue(instance, [Key]);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public override void SetValue(object instance, object? value)
    {
        if (indexer.CanWrite)
            indexer.SetValue(instance, value, [Key]);
    }
}
