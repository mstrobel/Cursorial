using System.Runtime.CompilerServices;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Bars;

/// <summary>
/// Registers the bar control themes into the framework's assembly theme-contribution tier
/// (<see cref="ThemeContributions"/>) at module load — before any bar control first renders — so the suite is
/// self-contained (no consumer dictionary merge) and each control template's <c>{DynamicResource}</c>
/// references resolve through a real chain node. This replaces the former per-control
/// <c>Control.Theme</c> type-default installs (which delivered the theme <see cref="Cursorial.UI.Style"/> but
/// gave its DynamicResource references nowhere to resolve).
/// </summary>
internal static class BarsThemeModule
{
    // The sanctioned advanced use of [ModuleInitializer]: register the suite's control themes into the
    // contribution tier at module load, before any bar control renders (mirrors XamlModule / the Drawing
    // interpolator registration).
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize() => ThemeContributions.Register(CursorialBarsTheme.BuildContribution());
}
