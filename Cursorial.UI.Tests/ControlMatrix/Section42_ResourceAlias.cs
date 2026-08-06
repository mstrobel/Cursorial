using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

using static Cursorial.Tests.UI.ControlMatrix.ControlMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §42 — resource-alias (<see cref="ResourceReference"/>) chasing in the lookup chain (design doc §11.4a),
/// the per-control-key spine: a resolved value that is a <see cref="ResourceReference"/> is a live alias of
/// another key, re-resolved from the SAME element so an app override of either the alias key OR its target
/// wins, bounded against cycles. The style-guide per-control <c>Theme.&lt;Control&gt;&lt;Role&gt;</c> keys
/// (e.g. <c>ButtonBackgroundNormal</c>) are such aliases of the palette role tokens.
/// </summary>
public sealed class Section42_ResourceAlias
{
    private const string Alias = "Alias.One";
    private const string Alias2 = "Alias.Two";

    [Fact] // A stored ResourceReference is chased to its target value (one hop).
    public void StoredAlias_ChasesToTarget()
    {
        using var host = UIHeadlessHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        host.Application.Resources[ThemeKeys.SurfaceBrush] = Vbrush; // a concrete target in the chain
        host.Application.Resources[Alias] = new ResourceReference(ThemeKeys.SurfaceBrush);

        Assert.Same(Vbrush, tree.Leaf.FindResource(Alias));
    }

    [Fact] // An app override of the ALIAS KEY itself wins over the alias indirection (the per-control override).
    public void AliasKeyOverride_Wins()
    {
        using var host = UIHeadlessHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        host.Application.Resources[Alias] = new ResourceReference(ThemeKeys.SurfaceBrush);
        // A nearer scope re-keys the alias to a concrete brush — overriding "just this key".
        tree.MidA.Res[Alias] = Vbrush2;

        Assert.Same(Vbrush2, tree.Leaf.FindResource(Alias));
    }

    [Fact] // An override of the alias TARGET cascades through the alias (resolve re-chases from the element).
    public void AliasTargetOverride_CascadesThroughAlias()
    {
        using var host = UIHeadlessHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        host.Application.Resources[Alias] = new ResourceReference(ThemeKeys.SurfaceBrush);
        // Override the TARGET nearer than BuiltIn — every alias consumer follows.
        tree.Root.Res[ThemeKeys.SurfaceBrush] = Vbrush;

        Assert.Same(Vbrush, tree.Leaf.FindResource(Alias));
    }

    [Fact] // A multi-hop alias chain resolves (A → B → concrete).
    public void MultiHopChain_Resolves()
    {
        using var host = UIHeadlessHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        host.Application.Resources[ThemeKeys.SurfaceBrush] = Vbrush;
        host.Application.Resources[Alias2] = new ResourceReference(ThemeKeys.SurfaceBrush);
        host.Application.Resources[Alias] = new ResourceReference(Alias2);

        Assert.Same(Vbrush, tree.Leaf.FindResource(Alias));
    }

    [Fact] // A self-referential alias is bounded (no infinite loop): a miss + the Cycle diagnostic.
    public void CyclicAlias_BoundedAndDiagnosed()
    {
        using var host = UIHeadlessHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        host.Application.Resources[Alias] = new ResourceReference(Alias); // points at itself

        string? cycle = null;
        void Handler(string m) => cycle ??= m;
        ResourceDiagnostics.Cycle += Handler;
        try
        {
            Assert.False(tree.Leaf.TryFindResource(Alias, out var value));
            Assert.Null(value);
        }
        finally
        {
            ResourceDiagnostics.Cycle -= Handler;
        }

        Assert.NotNull(cycle);
        Assert.Contains(Alias, cycle);
    }

    [Fact] // A live DynamicResource on a property, through an alias, re-resolves on a variant flip (the cascade).
    public void AliasedSubscription_FollowsVariantFlip()
    {
        using var host = UIHeadlessHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        host.Application.RequestedThemeBase = ThemeBase.Dark;
        // The alias targets a palette role token whose brush differs per base — so a flip MUST re-push.
        host.Application.Resources[Alias] = new ResourceReference(ThemeKeys.AccentBrush);
        tree.Leaf.SetResourceReference(Probe.P, Alias);
        host.RunUntilIdle();

        var dark = Assert.IsType<SolidColorBrush>(tree.Leaf.GetValue(Probe.P));
        Assert.Equal(Color.FromHex("#6090f6"), dark.Color);

        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunUntilIdle();

        var light = Assert.IsType<SolidColorBrush>(tree.Leaf.GetValue(Probe.P));
        Assert.Equal(Color.FromHex("#34548a"), light.Color);
    }

    [Fact] // CA7 — the BuiltIn per-control key (ButtonBackgroundNormal) is a live alias of the SurfaceBrush role token.
    public void BuiltInPerControlKey_AliasesRoleToken()
    {
        using var host = UIHeadlessHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        host.Application.RequestedThemeBase = ThemeBase.Dark;

        var perControl = tree.Leaf.FindResource(ThemeKeys.ButtonBackgroundNormal);
        var roleToken = tree.Leaf.FindResource(ThemeKeys.SurfaceBrush);
        Assert.Same(roleToken, perControl); // chases to the very same brush instance
    }
}
