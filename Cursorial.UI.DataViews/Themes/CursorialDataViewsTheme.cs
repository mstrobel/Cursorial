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
    /// The grid anatomy (§3.1): group panel, header band, and auto-filter row docked top (in that
    /// order), summary footer (bottom), the ScrollViewer-hosted rows presenter filling the rest
    /// (the presenter is the SCP's content — the virtualization seam). The optional bands collapse
    /// in their own measure (Show* false ⇒ 0 rows), so the template carries no visibility bindings.
    /// </summary>
    private static ControlTemplate DataGridTemplate() => new(ctx =>
    {
        var dock = new DockPanel { LastChildFill = true };

        var groupPanel = new DataGridGroupPanel();
        DockPanel.SetDock(groupPanel, Dock.Top);
        groupPanel.SetResourceReference(DataGridGroupPanel.BackgroundProperty, ThemeKeys.PanelBrush);
        groupPanel.SetResourceReference(DataGridGroupPanel.ChipBackgroundProperty, ThemeKeys.SurfaceBrush);
        groupPanel.SetResourceReference(DataGridGroupPanel.TextBrushProperty, ThemeKeys.TextBrush);
        groupPanel.SetResourceReference(DataGridGroupPanel.GlyphBrushProperty, ThemeKeys.CoolBrush);
        groupPanel.SetResourceReference(DataGridGroupPanel.PromptBrushProperty, ThemeKeys.MutedBrush);

        var header = new DataGridHeaderPresenter();
        DockPanel.SetDock(header, Dock.Top);
        header.SetResourceReference(DataGridHeaderPresenter.BackgroundProperty, ThemeKeys.SurfaceBrush);
        header.SetResourceReference(DataGridHeaderPresenter.ForegroundProperty, ThemeKeys.AccentBrush);
        header.SetResourceReference(DataGridHeaderPresenter.SortGlyphBrushProperty, ThemeKeys.CoolBrush);
        header.SetResourceReference(DataGridHeaderPresenter.FilterGlyphBrushProperty, ThemeKeys.MutedBrush);
        header.SetResourceReference(DataGridHeaderPresenter.ActiveFilterBrushProperty, ThemeKeys.AmberBrush);
        header.SetResourceReference(DataGridHeaderPresenter.HoverBackgroundProperty, ThemeKeys.HoverBrush);

        var autoFilter = new DataGridAutoFilterRow();
        DockPanel.SetDock(autoFilter, Dock.Top);
        autoFilter.SetResourceReference(DataGridAutoFilterRow.BackgroundProperty, ThemeKeys.SurfaceBrush);
        autoFilter.SetResourceReference(DataGridAutoFilterRow.TextBrushProperty, ThemeKeys.TextBrush);
        autoFilter.SetResourceReference(DataGridAutoFilterRow.PlaceholderBrushProperty, ThemeKeys.MutedBrush);
        autoFilter.SetResourceReference(DataGridAutoFilterRow.WellBackgroundProperty, ThemeKeys.WellBrush);

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
        rows.SetResourceReference(DataGridRowsPresenter.DataBarFillBrushProperty, ThemeKeys.CoolBrush);
        rows.SetResourceReference(DataGridRowsPresenter.DataBarTrackBrushProperty, ThemeKeys.FaintBrush);

        var scrollViewer = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, // v1: full-width scenes; Auto degrades (§3.1)
        };

        dock.Children.Add(groupPanel);
        dock.Children.Add(header);
        dock.Children.Add(autoFilter);
        dock.Children.Add(footer);
        dock.Children.Add(scrollViewer);

        ctx.RegisterName(DataGrid.PartGroupPanel, groupPanel);
        ctx.RegisterName(DataGrid.PartHeader, header);
        ctx.RegisterName(DataGrid.PartAutoFilterRow, autoFilter);
        ctx.RegisterName(DataGrid.PartFooter, footer);
        ctx.RegisterName(DataGrid.PartScrollViewer, scrollViewer);
        ctx.RegisterName(DataGrid.PartRows, rows);

        return dock;
    });
}
