using System.Runtime.CompilerServices;

using Cursorial.UI.Dialogs.Themes;
using Cursorial.UI.Themes;
using Cursorial.UI.Xaml;

namespace Cursorial.UI.Dialogs;

internal static class DialogsThemeModule
{
    // The sanctioned advanced use of [ModuleInitializer]: register the suite's control themes into the
    // contribution tier at module load, before any dialog control renders (mirrors XamlModule / the Drawing
    // interpolator registration).
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        XamlSchemaContext.Default.RegisterAssembly(typeof(DialogsThemeModule).Assembly);
        ThemeContributions.Register(CursorialDialogThemes.BuildContribution());
    }
}
