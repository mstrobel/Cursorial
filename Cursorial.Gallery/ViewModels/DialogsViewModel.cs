using System.Windows.Input;

using Cursorial.Gallery.Infrastructure;
using Cursorial.UI;
using Cursorial.UI.Dialogs;

using MessageBox = Cursorial.UI.Dialogs.MessageBox;
using MessageBoxButton = Cursorial.UI.Dialogs.MessageBoxButton;

namespace Cursorial.Gallery.ViewModels;

public class DialogsViewModel : PageViewModel
{
    public override string Title => "Dialogs";
    public override string Summary => "Examples of message and task dialogs and their usage.";

    private bool _isDialogShowing;

    public DialogsViewModel(UIApplication application)
    {
        Application = application;
        ShowTaskDialogCommand = new RelayCommand(ShowTaskDialog, () => !_isDialogShowing);
        ShowMessageDialogCommand = new RelayCommand(ShowMessageDialog, () => !_isDialogShowing);
        DialogService = new TaskDialogService(application);
    }

    private UIApplication Application { get; }
    
    private ITaskDialogService DialogService { get; }

    public ICommand ShowTaskDialogCommand { get; }

    public ICommand ShowMessageDialogCommand { get; }

    public string? Status
    {
        get;
        set => Set(ref field, value);
    }

    private async void ShowMessageDialog()
    {
        try
        {
            if (_isDialogShowing) return;

            _isDialogShowing = true;


            var result = await MessageBox.ShowAsync(
                             Application,
                             message: "Would you like to drink the purple vial?",
                             title: "Make a Choice",
                             buttons: MessageBoxButton.YesNo,
                             defaultButton: MessageBoxButton.Yes,
                             cancelButton: MessageBoxButton.No);

            Status = $"You chose: {result}";
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
        finally
        {
            _isDialogShowing = false;
        }
    }

    private async void ShowTaskDialog()
    {
        try
        {
            if (_isDialogShowing) return;

            _isDialogShowing = true;

            var r = new TaskDialogRequest("Make a Choice")
                    {
                        Title = "Very Serious",
                        Content = "You come across a collection of vials. " +
                                  "There are no descriptions to be found. " +
                                  "What do you do?",
                        Buttons =
                        [
                            new TaskDialogButton("Drink", "_Drink It")
                            {
                                Explanation = "Drink the purple vial. You know you want to. It's [i]purple[/i].",
                                ExplanationContainsMarkup = true,
                                IsDefault = true
                            },
                            new TaskDialogButton("Smash", "_Smash It")
                            {
                                Explanation = "Smash the red vial. It had it coming."
                            },
                            new TaskDialogButton("Toss", "_Toss It")
                            {
                                Explanation = "Hurl the red vial at the butler. Screw that guy."
                            },
                            new TaskDialogButton("Wat", "_Wat"),
                            new TaskDialogButton("Nah", "Yeah, _Nah") { IsCancel = true }
                        ],
                        VerificationText = "I am a meat popsicle",
                        ExpandedInformation = "You're a meat popsicle. You're a meat popsicle. You're a meat popsicle.",
                        Severity = TaskDialogSeverity.Question
                    };
            
            var result = await DialogService.ShowAsync(r);

            Status = $"You chose: {result.Button?.Id ?? "Wimp Out"}";
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
        }
        finally
        {
            _isDialogShowing = false;
        }
    }
}