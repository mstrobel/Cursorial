using System.Windows.Input;

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

    private void Toggle(CheckableCommandParameter state, string label)
    {
        state.Toggle();
        Report($"{label} {(state.IsChecked ? "on" : "off")}");
    }

    private void Report(string what) => Status = $"{what} invoked.";
}
