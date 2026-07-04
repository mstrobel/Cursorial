using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace Cursorial.UI.Configuration;

/// <summary>
/// The Cursorial user-options store (FB-17 Stage A): a flat string-keyed option map persisted under
/// the configuration root (default <c>~/.cursorial</c>) as two JSON files — one <b>global</b>
/// (<c>options.json</c>, shared by every Cursorial app) and one <b>per-application</b>
/// (<c>apps/&lt;appId&gt;.json</c>). Reads overlay with <b>tri-state semantics</b>: a per-app file
/// overrides only the keys it explicitly sets; clearing a per-app key
/// (<see cref="SetApplication(string, string?)" /> with <see langword="null" />) re-exposes the
/// global value rather than freezing a stale copy of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Durability.</b> <see cref="Save" /> writes each file atomically (temp file + rename in the
/// same directory). <see cref="Load" /> never throws for a missing, unreadable, or corrupt file —
/// it falls back to an empty map and records the problem in <see cref="LoadDiagnostics" />, so a
/// damaged options file can never take an app down at startup.
/// </para>
/// <para>
/// <b>Format.</b> A single JSON object of string values (<c>{"theme.base": "dark"}</c>) — trailing
/// commas and <c>//</c> comments are tolerated on read for hand editing; booleans and numbers are
/// coerced to their literal text. Unknown keys round-trip untouched, so old binaries never destroy
/// options written by newer ones and apps may keep their own keys (see <see cref="UserOptionKeys" />
/// for the framework's). Keys are ordinal case-sensitive.
/// </para>
/// <para>
/// <b>Threading.</b> Not thread-safe; intended for UI-thread use (the framework loads it during
/// startup and Stage B's Options dialog reads/writes it on the UI thread).
/// </para>
/// </remarks>
public sealed class UserOptionsStore
{
    private const string GlobalFileName = "options.json";
    private const string ApplicationsDirectoryName = "apps";
    private readonly Dictionary<string, string> _application = new(StringComparer.Ordinal);
    private readonly List<string> _diagnostics = [];

    private readonly Dictionary<string, string> _global = new(StringComparer.Ordinal);

    private UserOptionsStore(string applicationId, string configurationRoot)
    {
        ApplicationId = applicationId;
        ConfigurationRoot = configurationRoot;
        GlobalValues = new ReadOnlyDictionary<string, string>(_global);
        ApplicationValues = new ReadOnlyDictionary<string, string>(_application);
    }

    /// <summary>The per-app overlay identity this store was loaded for.</summary>
    public string ApplicationId { get; }

    /// <summary>The configuration root directory (default <c>~/.cursorial</c>).</summary>
    public string ConfigurationRoot { get; }

    /// <summary>The global options file (<c>options.json</c> under <see cref="ConfigurationRoot" />).</summary>
    public string GlobalFilePath => Path.Combine(ConfigurationRoot, GlobalFileName);

    /// <summary>The per-application overlay file (<c>apps/&lt;appId&gt;.json</c>, file-name-sanitized).</summary>
    public string ApplicationFilePath
        => Path.Combine(ConfigurationRoot, ApplicationsDirectoryName, SanitizeFileName(ApplicationId) + ".json");

    /// <summary>
    /// Problems encountered while loading or applying the store (corrupt file, unreadable value, …).
    /// Empty on a clean load; the store itself is always usable — defaults stand in for whatever
    /// could not be read.
    /// </summary>
    public IReadOnlyList<string> LoadDiagnostics => _diagnostics;

    /// <summary>The global map (read-only live view — keys the global file explicitly sets).</summary>
    public IReadOnlyDictionary<string, string> GlobalValues { get; }

    /// <summary>The per-app overlay map (read-only live view — only keys the app file explicitly sets).</summary>
    public IReadOnlyDictionary<string, string> ApplicationValues { get; }

    /// <summary>
    /// Loads the store for <paramref name="applicationId" />: the global file plus the app's overlay
    /// file, each tolerantly (missing → empty; corrupt → empty + a <see cref="LoadDiagnostics" />
    /// entry). Never throws for file-system or parse problems.
    /// </summary>
    /// <param name="applicationId">The per-app overlay identity (see <see cref="UserConfigurationOptions.ApplicationId" />).</param>
    /// <param name="pathProvider">The configuration root; <see langword="null" /> = <c>~/.cursorial</c>.</param>
    public static UserOptionsStore Load(string applicationId, IUserConfigurationPathProvider? pathProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);

        string root;

        try
        {
            root = (pathProvider ?? DefaultUserConfigurationPathProvider.Instance).ConfigurationRoot;
        }
        catch (Exception ex)
        {
            var fallback = new UserOptionsStore(applicationId, string.Empty);

            fallback.AddDiagnostic(
                $"The configuration path provider failed ({ex.GetType().Name}: {ex.Message}); using defaults.");

            return fallback;
        }

