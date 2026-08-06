using Cursorial.UI.Data;

namespace Cursorial.UI.Configuration;

/// <summary>
/// One catalogued option projected against the owning <see cref="UserOptionsViewModel"/>'s
/// current edit scope — the shared row model both the settings dialog and the wizard bind.
/// Tri-state aware: the row knows whether the current layer SETS the key, what the effective
/// value is, and where an inherited value comes from; <see cref="ClearToInherited"/> is the
/// unset direction.
/// </summary>
public abstract class OptionViewModel : ObservableObject
{
    private protected OptionViewModel(UserOptionsViewModel owner, UserOptionDescriptor descriptor)
    {
        Owner = owner;
        Descriptor = descriptor;
    }

    /// <summary>The owning options view-model (edit scope, session, test coordination).</summary>
    public UserOptionsViewModel Owner { get; }

    /// <summary>The catalog entry this row edits.</summary>
    public UserOptionDescriptor Descriptor { get; }

    /// <summary>The row label (with the reserved-key annotation folded in).</summary>
    public string DisplayName => Descriptor.ReservedForFuture
                                     ? Descriptor.DisplayName + " (*)"
                                     : Descriptor.DisplayName;

    /// <summary>The catalog description.</summary>
    public string Description => Descriptor.Description;

    /// <summary>Whether the CURRENT edit layer explicitly sets this key (the "set" leg of the tri-state).</summary>
    public bool IsSetInCurrentScope => Owner.Session.GetValue(Owner.EditScope, Descriptor.Key) is not null;

    /// <summary>
    /// Where the row's effective value comes from when the current layer does NOT set it:
    /// "inherited from global" (app scope, global sets it) or "default". Empty when the current
    /// layer sets the key.
    /// </summary>
    public string InheritanceLabel
    {
        get
        {
            if (IsSetInCurrentScope)
                return string.Empty;

            if (Owner.EditScope == UserOptionScope.Application &&
                Owner.Session.GetValue(UserOptionScope.Global, Descriptor.Key) is not null)
            {
                return "inherited from global";
            }

            return "default";
        }
    }

    /// <summary>The effective stored value (app → global → catalog default).</summary>
    public string EffectiveValue => Owner.Session.GetEffectiveValue(Descriptor.Key) ?? Descriptor.DefaultValue;

    /// <summary>
    /// Clears the key in the current layer — the tri-state's unset/inherit direction. The row
    /// re-reads its effective value from the layer(s) below.
    /// </summary>
    public void ClearToInherited()
    {
        Owner.Session.SetValue(Owner.EditScope, Descriptor.Key, null);
        Owner.NotifyOptionChanged(this);
    }

    /// <summary>Writes <paramref name="value"/> into the current layer and refreshes.</summary>
    private protected void WriteCurrentScope(string? value)
    {
        Owner.Session.SetValue(Owner.EditScope, Descriptor.Key, value);
        Owner.NotifyOptionChanged(this);
    }

    /// <summary>Re-raises every projected property (scope switch, Reset, another row's write).</summary>
    internal virtual void Refresh()
    {
        OnPropertyChanged(nameof(IsSetInCurrentScope));
        OnPropertyChanged(nameof(InheritanceLabel));
        OnPropertyChanged(nameof(EffectiveValue));
    }
}

/// <summary>A toggle row. The control shows the EFFECTIVE value; toggling writes the current layer.</summary>
public sealed class BooleanOptionViewModel : OptionViewModel
{
    internal BooleanOptionViewModel(UserOptionsViewModel owner, UserOptionDescriptor descriptor)
        : base(owner, descriptor) { }

    /// <summary>The effective boolean (what the running app is honoring / would honor).</summary>
    public bool IsChecked
    {
        get => string.Equals(EffectiveValue, "true", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value == IsChecked && IsSetInCurrentScope)
                return;

            WriteCurrentScope(value ? "true" : "false");
        }
    }

    internal override void Refresh()
    {
        base.Refresh();
        OnPropertyChanged(nameof(IsChecked));
    }
}

