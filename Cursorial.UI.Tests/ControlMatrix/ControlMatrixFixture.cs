using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// The §0.2 fixture surface for the resource + control-infrastructure matrix (R0–R3 rows). A
/// <see cref="Probe"/> is a plain <see cref="UIElement"/> with optional <c>Resources</c> and a public
/// child-adoption shim; the helpers build the canonical Root→midA→leaf tree and capability snapshots.
/// </summary>
public static class ControlMatrixFixture
{
    /// <summary>A distinct <see cref="SolidColorBrush"/> value (a Drawing instance), per the §0.2 fixture.</summary>
    public static readonly SolidColorBrush Vbrush = new(Color.FromRgb(0xF9, 0x26, 0x72));

    /// <summary>A second distinct brush value.</summary>
    public static readonly SolidColorBrush Vbrush2 = new(Color.FromRgb(0x66, 0xD9, 0xEF));

    /// <summary>The §0.2 default tree: Root → midA → leaf (Probes with optional Resources).</summary>
    public static Tree BuildTree()
    {
        var root = new Probe { Name = "Root" };
        var midA = new Probe { Name = "midA" };
        var leaf = new Probe { Name = "leaf" };
        root.Adopt(midA);
        midA.Adopt(leaf);
        return new Tree(root, midA, leaf);
    }

    /// <summary>Builds a capability snapshot from a preset, optionally overriding background + depth (the §0.2 <c>caps(...)</c>/<c>bg(...)</c> helper).</summary>
    public static TerminalCapabilities Caps(Color? background = null, ColorDepth? depth = null, TerminalCapabilities? preset = null)
    {
        var caps = preset ?? TestCapabilities.KittyTruecolor;
        var color = caps.Output.Color;
        if (background is not null)
            color = color with { DefaultBackground = background };
        if (depth is not null)
            color = color with { Depth = depth.Value };

        return caps with { Output = caps.Output with { Color = color } };
    }

    /// <summary>The §0.2 default tree carrier.</summary>
    public sealed record Tree(Probe Root, Probe MidA, Probe Leaf);
}

/// <summary>A plain <see cref="UIElement"/> with optional <c>Resources</c> and a child-adoption shim (the §0.2 <c>Probe</c>).</summary>
public class Probe : UIElement
{
    /// <summary>P — an <see cref="IBrush"/>?, default null, <c>AffectsRender</c> (the resource-fed property).</summary>
    public static readonly StyledProperty<IBrush?> P = UIProperty.Register<Probe, IBrush?>("P");

    /// <summary>Pi — an int, default 7 (lower-source-promotion observability).</summary>
    public static readonly StyledProperty<int> Pi = UIProperty.Register<Probe, int>("Pi", defaultValue: 7);

    static Probe() => AffectsRender<Probe>(P);

    /// <summary>Adopts <paramref name="child"/> as a logical + visual child (the resource-chain link).</summary>
    public void Adopt(UIElement child) => AdoptChild(child, -1);

    /// <summary>Removes <paramref name="child"/> (both visual + logical) — the cross-root-move shim.</summary>
    public void RemoveChildForTest(UIElement child) => DisownChild(child);

    /// <summary>The <c>e.Res[K]</c> shim — touches the lazy <c>Resources</c> dictionary.</summary>
    public ResourceDictionary Res => Resources;

    protected override Size MeasureOverride(Size availableSize) => default;
    protected override Size ArrangeOverride(Size finalSize) => finalSize;
}

/// <summary>A recording <see cref="IResourceChangeListener"/> (the §0.2 subscription probe).</summary>
public sealed class ResourceProbe : IResourceChangeListener
{
    public readonly List<(object Key, object? Value)> Changes = [];

    public void OnResourceChanged(object key, object? newValue) => Changes.Add((key, newValue));

    public object? Last => Changes.Count > 0 ? Changes[^1].Value : null;

    public void Clear() => Changes.Clear();
}

/// <summary>A deferred entry whose realization counts calls and returns a fixed value (the §0.2 deferred-entry probe).</summary>
public sealed class CountingDeferred(Func<IResourceScope, object?> realize) : IDeferredResourceEntry
{
    public int Calls { get; private set; }

    public object? Realize(IResourceScope lexicalScope)
    {
        Calls++;
        return realize(lexicalScope);
    }
}
