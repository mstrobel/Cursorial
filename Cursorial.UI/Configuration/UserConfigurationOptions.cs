namespace Cursorial.UI.Configuration;

/// <summary>
/// Options for <see cref="UIApplicationBuilder.WithUserConfiguration" />. User configuration is
/// opt-in: an application that never calls the builder method loads nothing and exposes a
/// <see langword="null" /> <see cref="UIApplication.UserOptions" />.
/// </summary>
public sealed class UserConfigurationOptions
{
    /// <summary>
    /// The per-app overlay identity. Default (<see langword="null" />): the entry-assembly name.
    /// Set it explicitly so an assembly rename does not orphan the app's saved options — or to
    /// deliberately share one profile between related tools.
    /// </summary>
    public string? ApplicationId { get; set; }

    /// <summary>
    /// The configuration-root seam (default <c>~/.cursorial</c> via
    /// <see cref="DefaultUserConfigurationPathProvider" />). Tests point it at a scratch directory.
    /// </summary>
    public IUserConfigurationPathProvider? PathProvider { get; set; }

    /// <summary>
    /// The chord that opens the options dialog on any active root (default
    /// <c>Ctrl+Shift+O</c>). Set <see langword="null"/> to install no chord — the app can still
    /// open the dialog via <see cref="UIApplication.ShowUserOptionsDialogAsync"/>.
    /// </summary>
    public Input.KeyGesture? OptionsDialogGesture { get; set; } = Input.KeyGesture.Parse("Ctrl+Alt+O");

    /// <summary>
    /// Whether the first-run wizard shows when no Cursorial app has ever completed it on this
    /// system (the <c>meta.firstRunCompleted</c> marker in the GLOBAL store). Opt-in per the
    /// design notes ("on first run, <i>if configured</i>") — default <see langword="false"/>:
    /// enabling an onboarding modal is an app decision, never a surprise.
    /// </summary>
    public bool ShowFirstRunWizard { get; set; }

    /// <summary>
    /// Shows the wizard on THIS run regardless of the marker — an app-owned re-onboarding lever
    /// (the marker is still written afterwards). Default <see langword="false"/>.
    /// </summary>
    public bool ForceFirstRunWizard { get; set; }
}