using System.Collections;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// Keyed resource storage with merged dictionaries, theme-variant sub-dictionaries, deferred
/// entries, sealing, and coalesced change pulses (design doc §11.1). Keys are <c>string</c>
/// (ordinal), <see cref="Type"/>, <see cref="DataTemplateKey"/>, or any object (CD1); values are
/// anything except <see cref="UIProperty.UnsetValue"/> (the miss sentinel, CD2). The lookup chain
/// (<see cref="ResourceExtensions.FindResource"/>) walks across dictionaries; a single dictionary's
/// in-order probe is <see cref="TryGetResource"/>.
/// </summary>
public sealed class ResourceDictionary : IEnumerable<KeyValuePair<object, object?>>
{
    // Slots are boxed so a deferred entry realizes IN PLACE (the slot object is never replaced —
    // enumeration-safe, CD6). A plain value uses a Value slot; a deferred entry a Deferred slot.
    private readonly Dictionary<object, Slot> _entries = new(ResourceKeyComparer.Instance);
    private List<ResourceDictionary>? _merged;
    private ThemeDictionaryCollection? _themeDictionaries;
    private Styles? _styles;
    private object? _owner;        // single-parent guard (CD5)
    private int _updateDepth;      // BeginUpdate refcount
    private bool _pendingCatchAll; // a coalesced pulse owed at the outermost dispose
    private Uri? _source;

    /// <summary>The realization state of a deferred slot (CD6; surfaced via <see cref="ResourceDiagnostics"/>).</summary>
    public enum DeferredState : byte { Deferred, Realizing, Realized }

    private sealed class Slot
    {
        public object? Value;
        public IDeferredResourceEntry? Deferred;
        public DeferredState State;
        public ThemeVariant? RealizedAtVariant;
    }

    /// <summary>The process-global loader callback, installed by <c>Cursorial.UI.Xaml</c>'s module init at P6.</summary>
    public static Func<Uri, ResourceDictionary>? LoadCallback { get; set; }

    /// <summary>Reads or writes a resource by key. The getter realizes a deferred slot; the setter overwrites and pulses <see cref="ResourceChangeKind.Keyed"/>.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="UIProperty.UnsetValue"/> (CD2).</exception>
    public object? this[object key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);
            return _entries.TryGetValue(key, out var slot) ? Realize(key, slot) : null;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(key);
            ThrowIfSealed();
            RejectUnset(value);

