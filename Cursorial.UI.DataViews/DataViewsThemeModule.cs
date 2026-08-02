using System.Runtime.CompilerServices;

using Cursorial.UI.Themes;
using Cursorial.UI.Xaml;

namespace Cursorial.UI.DataViews;

/// <summary>
/// Registers the DataViews control themes into the framework's assembly theme-contribution tier
/// (<see cref="ThemeContributions"/>) at module load — before any grid first renders — so the suite
/// is self-contained (no consumer dictionary merge) and the template's resource references resolve
/// through a real chain node (the Bars/Dialogs pattern).
/// </summary>
internal static class DataViewsThemeModule
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        XamlSchemaContext.Default.RegisterAssembly(typeof(DataViewsThemeModule).Assembly);
        ThemeContributions.Register(Themes.CursorialDataViewsTheme.BuildContribution());
    }
}
