using System.Windows.Input;

using Cursorial.UI;
using Cursorial.UI.Bars;

namespace Cursorial.Gallery.ViewModels;

/// <summary>
/// The Bars / Toolbar page: one set of <see cref="BarCommand"/>s bound to a <see cref="Toolbar"/> of bar controls.
/// Each command's <c>Text</c>/gesture auto-fills its button (define-once), the two toggles reflect a command-owned
/// <see cref="CheckableCommandParameter"/>, and <see cref="Status"/> echoes the last invocation. Narrow the window to
/// watch the trailing items fold into the <c>»</c> overflow popup; the "Settings" button is pinned to the popup
/// (<see cref="ToolbarOverflowMode.Always"/>).
/// </summary>
public sealed class BarsViewModel : PageViewModel
{
    private string _status = "Ready — invoke a command, or narrow the window to overflow the bar.";

    public BarsViewModel()
    {
        Cut = new BarCommand(() => Report("Cut")) { Text = "Cu_t", InputGestureText = "Ctrl+X" };
        Copy = new BarCommand(() => Report("Copy")) { Text = "_Copy", InputGestureText = "Ctrl+C" };
        Paste = new BarCommand(() => Report("Paste")) { Text = "_Paste", InputGestureText = "Ctrl+V" };
        Undo = new BarCommand(() => Report("Undo")) { Text = "_Undo", InputGestureText = "Ctrl+Z" };
        Redo = new BarCommand(() => Report("Redo")) { Text = "_Redo", InputGestureText = "Ctrl+Y" };
        Find = new BarCommand(() => Report("Find")) { Text = "_Find", InputGestureText = "Ctrl+F" };
        Settings = new BarCommand(() => Report("Settings")) { Text = "_Settings" };

        BoldState = new CheckableCommandParameter();
        Bold = new BarCommand(p => Toggle((CheckableCommandParameter) p!, "Bold")) { Text = "_Bold", IsCheckable = true };

        ItalicState = new CheckableCommandParameter();
        Italic = new BarCommand(p => Toggle((CheckableCommandParameter) p!, "Italic")) { Text = "_Italic", IsCheckable = true };

        // A split button (primary action + ▾ dropdown) and a popup button (whole control opens its menu); the dropdown
        // rows invoke Pick with a label. The combobox picks a font (SelectedFont).
        Color = new BarCommand(() => Report("Color (default)")) { Text = "Color" };
        Pick = new BarCommand(p => Report(p?.ToString() ?? ""));
    }

    public override string Title => "Bars / Toolbar";

    public override string Summary => "A command Toolbar with discrete overflow — narrow the window to fold the trailing items into the » popup.";

    /// <summary>Echoes the last command invocation (bound by the shell status bar + the page body).</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    public ICommand Cut { get; }
    public ICommand Copy { get; }
    public ICommand Paste { get; }
    public ICommand Undo { get; }
    public ICommand Redo { get; }
    public ICommand Find { get; }
    public ICommand Settings { get; }

    public ICommand Bold { get; }
    public CheckableCommandParameter BoldState { get; }

    public ICommand Italic { get; }
    public CheckableCommandParameter ItalicState { get; }

    /// <summary>The split button's primary action; the ▾ zone opens a dropdown of <see cref="Pick"/> choices.</summary>
    public ICommand Color { get; }

    /// <summary>A dropdown/menu choice (parameter = the label shown in <see cref="Status"/>).</summary>
    public ICommand Pick { get; }

    /// <summary>The BarComboBox choices (font family).</summary>
    public string[] Fonts { get; } = { "Cascadia Code", "Consolas", "Menlo", "Fira Code", "JetBrains Mono" };

    private string _selectedFont = "Cascadia Code";

    /// <summary>The BarComboBox selection.</summary>
    public string SelectedFont
    {
        get => _selectedFont;
        set { if (Set(ref _selectedFont, value)) Report($"Font → {value}"); }
    }

    /// <summary>The BarGallery choices (font size — the drop-down lays them out as a grid).</summary>
    public string[] Sizes { get; } = { "10", "11", "12", "13", "14", "16", "18", "24" };

    private string _selectedSize = "13";

    /// <summary>The BarGallery selection.</summary>
    public string SelectedSize
    {
        get => _selectedSize;
        set { if (Set(ref _selectedSize, value)) Report($"Size → {value}"); }
    }

    private void Toggle(CheckableCommandParameter state, string label)
    {
        state.Toggle(); // the command owns the checked state; the toggle button re-syncs on the re-query
        Report($"{label} {(state.IsChecked is true ? "on" : state.IsChecked is false ? "off" : "indeterminate")}");
    }

    private void Report(string what) => Status = $"{what} invoked.";
}
