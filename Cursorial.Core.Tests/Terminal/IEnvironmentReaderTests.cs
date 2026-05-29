using System.Runtime.InteropServices;

using Cursorial.Terminal;

namespace Cursorial.Tests.Terminal;

/// <summary>
/// Tests for the env-var-dependent default helpers on <see cref="IEnvironmentReader"/>. The
/// stub is configured with the desired env vars (and platform, where the helper short-circuits
/// on it), and the public default-implementation accessor is invoked. The static
/// <c>Default*</c> helpers are protected on the interface, so we drive them transitively
/// through the public methods rather than calling them directly.
/// </summary>
public class IEnvironmentReaderTests
{
    // ---- IsSSH ----

    [Fact]
    public void IsSSH_BothSshVarsUnset_ReturnsFalse()
    {
        IEnvironmentReader env = new StubEnvironmentReader();
        Assert.False(env.IsSSH());
    }

    [Fact]
    public void IsSSH_SshConnectionSet_ReturnsTrue()
    {
        // OpenSSH sets SSH_CONNECTION to "client_ip client_port server_ip server_port".
        IEnvironmentReader env = new StubEnvironmentReader()
            .Set("SSH_CONNECTION", "10.0.0.5 56789 10.0.0.1 22");
        Assert.True(env.IsSSH());
    }

    [Fact]
    public void IsSSH_SshTtySet_ReturnsTrue()
    {
        IEnvironmentReader env = new StubEnvironmentReader().Set("SSH_TTY", "/dev/pts/3");
        Assert.True(env.IsSSH());
    }

    [Fact]
    public void IsSSH_BothSet_ReturnsTrue()
    {
        IEnvironmentReader env = new StubEnvironmentReader()
            .Set("SSH_CONNECTION", "10.0.0.5 56789 10.0.0.1 22")
            .Set("SSH_TTY", "/dev/pts/3");
        Assert.True(env.IsSSH());
    }

    [Fact]
    public void IsSSH_EmptyString_ReturnsFalse()
    {
        // An empty SSH_CONNECTION isn't a real SSH session; the default checks Length > 0.
        IEnvironmentReader env = new StubEnvironmentReader()
            .Set("SSH_CONNECTION", "")
            .Set("SSH_TTY", "");
        Assert.False(env.IsSSH());
    }

    // ---- IsCygwin ----

    [Fact]
    public void IsCygwin_WindowsAndCygwinVarSet_ReturnsTrue()
    {
        // Cygwin sets CYGWIN to a configuration string; the helper treats presence as
        // sufficient (any value, including an empty string, signals "Cygwin shell").
        IEnvironmentReader env = new StubEnvironmentReader()
            .WithPlatform(OSPlatform.Windows)
            .Set("CYGWIN", "nodosfilewarning");
        Assert.True(env.IsCygwin());
    }

    [Fact]
    public void IsCygwin_WindowsAndCygwinEmpty_ReturnsTrue()
    {
        // Presence semantics: the variable's value is irrelevant — only whether it's set.
        IEnvironmentReader env = new StubEnvironmentReader()
            .WithPlatform(OSPlatform.Windows)
            .Set("CYGWIN", "");
        Assert.True(env.IsCygwin());
    }

    [Fact]
    public void IsCygwin_WindowsButCygwinUnset_ReturnsFalse()
    {
        IEnvironmentReader env = new StubEnvironmentReader().WithPlatform(OSPlatform.Windows);
        Assert.False(env.IsCygwin());
    }

    [Fact]
    public void IsCygwin_NotWindows_AlwaysReturnsFalse()
    {
        IEnvironmentReader env = new StubEnvironmentReader()
            .WithPlatform(OSPlatform.Linux)
            .Set("CYGWIN", "nodosfilewarning");
        Assert.False(env.IsCygwin());
    }

    // ---- IsMinGW ----

    [Fact]
    public void IsMinGW_WindowsAndMsystemSet_ReturnsTrue()
    {
        // MSYSTEM is set to "MINGW64", "MINGW32", "MSYS", "UCRT64", etc. by MSYS2 launchers.
        IEnvironmentReader env = new StubEnvironmentReader()
            .WithPlatform(OSPlatform.Windows)
            .Set("MSYSTEM", "MINGW64");
        Assert.True(env.IsMinGW());
    }

    [Fact]
    public void IsMinGW_WindowsAndMsystemEmpty_ReturnsTrue()
    {
        IEnvironmentReader env = new StubEnvironmentReader()
            .WithPlatform(OSPlatform.Windows)
            .Set("MSYSTEM", "");
        Assert.True(env.IsMinGW());
    }

    [Fact]
    public void IsMinGW_WindowsButMsystemUnset_ReturnsFalse()
    {
        IEnvironmentReader env = new StubEnvironmentReader().WithPlatform(OSPlatform.Windows);
        Assert.False(env.IsMinGW());
    }

    [Fact]
    public void IsMinGW_NotWindows_AlwaysReturnsFalse()
    {
        IEnvironmentReader env = new StubEnvironmentReader()
            .WithPlatform(OSPlatform.Linux)
            .Set("MSYSTEM", "MINGW64");
        Assert.False(env.IsMinGW());
    }

    // ---- IsWSL ----

    // The default IsWSL implementation reads /proc/version unconditionally on Linux. We can
    // test the platform short-circuit (non-Linux always returns false) without touching the
    // filesystem; the full WSL-detection path requires a real Linux runtime and is noted in
    // the interface's own remarks as UNRELIABLE.

    [Fact]
    public void IsWSL_NotLinux_ReturnsFalse()
    {
        IEnvironmentReader env = new StubEnvironmentReader().WithPlatform(OSPlatform.Windows);
        Assert.False(env.IsWSL());
    }

    [Fact]
    public void IsWSL_MacOS_ReturnsFalse()
    {
        IEnvironmentReader env = new StubEnvironmentReader().WithPlatform(OSPlatform.OSX);
        Assert.False(env.IsWSL());
    }
}
