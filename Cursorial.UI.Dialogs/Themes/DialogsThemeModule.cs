using System.Runtime.CompilerServices;

using Cursorial.UI.Themes;

namespace Cursorial.UI.Dialogs.Themes;

internal static class DialogsThemeModule
{
    // The sanctioned advanced use of [ModuleInitializer]: register the suite's control themes into the
    // contribution tier at module load, before any dialog control renders (mirrors XamlModule / the Drawing
    // interpolator registration).
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize() => ThemeContributions.Register(CursorialDialogThemes.BuildContribution());
}
