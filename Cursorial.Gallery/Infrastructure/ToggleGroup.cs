using Cursorial.UI;
using Cursorial.UI.Bars;

namespace Cursorial.Gallery.Infrastructure;

internal sealed record ToggleGroup
{
    public ToggleGroup(params BarCommand[] Commands)
    {
        this.Commands = new BarCommand[Commands.Length];
            
        for (int i = 0; i < Commands.Length; i++)
        {
            var c = Commands[i];

            var nc = new BarCommand
                     {
                         Icon = c.Icon,
                         Text = c.Text,
                         Description = c.Description,
                         IsCheckable = c.IsCheckable,
                         InputGestureText = c.InputGestureText
                     };

            nc.Command = new DelegateCommand(p =>
                                             {
                                                 if (p is CheckableCommandParameter cp)
                                                     cp.Override(Select(nc));
                                             },
                                             p =>
                                             {
                                                 if (p is CheckableCommandParameter cp)
                                                     cp.Override(Selected == nc);

                                                 return c.Command?.CanExecute(p) ?? false;
                                             });

            this.Commands[i] = nc;
        }
    }
    public BarCommand? Selected { get; private set; }
    public BarCommand[] Commands { get; init; }

    public bool Select(BarCommand command)
    {
        if (Selected == command)
            Selected = null;
        else
            Selected = command;

        foreach (var c in Commands)
            c.RaiseCanExecuteChanged();
            
        return true;
    }

    public void Deconstruct(out BarCommand[] Commands)
    {
        Commands = this.Commands;
    }
}