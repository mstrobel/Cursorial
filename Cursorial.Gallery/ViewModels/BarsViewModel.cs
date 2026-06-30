using System.Windows.Input;

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

    private void Toggle(CheckableCommandParameter state, string label)
    {
        state.Toggle(); // the command owns the checked state; the toggle button re-syncs on the re-query
        Report($"{label} {(state.IsChecked ? "on" : "off")}");
    }

    private void Report(string what) => Status = $"{what} invoked.";
}
