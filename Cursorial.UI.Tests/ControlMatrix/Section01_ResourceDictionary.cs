using Cursorial.Output;
using Cursorial.UI;

using Style = Cursorial.UI.Style;

using static Cursorial.Tests.UI.ControlMatrix.ControlMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>§1 — <c>ResourceDictionary</c> storage, keys, merged/seal/deferred (R0); rows C1–C30.</summary>
public sealed class Section01_ResourceDictionary
{
    private const string K = "Brush.A";
    private const string K2 = "Brush.B";

    [Fact] // C1
    public void C1_KeyedStorage_RoundTrips()
    {
        var dict = new ResourceDictionary
                   {
                       [K] = Vbrush
                   };

        Assert.Same(Vbrush, dict[K]);
        Assert.True(dict.ContainsKey(K));
        Assert.Equal(1, dict.Count);
    }

    [Fact] // C2
    public void C2_StringKeys_AreOrdinal()
    {
        var dict = new ResourceDictionary
                   {
                       ["Theme.X"] = Vbrush
                   };

        Assert.False(dict.TryGetValue("theme.x", out _)); // distinct ordinal key
        dict["theme.x"] = Vbrush2;
        Assert.Equal(2, dict.Count); // two entries coexist
        Assert.Same(Vbrush, dict["Theme.X"]);
        Assert.Same(Vbrush2, dict["theme.x"]);
    }

    [Fact] // C3
    public void C3_TypeAndDataTemplateKeys_ResolveByValue_AndAreCollisionFree()
    {
        var dict = new ResourceDictionary
                   {
                       [typeof(Probe)] = "type-entry",
                       [new DataTemplateKey(typeof(Probe))] = "dt-entry"
                   };

        Assert.Equal("type-entry", dict[typeof(Probe)]);
        Assert.Equal("dt-entry", dict[new DataTemplateKey(typeof(Probe))]);
        Assert.Equal(2, dict.Count); // Type and DataTemplateKey are distinct keys
    }

    [Fact] // C4
    public void C4_UnsetValue_RejectedAtInsert_NullIsLegal()
    {
        var dict = new ResourceDictionary();
        Assert.Throws<ArgumentException>(() => dict[K] = UIProperty.UnsetValue);
        Assert.Throws<ArgumentException>(() => dict.Add(K2, UIProperty.UnsetValue));

        dict[K] = null;
        Assert.True(dict.TryGetValue(K, out var v));
        Assert.Null(v); // stored null is legal, distinct from a miss
    }

    [Fact] // C5
    public void C5_DuplicateAdd_Throws_IndexerOverwritesAndPulses()
    {
        var dict = new ResourceDictionary
                   {
                       [K] = Vbrush
                   };

        Assert.Throws<ArgumentException>(() => dict.Add(K, Vbrush2));

        var pulses = new List<ResourcesChangedEventArgs>();
        dict.Changed += (_, e) => pulses.Add(e);
        var versionBefore = dict.Version;

        dict[K] = Vbrush2;
        Assert.Same(Vbrush2, dict[K]);
        Assert.Equal(new ResourcesChangedEventArgs(ResourceChangeKind.Keyed, K), Assert.Single(pulses));
        Assert.True(dict.Version > versionBefore);
    }

    [Fact] // C6
    public void C6_Remove_PresentPulses_AbsentNoPulse()
    {
        var dict = new ResourceDictionary
                   {
                       [K] = Vbrush
                   };

        var pulses = new List<ResourcesChangedEventArgs>();
        dict.Changed += (_, e) => pulses.Add(e);

        Assert.True(dict.Remove(K));
        Assert.Equal(new ResourcesChangedEventArgs(ResourceChangeKind.Keyed, K), Assert.Single(pulses));
        var versionAfterRemove = dict.Version;

        Assert.False(dict.Remove("absent"));
        Assert.Single(pulses); // no new pulse
        Assert.Equal(versionAfterRemove, dict.Version);
    }

    [Fact] // C7
    public void C7_ContainsKeyKeysCount_DoNotRealize()
    {
        var dict = new ResourceDictionary();
        var entry = new CountingDeferred(_ => Vbrush);
        dict.SetDeferred(K, entry);

        Assert.True(dict.ContainsKey(K));
        _ = dict.Keys;
        _ = dict.Count;
        Assert.Equal(0, entry.Calls);

        Assert.True(dict.TryGetDeferredInfo(K, out var info));
        Assert.Equal(ResourceDictionary.DeferredState.Deferred, info.State);
    }

