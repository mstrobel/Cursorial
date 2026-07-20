using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The column filter checklist popup (design doc §3.4; the mockup's <c>fpop</c>): a light-dismiss
/// <see cref="Popup"/> anchored below the column's header cell carrying a search box, a
/// "(Select All)" master row, one <see cref="CheckBox"/> per distinct formatted value (from
/// <see cref="DataViewController.GetDistinctValues"/> — typed dedupe, sorted, "(Blanks)" for null
/// keys), and OK/Cancel. OK writes the column's <see cref="FilterNode.InSet"/> fragment (all
/// checked ⇒ clears the fragment — an unfiltered column carries no tree); Cancel/light-dismiss/Esc
/// close without writing. CheckBox rows (not ListBox selection) carry the check model — search
/// filtering only flips row <see cref="Visibility"/>, so hidden rows keep their state and the
/// arrow keys ride the stack's contained directional navigation (the Ribbon QAT checklist idiom).
/// </summary>
internal sealed class DataGridFilterPopup
{
    private readonly DataGrid _grid;
    private Popup? _popup;
    private DataGridColumn? _column;
    private readonly List<(object? Raw, string Display, CheckBox Check)> _rows = [];
    private CheckBox? _selectAll;
    private bool? _selectAllState; // the RECORDED tri-state truth (SyncSelectAllState writes it)
    private TextBox? _search;
    private bool _syncingChecks;

    public DataGridFilterPopup(DataGrid grid) => _grid = grid;

    /// <summary>Whether the popup is currently open (the grid's test hook gates on it).</summary>
    internal bool IsOpen { get; private set; }

    /// <summary>The distinct entries backing the rows (tests assert the population).</summary>
    internal IReadOnlyList<(object? Raw, string Display, CheckBox Check)> Rows => _rows;

    internal TextBox? SearchBox => _search;

    internal CheckBox? SelectAllBox => _selectAll;

    /// <summary>The row checkbox for a raw value (tests toggle through the real control).</summary>
    internal CheckBox? CheckBoxFor(object? raw)
    {
        foreach (var (rowRaw, _, check) in _rows)
        {
            if (Equals(rowRaw, raw))
                return check;
        }
        return null;
    }

    /// <summary>Opens (or re-anchors) the checklist for <paramref name="column"/> below its header cell.</summary>
    public void Open(DataGridColumn column, UIElement anchor, int cellX)
    {
        var controller = _grid.Controller;
        if (controller is null)
            return;

        if (IsOpen)
            _popup?.SetCurrentValue(Popup.IsOpenProperty, false); // a Popup re-places only on the closed→open edge

        _column = column;
        _popup ??= CreatePopup();
        _popup.Child = BuildContent(column, controller);
        _popup.PlacementTarget = anchor;
        _popup.Placement = PlacementMode.Bottom;
        _popup.HorizontalOffset = cellX; // below the CELL, not the band edge (the WM clamps on-screen)
        _popup.SetCurrentValue(Popup.IsOpenProperty, true);
        IsOpen = true;

        // Focus the search box once the popup surface materializes (the parked-work idiom).
        var search = _search;
        UIApplication.Current?.Dispatcher.Post(() =>
        {
            if (IsOpen && ReferenceEquals(search, _search))
                search?.Focus(FocusNavigationMethod.Programmatic);
        });
    }

    /// <summary>Closes without writing (Cancel / teardown).</summary>
    public void Close() => _popup?.SetCurrentValue(Popup.IsOpenProperty, false);

    private Popup CreatePopup()
    {
        var popup = new Popup { StaysOpen = false }; // light-dismiss participant; Esc closes (CloseOnEscape default)
        popup.Closed += OnPopupClosed;
        return popup;
    }

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        // Every close path funnels here (OK, Cancel, light dismiss, Esc, teardown): drop the row
        // model and hand focus back to the grid deterministically (the header anchor isn't focusable).
        IsOpen = false;
        _rows.Clear();
        _selectAll = null;
        _search = null;
        _grid.Focus(FocusNavigationMethod.Programmatic);
    }

    // ── Content (rebuilt per open — the distinct set is a snapshot of the moment) ─────────────────

    private UIElement BuildContent(DataGridColumn column, DataViewController controller)
    {
        _rows.Clear();

        // Pre-selection: an active InSet fragment re-checks its members; anything else (no filter /
        // a grammar Condition the checklist will replace) starts all-checked.
        var active = _grid.GetColumnFilter(column) is FilterValueSetNode set
            ? new HashSet<object?>(set.Values)
            : null;

        var panel = new StackPanel(); // vertical
        // One directional scope over the whole popup (search → select-all → values → OK/Cancel):
        // Up/Down walk the rows from the focused search box, Contained keeps arrows inside the
        // popup (the Ribbon QAT checklist idiom — a stray Down must not leak to the grid below).
        KeyboardNavigation.SetDirectionalNavigation(panel, DirectionalNavigationMode.Contained);

        var title = new TextBlock { Text = $"Filter: {column.EffectiveHeader}" };
        title.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);
        panel.Children.Add(title);

        _search = new TextBox();
        panel.Children.Add(_search);
        _search.TextChanged += (_, _) => ApplySearchFilter();

        var rowsHost = new StackPanel();

        _selectAll = new CheckBox { Content = "(Select All)" };
        _selectAll.Click += (_, _) => OnSelectAllClicked();
        rowsHost.Children.Add(_selectAll);

        foreach (var (formatted, raw, count) in controller.GetDistinctValues(column, maxCount: 1000))
        {
            string display = raw is null ? "(Blanks)" : formatted;
            var check = new CheckBox
            {
                // The mockup shows each value's row COUNT beside it; search still matches on Display.
                Content = count > 0 ? $"{display}   {count}" : display,
                IsChecked = active is null || active.Contains(raw),
            };
            check.Click += (_, _) => SyncSelectAllState();
            _rows.Add((raw, display, check));
            rowsHost.Children.Add(check);
        }
        SyncSelectAllState();

        panel.Children.Add(new ScrollViewer
        {
            Content = rowsHost,
            MaxHeight = 10,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var ok = new Button { Content = "OK" };
        // The primary action reads accent-filled (the mockup's OK-in-accent row); the dialog-ground
        // ink inverts to legible contrast in both themes.
        ok.SetResourceReference(Control.BackgroundProperty, ThemeKeys.AccentBrush);
        ok.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.ElevationDialog);
        ok.Click += (_, _) => Apply();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var border = new Border { Child = panel, Padding = new Margins(1, 0, 1, 0), MinWidth = 24 };
        border.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
        border.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);

        // Enter anywhere in the popup = OK (the checklist's commit gesture; Esc rides the Popup's
        // own CloseOnEscape). On the PREVIEW tunnel deliberately: ButtonBase activates on the
        // Enter key-DOWN, so a bubble handler would run after a focused CheckBox already consumed
        // Enter as a toggle — the root's tunnel pass preempts that (Enter means commit everywhere
        // in this popup, the mockup's OK-in-accent row).
        border.AddHandler(UIElement.PreviewKeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            if (e.Key == Key.Enter && !e.Handled)
            {
                Apply();
                e.Handled = true;
            }
        });

        return border;
    }

    private void ApplySearchFilter()
    {
        string needle = _search?.Text ?? string.Empty;
        foreach (var (_, display, check) in _rows)
        {
            check.Visibility = needle.Length == 0 || display.Contains(needle, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed; // hidden rows KEEP their check state — search never unchecks
        }
    }

    private void OnSelectAllClicked()
    {
        if (_selectAll is null)
            return;

        // The target derives from the RECORDED pre-toggle tri-state (ToggleButton's own cycle
        // already ran null→false before Click reaches us, so the live IsChecked can't tell
        // partial from none): partial (null) and none (false) both CHECK ALL — the
        // Excel/DevExpress/Explorer checklist convention — and only a fully-checked master
        // unchecks. The trailing sync stamps the master over the cycle's landing value.
        bool target = _selectAllState != true;
        _syncingChecks = true;
        try
        {
            foreach (var (_, _, check) in _rows)
                check.IsChecked = target;
        }
        finally
        {
            _syncingChecks = false;
        }
        SyncSelectAllState(); // recomputes over the settled rows ⇒ records + stamps `target`
    }

    /// <summary>
    /// The tri-state master mark: all ⇒ ✓, none ⇒ empty, partial ⇒ indeterminate (▪). Records the
    /// computed state in <see cref="_selectAllState"/> — the pre-toggle truth
    /// <see cref="OnSelectAllClicked"/> reads — then stamps the CheckBox.
    /// </summary>
    private void SyncSelectAllState()
    {
        if (_syncingChecks || _selectAll is null)
            return;

        int selected = 0;
        foreach (var (_, _, check) in _rows)
        {
            if (check.IsChecked == true)
                selected++;
        }

        _selectAllState = selected == _rows.Count ? true
            : selected == 0 ? false
            : null;
        _selectAll.IsChecked = _selectAllState;
    }

    /// <summary>OK: writes the InSet fragment (all-checked ⇒ clears) and closes.</summary>
    internal void Apply()
    {
        if (_column is { } column)
        {
            var raws = new List<object?>();
            string? single = null;
            foreach (var (raw, display, check) in _rows)
            {
                if (check.IsChecked == true)
                {
                    raws.Add(raw);
                    single = raws.Count == 1 ? display : null;
                }
            }

            if (raws.Count == _rows.Count)
            {
                _grid.SetColumnFilter(column, null); // everything checked — an unfiltered column carries no tree
            }
            else
            {
                // The auto-filter picker cell's summary: the lone value, or a count digest.
                string summary = single ?? $"({raws.Count})";
                _grid.SetColumnFilter(column, FilterNode.InSet(column, raws), summary);
            }
        }

        Close();
    }
}
