using Cursorial.Terminal.Stdio;

namespace Cursorial.Tests.Terminal;

public class ResizeMonitorFactoryTests
{
    [Fact]
    public void Create_PicksImplementationMatchingHostOs()
    {
        // The factory dispatches to the platform-specific impl. We can't easily verify the
        // concrete type without InternalsVisibleTo gymnastics, but we can verify that on
        // supported OSes it returns non-null and on unsupported it returns null.
        IResizeMonitor? monitor = ResizeMonitor.Create(_ => { });

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ||
            OperatingSystem.IsWindows())
        {
            Assert.NotNull(monitor);
            monitor.Dispose();
        }
        else
        {
            Assert.Null(monitor);
        }
    }

    [Fact]
    public void Create_NullCallback_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ResizeMonitor.Create(null!));
    }

    [Fact]
    public void Create_PosixHost_ReturnsPosixMonitor()
    {
        // POSIX dispatch path — verify by checking the concrete type when we're on a
        // POSIX platform. Skips on Windows (the Windows assertion is in a sibling test).
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
            return;

        using var monitor = ResizeMonitor.Create(_ => { });
        Assert.NotNull(monitor);
        Assert.IsType<PosixResizeMonitor>(monitor);
    }

    [Fact]
    public void Create_WindowsHost_ReturnsWindowsMonitor()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var monitor = ResizeMonitor.Create(_ => { });
        Assert.NotNull(monitor);
        Assert.IsType<WindowsResizeMonitor>(monitor);
    }
}
