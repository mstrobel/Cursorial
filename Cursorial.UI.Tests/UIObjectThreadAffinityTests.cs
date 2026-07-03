using Cursorial.UI;

namespace Cursorial.Tests.UI;

/// <summary>
/// Invariant 6 — single UI thread: thread affinity is captured at construction;
/// <c>VerifyAccess</c> asserts in DEBUG and compiles away in release.
/// </summary>
public class UIObjectThreadAffinityTests
{
    private sealed class AffinityHost : UIObject;

    [Fact]
    public void CheckAccess_TrueOnConstructingThread()
    {
        var host = new AffinityHost();

        Assert.True(host.CheckAccess());
    }

    [Fact]
    public async Task CheckAccess_FalseOnOtherThread()
    {
        var host = new AffinityHost();

        var fromOtherThread = await Task.Run(host.CheckAccess);

        Assert.False(fromOtherThread);
    }

    [Fact]
    public void VerifyAccess_SameThread_DoesNotThrow()
    {
        var host = new AffinityHost();

        host.VerifyAccess(); // no-op on the owning thread in DEBUG; stripped entirely in release
    }

#if DEBUG
    [Fact]
    public async Task VerifyAccess_Debug_ThrowsCrossThread()
    {
        var host = new AffinityHost();

        var thrown = await Task.Run(() => Record.Exception(() => host.VerifyAccess()));

        Assert.IsType<InvalidOperationException>(thrown);
    }
#endif
}
