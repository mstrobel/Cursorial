using System.Windows.Input;

using Cursorial.CLI.Wire;
using Cursorial.UI;

namespace Cursorial.CLI.Commandlets;

/// <summary>
/// `curio write` — a multiline text prompt. Enter inserts a newline inside the editor
/// (<c>AcceptsReturn</c>), so accepting is the Ctrl+D root key binding instead (Ctrl+Enter is
/// swallowed by the editor's AcceptsReturn arm, and legacy terminals can't even distinguish it
/// from Enter — both send CR). The multiline value is one Text variable; `--emit lines` writes it
/// verbatim, newlines included.
/// </summary>
public sealed class WriteViewModel : CommandletViewModel
{
    public WriteViewModel(UIApplication app, string prompt, string initialValue, string placeholder = "",
                          int lines = 5) : base(app)
    {
        Prompt = prompt;
        Placeholder = placeholder;
        Lines = lines;
        Text = initialValue;
        AcceptCommand = new DelegateCommand(Accept);
    }

    public int Lines { get; init => SetProperty(ref field, value); }

    public string Prompt { get; init => SetProperty(ref field, value); }

    public string Text { get; set => SetProperty(ref field, value); }

    public string Placeholder { get; init => SetProperty(ref field, value); }

    public ICommand AcceptCommand { get; init => SetProperty(ref field, value); }

    public override Variable BuildResult(string name)
        => new(name, VariableKind.Text, [Text], []);
}
