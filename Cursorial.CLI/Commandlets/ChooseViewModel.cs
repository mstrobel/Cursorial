using System.Windows.Input;

using Cursorial.CLI.Wire;
using Cursorial.UI;

namespace Cursorial.CLI.Commandlets;

/// <summary>`curio choose` — pick one item from a list (multi-select lands with M1).</summary>
public sealed class ChooseViewModel : CommandletViewModel
{
    private string? _selected;

    public ChooseViewModel(UIApplication app, string prompt, IReadOnlyList<string> items) : base(app)
    {
        Prompt = prompt;
        Items = items;
        _selected = items.Count > 0 ? items[0] : null;
        AcceptCommand = new DelegateCommand(() =>
        {
            if (_selected is not null)
                Accept();
        });
    }

    public string Prompt { get; init => SetProperty(ref field, value); }

    public IReadOnlyList<string> Items { get; init => SetProperty(ref field, value); }

    public string? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public ICommand AcceptCommand { get; init => SetProperty(ref field, value); }

    public override Variable BuildResult(string name)
    {
        var index = 0;
        for (; index < Items.Count && !string.Equals(Items[index], _selected, StringComparison.Ordinal); index++) { }
        return new Variable(name, VariableKind.Selection, [_selected ?? ""], [index]);
    }
}
