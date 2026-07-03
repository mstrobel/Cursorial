using System.Collections.ObjectModel;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C20 — TreeView HierarchicalDataTemplate (data-driven hierarchy). A TreeViewItem generated for a
// data object takes its Header from the (hierarchical) item template and its children from the template's
// ItemsSourcePath; the by-type DataTemplate chain recurses for a homogeneous tree. Both ItemTemplate and a
// resources-keyed-by-type template drive it.
public sealed class Section33_TreeViewHDT
{
    private sealed class Node
    {
        public Node(string name, params Node[] children)
        {
            Name = name;
            foreach (var c in children)
                Children.Add(c);
        }

        public string Name { get; }
        public ObservableCollection<Node> Children { get; } = new();
        public override string ToString() => Name;
    }

    private static HierarchicalDataTemplate Hdt() => new()
    {
        ItemsSourcePath = nameof(Node.Children),
        Content = new FuncTemplateContent(_ => new TextBlock()),
    };

    private static Node[] Sample() =>
    [
        new Node("a", new Node("a1"), new Node("a2")),
        new Node("b"),
    ];

    private static TreeViewItem Container(ItemsControl owner, int index)
        => (TreeViewItem)owner.ItemContainerGenerator.ContainerFromIndex(index)!;

    [Fact] // C20.1: a data ItemsSource + an HDT ItemTemplate generates TreeViewItem containers with Header + children
    public void C20_1_HdtViaItemTemplate()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var roots = Sample();
        var tree = new TreeView { ItemTemplate = Hdt(), ItemsSource = roots, Width = 20, Height = 10 };
        host.ShowRoot(tree);
        host.RunUntilIdle();

        var a = Container(tree, 0);
        Assert.Same(roots[0], a.Header);              // header = the data item
        Assert.Same(roots[0].Children, a.ItemsSource); // children from ItemsSourcePath="Children"
        Assert.True(a.HasItems);
        Assert.False(Container(tree, 1).HasItems);     // "b" is a leaf
    }

    [Fact] // C20.2: the hierarchy recurses — a root's container has child containers for its data children
    public void C20_2_Recurses()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var roots = Sample();
        var tree = new TreeView { ItemTemplate = Hdt(), ItemsSource = roots, Width = 20, Height = 10 };
        host.ShowRoot(tree);
        host.RunUntilIdle();

        var a = Container(tree, 0);
        Assert.Equal(2, a.ItemContainerGenerator.ContainerCount);
        Assert.Same(roots[0].Children[0], Container(a, 0).Header); // the "a1" node
    }

    [Fact] // C20.3: an HDT registered in resources keyed by DataType (no ItemTemplate) also drives the hierarchy
    public void C20_3_HdtViaResources()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var roots = Sample();
        var tree = new TreeView { Width = 20, Height = 10 };
        tree.Resources[new DataTemplateKey(typeof(Node))] = Hdt(); // register the by-type template BEFORE the items realize
        tree.ItemsSource = roots;
        host.ShowRoot(tree);
        host.RunUntilIdle();

        var a = Container(tree, 0);
        Assert.Same(roots[0], a.Header);
        Assert.Same(roots[0].Children, a.ItemsSource);
        Assert.Same(roots[0].Children[0], Container(a, 0).Header); // recursion via the by-type chain
    }

    [Fact] // C20.4: the container's HeaderTemplate is wired to the HDT (so the header renders through it)
    public void C20_4_HeaderTemplateWired()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var roots = Sample();
        var hdt = Hdt();
        var tree = new TreeView { ItemTemplate = hdt, ItemsSource = roots, Width = 20, Height = 10 };
        host.ShowRoot(tree);
        host.RunUntilIdle();
        Assert.Same(hdt, Container(tree, 0).HeaderTemplate);
    }

    [Fact] // C20.5: a live add to a node's children collection materializes a new child container
    public void C20_5_LiveChildAdd()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var roots = Sample();
        var tree = new TreeView { ItemTemplate = Hdt(), ItemsSource = roots, Width = 20, Height = 10 };
        host.ShowRoot(tree);
        host.RunUntilIdle();

        var a = Container(tree, 0);
        Assert.Equal(2, a.ItemContainerGenerator.ContainerCount);

        roots[0].Children.Add(new Node("a3")); // ObservableCollection ⇒ the generator realizes a new container
        host.RunUntilIdle();
        Assert.Equal(3, a.ItemContainerGenerator.ContainerCount);
        Assert.Same(roots[0].Children[2], Container(a, 2).Header);
    }

    [Fact] // C20.6: selection over HDT-generated containers resolves to the data item
    public void C20_6_SelectionResolvesDataItem()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var roots = Sample();
        var tree = new TreeView { ItemTemplate = Hdt(), ItemsSource = roots, Width = 20, Height = 10 };
        host.ShowRoot(tree);
        host.RunUntilIdle();

        Container(tree, 0).IsSelected = true; // select the "a" node's container
        host.RunUntilIdle();
        Assert.Same(roots[0], tree.SelectedItem); // SelectedItem is the data Node, not the container
    }

    // ── audit regressions (CD-P2H-1 audit) ──────────────────────────────────────────────────────────────

    [Fact] // C20.7: a cyclic data graph does NOT StackOverflow — the recursion stops where a data item repeats an ancestor
    public void C20_7_CyclicGraphDoesNotOverflow()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var a = new Node("a");
        var b = new Node("b");
        a.Children.Add(b);
        b.Children.Add(a); // a → b → a → …  (cycle)

        var tree = new TreeView { ItemTemplate = Hdt(), ItemsSource = new[] { a }, Width = 20, Height = 10 };
        host.ShowRoot(tree); // must NOT crash the process with a StackOverflow
        host.RunUntilIdle();

        var ca = Container(tree, 0);     // a
        var cb = Container(ca, 0);       // b
        Assert.Same(b, cb.Header);
        var ca2 = Container(cb, 0);      // a again (b's child)
        Assert.Same(a, ca2.Header);
        Assert.False(ca2.HasItems);      // the cycle stopped here — the repeated 'a' renders as a leaf
    }
}
