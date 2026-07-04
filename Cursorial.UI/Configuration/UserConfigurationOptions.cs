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
}