using System.Security.Cryptography;
using System.Text;

using Cursorial.Terminal;

namespace Cursorial.CLI;

/// <summary>
/// The on-disk terminal-capability cache (docs/cli-design.md §6, FW-1) — the big
/// interactive-startup lever. A cold run pays the negotiator's sentinel-bounded probe rounds
/// (2–3 terminal RTTs; up to 1.5&#x202f;s of timeout budget on a mute terminal) and persists the
/// realized <see cref="TerminalCapabilities"/> snapshot; a warm run seeds the session via
/// <see cref="TerminalSessionOptions.CachedCapabilities"/> and skips the wire handshake
/// entirely (opt-in enables still applied, restore parity preserved — see the framework docs
/// on that property).
/// </summary>
/// <remarks>
/// <para>
/// <b>Location.</b> <c>$XDG_CACHE_HOME/curio/caps/&lt;key&gt;.json</c>, falling back to
/// <c>~/.cache/curio/caps/</c> when <c>XDG_CACHE_HOME</c> is unset — one file per terminal
/// identity.
/// </para>
/// <para>
/// <b>Key.</b> A filesystem-safe slug + hash over the negotiator's own identity inputs:
/// <c>TERM</c>, <c>TERM_PROGRAM</c>, <c>TERM_PROGRAM_VERSION</c>, and the presence of the
/// multiplexer variables <c>TMUX</c> / <c>STY</c> / <c>ZELLIJ</c>. A terminal upgrade
/// (version change) or moving in/out of a multiplexer changes the key, so drifted entries are
/// simply never hit and a fresh negotiation writes the new entry.
/// </para>
/// <para>
/// <b>Failure posture.</b> Everything here is best-effort: an unreadable, corrupt, or
/// version-drifted cache file reads as cold (and is deleted so the rewrite starts clean);
/// store failures are swallowed. The cache can only ever save time, never break a run.
/// Kill-switches: the <c>--no-caps-cache</c> flag and the <c>CURIO_NO_CAPS_CACHE</c>
/// environment variable (any non-empty value).
/// </para>
/// <para>
/// <b>AOT.</b> Serialization goes through the Core hand-rolled
/// <see cref="TerminalCapabilitiesSerializer"/> (Utf8JsonWriter / JsonDocument) — zero
/// reflection-based serialization in this binary.
/// </para>
/// </remarks>
public static class CapabilityCache
{
    /// <summary>Environment kill-switch: any non-empty value disables load AND store.</summary>
    public static bool IsDisabledByEnvironment =>
        Environment.GetEnvironmentVariable("CURIO_NO_CAPS_CACHE") is { Length: > 0 };

    /// <summary>
    /// Compute the cache key for a terminal identity. Pure — exposed (with explicit inputs)
    /// so tests can pin the slugging/hashing behavior without touching process environment.
    /// The result is filesystem-safe: a lowercase <c>[a-z0-9._-]</c> slug (readable prefix,
    /// for humans inspecting the cache directory) plus a 16-hex-digit SHA-256 prefix over the
    /// full identity tuple (the actual discriminator).
    /// </summary>
    public static string ComputeKey(string? term, string? termProgram, string? termProgramVersion,
                                    bool tmux, bool screen, bool zellij)
    {
        var identity = $"term={term}\nprog={termProgram}\nver={termProgramVersion}\n" +
                       $"tmux={(tmux ? 1 : 0)}\nscreen={(screen ? 1 : 0)}\nzellij={(zellij ? 1 : 0)}";

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 8));

        return $"{Slugify(termProgram ?? term)}-{hash}";
    }

    /// <summary>
    /// Resolve the cache directory from explicit inputs. Pure — the env-reading overloads sit
    /// on top. <paramref name="xdgCacheHome"/> wins when non-empty; otherwise
    /// <c>&lt;home&gt;/.cache</c>.
    /// </summary>
    public static string ResolveCacheDirectory(string? xdgCacheHome, string homeDirectory)
    {
        var cacheRoot = string.IsNullOrEmpty(xdgCacheHome)
            ? Path.Combine(homeDirectory, ".cache")
            : xdgCacheHome;

        return Path.Combine(cacheRoot, "curio", "caps");
    }

    /// <summary>
    /// Try to load the snapshot for the CURRENT terminal identity (process environment).
    /// Null on: kill-switch, no entry, unreadable file, or a corrupt / drifted entry (which
    /// is deleted so the post-negotiation store starts clean).
    /// </summary>
    public static TerminalCapabilities? TryLoad()
    {
        if (IsDisabledByEnvironment) return null;

        try
        {
            var path = CurrentCacheFilePath();
            if (!File.Exists(path)) return null;

            var bytes = File.ReadAllBytes(path);
            if (TerminalCapabilitiesSerializer.TryDeserialize(bytes, out var capabilities))
                return capabilities;

            // Corrupt or schema-drifted: silently treat as cold. Delete so this run's
            // post-negotiation TryStore rewrites the entry in the current shape.
            File.Delete(path);
            return null;
        }
        catch
        {
            // Unreadable cache (permissions, races, exotic filesystems) is just a cold run.
            return null;
        }
    }

    /// <summary>
    /// Persist a freshly negotiated snapshot for the CURRENT terminal identity. Best-effort:
    /// failures are swallowed. Writes via a temp file + atomic rename so a concurrent curio
    /// never observes a torn entry.
    /// </summary>
    public static void TryStore(TerminalCapabilities capabilities)
    {
        if (IsDisabledByEnvironment) return;

        try
        {
            var path = CurrentCacheFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllBytes(temp, TerminalCapabilitiesSerializer.Serialize(capabilities));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // The cache is an optimization; a failed store must never fail the run.
        }
    }

    private static string CurrentCacheFilePath()
    {
        var key = ComputeKey(
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("TERM_PROGRAM"),
            Environment.GetEnvironmentVariable("TERM_PROGRAM_VERSION"),
            tmux: Environment.GetEnvironmentVariable("TMUX") is { Length: > 0 },
            screen: Environment.GetEnvironmentVariable("STY") is { Length: > 0 },
            zellij: Environment.GetEnvironmentVariable("ZELLIJ") is { Length: > 0 });

        var directory = ResolveCacheDirectory(
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        return Path.Combine(directory, key + ".json");
    }

    /// <summary>
    /// Reduce a terminal identifier to a lowercase <c>[a-z0-9._-]</c> slug, capped at 24
    /// chars. Purely cosmetic — the SHA-256 suffix is the discriminator — but it makes
    /// <c>ls ~/.cache/curio/caps</c> legible ("ghostty-…", "iterm.app-…").
    /// </summary>
    private static string Slugify(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return "term";

        Span<char> slug = stackalloc char[Math.Min(identifier.Length, 24)];
        for (int i = 0; i < slug.Length; i++)
        {
            var c = char.ToLowerInvariant(identifier[i]);
            slug[i] = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' ? c : '-';
        }

        var trimmed = ((ReadOnlySpan<char>) slug).Trim('-');
        return trimmed.IsEmpty ? "term" : new string(trimmed);
    }
}
