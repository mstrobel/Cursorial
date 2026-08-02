using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;

using Cursorial.Gallery.Infrastructure;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Dialogs;
using Cursorial.UI.Dialogs.Themes;

using MessageBox = Cursorial.UI.Dialogs.MessageBox;
using MessageBoxButton = Cursorial.UI.Dialogs.MessageBoxButton;

namespace Cursorial.Gallery.ViewModels;

public class DialogsViewModel : PageViewModel
{
    public override string Title => "Dialogs";
    public override string Summary => "Examples of message and task dialogs and their usage.";

    private bool _isDialogShowing;

    public DialogsViewModel(UIApplication? application = null)
    {
        RuntimeHelpers.RunModuleConstructor(typeof(CursorialDialogThemes).Module.ModuleHandle);

        Application = application ?? UIApplication.Current;
        ShowTaskDialogCommand = new RelayCommand(ShowTaskDialog, () => !_isDialogShowing);
        ShowMessageDialogCommand = new RelayCommand(ShowMessageDialog, () => !_isDialogShowing);
        ShowFileOpenDialogCommand = new RelayCommand(ShowFileOpenDialog, () => !_isDialogShowing);
        ShowFileSaveDialogCommand = new RelayCommand(ShowFileSaveDialog, () => !_isDialogShowing);

        ToggleMessageBoxButtonCommand = new RelayCommand<ICheckableCommandParameter?>(
            ExecuteToggleMessageBoxButtonCommand,
            CanExecuteToggleMessageBoxButtonCommand);
    }

    private bool CanExecuteToggleMessageBoxButtonCommand(ICheckableCommandParameter? p)
    {
        p?.Handled = true;
        
        if (p?.Tag is not MessageBoxButton button)
        {
            p?.IsCheckedOverride = false;
            return false;
        }

        p.IsCheckedOverride = MessageBoxButtons == button;
        return true;
    }

    private void ExecuteToggleMessageBoxButtonCommand(ICheckableCommandParameter? p)
    {
        if (p?.Tag is not MessageBoxButton button)
            return;

        MessageBoxButtons = button;

        p.IsCheckedOverride = true;
        p.Handled = true;
        
        ToggleMessageBoxButtonCommand.RaiseCanExecuteChanged();
    }

    private UIApplication? Application { get; }
    
    public ICommand ShowTaskDialogCommand { get; }

    public ICommand ShowMessageDialogCommand { get; }

    public ICommand ShowFileOpenDialogCommand { get; }

    public ICommand ShowFileSaveDialogCommand { get; }

    public RelayCommand<ICheckableCommandParameter?> ToggleMessageBoxButtonCommand { get; }

    public override string? Status
    {
        get;
        protected set => Set(ref field, value);
    }

    public MessageBoxButton MessageBoxButtons { get; set => Set(ref field, value); } = MessageBoxButton.YesNo;
    
    public TaskDialogSeverity TaskDialogSeverity { get; set => Set(ref field, value); } = TaskDialogSeverity.None;
    public bool TaskDialogIncludeContent { get; set => Set(ref field, value); } = true;
    public bool TaskDialogIncludeCommandLinks { get; set => Set(ref field, value); } = true;
    public bool TaskDialogIncludeButtons { get; set => Set(ref field, value); } = true;
    public bool TaskDialogIncludeExpandedContent { get; set => Set(ref field, value); } = true;
    public bool TaskDialogIncludeVerification { get; set => Set(ref field, value); } = true;
    public bool TaskDialogIncludeProgressBar { get; set => Set(ref field, value); } = true;

    public bool FileDialogFileMustExist {  get; set => Set(ref field, value); } = true;
    public bool FileDialogUseRealFileSystem {  get; set => Set(ref field, value); } = true;
    public bool FileDialogShowHidden {  get; set => Set(ref field, value); } = false;
    public bool FileDialogConfirmOverwrite {  get; set => Set(ref field, value); } = true;
    public bool FileDialogCanCreateDirectories {  get; set => Set(ref field, value); } = true;
    
