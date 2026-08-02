using System.Collections.ObjectModel;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix §C46 — ListView: the switchable view modes (Details / List / SmallIcons / Tiles), the
// column grid + sortable header, the column-major UniformWrapPanel and the per-view keyboard geometry.
public sealed class Section47_ListView
{
    private sealed record FileRow(string Name, string Size, string Kind, string Modified)
    {
        public string Icon => "*";
    }

    private static readonly FileRow[] Files =
    [
        new("alpha.txt", "10", "Text", "2026-01-01"),
        new("bravo.md", "200", "Markdown", "2026-02-02"),
        new("cobalt.cs", "30", "C#", "2026-03-03"),
        new("delta.json", "4000", "JSON", "2026-04-04")
    ];

    private static (UIHeadlessHost Host, ListView List) Show(ListView list, int columns = 60, int rows = 20)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(columns, rows),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
            CaptureFrameBytes = true
        });

        host.ShowRoot(list);
        host.RunUntilIdle();
        return (host, list);
    }

    private static ListView DetailsList(IEnumerable<FileRow>? source = null)
    {
        var list = new ListView { ItemsSource = (System.Collections.IEnumerable?) source ?? Files };
        list.Columns.Add(new ListViewColumn { Header = "Name", DisplayMemberPath = "Name", Width = GridLength.Star() });
        list.Columns.Add(new ListViewColumn { Header = "Size", DisplayMemberPath = "Size", Width = 6, HorizontalAlignment = HorizontalAlignment.Right });
        list.Columns.Add(new ListViewColumn { Header = "Kind", DisplayMemberPath = "Kind" });
        return list;
    }

    private static ListView WrapList(ListViewViewMode view)
        => new()
        {
            View = view,
            ItemsSource = Files,
            DisplayMemberPath = "Name",
            IconMemberPath = "Icon",
            SecondaryMemberPath = "Kind"
        };

    private static ListViewItem Item(ListView list, int index)
        => (ListViewItem) list.ItemContainerGenerator.ContainerFromIndex(index)!;

    private static UniformWrapPanel WrapPanelOf(ListView list)
        => (UniformWrapPanel) ItemsControl.ItemsPanelFromItemsControl(list)!;

    // ───────────────────────────── view modes render ─────────────────────────────

    [Fact] // C46.1: Details renders the header row above the column grid, with every column's text
    public void C46_1_Details_RendersHeaderAndColumns()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;

        // The Auto/Star column negotiation converges — the frame settled rather than re-invalidating forever.
        Assert.True(host.RunUntilIdle());

        var header = host.GetRowText(0);
        Assert.Contains("Name", header);
        Assert.Contains("Size", header);
        Assert.Contains("Kind", header);

        var firstRow = host.GetRowText(1);
        Assert.Contains("alpha.txt", firstRow);
        Assert.Contains("10", firstRow);
        Assert.Contains("Text", firstRow);

        Assert.Equal(ListViewViewMode.Details, list.View);
    }

    [Fact] // C46.2: List wraps COLUMN-MAJOR — consecutive items go DOWN rows, not across
    public void C46_2_List_WrapsColumnMajor()
    {
        var (host, list) = Show(WrapList(ListViewViewMode.List), columns: 40, rows: 3);
        using var _ = host;

        // 3 rows tall ⇒ 3 items per column, so the 4th item starts the second column.
        var panel = WrapPanelOf(list);
        Assert.Equal(Orientation.Vertical, panel.Orientation);
        Assert.Equal(3, panel.ItemsPerLine);
        Assert.Equal(2, panel.LineCount);

        // The row bar carries the theme's 1-cell side padding, so each cell starts one column in.
        Assert.StartsWith(" alpha.txt", host.GetRowText(0));
        Assert.StartsWith(" bravo.md", host.GetRowText(1));
        Assert.StartsWith(" cobalt.cs", host.GetRowText(2));
        Assert.Contains("delta.json", host.GetRowText(0)); // wrapped ACROSS into column 2
    }

    [Fact] // C46.3: SmallIcons puts the icon glyph before the name, still column-major
    public void C46_3_SmallIcons_RendersIconThenName()
    {
        var (host, list) = Show(WrapList(ListViewViewMode.SmallIcons), columns: 40, rows: 4);
        using var _ = host;

        Assert.Equal(Orientation.Vertical, WrapPanelOf(list).Orientation);
        Assert.StartsWith(" * alpha.txt", host.GetRowText(0)); // 1-cell row padding, then icon + gap + name
        Assert.StartsWith(" * bravo.md", host.GetRowText(1));
    }

    [Fact] // C46.4: Tiles wraps ROW-MAJOR with a two-line cell (name line + secondary line)
    public void C46_4_Tiles_RowMajorTwoLineCells()
    {
        var (host, list) = Show(WrapList(ListViewViewMode.Tiles), columns: 60, rows: 10);
        using var _ = host;

        var panel = WrapPanelOf(list);
        Assert.Equal(Orientation.Horizontal, panel.Orientation);
        Assert.Equal(2, panel.CellHeight); // icon+name line, then the secondary line

        Assert.Contains("alpha.txt", host.GetRowText(0));
        Assert.Contains("Text", host.GetRowText(1)); // the secondary line sits directly below
    }

    [Fact] // C46.5: the view switch swaps the ITEMS PANEL at runtime (the ItemsPanelTemplate seam)
    public void C46_5_ViewSwitch_SwapsItemsPanel()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;

        Assert.IsType<StackPanel>(ItemsControl.ItemsPanelFromItemsControl(list));

        list.View = ListViewViewMode.List;
        host.RunUntilIdle();
        var columnMajor = WrapPanelOf(list);
        Assert.Equal(Orientation.Vertical, columnMajor.Orientation);

        list.View = ListViewViewMode.Tiles;
        host.RunUntilIdle();
        Assert.Equal(Orientation.Horizontal, WrapPanelOf(list).Orientation);

        list.View = ListViewViewMode.Details;
        host.RunUntilIdle();
        Assert.IsType<StackPanel>(ItemsControl.ItemsPanelFromItemsControl(list));

        // Containers survive the swap and follow the mode.
        Assert.Equal(ListViewViewMode.Details, Item(list, 0).View);
    }

    [Fact] // C46.6: the view switch flips the scroll axis (column-major scrolls sideways, never down)
    public void C46_6_ViewSwitch_FlipsScrollAxis()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;

        Assert.Equal(ScrollBarVisibility.Disabled, list.GetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty));
        Assert.Equal(ScrollBarVisibility.Auto, list.GetValue(ScrollViewer.VerticalScrollBarVisibilityProperty));

        list.View = ListViewViewMode.List;
        host.RunUntilIdle();

        Assert.Equal(ScrollBarVisibility.Auto, list.GetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty));
        Assert.Equal(ScrollBarVisibility.Disabled, list.GetValue(ScrollViewer.VerticalScrollBarVisibilityProperty));
    }

    // ───────────────────────────── columns + sorting ─────────────────────────────

    [Fact] // C46.7: a star column absorbs the slack and a Right-aligned cell hugs its column's right edge
    public void C46_7_ColumnWidths_StarAndRightAlignment()
    {
        var (host, list) = Show(DetailsList(), columns: 40, rows: 8);
        using var _ = host;

        var row = host.GetRowText(1);
        var sizeEnd = row.IndexOf("Text", StringComparison.Ordinal);
        Assert.True(sizeEnd > 0, $"expected the Kind column after the Size column; row was '{row}'");

        // "10" is right-aligned in its 6-cell column, so it ends immediately before the Kind column's gap.
        Assert.Equal("10", row[(sizeEnd - 3)..(sizeEnd - 1)]);
        Assert.Equal(3, list.Columns.Count);
    }

    [Fact] // C46.8: a header click raises Sorting (ascending), a second click cycles to descending
    public void C46_8_HeaderClick_RaisesSortingAndCycles()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;

        var seen = new List<(string? Path, ListViewSortDirection Direction)>();
        list.Sorting += (_, e) => seen.Add((e.SortMemberPath, e.Direction));

        var name = list.Columns[0];
        list.CycleSort(name);
        host.RunUntilIdle();

        Assert.Equal(("Name", ListViewSortDirection.Ascending), seen[0]);
        Assert.Same(name, list.SortColumn);
        Assert.Equal(ListViewSortDirection.Ascending, list.SortDirection);

        list.CycleSort(name);
        host.RunUntilIdle();

        Assert.Equal(("Name", ListViewSortDirection.Descending), seen[1]);
        Assert.Equal(ListViewSortDirection.Descending, list.SortDirection);
    }

    [Fact] // C46.9: the sort indicator RENDERS in the header row and flips with the direction
    public void C46_9_SortIndicator_Renders()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;

        Assert.DoesNotContain("▲", host.GetRowText(0));

        list.CycleSort(list.Columns[0]);
        host.RunUntilIdle();
        Assert.Contains("▲", host.GetRowText(0));

        list.CycleSort(list.Columns[0]);
        host.RunUntilIdle();
        Assert.Contains("▼", host.GetRowText(0));
        Assert.DoesNotContain("▲", host.GetRowText(0));
    }

    [Fact] // C46.10: Cancel vetoes the whole sort — no indicator, no state change
    public void C46_10_Sorting_CancelVetoes()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;

        list.Sorting += (_, e) => e.Cancel = true;
        list.CycleSort(list.Columns[0]);
        host.RunUntilIdle();

        Assert.Null(list.SortColumn);
        Assert.Equal(ListViewSortDirection.None, list.SortDirection);
    }

    [Fact] // C46.11: the built-in sort is OPT-IN — off by default the source order is untouched
    public void C46_11_BuiltInSort_IsOptIn()
    {
        var source = new ObservableCollection<FileRow>(Files);
        var list = DetailsList(source);
        list.ItemsSource = source;

        var (host, _) = Show(list);
        using var _1 = host;

        list.CycleSort(list.Columns[0]); // Name — the indicator moves, the collection does not
        host.RunUntilIdle();
        Assert.Same(list.Columns[0], list.SortColumn);
        Assert.Equal(["alpha.txt", "bravo.md", "cobalt.cs", "delta.json"], source.Select(f => f.Name));

        list.IsBuiltInSortEnabled = true;
        list.CycleSort(list.Columns[1]); // Size, ascending (a different column restarts the cycle)
        host.RunUntilIdle();

        // Size is text here, so the comparer orders "10" < "200" < "30" < "4000" lexicographically.
        Assert.Equal(["10", "200", "30", "4000"], source.Select(f => f.Size));
        Assert.Equal("alpha.txt", host.GetRowText(1).TrimStart()[..9]);
    }

    [Fact] // C46.12: the built-in DESCENDING sort reverses, and the selection follows its item
    public void C46_12_BuiltInSort_DescendingKeepsSelection()
    {
        var source = new ObservableCollection<FileRow>(Files);
        var list = DetailsList(source);
        list.ItemsSource = source;
        list.IsBuiltInSortEnabled = true;

        var (host, _) = Show(list);
        using var _1 = host;

        list.SelectedIndex = 0; // alpha.txt
        host.RunUntilIdle();

        list.CycleSort(list.Columns[0]); // Name ascending — already sorted
        list.CycleSort(list.Columns[0]); // Name descending
        host.RunUntilIdle();

        Assert.Equal(["delta.json", "cobalt.cs", "bravo.md", "alpha.txt"], source.Select(f => f.Name));
        Assert.Equal("alpha.txt", ((FileRow) list.SelectedItem!).Name);
        Assert.Equal(3, list.SelectedIndex);
    }

    // ───────────────────────────── keyboard ─────────────────────────────

    [Fact] // C46.13: column-major nav — ↓ walks DOWN the column, → jumps to the NEXT column
    public void C46_13_ColumnMajor_ArrowNavigation()
    {
        var (host, list) = Show(WrapList(ListViewViewMode.List), columns: 40, rows: 3);
        using var _ = host;

        Assert.Equal(3, WrapPanelOf(list).ItemsPerLine);

        Item(list, 0).Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(1, list.SelectedIndex); // within the column

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(3, list.SelectedIndex); // + ItemsPerLine ⇒ next column, same row

        host.SendKey(Key.LeftArrow);
        host.RunUntilIdle();
        Assert.Equal(0, list.SelectedIndex); // clamped back into column 1 at row 0
    }

    [Fact] // C46.14: row-major (Tiles) nav — → walks ACROSS the row, ↓ jumps to the NEXT row
    public void C46_14_RowMajor_ArrowNavigation()
    {
        var (host, list) = Show(WrapList(ListViewViewMode.Tiles), columns: 40, rows: 10);
        using var _ = host;

        var perLine = WrapPanelOf(list).ItemsPerLine;
        Assert.True(perLine is >= 2 and < 4, $"expected a genuine wrap; ItemsPerLine was {perLine}");

        Item(list, 0).Focus();
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(1, list.SelectedIndex);

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(1 + perLine, list.SelectedIndex);
    }

    [Fact] // C46.15: Details keeps the plain vertical list — ↓ is ±1 and ←/→ stay unhandled
    public void C46_15_Details_VerticalNavigation()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;

        Item(list, 0).Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(1, list.SelectedIndex);

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(1, list.SelectedIndex); // unhandled — the selection did not move

        host.SendKey(Key.End);
        host.RunUntilIdle();
        Assert.Equal(3, list.SelectedIndex);

        host.SendKey(Key.Home);
        host.RunUntilIdle();
        Assert.Equal(0, list.SelectedIndex);
    }

    [Fact] // C46.16: Enter and a double-click both raise ItemInvoked
    public void C46_16_ItemInvoked_FromEnterAndDoubleClick()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;

        var invoked = new List<int>();
        list.ItemInvoked += (_, e) => invoked.Add(e.Index);

        Item(list, 2).Focus();
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal([2], invoked);

        list.HandleContainerPointerSelect(Item(list, 1), KeyModifiers.None, clickCount: 2);
        host.RunUntilIdle();
        Assert.Equal([2, 1], invoked);
    }

    [Fact] // C46.17: type-ahead jumps the selection using DisplayMemberPath
    public void C46_17_TypeAhead_UsesDisplayMemberPath()
    {
        var list = DetailsList();
        list.DisplayMemberPath = "Name";

        var (host, _) = Show(list);
        using var _1 = host;

        Item(list, 0).Focus();
        host.RunUntilIdle();

        host.SendText("c");
        host.RunUntilIdle();

        Assert.Equal(2, list.SelectedIndex); // cobalt.cs
    }

    [Fact] // C46.18: Ctrl-click accumulates and Shift-click ranges through the SelectionModel
    public void C46_18_MultiSelect_Gestures()
    {
        var list = DetailsList();
        list.SelectionMode = SelectionMode.Multiple;

        var (host, _) = Show(list);
        using var _1 = host;

        list.HandleContainerPointerSelect(Item(list, 0), KeyModifiers.Control, 1);
        list.HandleContainerPointerSelect(Item(list, 2), KeyModifiers.Control, 1);
        host.RunUntilIdle();

        Assert.True(Item(list, 0).IsSelected);
        Assert.False(Item(list, 1).IsSelected);
        Assert.True(Item(list, 2).IsSelected);

        list.HandleContainerPointerSelect(Item(list, 3), KeyModifiers.Shift, 1);
        host.RunUntilIdle();

        Assert.True(Item(list, 2).IsSelected);
        Assert.True(Item(list, 3).IsSelected);
    }

    [Fact] // C46.19: Shift+↓ extends the keyboard selection in Multiple mode
    public void C46_19_MultiSelect_ShiftArrowExtends()
    {
        var list = WrapList(ListViewViewMode.List);
        list.SelectionMode = SelectionMode.Multiple;

        var (host, _) = Show(list, columns: 40, rows: 4);
        using var _1 = host;

        Item(list, 0).Focus();
        host.RunUntilIdle();
        list.HandleContainerPointerSelect(Item(list, 0), KeyModifiers.None, 1);

        host.SendKey(Key.DownArrow, KeyModifiers.Shift);
        host.SendKey(Key.DownArrow, KeyModifiers.Shift);
        host.RunUntilIdle();

        Assert.True(Item(list, 0).IsSelected);
        Assert.True(Item(list, 1).IsSelected);
        Assert.True(Item(list, 2).IsSelected);
        Assert.False(Item(list, 3).IsSelected);
    }

    // ───────────────────────────── the panel on its own ─────────────────────────────

    [Fact] // C46.20: UniformWrapPanel is a usable standalone primitive — column-major geometry at a constrained size
    public void C46_20_UniformWrapPanel_ColumnMajorGeometry()
    {
        var panel = new UniformWrapPanel { Orientation = Orientation.Vertical };
        for (var i = 0; i < 7; i++)
            panel.Children.Add(new TextBlock { Text = $"item{i}" });

        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 3) });
        using var _ = host;
        host.ShowRoot(panel);
        host.RunUntilIdle();

        Assert.Equal(3, panel.ItemsPerLine); // 3 rows tall ⇒ 3 per column
        Assert.Equal(3, panel.LineCount);    // 7 items ⇒ 3 columns
        Assert.Equal(5, panel.CellWidth);    // uniform: the widest child ("item0" = 5 cells)
        Assert.Equal(1, panel.CellHeight);

        Assert.Equal("item0", host.GetRowText(0)[..5]);
        Assert.Equal("item3", host.GetRowText(0)[5..10]); // the second column starts at CellWidth
        Assert.Equal("item1", host.GetRowText(1)[..5]);
    }

    [Fact] // C46.21: the same panel row-major — fill across, then wrap down
    public void C46_21_UniformWrapPanel_RowMajorGeometry()
    {
        var panel = new UniformWrapPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 7; i++)
            panel.Children.Add(new TextBlock { Text = $"item{i}" });

        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(16, 6) });
        using var _ = host;
        host.ShowRoot(panel);
        host.RunUntilIdle();

        Assert.Equal(3, panel.ItemsPerLine); // 16 / 5 = 3 per row
        Assert.Equal(3, panel.LineCount);

        Assert.Equal("item0", host.GetRowText(0)[..5]);
        Assert.Equal("item1", host.GetRowText(0)[5..10]);
        Assert.Equal("item3", host.GetRowText(1)[..5]);
    }

    [Fact] // C46.22: an explicit ItemWidth/ItemHeight overrides the derived uniform cell
    public void C46_22_UniformWrapPanel_ExplicitCell()
    {
        var panel = new UniformWrapPanel { Orientation = Orientation.Vertical, ItemWidth = 8, ItemHeight = 2 };
        for (var i = 0; i < 5; i++)
            panel.Children.Add(new TextBlock { Text = $"i{i}" });

        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 4) });
        using var _ = host;
        host.ShowRoot(panel);
        host.RunUntilIdle();

        Assert.Equal(8, panel.CellWidth);
        Assert.Equal(2, panel.CellHeight);
        Assert.Equal(2, panel.ItemsPerLine); // 4 rows / 2 per cell
        Assert.Equal("i0", host.GetRowText(0)[..2]);
        Assert.Equal("i2", host.GetRowText(0)[8..10]);
        Assert.Equal("i1", host.GetRowText(2)[..2]);
    }

    // ───────────────────────────── structure ─────────────────────────────

    [Fact] // C46.23: containers are ListViewItems and a ListViewItem item is its own container
    public void C46_23_Containers()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;
        Assert.IsType<ListViewItem>(list.ItemContainerGenerator.ContainerFromIndex(0));

        var leaf = new ListViewItem();
        var (host2, list2) = Show(new ListView { ItemsSource = new object[] { leaf } });
        using var _2 = host2;
        Assert.Same(leaf, list2.ItemContainerGenerator.ContainerFromIndex(0));
    }

    [Fact] // C46.24: adding a column at runtime rebuilds every row's cells
    public void C46_24_ColumnAddedAtRuntime_RebuildsRows()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;

        Assert.DoesNotContain("2026-01-01", host.GetRowText(1));

        list.Columns.Add(new ListViewColumn { Header = "Modified", DisplayMemberPath = "Modified" });
        host.RunUntilIdle();

        Assert.Contains("Modified", host.GetRowText(0));
        Assert.Contains("2026-01-01", host.GetRowText(1));
    }

    [Fact] // C46.26: the header row is genuinely CLICKABLE — a real mouse click on it sorts
    public void C46_26_HeaderRow_IsClickable()
    {
        var (host, list) = Show(DetailsList(), columns: 40, rows: 8);
        using var _ = host;

        Assert.Equal("Name", host.GetRowText(0)[..4]);

        // The pointer must be over the header for ButtonBase's release-click (C187), so move first.
        host.SendMouseMove(1, 0); // row 0 is the pinned header strip; column 1 is inside the Name heading
        host.RunUntilIdle();
        host.SendClick(1, 0);
        host.RunUntilIdle();

        Assert.Same(list.Columns[0], list.SortColumn);
        Assert.Equal(ListViewSortDirection.Ascending, list.SortDirection);
        Assert.Contains("▲", host.GetRowText(0));
    }

    [Fact] // C46.27: an explicit ItemTemplate is the escape hatch — it wins over the composed icon/name cell
    public void C46_27_ItemTemplate_WinsInWrappingViews()
    {
        var list = WrapList(ListViewViewMode.SmallIcons);
        list.ItemTemplate = new DataTemplate { Content = new FuncTemplateContent(static _ => new TextBlock { Text = "[t]" }) };

        var (host, _) = Show(list, columns: 40, rows: 4);
        using var _1 = host;

        Assert.Contains("[t]", host.GetRowText(0));
        Assert.DoesNotContain("alpha.txt", host.GetRowText(0));
    }

    [Fact] // C46.28: CanUserSortColumns=false and a per-column opt-out both make the header inert
    public void C46_28_SortOptOuts()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;

        list.CanUserSortColumns = false;
        list.CycleSort(list.Columns[0]);
        host.RunUntilIdle();
        Assert.Null(list.SortColumn);

        list.CanUserSortColumns = true;
        list.Columns[0].CanUserSort = false;
        list.CycleSort(list.Columns[0]);
        host.RunUntilIdle();
        Assert.Null(list.SortColumn);

        list.CycleSort(list.Columns[2]); // still sortable
        host.RunUntilIdle();
        Assert.Same(list.Columns[2], list.SortColumn);
    }

    [Fact] // C46.29: editing a column's Header re-renders the header cell
    public void C46_29_HeaderEdit_RerendersHeaderCell()
    {
        var (host, list) = Show(DetailsList());
        using var _ = host;

        Assert.Contains("Kind", host.GetRowText(0));

        list.Columns[2].Header = "Type";
        host.RunUntilIdle();

        Assert.Contains("Type", host.GetRowText(0));
        Assert.DoesNotContain("Kind", host.GetRowText(0));
    }

    [Fact] // C46.25: a CellTemplate column wins over DisplayMemberPath
    public void C46_25_CellTemplate_Wins()
    {
        var list = new ListView { ItemsSource = Files };
        list.Columns.Add(new ListViewColumn
        {
            Header = "Tag",
            DisplayMemberPath = "Name",
            CellTemplate = new DataTemplate { Content = new FuncTemplateContent(static _ => new TextBlock { Text = "<tag>" }) }
        });

        var (host, _) = Show(list);
        using var _1 = host;

        Assert.Contains("<tag>", host.GetRowText(1));
        Assert.DoesNotContain("alpha.txt", host.GetRowText(1));
    }
}
