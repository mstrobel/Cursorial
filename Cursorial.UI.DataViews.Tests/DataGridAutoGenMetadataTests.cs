using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Cursorial.Rendering;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Annotations;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// Auto-generated column metadata: <c>System.ComponentModel.DataAnnotations</c>-derived header /
/// format / order / skip, and the per-type default format that rounds floating values (so a grid
/// never shows "G" full precision). Design doc §1 "[panel] per-type format defaults".
/// </summary>
public class DataGridAutoGenMetadataTests
{
    private sealed class Product
    {
        [Display(Name = "Product", Order = 0)]
        public string Name { get; set; } = string.Empty;

        [Display(Name = "Unit Price", Order = 1)]
        [DisplayFormat(DataFormatString = "{0:N2}")]
        public decimal UnitPrice { get; set; }

        public double Ratio { get; set; } // no annotation → per-type default "N"

        public int Quantity { get; set; } // integer → no default format

        [Browsable(false)]
        public int InternalId { get; set; }

        [Display(AutoGenerateField = false)]
        public string Secret { get; set; } = string.Empty;
    }

    private static DataGrid AutoGrid(params Product[] rows)
    {
        var grid = new DataGrid { ItemsSource = new ObservableCollection<Product>(rows) };
        // Auto-gen runs on the first measure; a headless host drives it.
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 10) });
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return grid;
    }

    [Fact]
    public void Annotations_drive_header_order_and_skip()
    {
        var grid = AutoGrid(new Product { Name = "Widget", UnitPrice = 9.5m, Ratio = 0.5, Quantity = 3 });

        // [Browsable(false)] and [Display(AutoGenerateField=false)] both skip.
        Assert.DoesNotContain(grid.Columns, c => c.FieldName is "InternalId" or "Secret");

        // [Display(Order)] orders the annotated pair first (Name=0, UnitPrice=1), then the
        // un-ordered remainder in declaration order (Ratio, Quantity).
        Assert.Equal(new[] { "Name", "UnitPrice", "Ratio", "Quantity" }, grid.Columns.Select(c => c.FieldName));

        // [Display(Name)] → header; an un-annotated column falls back to the property name.
        Assert.Equal("Product", grid.Columns[0].EffectiveHeader);
        Assert.Equal("Unit Price", grid.Columns[1].EffectiveHeader);
        Assert.Equal("Ratio", grid.Columns[2].EffectiveHeader);
    }

    [Fact]
    public void Format_precedence_annotation_then_per_type_default()
    {
        var grid = AutoGrid(new Product { Name = "Widget" });

        // [DisplayFormat("{0:N2}")] → the raw "N2" format.
        Assert.Equal("N2", grid.Columns.First(c => c.FieldName == "UnitPrice").Format);
        // No annotation on a double → the per-type float default: separators + up to the culture's
        // fraction digits as OPTIONAL places (no forced trailing zeros).
        int digits = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalDigits;
        string expectedFloat = digits > 0 ? "#,0." + new string('#', digits) : "#,0";
        Assert.Equal(expectedFloat, grid.Columns.First(c => c.FieldName == "Ratio").Format);
        // Integers keep plain ToString (no default format).
        Assert.Null(grid.Columns.First(c => c.FieldName == "Quantity").Format);
    }

    private sealed class Sale
    {
        [DefaultSort(SortDirection.Descending)]
        [DataBar]
        [Highlight(FilterOperator.GreaterThan, 1000, foreground: "#F7768E", bold: true)]
        public decimal Amount { get; set; }

        [DefaultGroup]
        public string Region { get; set; } = string.Empty;
    }

    [Fact]
    public void DataViews_annotations_configure_sort_group_and_rules()
    {
        var grid = new DataGrid
        {
            ItemsSource = new ObservableCollection<Sale>
            {
                new() { Amount = 1500m, Region = "East" },
                new() { Amount = 500m, Region = "West" },
            },
        };

        // [DefaultSort(Descending)] on Amount.
        var sort = Assert.Single(grid.SortDescriptions);
        Assert.Equal(SortDirection.Descending, sort.Direction);
        Assert.Same(grid.Columns.First(c => c.FieldName == "Amount"), sort.ColumnKey);

        // [DefaultGroup] on Region.
        var group = Assert.Single(grid.GroupDescriptions);
        Assert.Same(grid.Columns.First(c => c.FieldName == "Region"), group.ColumnKey);

        // [DataBar] + [Highlight] on Amount.
        var amountRules = grid.Columns.First(c => c.FieldName == "Amount").FormatRules;
        Assert.Contains(amountRules, r => r is DataBarRule);
        var threshold = Assert.IsType<ThresholdRule>(amountRules.First(r => r is ThresholdRule));
        var entry = Assert.Single(threshold.Entries);
        Assert.Equal(FilterOperator.GreaterThan, entry.Operator);
        Assert.True(entry.Format.Bold);
        Assert.NotNull(entry.Format.Foreground);
    }

    [Fact]
    public void AutoGeneratingColumn_customizes_cancels_and_fires_generated()
    {
        var grid = new DataGrid();
        var seen = new List<string>();
        bool generatedFired = false;
        grid.AutoGeneratingColumn += (_, e) =>
        {
            seen.Add(e.PropertyName);
            if (e.PropertyName == "Quantity")
                e.Cancel = true; // skip this property
            if (e.PropertyName == "UnitPrice")
                e.Column.FormatRules.Add(new DataBarRule { ColumnKey = e.Column }); // customize
        };
        grid.AutoGeneratedColumns += (_, _) => generatedFired = true;

        // Setting the source triggers auto-generation synchronously (the WPF contract: attach first).
        grid.ItemsSource = new ObservableCollection<Product> { new() { Name = "W" } };

        // The event fired for each attribute-eligible property (InternalId/Secret pre-skipped).
        Assert.Equal(new[] { "Name", "UnitPrice", "Ratio", "Quantity" }, seen);
        // Quantity was canceled; UnitPrice carries the handler's rule; and the done event fired.
        Assert.DoesNotContain(grid.Columns, c => c.FieldName == "Quantity");
        Assert.IsType<DataBarRule>(Assert.Single(grid.Columns.First(c => c.FieldName == "UnitPrice").FormatRules));
        Assert.True(generatedFired);
    }

    [Fact]
    public void A_double_column_rounds_by_default_but_never_forces_trailing_zeros()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 10) });
        using var _ = host;
        var grid = new DataGrid
        {
            ItemsSource = new ObservableCollection<Product>
            {
                new() { Name = "A", Ratio = 1.0 / 3.0 },  // fractional → rounds to the culture digits
                new() { Name = "B", Ratio = 5.0 },        // integer-valued → NO trailing ".00" (not harsh)
                new() { Name = "C", Ratio = 12000.0 },    // thousands read with a group separator
            },
        };
        host.ShowRoot(grid);
        host.RunUntilIdle();

        int digits = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalDigits;
        string fmt = digits > 0 ? "#,0." + new string('#', digits) : "#,0";

        var all = string.Join("\n", Enumerable.Range(0, 10).Select(host.GetRowText));
        Assert.Contains((1.0 / 3.0).ToString(fmt), all);   // rounded to the culture's fraction digits
        Assert.DoesNotContain("0.33333", all);              // NOT full "G" precision (0.3333333333333333)
        Assert.DoesNotContain("5.00", all);                 // integer-valued double: optional (#) ⇒ no forced ".00"
        Assert.Contains((12000.0).ToString(fmt), all);      // grouped per the culture
    }
}
