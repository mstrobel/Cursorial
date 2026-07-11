// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The property → pseudo-class bridge (design doc §3.2): a control type declares, once in its
/// static constructor, that a styled property's value mirrors a pseudo-class
/// (<c>ToggleButton.IsCheckedProperty → ":checked"</c>). The styling engine flips the class through
/// the ordinary change pipeline — control properties become selector-visible state with no styling
/// code on the control.
/// </summary>
/// <remarks>
/// <para>
/// Registrations are <b>process-global</b> and keyed by (owner type, property): a second
/// registration for the same pair throws. Mappings apply to instances assignable to the registered
/// owner type only (S116). <see cref="InteractionState"/>-backed names are rejected — those bits
/// flow exclusively through <c>SetInteractionState</c> (ledger B9).
/// </para>
/// <para>
/// A value transition retires the old class and sets the new one in <b>one</b> styling pass
/// (S117 — the <c>bool?</c> → <c>:checked</c>/<c>:indeterminate</c>/none shape is the canonical
/// multi-class use).
/// </para>
/// </remarks>
public static class PseudoClassMapping
{
    /// <summary>One registered property → pseudo-class mapping, as seen by tooling.</summary>
    public readonly record struct PseudoClassMappingInfo(Type OwnerType, UIProperty Property, IReadOnlyList<string> PseudoClasses);

    private static readonly Lock RegistryLock = new();

    // volatile: lock-free readers (NotifyPropertyChanged, Snapshot) never take RegistryLock, so
    // the copy-on-write publication must be a release/acquire in its own right. The documented
    // .NET memory model already gives reference stores release semantics (and NotifyPropertyChanged
    // additionally pairs through the volatile _anyRegistered), but Snapshot reads this field with
    // no other fence — and ECMA-335-conservative correctness shouldn't require an archaeology dig.
    private static volatile Dictionary<int, Mapping[]> _byPropertyId = [];
    private static volatile bool _anyRegistered;

    /// <summary>Maps a <see cref="bool"/> property to one pseudo-class: set ⇒ class on, cleared ⇒ class off.</summary>
    /// <typeparam name="TOwner">The element type the mapping applies to (assignable instances only).</typeparam>
    /// <param name="property">The mapped property.</param>
    /// <param name="pseudoClass">The <c>':'</c>-prefixed class name.</param>
    /// <exception cref="ArgumentException">The name is not <c>':'</c>-prefixed.</exception>
    /// <exception cref="InvalidOperationException">The name is interaction-backed, or the (owner, property) pair is already mapped.</exception>
    public static void Register<TOwner>(UIProperty property, string pseudoClass)
        where TOwner : UIElement
    {
        ArgumentNullException.ThrowIfNull(property);

        if (property.PropertyType != typeof(bool))
            throw new ArgumentException($"PseudoClass-mapped properties must have a property type of {nameof(Boolean)}.", nameof(property));

        var interned = ValidateName(pseudoClass);

        Add(new ClassifyMapping<bool>(typeof(TOwner), property, [interned], value => value ? interned : null));
    }

    /// <summary>
    /// The multi-class classify overload (canonical per ledger B9 — e.g. <c>bool?</c> →
    /// <c>":checked"</c> / <c>":indeterminate"</c> / <see langword="null"/>): a value transition
    /// retires the old class and sets the new one in one pass.
    /// </summary>
    /// <typeparam name="TOwner">The element type the mapping applies to.</typeparam>
    /// <typeparam name="TValue">The property's value type.</typeparam>
    /// <param name="property">The mapped property.</param>
    /// <param name="classify">Maps a value to the active class (a member of <paramref name="pseudoClasses"/>) or <see langword="null"/> for none.</param>
    /// <param name="pseudoClasses">The fixed class vocabulary the mapping toggles.</param>
    public static void Register<TOwner, TValue>(
        StyledProperty<TValue> property, Func<TValue, string?> classify, params ReadOnlySpan<string> pseudoClasses)
        where TOwner : UIElement
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(classify);

        var vocabulary = new string[pseudoClasses.Length];
        for (var i = 0; i < pseudoClasses.Length; i++)
            vocabulary[i] = ValidateName(pseudoClasses[i]);

