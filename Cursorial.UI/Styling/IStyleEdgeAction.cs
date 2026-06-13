// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// An action run on a style rule's activation edges (design doc §3.7 / ledger B5; the P3 seam —
/// inversion 3). The engine invokes <see cref="OnActivated"/> for each entry of the rule's
/// <see cref="Style.Enter"/> collection on the inactive→active edge and <see cref="OnRetracted"/>
/// for each <see cref="Style.Exit"/> entry on the active→inactive edge (including detach-driven
/// retraction), in rule-document order, on <b>every</b> edge.
/// </summary>
/// <remarks>
/// P3 ships no built-in actions: the storyboard ignition vocabulary
/// (<c>BeginStoryboard</c>/<c>StopStoryboard</c>), the per-element <c>(igniter, scope)</c>
/// instancing registry, and the no-throw contract are S5's at P8. Actions are shared per style —
/// implementations must not assume per-element identity.
/// </remarks>
public interface IStyleEdgeAction
{
    /// <summary>Invoked when the owning rule transitions inactive → active on <paramref name="element"/> (the matched element).</summary>
    void OnActivated(UIElement element);

    /// <summary>Invoked when the owning rule transitions active → inactive on <paramref name="element"/>, including detach retraction.</summary>
    void OnRetracted(UIElement element);
}
