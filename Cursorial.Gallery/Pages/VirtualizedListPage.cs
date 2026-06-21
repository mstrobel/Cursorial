using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery.Pages;

/// <summary>
/// The virtualizing <see cref="ListBox"/> page — exercises the container-virtualization epic (V0–V5). The list holds
/// up to 100,000 items yet only the visible band is ever realized, so it shows + scrolls instantly; toggles flip the
/// item count and uniform-vs-variable item heights. The headline things to verify by hand: a huge count is responsive
/// (a non-virtualized list of this size would stall), <c>Home</c>/<c>End</c> jump across the realization boundary and
/// the target renders, and a selection made deep in the list survives scrolling it out of view and back (V3 —
/// selection is item-indexed, not container-bound).
/// </summary>
internal sealed class VirtualizedListPage : IGalleryPage
{
    public string Title => "ListBox (virtual)";

    // Variable-height items are real TextBlock element items (multi-line), so they cost a UIElement each — bound that
    // allocation independently of the uniform count (which is just cheap strings and can run to 100k).
    private const int VariableCap = 5_000;

    private ListBox _list = null!;
    private TextBlock _status = null!;
    private bool _variable;
    private int _count = 10_000;

    public UIElement Build()
    {
        _variable = false;
        _count = 10_000;

        _list = new ListBox { ItemsPanel = new ItemsPanelTemplate(_ => new VirtualizingStackPanel()) };
        VirtualizingPanel.SetIsVirtualizing(_list, true); // before the items connect — the panel reads it on connect
        _list.SelectionChanged += (_, _) => UpdateStatus();
        RebuildItems();

        _status = new TextBlock { Foreground = Ink(180, 188, 200) };
        var statusBar = new Border { Background = Brush(30, 34, 42), Padding = new Margins(1, 0), Child = _status };
        DockPanel.SetDock(statusBar, Dock.Bottom);

        var toggles = new WrapPanel();
        toggles.Children.Add(Toggle("Count", () => DisplayCount().ToString("#,0"),
            () => { _count = _count switch { 1_000 => 10_000, 10_000 => 100_000, _ => 1_000 }; RebuildItems(); }));
        toggles.Children.Add(Toggle("Heights", () => _variable ? $"variable (≤{VariableCap:#,0})" : "uniform",
            () => { _variable = !_variable; RebuildItems(); }));
        var toggleBar = new Border { Background = Brush(30, 34, 42), Padding = new Margins(1, 0), Child = toggles };
        DockPanel.SetDock(toggleBar, Dock.Top);

        var root = new DockPanel();
        root.Children.Add(toggleBar);
        root.Children.Add(statusBar);
        root.Children.Add(_list); // last child fills
        return root;
    }

    // The realized count (variable items are clamped so the gallery stays responsive); uniform runs to the full count.
    private int DisplayCount() => _variable ? Math.Min(_count, VariableCap) : _count;

    private void RebuildItems()
    {
        var n = DisplayCount();
        _list.ItemsSource = _variable
            ? Enumerable.Range(0, n).Select(i => (object) new TextBlock
            {
                Foreground = Ink(214, 220, 230),
                // Every 4th item is taller (2–4 rows) — drives the §V4 variable-height prefix-sum + estimate path.
                Text = $"item {i:000000}" + (i % 4 == 0 ? "\n   ↳ tall row" + (i % 8 == 0 ? "\n   ↳ taller" : "") : ""),
            }).ToArray()
            : Enumerable.Range(0, n).Select(i => (object) $"item {i:000000}").ToArray();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_status is null)
            return;
        var sel = _list.SelectedIndex < 0 ? "none" : $"#{_list.SelectedIndex} ({_list.SelectedItem})";
        _status.Text = $" count={DisplayCount():#,0}  heights={(_variable ? "variable" : "uniform")}  selected={sel}" +
                       "   ·   ↑/↓ select, PgUp/PgDn page, Home/End jump — only the visible band is realized";
    }

    private Button Toggle(string label, Func<string> state, Action act)
    {
        var button = new Button { Margin = new Margins(1, 0) };
        void Refresh() => button.Content = $"{label}: {state()}";
        Refresh();
        button.Click += (_, _) =>
        {
            act();
            Refresh();
        };
        return button;
    }

    private static SolidColorBrush Brush(int r, int g, int b) => new(Color.FromRgb((byte) r, (byte) g, (byte) b));
    private static SolidColorBrush Ink(int r, int g, int b) => new(Color.FromRgb((byte) r, (byte) g, (byte) b));
}
