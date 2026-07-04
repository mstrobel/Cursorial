using Cursorial.UI.Configuration;

namespace Cursorial.Tests.UI.Configuration;

/// <summary>
/// A throwaway configuration root under the system temp directory — the
/// <see cref="IUserConfigurationPathProvider"/> seam every store/builder test points the framework
/// at, so no test ever touches a real <c>~/.cursorial</c>. Deleted (best-effort) on dispose.
/// </summary>
public sealed class TempConfigRoot : IUserConfigurationPathProvider, IDisposable
{
    /// <inheritdoc/>
    public string ConfigurationRoot { get; } =
        Path.Combine(Path.GetTempPath(), "cursorial-ui-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The global options file path (mirrors <see cref="UserOptionsStore.GlobalFilePath"/>).</summary>
    public string GlobalFilePath => Path.Combine(ConfigurationRoot, "options.json");

    /// <summary>Writes raw text as the global options file (corruption/hand-edit fixtures).</summary>
    public void WriteGlobalFile(string contents)
    {
        Directory.CreateDirectory(ConfigurationRoot);
        File.WriteAllText(GlobalFilePath, contents);
    }

    /// <summary>Writes raw text as an app overlay file (corruption/hand-edit fixtures).</summary>
    public void WriteApplicationFile(string applicationId, string contents)
    {
        var directory = Path.Combine(ConfigurationRoot, "apps");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, applicationId + ".json"), contents);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(ConfigurationRoot))
                Directory.Delete(ConfigurationRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup — a leaked temp directory must not fail the test
        }
    }
}
