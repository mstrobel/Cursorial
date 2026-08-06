using Cursorial.Media;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

using static Cursorial.Tests.UI.ControlMatrix.ControlMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>§5 — Resource diagnostics (R3); rows C113–C117.</summary>
public sealed class Section05_ResourceDiagnostics
{
    private const string K = "Brush.A";

    [Fact] // C113 — Trace: one line per hop incl. a marked hit
    public void C113_Trace_OneLinePerHop_HitMarked()
    {
        using var host = UIHeadlessHost.Create();
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
        using var host = UIHeadlessHost.Create();
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
        using var host = UIHeadlessHost.Create();
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
        using var host = UIHeadlessHost.Create();
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

    // ───────────────────────────── W3: GetResourceKey (resource-inspector hook) ─────────────────────────────

    [Fact] // GetResourceKey returns the key an instance SetResourceReference / {DynamicResource} feeds (the reverse of Trace)
    public void GetResourceKey_InstanceDynamicResource_ReturnsKey()
    {
        using var host = UIHeadlessHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);

        tree.Leaf.SetResourceReference(Probe.P, K);
        host.RunUntilIdle();

        Assert.Equal(K, ResourceDiagnostics.GetResourceKey(tree.Leaf, Probe.P));
        Assert.Same(Vbrush, tree.Leaf.GetValue(Probe.P)); // the key the hook names actually feeds the property
    }

    [Fact] // a literal (non-resource) value has no resource provenance
    public void GetResourceKey_LiteralValue_ReturnsNull()
    {
        using var host = UIHeadlessHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        tree.Leaf.SetValue(Probe.P, Vbrush); // a plain value, not resource-backed
        Assert.Null(ResourceDiagnostics.GetResourceKey(tree.Leaf, Probe.P));
    }

    [Fact] // a themed control's style {DynamicResource} setter exposes its key (the style-lane half of the hook)
    public void GetResourceKey_StyleResourceSetter_ReturnsKey()
    {
        using var host = UIHeadlessHost.Create();
        var listBox = new ListBox();
        host.ShowRoot(listBox);
        host.RunUntilIdle();

        // The BuiltIn ListBox control theme resolves Background via SetResource(WellBrush).
        Assert.Equal(ThemeKeys.WellBrush, ResourceDiagnostics.GetResourceKey(listBox, Control.BackgroundProperty));
        // The named key actually feeds the property: the effective Background equals the resolved WellBrush.
        Assert.Equal(listBox.FindResource(ThemeKeys.WellBrush), listBox.GetValue(Control.BackgroundProperty));
    }

    [Fact] // W7 #5: a LocalValue masking a resource-backed style setter is NOT resource-backed — report no key
    public void GetResourceKey_LocalValueMasksStyleResource_ReturnsNull()
    {
        using var host = UIHeadlessHost.Create();
        var listBox = new ListBox();
        host.ShowRoot(listBox);
        host.RunUntilIdle();

        // At rest the WellBrush DynamicResource setter (Style lane) owns Background → the hook names it.
        Assert.Equal(ThemeKeys.WellBrush, ResourceDiagnostics.GetResourceKey(listBox, Control.BackgroundProperty));

        // A LocalValue literal wins over the Style lane (LocalValue > Style): the effective value is the literal,
        // not resource-backed, so GetResourceKey must report null rather than the masked style setter's key.
        listBox.SetValue(Control.BackgroundProperty, Vbrush);
        host.RunUntilIdle();
        Assert.Null(ResourceDiagnostics.GetResourceKey(listBox, Control.BackgroundProperty));
    }

    private sealed class Shell : ContentControl;

    [Fact] // audit fix 2026-07-12: an active conditional rule piercing a Template-lane reference is NOT resource-backed
    public void GetResourceKey_TriggerRulePiercesTemplateResource_ReturnsNull_ThenKeyOnRetraction()
    {
        var constant = new Cursorial.Drawing.Media.SolidColorBrush(Color.FromRgb(90, 0, 0));

        using var host = UIHeadlessHost.Create();
        Cursorial.UI.Controls.Border? part = null;
        var shell = new Shell
        {
            Template = new ControlTemplate(_ =>
            {
                part = new Cursorial.UI.Controls.Border();
                part.SetResourceReference(Control.BackgroundProperty, K); // the Template-lane reference
                return part;
            }),
        };
        host.Application.Resources[K] = Vbrush;
        host.Application.Styles.Add(
            new Style(Selectors.OfType<Shell>().Class("alert").Template().OfType<Cursorial.UI.Controls.Border>())
                .Set(Control.BackgroundProperty, constant)); // conditional ⇒ StyleTrigger — pierces Template while active
        host.ShowRoot(shell);
        host.RunFrame();

        // Resting: the Template-lane reference owns the winning base — the hook names its key.
        Assert.Equal(K, ResourceDiagnostics.GetResourceKey(part!, Control.BackgroundProperty));

        shell.Classes.Add("alert");
        host.RunFrame();
        Assert.Same(constant, part!.GetValue(Control.BackgroundProperty));
        // Pierced: the effective value is the trigger rule's CONSTANT — the live subscription must
        // not make the hook claim the red hover came from the theme key (the provenance-lie gate).
        Assert.Null(ResourceDiagnostics.GetResourceKey(part, Control.BackgroundProperty));

        shell.Classes.Remove("alert");
        host.RunFrame();
        Assert.Equal(K, ResourceDiagnostics.GetResourceKey(part, Control.BackgroundProperty)); // clean retraction
    }
}