/// <summary>
/// A choice row. The selectable items are an "(inherit)" pseudo-entry — labeled with what
/// inheriting currently yields — followed by the catalog choices; selecting the pseudo-entry
/// clears the key in the current layer.
/// </summary>
public sealed class ChoiceOptionViewModel : OptionViewModel
{
    internal ChoiceOptionViewModel(UserOptionsViewModel owner, UserOptionDescriptor descriptor)
        : base(owner, descriptor) { }

    private IReadOnlyList<string>? _items;

    /// <summary>
    /// The display items: index 0 = the inherit pseudo-entry, then the catalog choices in order.
    /// The instance is CACHED and only replaced when the content actually changes — a fresh array
    /// per read would make every <see cref="Refresh"/> reset the bound ItemsSource, whose
    /// container regeneration transiently coerces SelectedIndex and (via the two-way binding)
    /// feeds a write back into the store: a synchronous refresh→reset→write→refresh livelock.
    /// </summary>
    public IReadOnlyList<string> Items => _items ??= BuildItems();

    private IReadOnlyList<string> BuildItems()
    {
        var choices = Descriptor.Choices!;
        var items = new string[choices.Count + 1];
        items[0] = InheritItemLabel;

        for (var i = 0; i < choices.Count; i++)
            items[i + 1] = choices[i].Label;

        return items;
    }

    /// <summary>The inherit pseudo-entry's label, naming what inheriting yields right now.</summary>
    public string InheritItemLabel
    {
        get
        {
            var below = InheritedValue();
            var label = LabelFor(below) ?? below;
            var source = Owner.EditScope == UserOptionScope.Application &&
                         Owner.Session.GetValue(UserOptionScope.Global, Descriptor.Key) is not null
                             ? "global"
                             : "default";

            return $"Inherit {source}: {label}";
        }
    }

    /// <summary>Index into <see cref="Items"/>: 0 when the current layer inherits, else 1 + the choice index.</summary>
    public int SelectedIndex
    {
        get
        {
            if (Owner.Session.GetValue(Owner.EditScope, Descriptor.Key) is not { } layerValue)
                return 0;

            var choices = Descriptor.Choices!;
            for (var i = 0; i < choices.Count; i++)
            {
                if (string.Equals(choices[i].Value, layerValue, StringComparison.OrdinalIgnoreCase))
                    return i + 1;
            }

            return 0; // an unrecognized stored value displays as inherit; writing replaces it
        }
        set
        {
            // A negative index is a transient container-regeneration state (an ItemsSource reset
            // clears the selection before re-realizing) — never a user choice; writing it through
            // would clear the key behind the user's back and re-trigger the refresh cycle.
            if (value < 0 || value == SelectedIndex)
                return;

            var choices = Descriptor.Choices!;
            WriteCurrentScope(value is >= 1 && value <= choices.Count ? choices[value - 1].Value : null);
        }
    }

    /// <summary>The effective value's display label (for read-only summaries).</summary>
    public string EffectiveLabel => LabelFor(EffectiveValue) ?? EffectiveValue;

    private string InheritedValue()
    {
        // What the current layer WOULD see if it cleared: app scope falls to global-then-default;
        // global scope falls to the catalog default.
        if (Owner.EditScope == UserOptionScope.Application &&
            Owner.Session.GetValue(UserOptionScope.Global, Descriptor.Key) is { } global)
        {
            return global;
        }

        return Descriptor.DefaultValue;
    }

    private string? LabelFor(string value)
    {
        foreach (var choice in Descriptor.Choices!)
        {
            if (string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase))
                return choice.Label;
        }

        return null;
    }

    internal override void Refresh()
    {
        base.Refresh();

        // Raise Items ONLY when the content really changed (the inherit slot's label moves with
        // scope/inheritance) — see the Items cache comment for the livelock this prevents.
        var fresh = BuildItems();

        if (_items is null || !fresh.SequenceEqual(_items, StringComparer.Ordinal))
        {
            _items = fresh;
            OnPropertyChanged(nameof(Items));
        }

        OnPropertyChanged(nameof(InheritItemLabel));
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(EffectiveLabel));
    }
}
