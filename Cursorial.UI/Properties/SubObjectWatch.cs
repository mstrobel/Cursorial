// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// One live sub-object subscription (the auto-watch a <see cref="UIObject"/> holds on a
/// <see cref="UIObject"/>-valued property's current value): the host, the host slot, and the
/// watched value. The <b>same node</b> sits in both parties' lists — the host's record list (so
/// replacement and the teardown sweep find what to detach without consulting the store's value
/// slots) and the target's watcher list (what the target's change dispatch fans out over) — so
/// attach and detach are two list operations on one shared identity, with no lookup table.
/// </summary>
internal sealed class SubObjectWatch(UIObject host, UIProperty hostProperty, UIObject target)
{
    /// <summary>The object whose property holds <see cref="Target"/> — where effects dispatch.</summary>
    public readonly UIObject Host = host;

    /// <summary>The host slot whose ceiling clamps the sub-change's effect level.</summary>
    public readonly UIProperty HostProperty = hostProperty;

    /// <summary>The watched sub-object (the property's current value).</summary>
    public readonly UIObject Target = target;
}