    [Fact] // C8
    public void C8_TryGetResource_SingleHop_Realizes()
    {
        var dict = new ResourceDictionary();
        var entry = new CountingDeferred(_ => Vbrush);
        dict.SetDeferred(K, entry);

        Assert.True(dict.TryGetResource(K, new ThemeVariant(ThemeBase.Dark, ColorDepth.Truecolor), out var value));
        Assert.Same(Vbrush, value);
        Assert.Equal(1, entry.Calls);
    }

    // ───────────────────────────── 1.2 MergedDictionaries ─────────────────────────────

    [Fact] // C9
    public void C9_OwnBeatsMerged()
    {
        var d = new ResourceDictionary { [K] = "A" };
        var m1 = new ResourceDictionary { [K] = "B" };
        d.MergedDictionaries.Add(m1);

        Assert.True(d.TryGetResource(K, Dark, out var v));
        Assert.Equal("A", v);
    }

    [Fact] // C10
    public void C10_LaterMergedWins()
    {
        var d = new ResourceDictionary();
        var m1 = new ResourceDictionary { [K] = "B" };
        var m2 = new ResourceDictionary { [K] = "C" };
        d.MergedDictionaries.Add(m1);
        d.MergedDictionaries.Add(m2);

        Assert.True(d.TryGetResource(K, Dark, out var v));
        Assert.Equal("C", v); // last-to-first scan
    }

    [Fact] // C11
    public void C11_OwnOfMerged_BeatsMergedOfMerged_Recursively()
    {
        var d = new ResourceDictionary();
        var m1 = new ResourceDictionary { [K] = "B" };
        var mm = new ResourceDictionary { [K] = "D" };
        m1.MergedDictionaries.Add(mm);
        d.MergedDictionaries.Add(m1);

        Assert.True(d.TryGetResource(K, Dark, out var v));
        Assert.Equal("B", v);
    }

    [Fact] // C12
    public void C12_SingleParentRule_NonSealedMergedAddTwice_Throws()
    {
        var d = new ResourceDictionary();
        var d2 = new ResourceDictionary();
        var m1 = new ResourceDictionary();
        d.MergedDictionaries.Add(m1);
        Assert.Throws<InvalidOperationException>(() => d2.MergedDictionaries.Add(m1));
    }

    [Fact] // C13
    public void C13_SealedExempt_FreelyShared()
    {
        var d = new ResourceDictionary();
        var d2 = new ResourceDictionary();
        var m1 = new ResourceDictionary { [K] = "B" };
        m1.Seal();

        d.MergedDictionaries.Add(m1);
        d2.MergedDictionaries.Add(m1); // sealed-exempt sharing
        Assert.True(d.TryGetResource(K, Dark, out _));
        Assert.True(d2.TryGetResource(K, Dark, out _));
    }

    // ───────────────────────────── 1.3 Seal + BeginUpdate + Version ─────────────────────────────

    [Fact] // C14
    public void C14_Seal_RecursivelyFreezes_AndMutationsThrow()
    {
        var d = new ResourceDictionary { [K] = Vbrush };
        var merged = new ResourceDictionary { [K2] = Vbrush2 };
        d.MergedDictionaries.Add(merged);
        var sub = new ResourceDictionary { [K] = Vbrush };
        d.ThemeDictionaries[new ThemeVariantKey(ThemeBase.Dark, null)] = sub;

        d.Seal();
        Assert.True(d.IsSealed);
        Assert.True(merged.IsSealed); // recursive
        Assert.True(sub.IsSealed);

        Assert.Throws<InvalidOperationException>(() => d[K] = Vbrush2);
        Assert.Throws<InvalidOperationException>(() => d.Add("new", Vbrush));
        Assert.Throws<InvalidOperationException>(() => d.Remove(K));
        Assert.Throws<InvalidOperationException>(() => merged[K] = Vbrush);
    }

    [Fact] // C15
    public void C15_SealedDeferredRealization_LegalNoPulseNoVersion()
    {
        var d = new ResourceDictionary();
        var entry = new CountingDeferred(_ => Vbrush);
        d.SetDeferred(K, entry);
        d.Seal();

        var pulses = 0;
        d.Changed += (_, _) => pulses++;
        var versionBefore = d.Version;

        Assert.Same(Vbrush, d[K]); // cache fill legal on sealed
        Assert.Equal(1, entry.Calls);
        Assert.Equal(0, pulses);
        Assert.Equal(versionBefore, d.Version);
    }

