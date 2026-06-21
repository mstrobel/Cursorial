using Cursorial.UI;

namespace Cursorial.Gallery;

/// <summary>
/// One gallery page — a control (or a group of related controls) exercised through live toggles. A page builds a
/// fresh element tree each time it is shown (selected in the nav), so toggling between pages never leaks state.
/// </summary>
internal interface IGalleryPage
{
    /// <summary>The nav-list label.</summary>
    string Title { get; }

    /// <summary>Build the page's element tree (fresh per show).</summary>
    UIElement Build();
}
