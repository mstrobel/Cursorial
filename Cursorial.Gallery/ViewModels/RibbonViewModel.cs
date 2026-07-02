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
    private bool _fullScreenBackstage;

    public RibbonViewModel()
    {
        // Descriptions light up SuperTips (rich hover help) — hover a control to see the titled, multi-line tip the
        // command auto-provisions (title = the command name, the accelerator, and this body).
        Cut = new BarCommand(() => Report("Cut")) { Text = "C_ut", InputGestureText = "Ctrl+X", Description = "Cut the selection to the clipboard." };
        Copy = new BarCommand(() => Report("Copy")) { Text = "_Copy", InputGestureText = "Ctrl+C", Description = "Copy the selection to the clipboard." };
        Paste = new BarCommand(() => Report("Paste")) { Text = "_Paste", InputGestureText = "Ctrl+V", Description = "Paste the clipboard contents at the cursor." };
        Find = new BarCommand(() => Report("Find")) { Text = "Fi_nd", InputGestureText = "Ctrl+F", Description = "Find text in the document." };
        Undo = new BarCommand(() => Report("Undo")) { Text = "Undo", InputGestureText = "Ctrl+Z", Description = "Undo the last action." };
        Redo = new BarCommand(() => Report("Redo")) { Text = "Redo", InputGestureText = "Ctrl+Y", Description = "Redo the last undone action." };
        Settings = new BarCommand(() => Report("Settings")) { Text = "_Settings", Description = "Open application settings." };

        // Quick Access Toolbar (caption row): Undo/Redo/Paste start ON; the customize ▾ checklist toggles the rest.
        QuickAccessDefaults = [(BarCommand) Undo, (BarCommand) Redo, (BarCommand) Paste];
        QuickAccessCandidates = [(BarCommand) Cut, (BarCommand) Copy, (BarCommand) Paste, (BarCommand) Find, (BarCommand) Undo, (BarCommand) Redo, (BarCommand) Settings];

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

    /// <summary>Commands that start ON the Quick Access Toolbar (GalleryRibbon populates the ribbon's QuickAccessCommands from this).</summary>
    public IReadOnlyList<BarCommand> QuickAccessDefaults { get; }

    /// <summary>The full candidate set the QAT customize ▾ checklist offers (a superset of the defaults).</summary>
    public IReadOnlyList<BarCommand> QuickAccessCandidates { get; }
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

    /// <summary>Whether the File tab opens Backstage full-screen (a maximized modal Window) or as a compact
    /// File-anchored menu (a light-dismiss Popup). Two-way from the body CheckBox — the user's stated worry is that
    /// full-screen overwhelms a terminal, so the menu form is the default and this reveals the full-screen form.</summary>
    public bool FullScreenBackstage
    {
        get => _fullScreenBackstage;
        set => Set(ref _fullScreenBackstage, value);
    }

    /// <summary>The <see cref="BackstageDisplayMode"/> the gallery opens Backstage in (from <see cref="FullScreenBackstage"/>).</summary>
    public BackstageDisplayMode BackstageMode => _fullScreenBackstage ? BackstageDisplayMode.FullScreen : BackstageDisplayMode.Menu;

    private void Toggle(CheckableCommandParameter state, string label)
    {
        state.Toggle();
        Report($"{label} {(state.IsChecked ? "on" : "off")}");
    }

    /// <summary>Echoes a Backstage event (open / destination selected / closed) into the page status. The gallery view
    /// (<c>GalleryApp</c>) builds + hosts the real <see cref="Backstage"/> on the File tab's <c>BackstageRequested</c>;
    /// the VM only reports (MVVM — the control tree is the view's, not the view-model's).</summary>
    public void ReportBackstage(string what) => Status = what;

    private void Report(string what) => Status = $"{what} invoked.";
}
