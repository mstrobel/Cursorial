using Cursorial.UI.Configuration;

namespace Cursorial.Tests.UI.Configuration;

// Spec for the FB-17 Stage A options store: two JSON files under the configuration root (global +
// per-app overlay), TRI-STATE overlay semantics (a per-app file overrides only keys it explicitly
// sets; clearing re-exposes global), atomic writes, and corrupt-file tolerance (loading never
// throws — defaults + a diagnostic).
public sealed class UserOptionsStoreTests
{
    private const string AppId = "test-app";

    // ───────────────────────────── round-trip ─────────────────────────────

    [Fact]
    public void RoundTrip_GlobalAndApplicationValues_SurviveSaveAndReload()
    {
        using var root = new TempConfigRoot();

        var store = UserOptionsStore.Load(AppId, root);
        store.SetGlobal(UserOptionKeys.ThemeBase, "dark");
        store.SetGlobal(UserOptionKeys.NerdFont, true);
        store.SetApplication(UserOptionKeys.ColorTier, "ansi256");
        store.Save();

        var reloaded = UserOptionsStore.Load(AppId, root);
        Assert.Empty(reloaded.LoadDiagnostics);
        Assert.Equal("dark", reloaded.GetString(UserOptionKeys.ThemeBase));
        Assert.Equal(true, reloaded.GetBoolean(UserOptionKeys.NerdFont));
        Assert.Equal("ansi256", reloaded.GetString(UserOptionKeys.ColorTier));
        Assert.True(File.Exists(reloaded.GlobalFilePath));
        Assert.True(File.Exists(reloaded.ApplicationFilePath));
    }