    [Fact] // C16
    public void C16_BeginUpdate_CoalescesToOneCatchAll()
    {
        var d = new ResourceDictionary();
        var pulses = new List<ResourcesChangedEventArgs>();
        d.Changed += (_, e) => pulses.Add(e);
        var versionBefore = d.Version;

        using (d.BeginUpdate())
        {
            d[K] = Vbrush;
            d[K2] = Vbrush2;
        }

        Assert.Equal(new ResourcesChangedEventArgs(ResourceChangeKind.CatchAll, null), Assert.Single(pulses));
        Assert.True(d.Version > versionBefore);
    }

    [Fact] // C17
    public void C17_NestedBeginUpdate_OutermostPulsesOnce_SealInsideThrows()
    {
        var d = new ResourceDictionary();
        var pulses = 0;
        d.Changed += (_, _) => pulses++;

        using (d.BeginUpdate())
        {
            using (d.BeginUpdate())
            {
                d[K] = Vbrush;
            }

            Assert.Equal(0, pulses);                          // inner dispose does not pulse
            Assert.Throws<InvalidOperationException>(d.Seal); // seal inside an open scope throws
            d[K2] = Vbrush2;
        }

        Assert.Equal(1, pulses); // refcounted: one pulse at the outermost dispose
    }

    [Fact] // C18
    public void C18_Version_BumpsOnStructuralChange_NotOnDeferredCacheFill()
    {
        var d = new ResourceDictionary();
        var v0 = d.Version;
        d[K] = Vbrush;
        var v1 = d.Version;
        Assert.True(v1 > v0);

        d[K] = Vbrush; // same value — an overwrite still structural
        var v2 = d.Version;
        Assert.True(v2 > v1);

        d[K] = Vbrush2;
        var v3 = d.Version;
        Assert.True(v3 > v2);

        d.Remove(K);
        Assert.True(d.Version > v3);
    }

    // ───────────────────────────── 1.4 Deferred entries ─────────────────────────────

    [Fact] // C19
    public void C19_DeferredRealization_AtMostOnceOnSuccess()
    {
        var d = new ResourceDictionary();
        var entry = new CountingDeferred(_ => Vbrush);
        d.SetDeferred(K, entry);

        Assert.Same(Vbrush, d[K]);
        Assert.Equal(1, entry.Calls);
        Assert.Same(Vbrush, d[K]); // cached
        Assert.Equal(1, entry.Calls);
    }

    [Fact] // C20
    public void C20_RealizationMutatesSlotInPlace_EnumerationSafe_VersionStable()
    {
        var d = new ResourceDictionary();
        d.SetDeferred(K, new CountingDeferred(_ => Vbrush));
        var versionBefore = d.Version;

        // Enumerating values triggers realization; the slot is mutated in place so the version check
        // does not trip mid-enumeration.
        var seen = new List<object?>();
        foreach (var pair in d)
            seen.Add(pair.Value);

        Assert.Same(Vbrush, Assert.Single(seen));
        Assert.Equal(versionBefore, d.Version);
    }

    [Fact] // C21
    public void C21_TransientRealizationFailure_NotPoisoned()
    {
        var d = new ResourceDictionary();
        var attempt = 0;
        d.SetDeferred(K, new CountingDeferred(_ => attempt++ == 0 ? throw new InvalidOperationException("boom") : Vbrush));

        Assert.Throws<InvalidOperationException>(() => _ = d[K]);
        Assert.True(d.TryGetDeferredInfo(K, out var info));
        Assert.Equal(ResourceDictionary.DeferredState.Deferred, info.State); // reset to Deferred

        Assert.Same(Vbrush, d[K]); // retried successfully
    }

    [Fact] // C22
    public void C22_RealizationCycle_Throws()
    {
        var d = new ResourceDictionary();
        var entry = new CountingDeferred(_ => d[K]); // re-enters the same key
        d.SetDeferred(K, entry);

        Assert.Throws<InvalidOperationException>(() => _ = d[K]);
    }

    [Fact] // C23
    public void C23_StaticResourceCapture_FreezesAtFirstRealizationVariant()
    {
        // The deferred entry captures the variant at realization; the matrix asserts RealizedAtVariant
        // makes the freeze observable (a unit-level proxy for the variant-frozen StaticResource).
        var d = new ResourceDictionary();
        d.SetDeferred(K, new CountingDeferred(_ => Vbrush));

        Assert.True(d.TryGetResource(K, new ThemeVariant(ThemeBase.Dark, ColorDepth.Ansi256), out _));
        Assert.True(d.TryGetDeferredInfo(K, out var info));
        Assert.Equal(ResourceDictionary.DeferredState.Realized, info.State);
        Assert.NotNull(info.RealizedAtVariant);
    }

