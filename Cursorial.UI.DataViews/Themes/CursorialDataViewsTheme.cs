using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews.Themes;

/// <summary>
/// The code-first DataViews control themes (design doc §4): Type-keyed over the core
/// <see cref="ThemeKeys"/> spine — the grid tracks the active palette and dark/light flips with zero
/// owned brushes (the mockup's Tokyo Night look IS the palette's). Presenter looks arrive as
/// <c>SetResourceReference</c>s on their styled brush properties (drawn rows cannot carry
/// pseudo-classes — §3.2). Registered via the assembly theme-contribution tier
/// (<see cref="DataViewsThemeModule"/>, the Bars pattern).
/// </summary>
internal static class CursorialDataViewsTheme
{
    internal static ResourceDictionary BuildContribution() => new()
    {
        [typeof(DataGrid)] = DataGridStyle(),
    };

    private static Style DataGridStyle() => new Style { Key = "DataViews.DataGrid" }
        .SetResource(Control.BackgroundProperty, ThemeKeys.PanelBrush)
        .SetResource(Control.ForegroundProperty, ThemeKeys.TextDimBrush)
        .Set(Control.TemplateProperty, DataGridTemplate());

    /// <summary>
    /// The grid anatomy (§3.1): header band (top), summary footer (bottom), the ScrollViewer-hosted
    /// rows presenter filling the rest (the presenter is the SCP's content — the virtualization
    /// seam). The group panel and auto-filter row join in their stages.
    /// </summary>
    private static ControlTemplate DataGridTemplate() => new(ctx =>
    {
        var dock = new DockPanel { LastChildFill = true };

        var header = new DataGridHeaderPresenter();
        DockPanel.SetDock(header, Dock.Top);
        header.SetResourceReference(DataGridHeaderPresenter.BackgroundProperty, ThemeKeys.SurfaceBrush);
        header.SetResourceReference(DataGridHeaderPresenter.ForegroundProperty, ThemeKeys.AccentBrush);
        header.SetResourceReference(DataGridHeaderPresenter.SortGlyphBrushProperty, ThemeKeys.CoolBrush);
        header.SetResourceReference(DataGridHeaderPresenter.FilterGlyphBrushProperty, ThemeKeys.MutedBrush);
        header.SetResourceReference(DataGridHeaderPresenter.ActiveFilterBrushProperty, ThemeKeys.AmberBrush);
        header.SetResourceReference(DataGridHeaderPresenter.HoverBackgroundProperty, ThemeKeys.HoverBrush);

        var footer = new DataGridSummaryPresenter();
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.SetResourceReference(DataGridSummaryPresenter.BackgroundProperty, ThemeKeys.SurfaceBrush);
        footer.SetResourceReference(DataGridSummaryPresenter.ValueBrushProperty, ThemeKeys.CoolBrush);
        footer.SetResourceReference(DataGridSummaryPresenter.LabelBrushProperty, ThemeKeys.MutedBrush);

        var rows = new DataGridRowsPresenter();
        rows.SetResourceReference(DataGridRowsPresenter.RowAlternationBackgroundProperty, ThemeKeys.AlternateRowBrush);
        rows.SetResourceReference(DataGridRowsPresenter.SelectionBackgroundProperty, ThemeKeys.SelectionBrush);
        rows.SetResourceReference(DataGridRowsPresenter.HoverBackgroundProperty, ThemeKeys.HoverBrush);
        rows.SetResourceReference(DataGridRowsPresenter.GroupRowBackgroundProperty, ThemeKeys.SurfaceBrush);
        rows.SetResourceReference(DataGridRowsPresenter.TextBrushProperty, ThemeKeys.TextDimBrush);
        rows.SetResourceReference(DataGridRowsPresenter.AccentBrushProperty, ThemeKeys.AccentBrush);
        rows.SetResourceReference(DataGridRowsPresenter.FocusCellBackgroundProperty, ThemeKeys.WellBrush);

        var scrollViewer = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, // v1: full-width scenes; Auto degrades (§3.1)
        };

        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(scrollViewer);

        ctx.RegisterName(DataGrid.PartHeader, header);
        ctx.RegisterName(DataGrid.PartFooter, footer);
        ctx.RegisterName(DataGrid.PartScrollViewer, scrollViewer);
        ctx.RegisterName(DataGrid.PartRows, rows);

        return dock;
    });
}
