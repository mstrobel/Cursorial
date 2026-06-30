namespace Cursorial.UI.Controls;

/// <summary>
/// The opt-in contract for an items-host <see cref="Panel"/> that participates in — or fully owns — container
/// adoption, so the <see cref="ItemsPresenter"/> can hand the panel a connect/disconnect lifecycle and (optionally)
/// step back from its default index-aligned <see cref="UIElementCollection"/> sync.
/// <para>
/// Implemented by <see cref="VirtualizingPanel"/> (for the lifecycle hooks; it defers the step-back decision to the
/// generator's virtualizing mode — see <see cref="ManagesContainerAdoption"/>) and by panels that distribute
/// containers across more than one visual host regardless of generator mode (the bars <c>Toolbar</c>'s overflow
/// panel, which splits the live containers between a row band and an overflow popup band).
/// </para>
/// </summary>
public interface IItemsHostPanel
{
    /// <summary>
    /// Whether this panel manages container adoption <b>itself</b>, so the <see cref="ItemsPresenter"/> must NOT run
    /// its default index-aligned <c>Children.Insert/Remove/Move</c> on structural change.
    /// <para>
    /// A <see cref="VirtualizingPanel"/> returns <see langword="false"/>: it owns adoption only while the generator is
    /// virtualizing (a state the presenter checks separately via <see cref="ItemContainerGenerator.IsVirtualizing"/>,
    /// so a NON-virtualizing virtualizing panel still wants the eager presenter path). A panel that owns distribution
    /// unconditionally — e.g. an overflow toolbar that always splits its containers across two bands — returns
    /// <see langword="true"/>.
    /// </para>
    /// </summary>
    bool ManagesContainerAdoption { get; }

    /// <summary>Called by the <see cref="ItemsPresenter"/> when this panel becomes the items host (it now has its
    /// owning <see cref="ItemsControl"/> + generator). The panel wires its generator subscription here.</summary>
    void OnItemsHostConnected(ItemsControl owner);

    /// <summary>Called by the <see cref="ItemsPresenter"/> before this panel is released (detach / <c>ItemsPanel</c>
    /// swap) so it can unhook its generator subscription and release any containers it adopted.</summary>
    void OnItemsHostDisconnected();
}