        Add(new ClassifyMapping<TValue>(typeof(TOwner), property, vocabulary, classify));
    }

    /// <summary>
    /// A snapshot of every registered mapping — the read-only tooling surface (selector
    /// completion and quick documentation in the designer). Same copy-on-write read the change
    /// pipeline uses; no lock.
    /// </summary>
    public static IReadOnlyList<PseudoClassMappingInfo> Snapshot()
    {
        var byPropertyId = _byPropertyId;
        var result = new List<PseudoClassMappingInfo>();
        foreach (var mappings in byPropertyId.Values)
        {
            foreach (var mapping in mappings)
                result.Add(new PseudoClassMappingInfo(mapping.OwnerType, mapping.Property, mapping.PseudoClasses));
        }

        return result;
    }

    /// <summary>
    /// The change-pipeline hook (called from <c>UIElement.OnPropertyChanged</c>): near-free when
    /// nothing is registered — one static <see langword="bool"/> read.
    /// </summary>
    internal static void NotifyPropertyChanged(UIElement element, in UIPropertyChangedEventArgs args)
    {
        if (!_anyRegistered)
            return;

        if (!_byPropertyId.TryGetValue(args.Property.Id, out var mappings))
            return;

        foreach (var mapping in mappings)
        {
            if (mapping.OwnerType.IsInstanceOfType(element))
                mapping.Apply(element, in args);
        }
    }

    private static void Add(Mapping mapping)
    {
        lock (RegistryLock)
        {
            _byPropertyId.TryGetValue(mapping.PropertyId, out var existing);

            if (existing is not null)
            {
                foreach (var entry in existing)
                {
                    if (entry.OwnerType == mapping.OwnerType)
                    {
                        throw new InvalidOperationException(
                            $"A pseudo-class mapping for ('{mapping.OwnerType.Name}', '{mapping.PropertyName}') is " +
                            "already registered — mappings are one per (owner type, property).");
                    }
                }
            }

            // Copy-on-write: the read side (the change pipeline) takes no lock.
            var grown = new Dictionary<int, Mapping[]>(_byPropertyId)
                        {
                            [mapping.PropertyId] = existing is null ? [mapping] : [.. existing, mapping]
                        };

            Volatile.WriteBarrier();

            _byPropertyId = grown;
            _anyRegistered = true;
        }
    }

    private static string ValidateName(string pseudoClass)
    {
        ArgumentException.ThrowIfNullOrEmpty(pseudoClass);

        if (pseudoClass[0] != ':' || pseudoClass.Length == 1)
        {
            throw new ArgumentException(
                $"Pseudo-class name '{pseudoClass}' is invalid: mapping targets are ':'-prefixed names.",
                nameof(pseudoClass));
        }

        if (InteractionPseudoClasses.IsInteractionBacked(pseudoClass))
        {
            throw new InvalidOperationException(
                $"Pseudo-class '{pseudoClass}' is backed by an InteractionState bit and flows only through " +
                "SetInteractionState (ledger B9) — it cannot be the target of a property mapping.");
        }

        return string.Intern(pseudoClass);
    }

    private abstract class Mapping(Type ownerType, UIProperty property, string[] pseudoClasses)
    {
        internal Type OwnerType { get; } = ownerType;

        internal UIProperty Property { get; } = property;

        internal int PropertyId { get; } = property.Id;

        internal string PropertyName { get; } = property.Name;

        /// <summary>The fixed class vocabulary the mapping toggles (tooling snapshot).</summary>
        internal string[] PseudoClasses { get; } = pseudoClasses;

        internal abstract void Apply(UIElement element, in UIPropertyChangedEventArgs args);
    }

    private sealed class ClassifyMapping<TValue>(Type ownerType, UIProperty property, string[] pseudoClasses, Func<TValue, string?> classify)
        : Mapping(ownerType, property, pseudoClasses)
    {
        internal override void Apply(UIElement element, in UIPropertyChangedEventArgs args)
        {
            var oldClass = classify(args.GetOldValue<TValue>());
            var newClass = classify(args.GetNewValue<TValue>());

            if (string.Equals(oldClass, newClass, StringComparison.Ordinal))
                return;

            // Retire-old + set-new in ONE styling pass (S117): both flips queue inside the apply
            // scope and reconcile together at its close.
            var engine = UIApplication.Current?.StyleEngineInternal;
            using (engine?.DeferReconciliation() ?? default)
            {
                if (oldClass is not null)
                    element.SetPseudoClassFromMapping(oldClass, active: false);

                if (newClass is not null)
                    element.SetPseudoClassFromMapping(newClass, active: true);
            }
        }
    }
}
