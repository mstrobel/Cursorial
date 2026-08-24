// ReSharper disable once CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// The presentation-model style stamps (design doc §3.3): exactly one of the pair is present on every
/// surface root, mirroring <see cref="UIApplication.IsPresentingInline"/>, and flips through the
/// capability-restamp fan-out on each <see cref="ApplicationModel.InlineWithSwitching"/> transition.
/// The <c>app-</c> prefix keeps <c>caps-*</c> strictly for negotiated terminal facts. The pair also
/// lives in the capability MASK (<see cref="StyleCapabilities.Inline"/>/<see cref="StyleCapabilities.FullScreen"/>),
/// so <see cref="Style.RequiresCapabilities"/> gates on the same single derivation as these classes.
/// Public for tooling/selector-completion — matching needs no registration (classes are free-form strings).
/// </summary>
public static class PresentationClasses
{
    /// <summary>Stamped while frames present into the inline region.</summary>
    public const string Inline = "app-inline";

    /// <summary>Stamped while frames present to the full (alternate) screen.</summary>
    public const string FullScreen = "app-fullscreen";

    /// <summary>The enumerable form for tooling/selector-completion (the
    /// <see cref="CapabilityClasses.Names"/> twin).</summary>
    public static IReadOnlyList<string> Names { get; } = [Inline, FullScreen];
}
