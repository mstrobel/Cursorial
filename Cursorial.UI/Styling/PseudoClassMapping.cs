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
    private static readonly Lock RegistryLock = new();
    private static Dictionary<int, Mapping[]> _byPropertyId = [];
    private static volatile bool _anyRegistered;

    /// <summary>Maps a <see cref="bool"/> property to one pseudo-class: set ⇒ class on, cleared ⇒ class off.</summary>
    /// <typeparam name="TOwner">The element type the mapping applies to (assignable instances only).</typeparam>
    /// <param name="property">The mapped property.</param>
    /// <param name="pseudoClass">The <c>':'</c>-prefixed class name.</param>
    /// <exception cref="ArgumentException">The name is not <c>':'</c>-prefixed.</exception>
    /// <exception cref="InvalidOperationException">The name is interaction-backed, or the (owner, property) pair is already mapped.</exception>
    public static void Register<TOwner>(StyledProperty<bool> property, string pseudoClass)
        where TOwner : UIElement
    {
        ArgumentNullException.ThrowIfNull(property);
        var interned = ValidateName(pseudoClass);
        Add(new ClassifyMapping<bool>(typeof(TOwner), property, value => value ? interned : null));
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

        foreach (var pseudoClass in pseudoClasses)
            ValidateName(pseudoClass);

        Add(new ClassifyMapping<TValue>(typeof(TOwner), property, classify));
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

    private abstract class Mapping(Type ownerType, UIProperty property)
    {
        internal Type OwnerType { get; } = ownerType;

        internal int PropertyId { get; } = property.Id;

        internal string PropertyName { get; } = property.Name;

        internal abstract void Apply(UIElement element, in UIPropertyChangedEventArgs args);
    }

    private sealed class ClassifyMapping<TValue>(Type ownerType, StyledProperty<TValue> property, Func<TValue, string?> classify)
        : Mapping(ownerType, property)
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
