namespace Cursorial.UI.Configuration;

/// <summary>
/// Supplies the root directory user configuration persists under. The default is
/// <c>~/.cursorial</c> (<see cref="DefaultUserConfigurationPathProvider"/>); tests and embedders
/// substitute their own root through
/// <see cref="UserConfigurationOptions.PathProvider"/> or <see cref="UserOptionsStore.Load"/>.
/// </summary>
public interface IUserConfigurationPathProvider
{
    /// <summary>The directory the option files live in (created on first save; may not exist yet).</summary>
    string ConfigurationRoot { get; }
}

/// <summary>The production path provider: <c>~/.cursorial</c> under the user profile.</summary>
public sealed class DefaultUserConfigurationPathProvider : IUserConfigurationPathProvider
{
    /// <summary>The shared instance (the provider is stateless).</summary>
    public static DefaultUserConfigurationPathProvider Instance { get; } = new();

    /// <inheritdoc/>
    public string ConfigurationRoot
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
                                                  Environment.SpecialFolderOption.DoNotVerify),
                        ".cursorial");
}
