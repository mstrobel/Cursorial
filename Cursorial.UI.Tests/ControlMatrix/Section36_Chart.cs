using Cursorial.Drawing;
using Cursorial.Drawing.Charts;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C24 — Chart hosting: ChartPresenter (primitive) + Chart (control). The presenter draws a
// cell-rendered IChart via RenderContext.DrawChart (no capability gate — charts are ordinary cells). When no chart is
// set, the inherited placeholder shows. Shares DrawnContentPresenter with ImagePresenter.
public sealed class Section36_Chart
{
    // A deterministic IChart: paints a single marker glyph at the top-left of its area (so a test can assert it drew).
    private sealed class StubChart : IChart
    {
        public void Render(DrawingContext context, in Rect area) =>
            context.Set(area.Column, area.Row, "X", default);
    }

    private static UIHeadlessHost Host(TerminalCapabilities? caps = null) =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12), Capabilities = caps ?? HeadlessCapabilities.KittyTruecolor });

    [Fact] // C24.1: a ChartPresenter with a Source renders the chart (its draw reaches the cells); placeholder collapsed
    public void C24_1_RendersChart()
    {
        using var host = Host();
        var presenter = new ChartPresenter { Source = new StubChart() };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.Equal("X", host.GetCell(0, 0).Grapheme); // the stub's marker landed at the presenter origin
        Assert.Equal(Visibility.Collapsed, presenter.PlaceholderPresenter.Visibility);
    }

    [Fact] // C24.2: no Source ⇒ the placeholder renders (its ContentPresenter child is Visible)
    public void C24_2_NoSourceShowsPlaceholder()
    {
        using var host = Host();
        var presenter = new ChartPresenter { PlaceholderContent = "no chart" };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.Equal(Visibility.Visible, presenter.PlaceholderPresenter.Visibility);
        Assert.NotNull(presenter.PlaceholderPresenter.Child);
    }

    [Fact] // C24.3: a chart fills its available (bounded) area
    public void C24_3_FillsAvailable()
    {
        using var host = Host();
        var presenter = new ChartPresenter { Source = new StubChart() };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.Equal(24, presenter.DesiredSize.Columns); // fills the 24×12 surface
        Assert.Equal(12, presenter.DesiredSize.Rows);
    }

    [Fact] // C24.4: NO capability gate — a chart renders even on a terminal with no graphics protocol
    public void C24_4_NoCapabilityGate()
    {
        using var host = Host(HeadlessCapabilities.Ansi16Legacy); // no graphics protocol at all
        var presenter = new ChartPresenter { Source = new StubChart() };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.Equal("X", host.GetCell(0, 0).Grapheme); // still drawn (cells, not a graphics protocol)
    }

    [Fact] // C24.5: the :placeholder pseudo-class + placeholder visibility flip with the Source
    public void C24_5_PlaceholderStateFlips()
    {
        using var host = Host();
        var presenter = new ChartPresenter { PlaceholderContent = "no chart" };
        host.ShowRoot(presenter);
        host.RunUntilIdle();
        Assert.True(presenter.HasCustomPseudoClass(":placeholder"));
        Assert.Equal(Visibility.Visible, presenter.PlaceholderPresenter.Visibility);

        presenter.Source = new StubChart();
        host.RunUntilIdle();
        Assert.False(presenter.HasCustomPseudoClass(":placeholder"));
        Assert.Equal(Visibility.Collapsed, presenter.PlaceholderPresenter.Visibility);
    }

    [Fact] // C24.6: a real chart (BarChart) renders non-blank cells
    public void C24_6_RealBarChartRenders()
    {
        using var host = Host();
        var presenter = new ChartPresenter { Source = new BarChart([1.0, 3.0, 2.0, 4.0, 2.0]) };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        var anyNonBlank = false;
        for (var r = 0; r < 12 && !anyNonBlank; r++)
            for (var c = 0; c < 24 && !anyNonBlank; c++)
                if (host.GetCell(c, r).Grapheme is { Length: > 0 } g && g != " ")
                    anyNonBlank = true;
        Assert.True(anyNonBlank); // the bar chart drew something
    }

    [Fact] // C24.7: the Chart control's template hosts a PART_ChartPresenter the Source flows to; the chart renders
    public void C24_7_ChartControlHostsPresenter()
    {
        using var host = Host();
        var chart = new Chart { Source = new StubChart() };
        host.ShowRoot(chart);
        host.RunUntilIdle();

        Assert.NotNull(chart.PresenterPart);
        Assert.Same(chart.Source, chart.PresenterPart!.Source); // TemplateBound control → presenter
        Assert.Equal("X", host.GetCell(0, 0).Grapheme);
    }

    [Fact] // C24.8: the Chart control with no Source shows the placeholder through its presenter
    public void C24_8_ChartControlPlaceholder()
    {
        using var host = Host();
        var chart = new Chart { PlaceholderContent = "none" };
        host.ShowRoot(chart);
        host.RunUntilIdle();

        Assert.NotNull(chart.PresenterPart);
        Assert.Equal(Visibility.Visible, chart.PresenterPart!.PlaceholderPresenter.Visibility);
    }

    [Fact] // C24.9: setting Source from null flips placeholder → chart live
    public void C24_9_SourceSetLiveRenders()
    {
        using var host = Host();
        var chart = new Chart { PlaceholderContent = "none" };
        host.ShowRoot(chart);
        host.RunUntilIdle();
        Assert.Equal(Visibility.Visible, chart.PresenterPart!.PlaceholderPresenter.Visibility);

        chart.Source = new StubChart();
        host.RunUntilIdle();
        Assert.Equal(Visibility.Collapsed, chart.PresenterPart!.PlaceholderPresenter.Visibility);
        Assert.Equal("X", host.GetCell(0, 0).Grapheme);
    }

    [Fact] // C24.10: a placeholder DataTemplate keyed by content type realizes the placeholder visual
    public void C24_10_PlaceholderByTypeTemplate()
    {
        using var host = Host();
        var presenter = new ChartPresenter { PlaceholderContent = new PlaceholderVm() };
        presenter.Resources[new DataTemplateKey(typeof(PlaceholderVm))] =
            new DataTemplate { Content = new FuncTemplateContent(_ => new TextBlock("NODATA")) };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.IsType<TextBlock>(presenter.PlaceholderPresenter.Child);
        Assert.Contains("NODATA", host.GetRowText(0));
    }

    private sealed class PlaceholderVm; // a content type for the by-type placeholder template (C24.10)
}
