using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery.Pages;

/// <summary>
/// The ScrollViewer page — the priority (scrolling is the framework's biggest bug surface). Live toggles cycle each
/// axis's <see cref="ScrollBarVisibility"/> and flip the content between fits-the-viewport / overflows and
/// fits-width / overflows-width, so every scrollbar policy × content-size combination is reachable by keyboard or
/// click; the content itself scrolls by wheel, arrows, and PageUp/PageDown.
/// </summary>
internal sealed class ScrollViewerPage : IGalleryPage
{
    public string Title => "ScrollViewer";

    private bool _tall = true;
    private bool _wide;
    private ScrollViewer _sv = null!;
    private TextBlock _status = null!;

    public UIElement Build()
    {
        _tall = true;
        _wide = false;

        _sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _status = new TextBlock { Foreground = Ink(180, 188, 200) };
        var statusBar = new Border { Background = Brush(30, 34, 42), Padding = new Margins(1, 0), Child = _status };
        DockPanel.SetDock(statusBar, Dock.Bottom);

        var toggles = new WrapPanel(); // horizontal flow that wraps the toggle buttons on a narrow terminal
        toggles.Children.Add(Cycler("V-bar", () => _sv.VerticalScrollBarVisibility, v => { _sv.VerticalScrollBarVisibility = v; UpdateStatus(); }));
        toggles.Children.Add(Cycler("H-bar", () => _sv.HorizontalScrollBarVisibility, v => { _sv.HorizontalScrollBarVisibility = v; UpdateStatus(); }));
        toggles.Children.Add(Toggle("Content", () => _tall ? "tall(60)" : "short(3)", () => { _tall = !_tall; RebuildContent(); }));
        toggles.Children.Add(Toggle("Width", () => _wide ? "wide" : "fit", () => { _wide = !_wide; RebuildContent(); }));
        var toggleBar = new Border { Background = Brush(30, 34, 42), Padding = new Margins(1, 0), Child = toggles };
        DockPanel.SetDock(toggleBar, Dock.Top);

        RebuildContent();

        var root = new DockPanel();
        root.Children.Add(toggleBar);
        root.Children.Add(statusBar);
        root.Children.Add(_sv); // last child fills
        return root;
    }

    private void RebuildContent()
    {
        var rows = new StackPanel { Orientation = Orientation.Vertical };
        if (_wide)
            rows.Width = 160; // wider than any realistic viewport → exercises horizontal overflow

        var count = _tall ? 60 : 3;
        for (var i = 0; i < count; i++)
        {
            var even = (i & 1) == 0;
            rows.Children.Add(new Border
            {
                Background = Brush(even ? 32 : 40, even ? 38 : 48, even ? 48 : 60),
                Padding = new Margins(1, 0),
                Child = new TextBlock { Text = $"row {i:000}" + (_wide ? "  " + new string('·', 130) + " ⟂end" : ""), Foreground = Ink(214, 220, 230) },
            });
        }

        _sv.Content = rows;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_status is null)
            return;
        _status.Text = $" V-bar={_sv.VerticalScrollBarVisibility}  H-bar={_sv.HorizontalScrollBarVisibility}  " +
                       $"content={(_tall ? "tall" : "short")}/{(_wide ? "wide" : "fit")}   ·   wheel + ↑/↓ scroll, PgUp/PgDn page, Home/End ends";
    }

    private static Button Cycler(string label, Func<ScrollBarVisibility> get, Action<ScrollBarVisibility> set)
    {
        var button = new Button { Margin = new Margins(1, 0) };
        void Refresh() => button.Content = $"{label}: {get()}";
        Refresh();
        button.Click += (_, _) =>
        {
            set(Next(get()));
            Refresh();
        };
        return button;
    }

    private static Button Toggle(string label, Func<string> state, Action act)
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

    private static ScrollBarVisibility Next(ScrollBarVisibility v) => v switch
    {
        ScrollBarVisibility.Auto => ScrollBarVisibility.Visible,
        ScrollBarVisibility.Visible => ScrollBarVisibility.Hidden,
        ScrollBarVisibility.Hidden => ScrollBarVisibility.Disabled,
        _ => ScrollBarVisibility.Auto,
    };

    private static SolidColorBrush Brush(int r, int g, int b) => new(Color.FromRgb((byte) r, (byte) g, (byte) b));
    private static SolidColorBrush Ink(int r, int g, int b) => new(Color.FromRgb((byte) r, (byte) g, (byte) b));
}
