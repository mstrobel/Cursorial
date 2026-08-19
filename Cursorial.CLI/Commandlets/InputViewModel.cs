using System.Windows.Input;

using Cursorial.CLI.Wire;
using Cursorial.UI;

namespace Cursorial.CLI.Commandlets;

/// <summary>`curio input` — a single-line text prompt.</summary>
public sealed class InputViewModel : CommandletViewModel
{
    public InputViewModel(UIApplication app, string prompt, string initialValue, string placeholder = "") : base(app)
    {
        Prompt = prompt;
        Placeholder = placeholder;
        Text = initialValue;
        AcceptCommand = new DelegateCommand(Accept);
    }

    public string Prompt { get; init => SetProperty(ref field, value); }

    public string Text { get; set => SetProperty(ref field, value); }

    public string Placeholder { get; init => SetProperty(ref field, value); }

    public ICommand AcceptCommand { get; init => SetProperty(ref field, value); }

    public override Variable BuildResult(string name)
        => new(name, VariableKind.Text, [Text], []);
}
