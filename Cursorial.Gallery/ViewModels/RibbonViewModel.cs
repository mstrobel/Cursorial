using System.Windows.Input;

using Cursorial.UI;
using Cursorial.UI.Bars;

namespace Cursorial.Gallery.ViewModels;

/// <summary>
/// The Ribbon page (Surface B): the SAME kind of <see cref="BarCommand"/> set the Toolbar page binds, arranged into
/// tabbed groups. Its own command instances (separate from the Toolbar page) so the two surfaces don't collide on
/// access keys — a real app hosts ONE surface at a time; here each is its own page. Large Paste/Find (glyph-over-
/// label); small B/I/U toggles; switch tabs by click or Left/Right; the File tab opens Backstage (a stub).
/// </summary>
public sealed class RibbonViewModel : PageViewModel
{
    private string _status = "Ready — invoke a command, switch tabs, or press Alt for access keys.";
    private bool _tableSelected;

    public RibbonViewModel()
    {
        Cut = new BarCommand(() => Report("Cut")) { Text = "Cu_t", InputGestureText = "Ctrl+X" };
        Copy = new BarCommand(() => Report("Copy")) { Text = "_Copy", InputGestureText = "Ctrl+C" };
        Paste = new BarCommand(() => Report("Paste")) { Text = "_Paste", InputGestureText = "Ctrl+V" };
        Find = new BarCommand(() => Report("Find")) { Text = "_Find", InputGestureText = "Ctrl+F" };
        Undo = new BarCommand(() => Report("Undo")) { Text = "_Undo", InputGestureText = "Ctrl+Z" };
        Redo = new BarCommand(() => Report("Redo")) { Text = "_Redo", InputGestureText = "Ctrl+Y" };
        Settings = new BarCommand(() => Report("Settings")) { Text = "_Settings" };

        BoldState = new CheckableCommandParameter();
        Bold = new BarCommand(p => Toggle((CheckableCommandParameter) p!, "Bold")) { Text = "_Bold", IsCheckable = true };
        ItalicState = new CheckableCommandParameter();
        Italic = new BarCommand(p => Toggle((CheckableCommandParameter) p!, "Italic")) { Text = "_Italic", IsCheckable = true };

        Options = new BarCommand(() => Report("Clipboard options")); // the ⋰ dialog launcher target

        // Contextual-tab demo (P3a): TableSelected (two-way from the body CheckBox) shows/hides the purple "Table" tab.
        MergeCells = new BarCommand(() => Report("Merge cells")) { Text = "_Merge" };
        SplitCells = new BarCommand(() => Report("Split cells")) { Text = "_Split" };
        DeleteTable = new BarCommand(() => Report("Delete table")) { Text = "_Delete" };
    }

    public override string Title => "Ribbon";

    public override string Summary => "A tabbed-group Ribbon over the same bar controls — Large glyph-over-label + small buttons, one command per surface.";

    /// <summary>Echoes the last command invocation (bound by the page body).</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    public ICommand Cut { get; }
    public ICommand Copy { get; }
    public ICommand Paste { get; }
    public ICommand Find { get; }
    public ICommand Undo { get; }
    public ICommand Redo { get; }
    public ICommand Settings { get; }
    public ICommand Options { get; }

    public ICommand Bold { get; }
    public CheckableCommandParameter BoldState { get; }
    public ICommand Italic { get; }
    public CheckableCommandParameter ItalicState { get; }

    public ICommand MergeCells { get; }
    public ICommand SplitCells { get; }
    public ICommand DeleteTable { get; }

    /// <summary>Whether a "table" is selected — drives the contextual Table tab's visibility (P3a). Two-way from the
    /// body CheckBox: checking it shows the purple Table tab, unchecking hides it (and the ribbon falls back if it
    /// was active).</summary>
    public bool TableSelected
    {
        get => _tableSelected;
        set
        {
            if (!Set(ref _tableSelected, value))
                return;
            Raise(nameof(TableToolsVisibility));
            Report(value ? "Table selected — the purple Table tab appears" : "Table deselected — the tab hides");
        }
    }

    /// <summary>The contextual Table tab's visibility (bound in XAML; a contextual tab is hidden when not relevant).</summary>
    public Visibility TableToolsVisibility => _tableSelected ? Visibility.Visible : Visibility.Collapsed;

    private void Toggle(CheckableCommandParameter state, string label)
    {
        state.Toggle();
        Report($"{label} {(state.IsChecked ? "on" : "off")}");
    }

    /// <summary>The File tab was activated (click / Enter / Alt+F) — Backstage is the P3e full-window File view (not
    /// built yet), so the gallery just echoes that File is a reachable, activatable command tab.</summary>
    public void NotifyBackstageRequested() =>
        Status = "File → Backstage requested (the full-window File view lands in Ribbon P3e).";

    private void Report(string what) => Status = $"{what} invoked.";
}
