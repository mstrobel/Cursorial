using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The column chooser popup (design doc §1 deferred item, now landed; the mockup's "Column chooser —
/// hidden columns &amp; drag-to-show"): a light-dismiss <see cref="Popup"/> anchored below the header
/// band listing HIDDEN columns as <c>⠿</c> grip chips (click = show — the column re-lands at its
/// original position because order IS the <see cref="DataGrid.Columns"/> collection and
/// <see cref="DataGridColumn.Visible"/> only filters layout) and visible columns as checked entries
/// (click = hide), with Show All / Hide All footer buttons. Esc / light dismiss close.
/// Checklist-style click-to-toggle by design; dragging a chip from the chooser ONTO the header is a
/// deferred polish (the click path covers the function, and the header's own drag already provides
/// the reverse — drag a header below the band to hide).
/// </summary>
internal sealed class DataGridColumnChooser
{
    private readonly DataGrid _grid;
    private Popup? _popup;
    private UIElement? _anchor;
    private int _anchorX;
    private readonly List<(DataGridColumn Column, Button Chip)> _hiddenChips = [];
    private readonly List<(DataGridColumn Column, CheckBox Check)> _visibleChecks = [];

    public DataGridColumnChooser(DataGrid grid) => _grid = grid;

    /// <summary>Whether the chooser is currently open (the grid's test hook gates on it).</summary>
    internal bool IsOpen { get; private set; }

    /// <summary>The hidden-column chips (tests click through the real controls).</summary>
    internal IReadOnlyList<(DataGridColumn Column, Button Chip)> HiddenChips => _hiddenChips;

    /// <summary>The visible-column check rows.</summary>
    internal IReadOnlyList<(DataGridColumn Column, CheckBox Check)> VisibleChecks => _visibleChecks;

    internal Button? ShowAllButton { get; private set; }

    internal Button? HideAllButton { get; private set; }

    /// <summary>Opens (or re-anchors) the chooser below the header band at <paramref name="anchorX"/>.</summary>
    public void Open(UIElement anchor, int anchorX)
    {
        if (IsOpen)
            _popup?.SetCurrentValue(Popup.IsOpenProperty, false); // a Popup re-places only on the closed→open edge

        _anchor = anchor;
        _anchorX = anchorX;
        _popup ??= CreatePopup();
        _popup.Child = BuildContent();
        _popup.PlacementTarget = anchor;
        _popup.Placement = PlacementMode.Bottom;
        _popup.HorizontalOffset = anchorX; // below the pressed cell (the WM clamps on-screen)
        _popup.SetCurrentValue(Popup.IsOpenProperty, true);
        IsOpen = true;

        // Focus the first row once the popup surface materializes (the filter popup's parked-work
        // idiom): keyboard users land on a toggle, and the Popup's CloseOnEscape only sees Esc when
        // focus lives INSIDE its surface.
        FocusFirstRow();
    }

    private void FocusFirstRow()
    {
        var expected = _popup?.Child;
        UIApplication.Current?.Dispatcher.Post(() =>
        {
            if (!IsOpen || !ReferenceEquals(expected, _popup?.Child))
                return; // closed or rebuilt since — the newer refresh posted its own focus
            UIElement? first = _hiddenChips.Count > 0 ? _hiddenChips[0].Chip
                : _visibleChecks.Count > 0 ? _visibleChecks[0].Check
                : ShowAllButton;
            first?.Focus(FocusNavigationMethod.Programmatic);
        });
    }

    /// <summary>Closes (light dismiss / teardown funnel here too via the Closed handler).</summary>
    public void Close() => _popup?.SetCurrentValue(Popup.IsOpenProperty, false);

    private Popup CreatePopup()
    {
        var popup = new Popup { StaysOpen = false }; // light-dismiss participant; Esc rides CloseOnEscape
        popup.Closed += OnPopupClosed;
        return popup;
    }

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        IsOpen = false;
        _hiddenChips.Clear();
        _visibleChecks.Clear();
        ShowAllButton = null;
        HideAllButton = null;
        _grid.Focus(FocusNavigationMethod.Programmatic); // the header anchor isn't focusable
    }

    /// <summary>
    /// A visibility toggle restructures the chip/check partition — rebuild the content in place
    /// while the popup stays open (the filter popup rebuilds per OPEN; here the model mutates
    /// per click, so the rebuild rides the click).
    /// </summary>
    private void RefreshOpenContent()
    {
        if (IsOpen && _popup is not null)
        {
            _popup.Child = BuildContent();
            // The clicked control just detached with the old subtree — re-park focus on the new
            // first row so Esc/arrows keep working without a deterministic-repair dependency.
            FocusFirstRow();
        }
    }

    private void SetVisible(DataGridColumn column, bool visible)
    {
        if (column.Visible == visible)
            return;
        column.Visible = visible;
        // Visible carries no change notification into layout — the chooser is a UX surface and owns
        // funnelling the geometry re-resolve (same contract as the header's resize writes).
        _grid.NotifyColumnGeometryChanged();
        RefreshOpenContent();
    }

    private UIElement BuildContent()
    {
        _hiddenChips.Clear();
        _visibleChecks.Clear();

        var panel = new StackPanel(); // vertical
        // Contained arrows keep keyboard focus walking the chooser's own rows (the checklist-popup
        // idiom — a stray Down must not leak into the grid below).
        KeyboardNavigation.SetDirectionalNavigation(panel, DirectionalNavigationMode.Contained);

        var title = new TextBlock { Text = "Column Chooser" };
        title.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);
        panel.Children.Add(title);

        var rowsHost = new StackPanel();

        // Hidden columns first (the mockup's parked chips): ⠿ grip + caption; click shows.
        var hidden = _grid.Columns.Where(c => !c.Visible).ToList();
        if (hidden.Count > 0)
        {
            var prompt = new TextBlock { Text = "Hidden columns — click to show:" };
            prompt.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);
            rowsHost.Children.Add(prompt);

            foreach (var column in hidden)
            {
                var chip = new Button { Content = $"⠿ {column.EffectiveHeader}" };
                var captured = column;
                chip.Click += (_, _) => SetVisible(captured, true);
                _hiddenChips.Add((column, chip));
                rowsHost.Children.Add(chip);
            }
        }

        // Visible columns as checked entries; unchecking hides.
        foreach (var column in _grid.Columns.Where(c => c.Visible))
        {
            var check = new CheckBox { Content = column.EffectiveHeader, IsChecked = true };
            var captured = column;
            check.Click += (_, _) => SetVisible(captured, false);
            _visibleChecks.Add((column, check));
            rowsHost.Children.Add(check);
        }

        panel.Children.Add(new ScrollViewer
        {
            Content = rowsHost,
            MaxHeight = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        // Footer: ▦ Show All / ⊘ Hide All (the mockup's cfoot band). Hide All deliberately allows
        // an all-hidden grid — the chooser itself remains the way back (DevExpress parity).
        ShowAllButton = new Button { Content = "▦ Show All" };
        ShowAllButton.Click += (_, _) => SetAll(visible: true);
        HideAllButton = new Button { Content = "⊘ Hide All" };
        HideAllButton.Click += (_, _) => SetAll(visible: false);
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        footer.Children.Add(ShowAllButton);
        footer.Children.Add(HideAllButton);
        panel.Children.Add(footer);

        var border = new Border { Child = panel, Padding = new Margins(1, 0, 1, 0), MinWidth = 26 };
        border.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
        border.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
        return border;
    }

    private void SetAll(bool visible)
    {
        bool changed = false;
        foreach (var column in _grid.Columns)
        {
            if (column.Visible != visible)
            {
                column.Visible = visible;
                changed = true;
            }
        }
        if (changed)
        {
            _grid.NotifyColumnGeometryChanged();
            RefreshOpenContent();
        }
    }
}