    private async void ShowMessageDialog()
    {
        try
        {
            if (Application is not {} app)
                return;

            if (_isDialogShowing) return;

            _isDialogShowing = true;

            var buttons = MessageBoxButtons;

            var defaultButton = buttons switch
                                {
                                    _ when buttons.HasFlag(MessageBoxButton.Save) => MessageBoxButton.Save,
                                    _ when buttons.HasFlag(MessageBoxButton.Yes)  => MessageBoxButton.Yes,
                                    _ when buttons.HasFlag(MessageBoxButton.Ok)   => MessageBoxButton.Ok,
                                    _ when buttons is MessageBoxButton.Cancel     => MessageBoxButton.Cancel,
                                    _                                             => default(MessageBoxButton?)
                                };

            var cancelButton = buttons switch
                               {
                                   _ when buttons.HasFlag(MessageBoxButton.Cancel)   => MessageBoxButton.Cancel,
                                   _ when buttons.HasFlag(MessageBoxButton.No)       => MessageBoxButton.No,
                                   _ when buttons.HasFlag(MessageBoxButton.DontSave) => MessageBoxButton.DontSave,
                                   _                                                 => default(MessageBoxButton?)
                               };

            var result = await MessageBox.ShowAsync(
                             app,
                             message: "Would you like to drink the purple vial?",
                             title: "Make a Choice",
                             buttons,
                             focusedButton: defaultButton,
                             defaultButton,
                             cancelButton,
                             app.Dispatcher.ShutdownToken);

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
            if (Application is not {} app)
                return;

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
                        Severity = TaskDialogSeverity
                    };

            int progress = 0;
            
            IAsyncDisposable? timer = null;

            if (TaskDialogIncludeProgressBar)
            {
                r.Progress.Report(null);

                var elapsed = Stopwatch.StartNew();

                timer = app.TimeProviderInternal.CreateTimer(_ =>
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

            var result = await TaskDialog.ShowAsync(app, r, app.Dispatcher.ShutdownToken);

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
    
    private async void ShowFileOpenDialog()
    {
        try
        {
            if (Application is not {} app)
                return;

            if (_isDialogShowing) return;

            _isDialogShowing = true;

            IFileSystemProvider fileSystem = FileDialogUseRealFileSystem 
                                                 ? PhysicalFileSystemProvider.Instance
                                                 : InMemoryFileSystemProvider.CreateSample();

            var filters = MakeFileFilters();

            var result = await FileOpenDialog.ShowAsync(
                             app,
                             new FileOpenDialogRequest("Open File")
                             {
                                 FileSystem = fileSystem,
                                 ShowHiddenEntries = FileDialogShowHidden,
                                 Filters = filters,
                                 SelectedFilterIndex = filters.Count - 1,
                                 MustExist = FileDialogFileMustExist,
                                 View = ListViewViewMode.SmallIcons
                             });

            Status = result.IsDismissed 
                         ? "You dismissed without opening a file."
                         : $"You opened: {result.FilePath}";
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

    private async void ShowFileSaveDialog()
    {
        try
        {
            if (Application is not {} app)
                return;

            if (_isDialogShowing) return;

            _isDialogShowing = true;

            IFileSystemProvider fileSystem = FileDialogUseRealFileSystem 
                                                 ? PhysicalFileSystemProvider.Instance
                                                 : InMemoryFileSystemProvider.CreateSample();

            var filters = MakeFileFilters();

            var result = await FileSaveDialog.ShowAsync(
                             app,
                             new FileSaveDialogRequest("Save File")
                             {
                                 FileSystem = fileSystem,
                                 ShowHiddenEntries = FileDialogShowHidden,
                                 CanCreateDirectories = FileDialogCanCreateDirectories,
                                 ConfirmOverwrite = FileDialogConfirmOverwrite,
                                 Filters = filters,
                                 SelectedFilterIndex = filters.Select((f, i) => f.Matches("new_file.txt") ? i : -i).FirstOrDefault(i => i > 0),
                                 InitialFileName = "new_file",
                                 View = ListViewViewMode.SmallIcons
                             });

            Status = result.IsDismissed 
                         ? "You dismissed without saving."
                         : $"You saved to: {result.FilePath}";
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

    private static IReadOnlyList<FileDialogFilter> MakeFileFilters()
    {
        return [
                   new("C# source", "*.cs"),
                   new("PDF document", "*.pdf"),
                   new("PNG image", "*.png"),
                   new("Xaml source", "*.xaml"),
                   new("Plain text", "*.txt"),
                   new("All files", "*.*")
               ];
    }
}