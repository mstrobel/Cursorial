using System.Windows.Input;

using Cursorial.UI;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// Executes an <see cref="ICommand"/> when the trigger fires (design doc §5), gated on
/// <see cref="ICommand.CanExecute"/>. The command parameter is <see cref="CommandParameter"/> when set,
/// else the trigger's firing payload (an <c>EventTrigger</c> passes the <c>RoutedEventArgs</c>).
/// <see cref="Command"/>/<see cref="CommandParameter"/> are styled properties, so they bind
/// (<c>Command="{Binding SaveCommand}"</c>) once the XAML lanes wire action bindings (P3).
/// </summary>
public class InvokeCommandAction : TriggerAction
{
    /// <summary>The command to execute (BCL <see cref="ICommand"/> — the framework's command surface).</summary>
    public static readonly StyledProperty<ICommand?> CommandProperty =
        UIProperty.Register<InvokeCommandAction, ICommand?>(nameof(Command));

    /// <summary>An explicit command parameter; when unset the trigger's firing payload is passed.</summary>
    public static readonly StyledProperty<object?> CommandParameterProperty =
        UIProperty.Register<InvokeCommandAction, object?>(nameof(CommandParameter));

    /// <inheritdoc cref="CommandProperty"/>
    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <inheritdoc cref="CommandParameterProperty"/>
    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    /// <inheritdoc/>
    protected override void Invoke(object? sender, object? parameter)
    {
        if (Command is not { } command)
            return;

        var argument = CommandParameter ?? parameter;
        if (command.CanExecute(argument))
            command.Execute(argument);
    }
}
