using Cursorial.UI.Data;

namespace Cursorial.UI.Configuration;

/// <summary>One category group (a dialog tab / wizard section) of option rows.</summary>
public sealed class OptionCategoryViewModel
{
    internal OptionCategoryViewModel(UserOptionCategory category, string displayName, IReadOnlyList<OptionViewModel> options)
    {
        Category = category;
        DisplayName = displayName;
        Options = options;
    }

    /// <summary>The catalog category.</summary>
    public UserOptionCategory Category { get; }

    /// <summary>The tab header / section title.</summary>
    public string DisplayName { get; }

    /// <summary>The rows, in catalog order.</summary>
    public IReadOnlyList<OptionViewModel> Options { get; }

    /// <summary>Whether this is the warning-gated Advanced category.</summary>
    public bool IsAdvanced => Category == UserOptionCategory.Advanced;
}

/// <summary>
/// The shared options view-model (FB-17 Stage B) — the one model BOTH the settings dialog and
/// the first-run wizard bind. Projects the <see cref="UserOptionCatalog"/> over a
/// <see cref="UserOptionsSession"/>: categories of tri-state rows, the dialog-level
/// <see cref="EditScope"/> switch, live preview through the session, Reset (restore open-time
/// state, stay open), Save, and the timed capability test for the staged Advanced values.
/// </summary>
public sealed class UserOptionsViewModel : ObservableObject, IDisposable
{
    /// <summary>How long <see cref="BeginTest"/> holds the staged capability values before auto-reverting.</summary>
    public static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(5);

    private readonly List<OptionViewModel> _allOptions = [];
    private UserOptionScope _editScope = UserOptionScope.Global;
    private IDisposable? _testScope;
    private UITimer? _testTimer;
    private int _testSecondsRemaining;

    /// <summary>Builds the view-model over a fresh <see cref="UserOptionsSession"/> for <paramref name="application"/>.</summary>
    public UserOptionsViewModel(UIApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        Session = new UserOptionsSession(application);

        var categories = new List<OptionCategoryViewModel>();

        foreach (var category in new[]
                 {
                     (UserOptionCategory.General, "General"),
                     (UserOptionCategory.Appearance, "Appearance"),
                     (UserOptionCategory.Input, "Input"),
                     (UserOptionCategory.Advanced, "Advanced")
                 })
        {
            var options = new List<OptionViewModel>();

            foreach (var descriptor in UserOptionCatalog.All)
            {
                if (descriptor.Category != category.Item1)
                    continue;

                OptionViewModel option = descriptor.Kind == UserOptionKind.Boolean
                                             ? new BooleanOptionViewModel(this, descriptor)
                                             : new ChoiceOptionViewModel(this, descriptor);

                options.Add(option);
                _allOptions.Add(option);
            }

            categories.Add(new OptionCategoryViewModel(category.Item1, category.Item2, options));
        }

        Categories = categories;
    }

    /// <summary>The editing session (store access, live preview, snapshot lifecycle).</summary>
    public UserOptionsSession Session { get; }

    /// <summary>The category groups, in tab order.</summary>
    public IReadOnlyList<OptionCategoryViewModel> Categories { get; }

    /// <summary>The layer edits target — the dialog-level "Editing: Global | This application" switch.</summary>
    public UserOptionScope EditScope
    {
        get => _editScope;
        set
        {
            if (!SetProperty(ref _editScope, value))
                return;

            OnPropertyChanged(nameof(IsEditingApplicationScope));
            RefreshAllOptions();
        }
    }

    /// <summary>Convenience for two-state toggles binding the scope switch.</summary>
    public bool IsEditingApplicationScope
    {
        get => _editScope == UserOptionScope.Application;
        set => EditScope = value ? UserOptionScope.Application : UserOptionScope.Global;
    }

    /// <summary>The per-app overlay identity (the scope switch's second label).</summary>
    public string ApplicationId => Session.Store.ApplicationId;

    /// <summary>The global options file (shown in the footer / wizard files page).</summary>
    public string GlobalFilePath => Session.Store.GlobalFilePath;

    /// <summary>The per-app overlay file (shown in the footer / wizard files page).</summary>
    public string ApplicationFilePath => Session.Store.ApplicationFilePath;

    /// <summary>The Nerd Font coverage sample (see <see cref="UserOptionCatalog.NerdFontProbeGlyphs"/>).</summary>
    public string NerdFontProbeGlyphs => UserOptionCatalog.NerdFontProbeGlyphs;

    // ───────────────────────────── timed capability test ─────────────────────────────

    /// <summary>Whether the timed test currently holds the staged capability values on screen.</summary>
    public bool IsTestRunning => _testScope is not null;

    /// <summary>Seconds until the running test auto-reverts (0 when no test runs).</summary>
    public int TestSecondsRemaining
    {
        get => _testSecondsRemaining;
        private set => SetProperty(ref _testSecondsRemaining, value);
    }

    /// <summary>Whether a test can start: staged Advanced changes exist and no test is running.</summary>
    public bool CanBeginTest => !IsTestRunning && Session.HasStagedDangerousChanges;

    /// <summary>
    /// Applies the staged capability values for <see cref="TestDuration"/>, counting down once a
    /// second, then auto-reverts — the "will my screen survive this?" probe. No-op when nothing
    /// is staged or a test already runs.
    /// </summary>
    public void BeginTest()
    {
        if (!CanBeginTest)
            return;

        _testScope = Session.BeginTest();
        TestSecondsRemaining = (int)TestDuration.TotalSeconds;
        _testTimer = UITimer.Start(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), OnTestTick);
        OnPropertyChanged(nameof(IsTestRunning));
        OnPropertyChanged(nameof(CanBeginTest));
    }

    /// <summary>Ends a running test immediately (reverts to the committed values).</summary>
    public void EndTest()
    {
        if (_testScope is null)
            return;

        _testTimer?.Dispose();
        _testTimer = null;
        _testScope.Dispose();
        _testScope = null;
        TestSecondsRemaining = 0;
        OnPropertyChanged(nameof(IsTestRunning));
        OnPropertyChanged(nameof(CanBeginTest));
    }

    private void OnTestTick()
    {
        if (_testScope is null)
            return;

        if (TestSecondsRemaining > 1)
        {
            TestSecondsRemaining -= 1;
            return;
        }

        EndTest();
    }

    // ───────────────────────────── lifecycle ─────────────────────────────

    /// <summary>
    /// The Reset button: ends any running test, restores the open-time snapshot (both layers),
    /// re-applies live, and refreshes every row — without closing the dialog.
    /// </summary>
    public void Reset()
    {
        EndTest();
        Session.Reset();
        RefreshAllOptions();
    }

    /// <summary>
    /// The OK path: ends any running test, applies the staged capability values, persists both
    /// files. Throws on I/O failure (the view surfaces it).
    /// </summary>
    public void Save()
    {
        EndTest();
        Session.Save();
    }

    /// <summary>The Cancel path: Reset without the refresh cost mattering — the view closes after.</summary>
    public void Cancel() => Reset();

    /// <summary>Row-write fan-out: staged-state and cross-row projections (inherit labels) may all shift.</summary>
    internal void NotifyOptionChanged(OptionViewModel changed)
    {
        // A single write can move other rows' projections (e.g. clearing a global key changes an
        // app-scope row's inherit label) and the staged-danger state. Row count is tiny — refresh all.
        RefreshAllOptions();
    }

    private void RefreshAllOptions()
    {
        foreach (var option in _allOptions)
            option.Refresh();

        OnPropertyChanged(nameof(CanBeginTest));
    }

    /// <inheritdoc/>
    public void Dispose() => EndTest();
}
