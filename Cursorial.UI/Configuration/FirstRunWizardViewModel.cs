using Cursorial.UI.Data;

namespace Cursorial.UI.Configuration;

/// <summary>What a wizard page shows: static content (welcome / file locations) or option rows.</summary>
public enum WizardPageKind : byte
{
    /// <summary>The welcome page: what Cursorial is, the framework-wide key bindings.</summary>
    Welcome,

    /// <summary>Option rows (a category subset re-used from the shared options view-model).</summary>
    Options,

    /// <summary>The configuration-file locations page (power users hand-edit these).</summary>
    Files
}

/// <summary>One wizard page: a title, a kind, and (for option pages) the rows it hosts.</summary>
public sealed class WizardPageViewModel
{
    internal WizardPageViewModel(WizardPageKind kind, string title, IReadOnlyList<OptionViewModel>? options = null)
    {
        Kind = kind;
        Title = title;
        Options = options ?? [];
    }

    /// <summary>The page kind (selects the view template).</summary>
    public WizardPageKind Kind { get; }

    /// <summary>The page title.</summary>
    public string Title { get; }

    /// <summary>The option rows an <see cref="WizardPageKind.Options"/> page hosts.</summary>
    public IReadOnlyList<OptionViewModel> Options { get; }
}

/// <summary>
/// The first-run wizard's model — a thin pager over the SAME <see cref="UserOptionsViewModel"/>
/// the settings dialog binds (the code-reuse contract of FB-17 Stage B). The wizard edits the
/// GLOBAL layer (first run configures every Cursorial app; the per-app overlay is the dialog's
/// business), walks Welcome → essentials → files, and on Finish persists; Skip keeps nothing.
/// Either exit marks the first run completed (the caller writes the marker).
/// </summary>
public sealed class FirstRunWizardViewModel : ObservableObject, IDisposable
{
    private int _pageIndex;

    /// <summary>Builds the wizard over a fresh options view-model pinned to the global scope.</summary>
    public FirstRunWizardViewModel(UIApplication application)
    {
        Options = new UserOptionsViewModel(application) { EditScope = UserOptionScope.Global };

        var bindings = new List<(string Gesture, string Description)>();

        if (application.UserConfigurationInternal?.OptionsDialogGesture is { } optionsGesture)
            bindings.Add((optionsGesture.ToString(), "Open the options dialog (any time, any Cursorial app)"));

        bindings.Add(("Ctrl+L", "Repaint the screen (recovers from external corruption)"));
        bindings.Add(("Alt", "Hold to reveal access-key underlines; tap to enter menu mode"));
        bindings.Add(("F10", "Menu mode (same as tapping Alt)"));
        KeyBindingSummary = bindings;

        var general = FindCategory(UserOptionCategory.General);
        var appearance = FindCategory(UserOptionCategory.Appearance);

        Pages =
        [
            new WizardPageViewModel(WizardPageKind.Welcome, "Welcome"),
            new WizardPageViewModel(WizardPageKind.Options, "Terminal", general),
            new WizardPageViewModel(WizardPageKind.Options, "Appearance", appearance),
            new WizardPageViewModel(WizardPageKind.Files, "Configuration files")
        ];
    }

    /// <summary>The shared options model (scope pinned to <see cref="UserOptionScope.Global"/>).</summary>
    public UserOptionsViewModel Options { get; }

    /// <summary>The pages, in order.</summary>
    public IReadOnlyList<WizardPageViewModel> Pages { get; }

    /// <summary>The current page index.</summary>
    public int PageIndex
    {
        get => _pageIndex;
        private set
        {
            if (!SetProperty(ref _pageIndex, value))
                return;

            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(IsLastPage));
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    /// <summary>The current page.</summary>
    public WizardPageViewModel CurrentPage => Pages[_pageIndex];

    /// <summary>Whether Back is available.</summary>
    public bool CanGoBack => _pageIndex > 0;

    /// <summary>Whether the current page is the last (Next becomes Finish).</summary>
    public bool IsLastPage => _pageIndex == Pages.Count - 1;

    /// <summary>"Step N of M" for the header.</summary>
    public string ProgressText => $"Step {_pageIndex + 1} of {Pages.Count}";

    /// <summary>The key bindings the welcome page lists (gesture + what it does), reflecting the
    /// app's CONFIGURED options chord (omitted entirely when the app disabled it).</summary>
    public IReadOnlyList<(string Gesture, string Description)> KeyBindingSummary { get; }

    /// <summary>Advances a page (call <see cref="Finish"/> from the last page instead).</summary>
    public void Next()
    {
        if (!IsLastPage)
            PageIndex += 1;
    }

    /// <summary>Goes back a page.</summary>
    public void Back()
    {
        if (CanGoBack)
            PageIndex -= 1;
    }

    /// <summary>
    /// Persists the wizard's edits (global layer) — the caller closes the window and writes the
    /// first-run marker.
    /// </summary>
    public void Finish() => Options.Save();

    /// <summary>
    /// Discards the wizard's edits (restores the open-time state live) — the caller closes the
    /// window and still writes the first-run marker (skipping IS completing the first run).
    /// </summary>
    public void Skip() => Options.Cancel();

    private IReadOnlyList<OptionViewModel> FindCategory(UserOptionCategory category)
    {
        foreach (var group in Options.Categories)
        {
            if (group.Category == category)
                return group.Options;
        }

        return [];
    }

    /// <inheritdoc/>
    public void Dispose() => Options.Dispose();
}
