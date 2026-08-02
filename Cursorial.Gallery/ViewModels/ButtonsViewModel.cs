using System.Windows.Input;

using Cursorial.Gallery.Infrastructure;

namespace Cursorial.Gallery.ViewModels;

public sealed class ButtonsViewModel : PageViewModel
{
    public override string Title => "Buttons";
    public override string Summary => "Buttons for user interaction, with various intent classes.";

    private int _clickCount;
    private DateTimeOffset _lastClickTime;

    public ButtonsViewModel()
    {
        ClickCommand = new RelayCommand<string>(ExecuteClickCommand);
    }

    public ICommand ClickCommand { get; }

    public override string? Status
    {
        get;
        protected set => Set(ref field, value);
    }

    private void ExecuteClickCommand(string name)
    {
        const int repeatInterval = 100;

        name = name.Replace("_", "");

        var now = DateTimeOffset.Now;

        if ((now - _lastClickTime).TotalMilliseconds <= repeatInterval && name is "Repeat")
            _clickCount++;
        else
            _clickCount = 1;

        _lastClickTime = now;
        
        Status = $"Clicked: {name}" + (_clickCount > 1 ? $" ({_clickCount}x)" : "");
    }
}