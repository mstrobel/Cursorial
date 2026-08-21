using System.Diagnostics;

namespace Cursorial.UI.Data;

/// <summary>
/// The DEBUG-only binding leak tracker (design doc §6.5/§6.10; BD4/BD19). "Strong handlers cannot
/// leak" is a <em>contract</em> enforced by the teardown sweep; this tracker is the DEBUG backstop
/// that proves the contract held. On install it captures the binding path + install site + a weak
/// reference to the target; on dispose it forgets the expression. <see cref="Sweep"/> — run in a
/// DEBUG leak gate AFTER a teardown sweep (window close tears the tree down; tests then sweep) —
/// reports every expression whose target is still
/// <em>alive</em> yet was never disposed (a real leak: a missed teardown sweep). Release builds
/// compile the whole tracker out (no <see cref="Track"/> / <see cref="Forget"/> calls are emitted,
/// so there is zero release-build cost); the matrix's release rows assert the sweep does not run.
/// </summary>
public static class BindingLeakTracker
{
    /// <summary>One tracked install (DEBUG): the live expression, its target (weak), path, and install site.</summary>
    /// <param name="Path">The binding path text.</param>
    /// <param name="TargetDescription">The target descriptor (e.g. <c>Widget#a.Text</c>).</param>
    /// <param name="InstallSite">The captured install call site (the first non-engine frame).</param>
    public readonly record struct LeakReport(string Path, string TargetDescription, string InstallSite);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<BindingExpressionBase, Entry> Tracked = new(ReferenceEqualityComparer.Instance);

    private readonly record struct Entry(WeakReference<UIObject> Target, string Path, string Description, string InstallSite);

    /// <summary>Records <paramref name="expression"/> on install with its install site (DEBUG only).</summary>
    [Conditional("DEBUG")]
    internal static void Track(BindingExpressionBase expression, UIObject target, string path, string description)
    {
        var site = CaptureInstallSite();
        lock (Gate)
            Tracked[expression] = new Entry(new WeakReference<UIObject>(target), path, description, site);
    }

    /// <summary>Forgets <paramref name="expression"/> on disposal (DEBUG only — the contract met).</summary>
    [Conditional("DEBUG")]
    internal static void Forget(BindingExpressionBase expression)
    {
        lock (Gate)
            Tracked.Remove(expression);
    }

    /// <summary>
    /// The weak-target sweep (DEBUG only — call on window close): every still-tracked expression whose
    /// target is alive is a missed teardown (a leak). Tracked entries whose target was collected are
    /// pruned (the target died cleanly; no leak). Returns the leak reports.
    /// <para>
    /// <b>DEBUG only.</b> <see cref="Track"/>/<see cref="Forget"/> are <c>[Conditional("DEBUG")]</c>,
    /// so in a Release build nothing is ever tracked and this <em>always returns an empty list</em>.
    /// A Release-configuration leak gate would therefore be a false pass — run leak detection in a
    /// DEBUG build only.
    /// </para>
    /// </summary>
    public static IReadOnlyList<LeakReport> Sweep()
    {
        var reports = new List<LeakReport>();
#if DEBUG
        lock (Gate)
        {
            var dead = new List<BindingExpressionBase>();
            foreach (var (expression, entry) in Tracked)
            {
                if (entry.Target.TryGetTarget(out _))
                    reports.Add(new LeakReport(entry.Path, entry.Description, entry.InstallSite));
                else
                    dead.Add(expression); // the target was GC'd — clean, prune it
            }

            foreach (var expression in dead)
                Tracked.Remove(expression);
        }
#endif
        return reports;
    }

    /// <summary>The current count of tracked (undisposed) expressions (DEBUG; 0 in release).</summary>
    public static int TrackedCount
    {
        get
        {
            lock (Gate)
                return Tracked.Count;
        }
    }

    /// <summary>Test-only reset of the tracker state.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
            Tracked.Clear();
    }

    private static string CaptureInstallSite()
    {
        // The first stack frame outside the Cursorial.UI.Data engine namespace is the install site.
        var trace = new StackTrace(skipFrames: 1, fNeedFileInfo: false);
        for (var i = 0; i < trace.FrameCount; i++)
        {
            var method = trace.GetFrame(i)?.GetMethod();
            var ns = method?.DeclaringType?.Namespace;
            if (ns is null || !ns.StartsWith("Cursorial.UI.Data", StringComparison.Ordinal))
                return method is null ? "<unknown>" : $"{method.DeclaringType?.Name}.{method.Name}";
        }

        return "<unknown>";
    }
}
