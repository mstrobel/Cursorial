// ReSharper disable CheckNamespace
// ReSharper disable UnusedParameter.Local

namespace Cursorial.UI;

/// <summary>
/// One per visual root (design doc §11.6): the keyed subscription buckets + flat node list, the
/// pulse-routing sweep with snapshot/tombstone semantics, scope containment, and Pause/Resume. The
/// owning <c>UIApplication</c> fans application-level changes to every root's registry.
/// </summary>
internal sealed class ResourceSubscriptionRegistry(UIApplication app)
{
    // One flat list; Pause/Resume are flag writes, sweeps test flags per node (design doc §11.6 —
    // no segregated active list). Keyed buckets accelerate the keyed-pulse path.
    private readonly Dictionary<object, List<Node>> _byKey = new(ResourceKeyComparer.Instance);
    private readonly List<Node> _all = [];
    // Nodes whose resolved value chased through ≥1 ResourceReference alias (§11.4a). A keyed pulse for a role
    // token must reach a subscription bucketed on a per-control ALIAS key whose chain targets that token — the
    // alias key isn't in the pulsed key's bucket, so these are re-resolved on every keyed pulse (each short-
    // circuits on an unchanged value). Stale entries (a node that stopped depending on an alias) are skipped by
    // the DependsOnAlias check and dropped at compaction.
    private readonly List<Node> _aliasNodes = [];
    private int _sweepDepth;          // >0 ⇒ mid-sweep (snapshot/tombstone semantics)
    private bool _needsCompaction;
    private int _pendingFollowups;    // re-entrant mutation queues a follow-up pulse

    /// <summary>The root-global monotonic version (design doc §11.6 — bumps on every pulse reaching this root).</summary>
    public int Version { get; private set; }

    /// <summary>The number of live (non-dead) nodes — the leak-tracking surface (C85/C115).</summary>
    public int LiveNodeCount
    {
        get
        {
            var count = 0;
            foreach (var node in _all)
                if (!node.Dead)
                    count++;
            return count;
        }
    }

    /// <summary>Subscribes a listener for a key against a scope, resolving the initial value (design doc §11.5).</summary>
    public Node Subscribe(UIElement scope, object key, IResourceChangeListener listener, out object? initialValue)
    {
        var resolved = Resolve(scope, key);
        initialValue = resolved.found ? resolved.value : UIProperty.UnsetValue;

        var node = new Node(this, scope, key, listener)
                   {
                       LastValue = initialValue,
                       ResolvedVersion = Version
                   };

        _all.Add(node);
        Bucket(key).Add(node);
        TrackAlias(node, resolved.chased);
        return node;
    }

    // Adds the node to the alias-dependent set the first time it resolves through an alias (§11.4a).
    private void TrackAlias(Node node, bool chased)
    {
        node.DependsOnAlias = chased;
        if (chased && !node.InAliasList)
        {
            node.InAliasList = true;
            _aliasNodes.Add(node);
        }
    }

    private List<Node> Bucket(object key)
    {
        if (!_byKey.TryGetValue(key, out var list))
            _byKey[key] = list = [];
        return list;
    }

    /// <summary>A keyed pulse: re-resolve every contained node whose key matches OR that shadowing could affect (design doc §11.6).</summary>
    public void PulseKeyed(UIElement pulsingScope, object key)
    {
        Version++;

        if (_byKey.TryGetValue(key, out var bucket))
            Sweep(bucket, pulsingScope, key);

        // Alias-dependent subscriptions (§11.4a): a per-control alias whose chain targets `key` is bucketed on
        // the alias key, not `key`, so it isn't in the bucket above — re-resolve them too. The per-pulse Version
        // guard in Sweep dedups any node already handled in the key bucket; each re-resolve short-circuits on an
        // unchanged value (a keyed pulse of an unrelated key touches no alias node's value).
        if (_aliasNodes.Count > 0)
            Sweep(_aliasNodes, pulsingScope, key, aliasOnly: true);

        DrainFollowups();
    }

    /// <summary>A catch-all pulse (theme swap, variant flip, BeginUpdate): re-resolve every contained node (design doc §11.6).</summary>
    public void PulseCatchAll(UIElement? pulsingScope)
    {
        Version++;
        Sweep(_all, pulsingScope, key: null);
        DrainFollowups();
    }

    private void Sweep(List<Node> candidates, UIElement? pulsingScope, object? key, bool aliasOnly = false)
    {
        // Snapshot/tombstone: copy the candidate list so mid-sweep Subscribe/Dispose is safe (a node
        // subscribed during the sweep isn't visited; a disposed one is Dead-skipped) — design doc §11.6.
        var snapshot = candidates.ToArray();
        _sweepDepth++;
        try
        {
            foreach (var node in snapshot)
            {
                if (node.Dead || node.Paused)
                    continue;

                // Already re-resolved earlier in THIS pulse (the alias sweep runs after the key bucket — §11.4a).
                if (node.ResolvedVersion == Version)
                    continue;

                // Stale alias-list entry: the node stopped depending on an alias (dropped at compaction).
                if (aliasOnly && !node.DependsOnAlias)
                    continue;

                // Scope containment (design doc §11.6): only nodes whose scope is the pulsing scope
                // or a logical descendant of it re-resolve — the nearer-scope shadowing that makes
                // C79/C80 correct. A catch-all (null scope) touches everyone.
                if (pulsingScope is not null && !IsContained(node.Scope, pulsingScope))
                    continue;

                ReResolve(node);
            }
        }
        finally
        {
            _sweepDepth--;
            if (_sweepDepth == 0 && _needsCompaction)
                Compact();
        }
    }