        var store = new UserOptionsStore(applicationId, root);
        store.LoadFile(store.GlobalFilePath, store._global);
        store.LoadFile(store.ApplicationFilePath, store._application);
        return store;
    }

    // ───────────────────────────── effective reads (per-app overlays global) ─────────────────────────────

    /// <summary>
    /// The effective value: the per-app overlay when it explicitly sets the key, else the global value, else
    /// <see langword="null" />.
    /// </summary>
    public string? GetString(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _application.TryGetValue(key, out var overlaid) ? overlaid : _global.GetValueOrDefault(key);
    }

    /// <summary>
    /// The effective value parsed as a boolean (<c>"true"</c>/<c>"false"</c>, case-insensitive);
    /// <see langword="null" /> when unset or unparseable.
    /// </summary>
    public bool? GetBoolean(string key)
    {
        return GetString(key) is {} text && bool.TryParse(text, out var value) ? value : null;
    }

    /// <summary>The effective value parsed as an invariant integer; <see langword="null" /> when unset or unparseable.</summary>
    public int? GetInt32(string key)
    {
        return GetString(key) is {} text &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                   ? value
                   : null;
    }

    // ───────────────────────────── writes (tri-state: null clears) ─────────────────────────────

    /// <summary>Sets (or, with <see langword="null" />, clears) a key in the <b>global</b> map.</summary>
    public void SetGlobal(string key, string? value)
    {
        Set(_global, key, value);
    }

    /// <summary>
    /// Sets a key in the <b>per-app</b> overlay, or — with <see langword="null" /> — clears it so
    /// the key inherits the global value again (the tri-state contract: set / unset / inherit).
    /// </summary>
    public void SetApplication(string key, string? value)
    {
        Set(_application, key, value);
    }

    /// <summary>Sets a global boolean (<c>"true"</c>/<c>"false"</c>).</summary>
    public void SetGlobal(string key, bool value)
    {
        SetGlobal(key, value ? "true" : "false");
    }

    /// <summary>Sets a per-app boolean (<c>"true"</c>/<c>"false"</c>).</summary>
    public void SetApplication(string key, bool value)
    {
        SetApplication(key, value ? "true" : "false");
    }

    private static void Set(Dictionary<string, string> map, string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (value is null)
            map.Remove(key);
        else
            map[key] = value;
    }

    // ───────────────────────────── persistence ─────────────────────────────

    /// <summary>
    /// Persists both files atomically (write-temp-then-rename in the target directory, so a crash
    /// mid-save can never leave a torn file). The per-app file contains only the keys the overlay
    /// explicitly sets — never a snapshot of global defaults (the tri-state contract). Throws on
    /// I/O failure: unlike loading, a failed <b>save</b> must be visible to the caller.
    /// </summary>
    public void Save()
    {
        WriteFileAtomic(GlobalFilePath, _global);
        WriteFileAtomic(ApplicationFilePath, _application);
    }

    /// <summary>Appends a load/apply diagnostic (also used by the startup option applier).</summary>
    internal void AddDiagnostic(string message)
    {
        _diagnostics.Add(message);
    }

    private void LoadFile(string path, Dictionary<string, string> target)
    {
        byte[] bytes;

        try
        {
            if (!File.Exists(path))
                return;

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            AddDiagnostic($"Could not read '{path}' ({ex.GetType().Name}: {ex.Message}); using defaults.");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                AddDiagnostic($"'{path}' is not a JSON object; using defaults.");
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        target[property.Name] = property.Value.GetString()!;
                        break;

                    // Hand-edited files plausibly use bare literals — coerce to the literal text.
                    case JsonValueKind.True:
                        target[property.Name] = "true";
                        break;

                    case JsonValueKind.False:
                        target[property.Name] = "false";
                        break;

                    case JsonValueKind.Number:
                        target[property.Name] = property.Value.GetRawText();
                        break;

                    default:
                        AddDiagnostic($"'{path}': key '{property.Name}' has an unsupported value kind " +
                                      $"({property.Value.ValueKind}); ignored.");

                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            target.Clear();
            AddDiagnostic($"'{path}' is not valid JSON ({ex.Message}); using defaults.");
        }
    }

    private static void WriteFileAtomic(string path, Dictionary<string, string> values)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = $"{path}.{Environment.ProcessId}.tmp";

        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                foreach (var (key, value) in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                    writer.WriteString(key, value);

                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temp, path, true); // same-directory rename — atomic on POSIX, best-effort-atomic on Windows
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
    }

    private static string SanitizeFileName(string applicationId)
    {
        var invalid = Path.GetInvalidFileNameChars();

        var sanitized = applicationId.Length <= 256
                            ? stackalloc char[applicationId.Length]
                            : new char[applicationId.Length];

        for (var i = 0; i < applicationId.Length; i++)
        {
            var c = applicationId[i];

            var unsafeChar = Array.IndexOf(invalid, c) >= 0 ||
                             c is '/' or '\\' ||   // separators are invalid only on some platforms — strip everywhere
                             (c is '.' && i is 0); // no dot-files / "." / ".." shapes

            sanitized[i] = unsafeChar ? '_' : c;
        }

        var name = new string(sanitized);
        return name.Length is 0 ? "_" : name;
    }
}