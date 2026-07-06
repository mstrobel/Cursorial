using System.Diagnostics;
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
    }

    private UIApplication Application { get; }
    
    public ICommand ShowTaskDialogCommand { get; }

    public ICommand ShowMessageDialogCommand { get; }

    public string? Status
    {
        get;
        set => Set(ref field, value);
    }
    
    public bool TaskDialogIncludeContent { get; set => Set(ref field, value); } = false;
    public bool TaskDialogIncludeCommandLinks { get; set => Set(ref field, value); } = false;
    public bool TaskDialogIncludeButtons { get; set => Set(ref field, value); } = false;
    public bool TaskDialogIncludeExpandedContent { get; set => Set(ref field, value); } = false;
    public bool TaskDialogIncludeVerification { get; set => Set(ref field, value); } = false;
    public bool TaskDialogIncludeProgressBar { get; set => Set(ref field, value); } = false;

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

            List<TaskDialogButton> buttons = [];

            if (TaskDialogIncludeCommandLinks)
            {
                buttons.AddRange(
                    [
                        new TaskDialogButton("Drink", "_Drink It")
                        {
                            Explanation = "Drink the purple vial. You know you want to. It's [i]purple[/i].",
                            ExplanationContainsMarkup = true,
                            IsDefault = true
                        },
                        new TaskDialogButton("Smash", "_Smash It")
                        {
                            Explanation = "Smash the yellow vial. It had it coming."
                        },
                        new TaskDialogButton("Toss", "_Toss It")
                        {
                            Explanation = "Hurl the red vial at the annoying guy. He should know better."
                        }
                    ]);
            }

            if (TaskDialogIncludeButtons)
            {
                buttons.AddRange(
                    [
                        new TaskDialogButton("Wat", "_Wat"),
                        new TaskDialogButton("Nah", "Yeah, _Nah") { IsCancel = true }
                    ]);
            }

            var r = new TaskDialogRequest("Make a Choice")
                    {
                        Title = "Very Serious",
                        Content = TaskDialogIncludeContent
                                      ? "You come across a collection of vials. " +
                                        "There are no descriptions to be found. " +
                                        "What do you do?"
                                      : null,
                        Buttons = buttons,
                        VerificationText = TaskDialogIncludeVerification ? "I am a meat _popsicle" : null,
                        ExpandedInformation = TaskDialogIncludeExpandedContent ? "Curiosity is a virtue. 🩷" : null,
                        ExpandedInformationContainsMarkup = true,
                        Severity = TaskDialogSeverity.Question
                    };

            int progress = 0;
            
            IAsyncDisposable? timer = null;

            if (TaskDialogIncludeProgressBar)
            {
                r.Progress.Report(null);

                var elapsed = Stopwatch.StartNew();

                timer = Application.TimeProviderInternal.CreateTimer(_ =>
                                                                     {
                                                                         if (elapsed.Elapsed < TimeSpan.FromSeconds(3))
                                                                             return;
                                                                         if (progress < 100)
                                                                             r.Progress.Report(++progress);
                                                                     },
                                                                     null,
                                                                     TimeSpan.FromMilliseconds(30d),
                                                                     TimeSpan.FromMilliseconds(30d));
            }

            var result = await TaskDialog.ShowAsync(Application, r);

            if (timer is not null)
                await timer.DisposeAsync();
            
            Status = $"You chose: {result.Button?.Id ?? "Wimp Out"}";
            
            if (r.VerificationChecked)
                Status += ", and you verified it.";
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