    [Fact]
    public void Save_LeavesNoTempFiles()
    {
        using var root = new TempConfigRoot();

        var store = UserOptionsStore.Load(AppId, root);
        store.SetGlobal(UserOptionKeys.ThemeBase, "light");
        store.Save();

        var strays = Directory.GetFiles(root.ConfigurationRoot, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(strays);
    }

    // ───────────────────────────── tri-state overlay ─────────────────────────────

    [Fact]
    public void Overlay_ApplicationOverridesOnlyExplicitlySetKeys()
    {
        using var root = new TempConfigRoot();

        var store = UserOptionsStore.Load(AppId, root);
        store.SetGlobal(UserOptionKeys.ThemeBase, "dark");
        store.SetGlobal(UserOptionKeys.ColorTier, "ansi16");
        store.SetApplication(UserOptionKeys.ColorTier, "truecolor"); // overrides this key ONLY
        store.Save();

        var reloaded = UserOptionsStore.Load(AppId, root);
        Assert.Equal("truecolor", reloaded.GetString(UserOptionKeys.ColorTier)); // explicitly set → overlays
        Assert.Equal("dark", reloaded.GetString(UserOptionKeys.ThemeBase));      // unset in the overlay → inherits
    }

    [Fact]
    public void Overlay_PerAppFile_NeverSnapshotsGlobalDefaults()
    {
        using var root = new TempConfigRoot();

        var store = UserOptionsStore.Load(AppId, root);
        store.SetGlobal(UserOptionKeys.ThemeBase, "dark");
        store.SetApplication(UserOptionKeys.ColorTier, "ansi256");
        store.Save();

        // The saved overlay file contains ONLY the explicitly set key — a later global change must
        // shine through (FB-17 review note 2: no snapshot-and-freeze).
        var appJson = File.ReadAllText(store.ApplicationFilePath);
        Assert.Contains(UserOptionKeys.ColorTier, appJson);
        Assert.DoesNotContain(UserOptionKeys.ThemeBase, appJson);

        var globalEdit = UserOptionsStore.Load(AppId, root);
        globalEdit.SetGlobal(UserOptionKeys.ThemeBase, "light");
        globalEdit.Save();

        var reloaded = UserOptionsStore.Load(AppId, root);
        Assert.Equal("light", reloaded.GetString(UserOptionKeys.ThemeBase));
    }

    [Fact]
    public void Overlay_ClearingAnApplicationKey_ReExposesTheGlobalValue()
    {
        using var root = new TempConfigRoot();

        var store = UserOptionsStore.Load(AppId, root);
        store.SetGlobal(UserOptionKeys.ThemeBase, "dark");
        store.SetApplication(UserOptionKeys.ThemeBase, "light");
        store.Save();

        Assert.Equal("light", UserOptionsStore.Load(AppId, root).GetString(UserOptionKeys.ThemeBase));

        var clearing = UserOptionsStore.Load(AppId, root);
        clearing.SetApplication(UserOptionKeys.ThemeBase, null); // unset → inherit (the tri-state contract)
        clearing.Save();

        Assert.Equal("dark", UserOptionsStore.Load(AppId, root).GetString(UserOptionKeys.ThemeBase));
    }

    [Fact]
    public void Overlay_DifferentAppIds_UseDisjointOverlayFiles()
    {
        using var root = new TempConfigRoot();

        var a = UserOptionsStore.Load("app-a", root);
        a.SetApplication(UserOptionKeys.ThemeBase, "light");
        a.Save();

        var b = UserOptionsStore.Load("app-b", root);
        Assert.Null(b.GetString(UserOptionKeys.ThemeBase));
        Assert.NotEqual(a.ApplicationFilePath, b.ApplicationFilePath);
    }

    // ───────────────────────────── corrupt-file tolerance ─────────────────────────────

    [Fact]
    public void CorruptGlobalFile_LoadsDefaults_WithDiagnostic_NeverThrows()
    {
        using var root = new TempConfigRoot();
        root.WriteGlobalFile("this is { not json ][");

        var store = UserOptionsStore.Load(AppId, root);

        Assert.Null(store.GetString(UserOptionKeys.ThemeBase));
        Assert.NotEmpty(store.LoadDiagnostics);
    }

    [Fact]
    public void CorruptApplicationFile_GlobalValuesStillApply()
    {
        using var root = new TempConfigRoot();
        root.WriteGlobalFile("""{ "theme.base": "dark" }""");
        root.WriteApplicationFile(AppId, "%%% garbage");

        var store = UserOptionsStore.Load(AppId, root);

        Assert.Equal("dark", store.GetString(UserOptionKeys.ThemeBase)); // the good file survives the bad one
        Assert.NotEmpty(store.LoadDiagnostics);
    }

    [Fact]
    public void NonObjectRoot_LoadsDefaults_WithDiagnostic()
    {
        using var root = new TempConfigRoot();
        root.WriteGlobalFile("[1, 2, 3]");

        var store = UserOptionsStore.Load(AppId, root);

        Assert.Empty(store.GlobalValues);
        Assert.NotEmpty(store.LoadDiagnostics);
    }

    [Fact]
    public void MissingFiles_LoadEmpty_WithNoDiagnostics()
    {
        using var root = new TempConfigRoot();

        var store = UserOptionsStore.Load(AppId, root);

        Assert.Empty(store.GlobalValues);
        Assert.Empty(store.ApplicationValues);
        Assert.Empty(store.LoadDiagnostics);
    }

    [Fact]
    public void ThrowingPathProvider_LoadsDefaults_WithDiagnostic_NeverThrows()
    {
        var store = UserOptionsStore.Load(AppId, new ThrowingProvider());

        Assert.Empty(store.GlobalValues);
        Assert.NotEmpty(store.LoadDiagnostics);
    }

    // ───────────────────────────── hand-edit tolerance + format ─────────────────────────────

    [Fact]
    public void HandEditedFile_CommentsTrailingCommasAndBareLiterals_AllTolerated()
    {
        using var root = new TempConfigRoot();
        root.WriteGlobalFile(
            """
            {
                // hand-edited by a power user
                "capabilities.nerdFont": true,
                "custom.retries": 3,
                "theme.base": "dark",
            }
            """);

        var store = UserOptionsStore.Load(AppId, root);

        Assert.Empty(store.LoadDiagnostics);
        Assert.Equal(true, store.GetBoolean(UserOptionKeys.NerdFont)); // bare literal coerced
        Assert.Equal(3, store.GetInt32("custom.retries"));
        Assert.Equal("dark", store.GetString(UserOptionKeys.ThemeBase));
    }

    [Fact]
    public void UnknownKeys_ArePreservedThroughLoadAndSave()
    {
        using var root = new TempConfigRoot();
        root.WriteGlobalFile("""{ "some.future.key": "kept" }""");

        var store = UserOptionsStore.Load(AppId, root);
        store.SetGlobal(UserOptionKeys.ThemeBase, "light");
        store.Save(); // an old binary saving must not destroy a newer binary's keys

        var reloaded = UserOptionsStore.Load(AppId, root);
        Assert.Equal("kept", reloaded.GetString("some.future.key"));
        Assert.Equal("light", reloaded.GetString(UserOptionKeys.ThemeBase));
    }

    [Fact]
    public void UnparseableTypedReads_ReturnNull()
    {
        using var root = new TempConfigRoot();
        root.WriteGlobalFile("""{ "a": "maybe", "b": "1.5" }""");

        var store = UserOptionsStore.Load(AppId, root);

        Assert.Null(store.GetBoolean("a"));
        Assert.Null(store.GetInt32("b"));
        Assert.Equal("maybe", store.GetString("a")); // still readable as raw text
    }

    [Fact]
    public void ApplicationId_WithPathHostileCharacters_IsSanitizedToAWritableFileName()
    {
        using var root = new TempConfigRoot();

        var store = UserOptionsStore.Load("../weird/app:id", root);
        store.SetApplication(UserOptionKeys.ThemeBase, "light");
        store.Save();

        Assert.True(File.Exists(store.ApplicationFilePath));
        var appsDirectory = Path.Combine(root.ConfigurationRoot, "apps");
        Assert.Equal(Path.GetFullPath(appsDirectory), Path.GetFullPath(Path.GetDirectoryName(store.ApplicationFilePath)!)); // never escapes the apps directory
        Assert.Equal("light", UserOptionsStore.Load("../weird/app:id", root).GetString(UserOptionKeys.ThemeBase));
    }

    private sealed class ThrowingProvider : IUserConfigurationPathProvider
    {
        public string ConfigurationRoot => throw new InvalidOperationException("no home directory");
    }
}
