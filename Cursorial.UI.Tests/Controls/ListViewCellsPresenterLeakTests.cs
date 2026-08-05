#if DEBUG
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

// ListViewCellsPresenter BUILDS its cells (ListView.BuildDetailsCell / BuildComposedCell) and
// discards them on every column-structure or view change. Removing a visual child deliberately does
// NOT sever bindings, so a discarded cell's bindings — including a Source-anchored Inverse link back
// to the presenter itself — keep the dead cell observed and alive unless the presenter tears them
// down. Rebuild runs on every structure change, so the leak is unbounded rather than one-off.
public sealed class ListViewCellsPresenterLeakTests
{
    private sealed record Row(string Name, string Size);

    [Fact]
    public void RebuildingCells_TearsDownTheDiscardedCellsBindings()
    {
        var rows = new List<Row> { new("a", "1"), new("b", "2") };
        var list = new ListView { ItemsSource = rows };
        list.Columns.Add(new ListViewColumn { Header = "Name", DisplayMemberPath = "Name" });
        list.Columns.Add(new ListViewColumn { Header = "Size", DisplayMemberPath = "Size" });

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(60, 20),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });
        host.ShowRoot(list);
        host.RunUntilIdle();

        BindingLeakTracker.ResetForTests();

        // Each column-structure change clears and rebuilds every row's cells.
        for (int i = 0; i < 8; i++)
        {
            list.Columns.Add(new ListViewColumn { Header = $"C{i}", DisplayMemberPath = "Name" });
            host.RunUntilIdle();
            list.Columns.RemoveAt(list.Columns.Count - 1);
            host.RunUntilIdle();
        }

        // Discard everything, so a binding the sweep still reports was never torn down (bindings on
        // surviving elements are live, not leaked).
        list.ItemsSource = null;
        host.RunUntilIdle();
        host.ShowRoot(new Border());
        host.RunUntilIdle();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var leaked = BindingLeakTracker.Sweep()
                                       .Where(l => l.InstallSite.Contains("BuildDetailsCell"))
                                       .ToList();
        BindingLeakTracker.ResetForTests();

        Assert.True(leaked.Count == 0,
                    $"{leaked.Count} cell bindings survived every rebuild — e.g. {leaked.FirstOrDefault().Path} on {leaked.FirstOrDefault().TargetDescription}");
    }
}
#endif
