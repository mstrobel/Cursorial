using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Testing;

using static Cursorial.Tests.UI.ControlMatrix.ControlMatrixFixture;

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>§5 — Resource diagnostics (R3); rows C113–C117.</summary>
public sealed class Section05_ResourceDiagnostics
{
    private const string K = "Brush.A";

    [Fact] // C113 — Trace: one line per hop incl. a marked hit
    public void C113_Trace_OneLinePerHop_HitMarked()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush; // a hit several hops up
        host.ShowRoot(tree.Root);

        var trace = ResourceDiagnostics.Trace(tree.Leaf, K);
        Assert.True(trace.Count >= 2); // at least the leaf/midA skip lines + the root hit
        Assert.Contains(trace, line => line.Contains("HIT"));
    }

    [Fact] // C114 — Explain (miss): renders the full searched chain, ending in BuiltIn
    public void C114_Explain_Miss_RendersChainEndingInBuiltIn()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        var explain = ResourceDiagnostics.Explain(tree.Leaf, "Nope.Missing");
        Assert.Contains(explain, line => line.Contains("UIApplication.Resources"));
        Assert.Contains(explain, line => line.Contains("BuiltIn"));
        Assert.DoesNotContain(explain, line => line.Contains("HIT")); // a miss
    }

    [Fact] // C115 — Subscriptions: lists live nodes; zero after detach
    public void C115_Subscriptions_LiveThenZeroAfterDetach()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);

        tree.Leaf.SetResourceReference(Probe.P, K);
        Assert.True(ResourceDiagnostics.Subscriptions(tree.Root) > 0);

        tree.MidA.RemoveChildForTest(tree.Leaf); // detach the owner
        Assert.Equal(0, ResourceDiagnostics.Subscriptions(tree.Root));
    }

    [Fact] // C116 — DeferredEntries: state + RealizedAtVariant
    public void C116_DeferredEntries_StatesAndVariant()
    {
        var d = new ResourceDictionary();
        d.SetDeferred(K, new CountingDeferred(_ => Vbrush));
        d.SetDeferred("Other", new CountingDeferred(_ => Vbrush2));

        var before = ResourceDiagnostics.DeferredEntries(d);
        Assert.Equal(2, before.Count);
        Assert.All(before, e => Assert.Equal(ResourceDictionary.DeferredState.Deferred, e.State));

        _ = d[K]; // realize one
        var after = ResourceDiagnostics.DeferredEntries(d);
        var realized = Assert.Single(after, e => e.State == ResourceDictionary.DeferredState.Realized);
        Assert.NotNull(realized.RealizedAtVariant);
    }

    [Fact] // C113/C114 companion: the one-time miss diagnostic fires (DEBUG-conventional channel)
    public void MissDiagnostic_FiresOncePerReference()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        ResourceDiagnostics.ResetMissMemo();
        var messages = new List<string>();
        void Handler(string m) => messages.Add(m);
        ResourceDiagnostics.ResourceMissed += Handler;
        try
        {
            tree.Leaf.SetResourceReference(Probe.P, "Missing.Key"); // a miss ⇒ one-time diagnostic
            Assert.Single(messages);
        }
        finally
        {
            ResourceDiagnostics.ResourceMissed -= Handler;
        }
    }
}
