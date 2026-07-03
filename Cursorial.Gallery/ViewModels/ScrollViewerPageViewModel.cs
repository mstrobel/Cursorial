using System.Collections.ObjectModel;
using System.Windows.Input;

using Cursorial.Gallery.Infrastructure;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery.ViewModels;

/// <summary>
/// The ScrollViewer page (the priority page — scrolling is the framework's biggest bug surface). Commands cycle
/// each axis's <see cref="ScrollBarVisibility"/> and flip the content between fits/overflows on each axis, so every
/// scrollbar-policy × content-size combination is reachable; the live <see cref="Status"/> reports the state. The
/// content is an items list the view scrolls by wheel / arrows / PageUp-Down.
/// </summary>
public sealed class ScrollViewerPageViewModel : PageViewModel
{
    private ScrollBarVisibility _vertical = ScrollBarVisibility.Auto;
    private ScrollBarVisibility _horizontal = ScrollBarVisibility.Disabled;
    private bool _tall = true;
    private bool _wide;

    public ScrollViewerPageViewModel()
    {
        CycleVerticalCommand = new RelayCommand(() => VerticalScrollBarVisibility = Next(_vertical));
        CycleHorizontalCommand = new RelayCommand(() => HorizontalScrollBarVisibility = Next(_horizontal));
        ToggleHeightCommand = new RelayCommand(() => { _tall = !_tall; RebuildRows(); Raise(nameof(Status)); });
        ToggleWidthCommand = new RelayCommand(() => { _wide = !_wide; RebuildRows(); Raise(nameof(ContentWidth)); Raise(nameof(Status)); });
        RebuildRows();
    }

    public override string Title => "ScrollViewer";
    public override string Summary => "Every scrollbar policy x content size; wheel / arrows / PageUp-Down scroll.";

    /// <summary>The vertical scrollbar policy bound to the <c>ScrollViewer</c>.</summary>
    public ScrollBarVisibility VerticalScrollBarVisibility
    {
        get => _vertical;
        private set { if (Set(ref _vertical, value)) Raise(nameof(Status)); }
    }

    /// <summary>The horizontal scrollbar policy bound to the <c>ScrollViewer</c>.</summary>
    public ScrollBarVisibility HorizontalScrollBarVisibility
    {
        get => _horizontal;
        private set { if (Set(ref _horizontal, value)) Raise(nameof(Status)); }
    }

    /// <summary>The content width: a fixed over-wide value when "wide" (forces horizontal overflow), else auto (null).</summary>
    public int? ContentWidth => _wide ? 160 : null;

    /// <summary>The scrolled rows (regenerated when the height/width toggles flip).</summary>
    public ObservableCollection<RowViewModel> Rows { get; } = [];

    public ICommand CycleVerticalCommand { get; }
    public ICommand CycleHorizontalCommand { get; }
    public ICommand ToggleHeightCommand { get; }
    public ICommand ToggleWidthCommand { get; }

    /// <summary>The live state line under the toggles.</summary>
    public string Status =>
        $"V-bar={_vertical}  H-bar={_horizontal}  content={(_tall ? "tall(60)" : "short(3)")}/{(_wide ? "wide" : "fit")}" +
        "   .   wheel + Up/Down scroll, PgUp/PgDn page, Home/End ends";

    private void RebuildRows()
    {
        Rows.Clear();
        var count = _tall ? 60 : 3;
        var suffix = _wide ? "  " + new string('.', 130) + " <end" : "";
        for (var i = 0; i < count; i++)
            Rows.Add(new RowViewModel($"row {i:000}{suffix}", even: (i & 1) == 0));
    }

    private static ScrollBarVisibility Next(ScrollBarVisibility v) => v switch
    {
        ScrollBarVisibility.Auto => ScrollBarVisibility.Visible,
        ScrollBarVisibility.Visible => ScrollBarVisibility.Hidden,
        ScrollBarVisibility.Hidden => ScrollBarVisibility.Disabled,
        _ => ScrollBarVisibility.Auto,
    };
}

/// <summary>One scrolled row: its label and a striping flag the row template binds.</summary>
public sealed class RowViewModel(string label, bool even)
{
    public string Label { get; } = label;

    /// <summary>True on even (0-based) rows — the view selects the striping brush.</summary>
    public bool Even { get; } = even;
}
