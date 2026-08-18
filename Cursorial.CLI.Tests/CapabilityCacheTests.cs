using System.Text.RegularExpressions;

using Cursorial.CLI;
using Cursorial.Terminal;

namespace Cursorial.Tests.CLI;

/// <summary>
/// The curio capability cache (docs/cli-design.md §6, FW-1). Key computation and directory
/// resolution are pure functions tested directly; the load/store/corruption behavior is tested
/// end-to-end through a scratch <c>XDG_CACHE_HOME</c>.
/// </summary>
public class CapabilityCacheTests
{
    // ---- Key computation (pure) ----

    [Fact]
    public void ComputeKey_IsDeterministic()
    {
        var a = CapabilityCache.ComputeKey("xterm-kitty", "kitty", "0.34.1", tmux: false, screen: false, zellij: false);
        var b = CapabilityCache.ComputeKey("xterm-kitty", "kitty", "0.34.1", tmux: false, screen: false, zellij: false);

        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeKey_DiscriminatesEveryIdentityInput()
    {
        var baseline = CapabilityCache.ComputeKey("xterm-kitty", "kitty", "0.34.1", tmux: false, screen: false, zellij: false);

        Assert.NotEqual(baseline, CapabilityCache.ComputeKey("xterm-256color", "kitty", "0.34.1", false, false, false));
        Assert.NotEqual(baseline, CapabilityCache.ComputeKey("xterm-kitty", "ghostty", "0.34.1", false, false, false));
        Assert.NotEqual(baseline, CapabilityCache.ComputeKey("xterm-kitty", "kitty", "0.35.0", false, false, false));
        Assert.NotEqual(baseline, CapabilityCache.ComputeKey("xterm-kitty", "kitty", "0.34.1", tmux: true, screen: false, zellij: false));
        Assert.NotEqual(baseline, CapabilityCache.ComputeKey("xterm-kitty", "kitty", "0.34.1", tmux: false, screen: true, zellij: false));
        Assert.NotEqual(baseline, CapabilityCache.ComputeKey("xterm-kitty", "kitty", "0.34.1", tmux: false, screen: false, zellij: true));
    }

    [Fact]
    public void ComputeKey_NullsDoNotCollideWithEmpties_ButStayFilesystemSafe()
    {
        var allNull = CapabilityCache.ComputeKey(null, null, null, false, false, false);

        Assert.Matches("^[a-z0-9._-]+$", allNull);
        Assert.StartsWith("term-", allNull);
    }

    [Theory]
    [InlineData("iTerm.app")]
    [InlineData("Apple_Terminal")]
    [InlineData("WezTerm (nightly)/2:1 \\ β")]
    [InlineData("../../../etc/passwd")]
    public void ComputeKey_IsAlwaysFilesystemSafe(string termProgram)
    {
        var key = CapabilityCache.ComputeKey("xterm-256color", termProgram, "1.0", false, false, false);

        Assert.Matches("^[a-z0-9._-]+$", key);
        Assert.Equal(key, Path.GetFileName(key)); // no separators — traversal cannot survive slugging
    }

    // ---- Directory resolution (pure) ----

    [Fact]
    public void ResolveCacheDirectory_PrefersXdgCacheHome()
    {
        var dir = CapabilityCache.ResolveCacheDirectory("/custom/cache", "/home/me");

        Assert.Equal(Path.Combine("/custom/cache", "curio", "caps"), dir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveCacheDirectory_FallsBackToDotCache(string? xdg)
    {
        var dir = CapabilityCache.ResolveCacheDirectory(xdg, "/home/me");

        Assert.Equal(Path.Combine("/home/me", ".cache", "curio", "caps"), dir);
    }

    // ---- Load / store / corruption (through a scratch XDG_CACHE_HOME) ----

    [Fact]
    public void StoreThenLoad_RoundTripsThroughTheRealFileLayout()
    {
        using var scratch = new ScratchCacheEnvironment();

        CapabilityCache.TryStore(TerminalCapabilities.None);

        var loaded = CapabilityCache.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal(TerminalCapabilities.None, loaded);
        Assert.Single(Directory.GetFiles(scratch.CapsDirectory, "*.json"));
    }

    [Fact]
    public void Load_CorruptEntry_IsColdAndDeletesTheFile()
    {
        using var scratch = new ScratchCacheEnvironment();

        CapabilityCache.TryStore(TerminalCapabilities.None);
        var path = Directory.GetFiles(scratch.CapsDirectory, "*.json").Single();
        File.WriteAllText(path, "{ definitely not a capability snapshot");

        Assert.Null(CapabilityCache.TryLoad());       // silently cold…
        Assert.False(File.Exists(path));              // …and the corrupt entry is gone
    }

    [Fact]
    public void Load_MissingEntry_IsCold()
    {
        using var scratch = new ScratchCacheEnvironment();

        Assert.Null(CapabilityCache.TryLoad());
    }

    [Fact]
    public void KillSwitch_DisablesLoadAndStore()
    {
        using var scratch = new ScratchCacheEnvironment();

        CapabilityCache.TryStore(TerminalCapabilities.None);
        Assert.NotNull(CapabilityCache.TryLoad());

        scratch.Set("CURIO_NO_CAPS_CACHE", "1");
        Assert.True(CapabilityCache.IsDisabledByEnvironment);
        Assert.Null(CapabilityCache.TryLoad());       // entry exists on disk, but the switch wins

        // A store under the switch must not touch the entry — write a DISTINGUISHABLE snapshot,
        // clear the switch, and confirm the original still loads.
        var changed = TerminalCapabilities.None with
        {
            Terminal = TerminalIdentification.Unknown with { Name = "written-under-kill-switch" },
        };
        CapabilityCache.TryStore(changed);

        scratch.Set("CURIO_NO_CAPS_CACHE", null);
        Assert.Equal(TerminalCapabilities.None, CapabilityCache.TryLoad());
    }

    /// <summary>
    /// Pins the cache-relevant process environment (XDG_CACHE_HOME → a scratch temp dir, a
    /// fixed terminal identity, kill-switch cleared) and restores every variable on dispose.
    /// Environment variables are process-global, so these tests share a serial xunit class
    /// (same test class = sequential) and never run concurrently with each other.
    /// </summary>
    private sealed class ScratchCacheEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _saved = [];
        private readonly string _root;

        public ScratchCacheEnvironment()
        {
            _root = Path.Combine(Path.GetTempPath(), "curio-caps-test-" + Guid.NewGuid().ToString("N"));

            Set("XDG_CACHE_HOME", _root);
            Set("TERM", "xterm-kitty");
            Set("TERM_PROGRAM", "kitty");
            Set("TERM_PROGRAM_VERSION", "0.34.1");
            Set("TMUX", null);
            Set("STY", null);
            Set("ZELLIJ", null);
            Set("CURIO_NO_CAPS_CACHE", null);
        }

        public string CapsDirectory => Path.Combine(_root, "curio", "caps");

        public void Set(string name, string? value)
        {
            _saved.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _saved)
                Environment.SetEnvironmentVariable(name, value);

            try { Directory.Delete(_root, recursive: true); }
            catch { /* scratch dir cleanup is best-effort */ }
        }
    }
}
