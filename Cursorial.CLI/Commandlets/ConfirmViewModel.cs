using System.Windows.Input;

using Cursorial.CLI.Wire;
using Cursorial.UI;

namespace Cursorial.CLI.Commandlets;

/// <summary>
/// `curio confirm` — a yes/no question. "Yes" accepts (exit 0, receipt retained); "No" exits with the
/// canceled code and clears, per the gum-parity convention (§4.4 of the design doc; `--optional` binding of
/// a false result arrives with the M1 runner semantics).
/// </summary>
public sealed class ConfirmViewModel : CommandletViewModel
{
    public ConfirmViewModel(UIApplication app, string message, bool defaultResponse = false) : base(app)
    {
        Message = message;
        YesCommand = new DelegateCommand(Accept);
        NoCommand = new DelegateCommand(Cancel);
        DefaultCommand = defaultResponse ? YesCommand : NoCommand;
        Prompt = defaultResponse ? "[Y/n]" : "[y/N]";
    }

    public string Prompt { get; init => SetProperty(ref field, value); }

    public string Message { get; init => SetProperty(ref field, value); }

    public ICommand YesCommand { get; init => SetProperty(ref field, value); }

    public ICommand NoCommand { get; init => SetProperty(ref field, value); }

    public ICommand DefaultCommand { get; init => SetProperty(ref field, value); }

    public override Variable BuildResult(string name)
        => new(name, VariableKind.Bool, ["true"], []);
}
