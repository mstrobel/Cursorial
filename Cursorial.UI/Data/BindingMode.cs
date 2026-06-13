namespace Cursorial.UI.Data;

/// <summary>
/// The direction of value flow between a binding source and its target (design doc §6.1). The
/// effective mode is resolved at install: <see cref="Default"/> becomes <see cref="TwoWay"/> when
/// the target property's metadata carries <see cref="PropertyEffects.BindsTwoWayByDefault"/>, else
/// <see cref="OneWay"/> (matrix B78/B79; BD10).
/// </summary>
public enum BindingMode : byte
{
    /// <summary>Resolved at install from <c>BindsTwoWayByDefault</c> metadata.</summary>
    Default,

    /// <summary>Source → target only.</summary>
    OneWay,

    /// <summary>Source ↔ target; target writes flow back per the <see cref="UpdateSourceTrigger"/>.</summary>
    TwoWay,

    /// <summary>Source → target once at activation (and on every DataContext change — BD3); no path subscription.</summary>
    OneTime,

    /// <summary>Target → source only; the chain re-resolves from the anchor on every write (BD11).</summary>
    OneWayToSource,
}