    private void ReResolve(Node node)
    {
        node.ResolvedVersion = Version;
        var resolved = Resolve(node.Scope, node.Key);
        var newValue = resolved.found ? resolved.value : UIProperty.UnsetValue;

        // Keep alias-dependency current: an app re-keying a resource can turn a direct node into an aliased one.
        TrackAlias(node, resolved.chased);

        if (Equals(node.LastValue, newValue))
            return; // identity / value short-circuit (design doc §11.6)

        node.LastValue = newValue;
        node.Listener.OnResourceChanged(node.Key, newValue);
    }

    /// <summary>Resume catch-up (design doc §11.5): re-resolve at most once iff a pulse happened while paused.</summary>
    internal void ResumeNode(Node node)
    {
        if (node.Dead)
            return;

        if (node.ResolvedVersion != Version)
            ReResolve(node);
    }

    /// <summary>Forces one re-resolve regardless of stored version (element attach — covers cross-root moves, CD16).</summary>
    internal void ForceReResolve(Node node)
    {
        if (!node.Dead && !node.Paused)
            ReResolve(node);
    }

    internal void Remove(Node node)
    {
        if (node.Dead)
            return;

        node.Dead = true;

        if (_sweepDepth > 0)
        {
            _needsCompaction = true; // tombstone; compacted after the sweep
            return;
        }

        Compact();
    }

    private void Compact()
    {
        _needsCompaction = false;
        _all.RemoveAll(static n => n.Dead);
        foreach (var bucket in _byKey.Values)
            bucket.RemoveAll(static n => n.Dead);
        // Drop dead nodes AND stale entries (nodes that stopped depending on an alias); clear InAliasList on the
        // latter so a later re-aliasing re-adds them.
        _aliasNodes.RemoveAll(static n =>
        {
            if (n.Dead)
                return true;
            if (n.DependsOnAlias)
                return false;
            n.InAliasList = false;
            return true;
        });
    }

    /// <summary>Re-entrant resource mutation during a sweep queues a follow-up pulse, drained to a fixpoint (design doc §11.6).</summary>
    internal void QueueFollowup()
    {
        if (_sweepDepth > 0)
            _pendingFollowups++;
    }

    private void DrainFollowups()
    {
        var generation = 0;
        while (_pendingFollowups > 0)
        {
            _pendingFollowups = 0;
            if (++generation > 16)
            {
                ResourceDiagnostics.OnCycle("Resource pulse follow-up exceeded generation cap 16 (a cyclic resource mutation, design doc §11.6).");
                break;
            }

            Version++;
            Sweep(_all, pulsingScope: null, key: null);
        }
    }

    private (bool found, object? value, bool chased) Resolve(UIElement scope, object key)
    {
        var variant = app.ActualThemeVariant;
        return ResourceExtensions.Walk(scope, key, variant, searched: null, out var value, out var chased)
            ? (true, value, chased)
            : (false, null, chased);
    }

    private static bool IsContained(UIElement node, UIElement scope)
    {
        // node is contained by scope iff scope is node or an ancestor reachable along the SAME chain
        // the resolution walk uses (ResourceExtensions.Walk): logical ancestors, with the template-root
        // hop to TemplatedParent when LogicalParent is null. Mirroring the walk keeps containment honest
        // for template-scoped parts (a higher-ancestor keyed pulse whose scope is a template part's
        // TemplatedParent must still reach the part). The VisualParent fallback covers a not-yet-fully-
        // logical-linked attach edge.
        for (var current = (UIElement?)node; current is not null;)
        {
            if (ReferenceEquals(current, scope))
                return true;

            if (current.LogicalParent is { } logicalParent)
                current = logicalParent;
            else if (current.TemplatedParent is { } templatedParent)
                current = templatedParent;
            else
                current = current.VisualParent;
        }

        return false;
    }

    /// <summary>A registry subscription node (design doc §11.6): scope + listener + lastValue + resolvedVersion + flags.</summary>
    internal sealed class Node(ResourceSubscriptionRegistry registry, UIElement scope, object key, IResourceChangeListener listener)
    {
        public UIElement Scope { get; } = scope;
        public object Key { get; } = key;
        public IResourceChangeListener Listener { get; } = listener;
        public object? LastValue { get; set; }
        public int ResolvedVersion { get; set; }
        public bool Paused { get; private set; }
        public bool Dead { get; set; }

        /// <summary>Whether this node's value currently resolves through ≥1 <see cref="ResourceReference"/> alias (§11.4a).</summary>
        public bool DependsOnAlias { get; set; }

        /// <summary>Whether this node is currently a member of the registry's alias-node list (de-dups adds).</summary>
        public bool InAliasList { get; set; }

        public void Pause() => Paused = true;

        public void Resume()
        {
            if (!Paused)
                return;
            Paused = false;
            registry.ResumeNode(this);
        }

        public void Dispose() => registry.Remove(this);
    }
}
