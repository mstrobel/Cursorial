using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery;

/// <summary>
/// The gallery chrome: a left nav list of pages + a content host that swaps to the selected page. The nav is a real
/// <see cref="ListBox"/> (so arrow-key / click navigation is itself part of the showcase); selecting an entry builds
/// that page fresh into the content host.
/// </summary>
internal sealed class GalleryShell
{
    private readonly IReadOnlyList<IGalleryPage> _pages;
    private readonly Border _content = new(); // a Border renders its Child directly (a bare ContentControl has no presenter)
    private ListBox? _nav;

    public GalleryShell(IReadOnlyList<IGalleryPage> pages) => _pages = pages;

    public UIElement Build()
    {
        var title = new TextBlock
        {
            Text = " Cursorial — Control Gallery   (↑/↓ or click to switch pages · q / Esc to quit)",
            Foreground = new SolidColorBrush(Color.FromRgb(220, 226, 235)),
        };
        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(36, 42, 54)),
            Padding = new Margins(1, 0),
            Child = title,
        };
        DockPanel.SetDock(titleBar, Dock.Top);

        _nav = new ListBox
        {
            Width = 24,
            ItemsSource = _pages.Select(p => p.Title).ToArray(),
        };
        _nav.SelectionChanged += (_, _) => ShowPage(_nav!.SelectedIndex);
        DockPanel.SetDock(_nav, Dock.Left);

        _content.Padding = new Margins(1, 0);

        var root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(24, 28, 36)) };
        root.Children.Add(titleBar);
        root.Children.Add(_nav);
        root.Children.Add(_content); // last child fills the remaining area

        _nav.SelectedIndex = 0; // show the first page
        return root;
    }

    private void ShowPage(int index)
        => _content.Child = index >= 0 && index < _pages.Count ? _pages[index].Build() : null;
}