    [Fact] // C24
    public void C24_DeferredRealization_ReceivesLexicalScope()
    {
        var d = new ResourceDictionary { ["StaticTarget"] = Vbrush2 };
        IResourceScope? captured = null;
        d.SetDeferred(K, new CountingDeferred(scope =>
        {
            captured = scope;
            scope.TryGetResource("StaticTarget", out var resolved); // resolve a StaticResource against the lexical scope
            return resolved;
        }));

        Assert.Same(Vbrush2, d[K]);
        Assert.NotNull(captured);
    }

    // ───────────────────────────── 1.5 Styles slot + ThemeDictionaries ─────────────────────────────

    [Fact] // C25
    public void C25_StylesSlot_SetAndMutate_RouteCatchAll()
    {
        var d = new ResourceDictionary();
        var pulses = new List<ResourcesChangedEventArgs>();
        d.Changed += (_, e) => pulses.Add(e);

        var styles = new Styles();
        d.Styles = styles;
        Assert.Same(styles, d.Styles);
        Assert.Equal(new ResourcesChangedEventArgs(ResourceChangeKind.CatchAll, null), pulses[^1]);

        var before = pulses.Count;
        styles.Add(new Style("Probe", new TestResolver()) { Setters = { new Setter(Probe.P, Vbrush) } });
        Assert.True(pulses.Count > before);
        Assert.Equal(ResourceChangeKind.CatchAll, pulses[^1].Kind);
    }

    [Fact] // C26
    public void C26_ThemeDictionaries_KeyedByVariantKey_LazyAllocated()
    {
        var d = new ResourceDictionary();
        var sub = new ResourceDictionary { [K] = Vbrush };
        d.ThemeDictionaries["Dark+Ansi16"] = sub;

        Assert.Same(sub, d.ThemeDictionaries[ThemeVariantKey.Parse("Dark+Ansi16")]);
        Assert.True(d.ThemeDictionaries.ContainsKey(new ThemeVariantKey(ThemeBase.Dark, ColorDepth.Ansi16)));
    }

    [Fact] // C27
    public void C27_NoThemeDictionaries_ShortCircuitsProbe()
    {
        var d = new ResourceDictionary { [K] = Vbrush };
        Assert.False(d.HasThemeDictionaries);
        Assert.True(d.TryGetResource(K, Dark, out var v));
        Assert.Same(Vbrush, v);
    }

    [Fact] // C28
    public void C28_SourceWithoutLoader_InformativeThrow()
    {
        var saved = ResourceDictionary.LoadCallback;
        try
        {
            ResourceDictionary.LoadCallback = null;
            var d = new ResourceDictionary();
            Assert.Throws<InvalidOperationException>(() => d.Source = new Uri("avares://x"));
        }
        finally
        {
            ResourceDictionary.LoadCallback = saved;
        }
    }

    [Fact] // C29
    public void C29_HasResources_NoLazyAllocOnRead()
    {
        var probe = new Probe();
        Assert.False(probe.HasResources); // no lazy-alloc on the HasResources read
        _ = probe.Resources;             // lazy-allocates
        probe.Resources[K] = Vbrush;
        Assert.True(probe.HasResources);
    }

    [Fact] // C30
    public void C30_DeferredEntries_ReportsStateAndVariant()
    {
        var d = new ResourceDictionary();
        d.SetDeferred(K, new CountingDeferred(_ => Vbrush));

        var before = ResourceDiagnostics.DeferredEntries(d);
        Assert.Equal(ResourceDictionary.DeferredState.Deferred, Assert.Single(before).State);

        _ = d[K]; // realize
        var after = ResourceDiagnostics.DeferredEntries(d);
        var info = Assert.Single(after);
        Assert.Equal(ResourceDictionary.DeferredState.Realized, info.State);
        Assert.NotNull(info.RealizedAtVariant);
    }

    private static ThemeVariant Dark => new(ThemeBase.Dark, ColorDepth.Truecolor);

    // A minimal selector type resolver for the Styles-slot row.
    private sealed class TestResolver : ISelectorTypeResolver
    {
        public Type? Resolve(string typeName, out IReadOnlyList<Type>? ambiguousCandidates)
        {
            ambiguousCandidates = null;
            return typeName == "Probe" ? typeof(Probe) : null;
        }
    }
}