            _entries[key] = new Slot { Value = value, State = DeferredState.Realized };
            Pulse(ResourceChangeKind.Keyed, key);
        }
    }

    /// <summary>Adds a new entry. A duplicate key throws (CD3).</summary>
    public void Add(object key, object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfSealed();
        RejectUnset(value);

        if (_entries.ContainsKey(key))
            throw new ArgumentException($"A resource with key '{key}' already exists.", nameof(key));

        _entries.Add(key, new Slot { Value = value, State = DeferredState.Realized });
        Pulse(ResourceChangeKind.Keyed, key);
    }

    /// <summary>Removes an entry. Returns true and pulses <see cref="ResourceChangeKind.Keyed"/> when present; false with no pulse when absent (CD3).</summary>
    public bool Remove(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfSealed();

        if (!_entries.Remove(key))
            return false;

        Pulse(ResourceChangeKind.Keyed, key);
        return true;
    }

    /// <summary>Whether this dictionary holds the key. Never realizes a deferred entry (CD6).</summary>
    public bool ContainsKey(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.ContainsKey(key);
    }

    /// <summary>The keys present in this dictionary. Reading never realizes (CD6).</summary>
    public IReadOnlyCollection<object> Keys => _entries.Keys;

    /// <summary>The number of entries. Never realizes (CD6).</summary>
    public int Count => _entries.Count;

    /// <summary>Probes this dictionary only (no chain), realizing a deferred slot. Variant-agnostic — see <see cref="TryGetResource"/>.</summary>
    public bool TryGetValue(object key, out object? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_entries.TryGetValue(key, out var slot))
        {
            value = Realize(key, slot);
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Registers a Fork C lazy node-graph slice (CD6): a still-to-realize slot the getter realizes on
    /// first read. This is a structural mutation, so — like every other write — it is rejected on a
    /// sealed dictionary (CD4). The Fork C XAML loader authors all deferred slots <b>before</b>
    /// <see cref="Seal"/> (the realization cache fill that runs afterward is the only post-seal slot
    /// activity, and it preserves <see cref="Version"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The dictionary is sealed (CD4).</exception>
    public void SetDeferred(object key, IDeferredResourceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(entry);

        // SetDeferred authors a slot, which is a structural mutation — rejected post-seal like any
        // write (the loader authors every deferred entry before Seal; cache fill afterward is in-place).
        ThrowIfSealed();

        _entries[key] = new Slot { Deferred = entry, State = DeferredState.Deferred };
        // No pulse: a deferred authoring is logically the same as a present-but-unrealized value.
        BumpVersion();
    }

    /// <summary>
    /// The merged-dictionary list (design doc §11.1): own entries beat merged, and a later merged
    /// beats an earlier one (last-to-first scan). Adding a non-sealed dictionary already owned
    /// elsewhere throws (single-parent, CD5).
    /// </summary>
    public IList<ResourceDictionary> MergedDictionaries => _mergedView ??= new MergedList(this);

    private MergedList? _mergedView;

    /// <summary>The theme-variant sub-dictionaries (keyed by <see cref="ThemeVariantKey"/>); lazily allocated.</summary>
    public ThemeDictionaryCollection ThemeDictionaries => _themeDictionaries ??= new ThemeDictionaryCollection(this);

    /// <summary>Whether <see cref="ThemeDictionaries"/> has been allocated (the probe short-circuit, C27).</summary>
    internal bool HasThemeDictionaries => _themeDictionaries is { Count: > 0 };

    /// <summary>
    /// The theme-styles channel (design doc §11.1/§11.8): selector styles a theme ships. Consumed from
    /// <c>UIApplication.Theme</c> (the app-theme leg) and from a dictionary registered into
    /// <see cref="Themes.ThemeContributions"/> (the library-contributed leg); element/window slots are ignored
    /// in v1.
    /// </summary>
    public Styles? Styles
    {
        get => _styles;
        set
        {
            ThrowIfSealed();
            if (ReferenceEquals(_styles, value))
                return;

            _styles?.Detach();
            _styles = value;
            value?.AttachTo(this);
            Pulse(ResourceChangeKind.CatchAll, null);
        }
    }

    /// <summary>
    /// The source URI; loading routes through <see cref="LoadCallback"/> (design doc §11.1). The loaded
    /// entries are copied into this dictionary inside a single <see cref="BeginUpdate"/> scope, so the
    /// whole load coalesces into one <see cref="ResourceChangeKind.CatchAll"/> pulse (never a per-key
    /// subscription storm). <b>Merge semantics:</b> a loaded key <em>overwrites</em> any pre-existing
    /// entry of the same key (WPF parity), so set <see cref="Source"/> before adding local entries you
    /// want to keep.
    /// </summary>
    public Uri? Source
    {
        get => _source;
        set
        {
            ThrowIfSealed();
            _source = value;
            if (value is null)
                return;

            if (LoadCallback is not { } callback)
            {
                throw new InvalidOperationException(
                    "ResourceDictionary.Source was set but no loader is installed. The XAML loader " +
                    "(Cursorial.UI.Xaml) registers LoadCallback at module init; it is absent at P5.");
            }

            var loaded = callback(value);
            using (BeginUpdate()) // one coalesced CatchAll for the whole load, not N keyed pulses
            {
                foreach (var pair in loaded)
                    this[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>Whether the dictionary is sealed (deep-frozen). Sealed dictionaries never pulse and are freely shared (CD4/CD5).</summary>
    public bool IsSealed { get; private set; }

    /// <summary>A monotonic structural version (bumps on insert/overwrite/remove; not on a deferred-realization cache fill, CD18).</summary>
    public int Version { get; private set; }

    /// <summary>Raised on a structural change (coalesced inside a <see cref="BeginUpdate"/> scope). Never fires on a sealed dictionary (CD4).</summary>
    public event EventHandler<ResourcesChangedEventArgs>? Changed;

    /// <summary>
    /// Deep-freezes the dictionary, its merged/theme sub-dictionaries, and its <see cref="Styles"/>
    /// (CD4). After this, every mutation throws and <see cref="Changed"/> never fires; deferred
    /// realization (cache fill) stays legal and does not bump <see cref="Version"/>.
    /// </summary>
    public void Seal()
    {
        if (IsSealed)
            return;

        if (_updateDepth > 0)
            throw new InvalidOperationException("Cannot Seal a ResourceDictionary inside an open BeginUpdate scope (C17).");

        IsSealed = true;

        if (_merged is { } merged)
            foreach (var m in merged)
                m.Seal();

        _themeDictionaries?.SealAll();
    }

    /// <summary>Single-hop resolution under <paramref name="variant"/> (design doc §11.2): ThemeDictionaries variant probe (recursive) → own entries → merged last-to-first.</summary>
    public bool TryGetResource(object key, ThemeVariant variant, out object? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        // 1. ThemeDictionaries variant probe (recursive into sub-dictionaries' own/merged).
        if (HasThemeDictionaries && _themeDictionaries!.TryGetResource(key, variant, out value))
            return true;

        // 2. own entries.
        if (_entries.TryGetValue(key, out var slot))
        {
            value = Realize(key, slot);
            return true;
        }

        // 3. merged, last-to-first (later wins).
        if (_merged is { } merged)
        {
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].TryGetResource(key, variant, out value))
                    return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Opens a coalescing update scope (design doc §11.1): mutations inside it raise one
    /// <see cref="ResourceChangeKind.CatchAll"/> pulse at the outermost dispose. Refcounted nesting.
    /// </summary>
    public ResourceUpdateScope BeginUpdate()
    {
        ThrowIfSealed();
        _updateDepth++;
        return new ResourceUpdateScope(this);
    }

    internal void EndUpdate()
    {
        if (_updateDepth == 0)
            return;

        if (--_updateDepth == 0 && _pendingCatchAll)
        {
            _pendingCatchAll = false;
            RaiseChanged(ResourceChangeKind.CatchAll, null);
        }
    }

    // ───────────────────────────── deferred-entry diagnostics surface (R3) ─────────────────────────────

    internal IEnumerable<DeferredEntryInfo> EnumerateDeferredEntries()
    {
        foreach (var (key, slot) in _entries)
        {
            if (slot.Deferred is not null || slot.State != DeferredState.Realized)
                yield return new DeferredEntryInfo(key, slot.State, slot.RealizedAtVariant);
        }
    }

    internal bool TryGetDeferredInfo(object key, out DeferredEntryInfo info)
    {
        if (_entries.TryGetValue(key, out var slot) && (slot.Deferred is not null || slot.State != DeferredState.Realized))
        {
            info = new DeferredEntryInfo(key, slot.State, slot.RealizedAtVariant);
            return true;
        }

        info = default;
        return false;
    }

    // ───────────────────────────── owner / sharing plumbing (CD5) ─────────────────────────────

    /// <summary>Acquires single ownership (CD5). Sealed dictionaries are freely shared (no owner check).</summary>
    internal void Acquire(object owner)
    {
        if (IsSealed)
            return; // sealed-exempt sharing

        if (_owner is not null && !ReferenceEquals(_owner, owner))
        {
            throw new InvalidOperationException(
                "This ResourceDictionary is already owned (single-parent rule, CD5). Seal it to share it freely.");
        }

        _owner = owner;
    }

    internal void Release(object owner)
    {
        if (ReferenceEquals(_owner, owner))
            _owner = null;
    }

    /// <summary>The variant used when this dictionary's deferred StaticResource captures freeze (set per-realization, CD7).</summary>
    internal ThemeVariant CurrentVariantForRealization
        => UIApplication.Current?.ActualThemeVariant ?? new ThemeVariant(ThemeBase.Dark, Output.ColorDepth.Truecolor);

    // ───────────────────────────── internals ─────────────────────────────

    private object? Realize(object key, Slot slot)
    {
        if (slot.Deferred is null || slot.State == DeferredState.Realized)
            return slot.Value;

        if (slot.State == DeferredState.Realizing)
            throw new InvalidOperationException($"Resource cycle realizing key '{key}' (it references itself, CD6).");

        slot.State = DeferredState.Realizing;
        try
        {
            var variant = CurrentVariantForRealization;
            var lexical = ResourceScopes.ForDictionary(this, parent: null);
            var realized = slot.Deferred.Realize(lexical);
            slot.Value = realized;
            slot.RealizedAtVariant = variant;
            slot.State = DeferredState.Realized;
            // Cache fill does NOT bump Version (CD6) and never pulses.
            return realized;
        }
        catch
        {
            slot.State = DeferredState.Deferred; // transient failure: retried next lookup (CD6/C21)
            throw;
        }
    }

    private void Pulse(ResourceChangeKind kind, object? key)
    {
        BumpVersion();

        if (_updateDepth > 0)
        {
            _pendingCatchAll = true; // coalesce into one CatchAll at the outermost dispose
            return;
        }

        RaiseChanged(kind, key);
    }

    private void BumpVersion()
    {
        if (!IsSealed)
            Version++;
    }

    private void RaiseChanged(ResourceChangeKind kind, object? key)
    {
        if (IsSealed)
            return; // sealed dictionaries never pulse (CD4)

        Changed?.Invoke(this, new ResourcesChangedEventArgs(kind, key));
    }

    /// <summary>
    /// A style re-match hook the owning <see cref="UIApplication"/> installs while this dictionary is the
    /// active <c>UIApplication.Theme</c> (R2/B13 / C100 — "re-read on theme-origin pulses"): a Styles-slot
    /// mutation on the active theme re-matches the Theme(2) leg. Null when this dictionary is not the app
    /// theme (element/window theme slots are ignored — C101), so a non-theme dictionary's Styles mutation
    /// stays a pure resource pulse.
    /// </summary>
    internal Action? ThemeStylesReMatchHook { get; set; }

    /// <summary>The Styles-slot mutation pulse path (the theme-styles channel, C25); routed from <c>Styles.NotifyChanged</c>.</summary>
    internal void OnStylesMutated()
    {
        ThemeStylesReMatchHook?.Invoke(); // re-match the Theme(2) leg before the resource pulse (only when this is app.Theme)
        Pulse(ResourceChangeKind.CatchAll, null);
    }

    private void ThrowIfSealed()
    {
        if (IsSealed)
            throw new InvalidOperationException("This ResourceDictionary is sealed and cannot be mutated (CD4).");
    }

    private static void RejectUnset(object? value)
    {
        if (ReferenceEquals(value, UIProperty.UnsetValue))
            throw new ArgumentException("UIProperty.UnsetValue is the miss sentinel and cannot be a stored resource value (CD2).", nameof(value));
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<object, object?>> GetEnumerator()
    {
        foreach (var (key, slot) in _entries)
            yield return new KeyValuePair<object, object?>(key, Realize(key, slot));
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // The merged-dictionaries list with the single-parent guard + seal/recursion.
    private sealed class MergedList(ResourceDictionary owner) : IList<ResourceDictionary>
    {
        private List<ResourceDictionary> Items => owner._merged ??= [];

        public ResourceDictionary this[int index]
        {
            get => Items[index];
            set
            {
                owner.ThrowIfSealed();
                value.Acquire(owner);
                Items[index].Release(owner);
                Items[index] = value;
                owner.Pulse(ResourceChangeKind.CatchAll, null);
            }
        }

        public int Count => owner._merged?.Count ?? 0;
        public bool IsReadOnly => owner.IsSealed;

        public void Add(ResourceDictionary item)
        {
            ArgumentNullException.ThrowIfNull(item);
            owner.ThrowIfSealed();
            item.Acquire(owner);
            Items.Add(item);
            owner.Pulse(ResourceChangeKind.CatchAll, null);
        }

        public void Insert(int index, ResourceDictionary item)
        {
            ArgumentNullException.ThrowIfNull(item);
            owner.ThrowIfSealed();
            item.Acquire(owner);
            Items.Insert(index, item);
            owner.Pulse(ResourceChangeKind.CatchAll, null);
        }

        public bool Remove(ResourceDictionary item)
        {
            owner.ThrowIfSealed();
            if (!Items.Remove(item))
                return false;

            item.Release(owner);
            owner.Pulse(ResourceChangeKind.CatchAll, null);
            return true;
        }

        public void RemoveAt(int index)
        {
            owner.ThrowIfSealed();
            Items[index].Release(owner);
            Items.RemoveAt(index);
            owner.Pulse(ResourceChangeKind.CatchAll, null);
        }

        public void Clear()
        {
            owner.ThrowIfSealed();
            if (owner._merged is not { } items)
                return;

            foreach (var m in items)
                m.Release(owner);
            items.Clear();
            owner.Pulse(ResourceChangeKind.CatchAll, null);
        }

        public bool Contains(ResourceDictionary item) => owner._merged?.Contains(item) ?? false;
        public int IndexOf(ResourceDictionary item) => owner._merged?.IndexOf(item) ?? -1;
        public void CopyTo(ResourceDictionary[] array, int arrayIndex) => owner._merged?.CopyTo(array, arrayIndex);
        public IEnumerator<ResourceDictionary> GetEnumerator() => (owner._merged ?? Enumerable.Empty<ResourceDictionary>().ToList()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>A <see langword="using"/>-scoped <see cref="ResourceDictionary.BeginUpdate"/> coalescing window (design doc §11.1).</summary>
public struct ResourceUpdateScope(ResourceDictionary dictionary) : IDisposable
{
    private ResourceDictionary? _dictionary = dictionary;

    /// <inheritdoc/>
    public void Dispose()
    {
        _dictionary?.EndUpdate();
        _dictionary = null;
    }
}

/// <summary>A deferred-entry diagnostic snapshot (design doc §11.10, CD7).</summary>
/// <param name="Key">The entry's key.</param>
/// <param name="State">The realization state.</param>
/// <param name="RealizedAtVariant">The variant the StaticResource captures froze at (CD7), or <see langword="null"/> when not yet realized.</param>
public readonly record struct DeferredEntryInfo(object Key, ResourceDictionary.DeferredState State, ThemeVariant? RealizedAtVariant);
