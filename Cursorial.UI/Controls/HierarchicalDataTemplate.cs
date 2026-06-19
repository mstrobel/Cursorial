using System.Collections;
using System.Reflection;

namespace Cursorial.UI.Controls;

/// <summary>
/// A <see cref="DataTemplate"/> for a node in a hierarchy (design doc §12.6 — the WPF
/// <c>HierarchicalDataTemplate</c> analog, used by <see cref="TreeView"/>): its <see cref="DataTemplate.Content"/> is
/// the node's header visual, and <see cref="ItemsSourcePath"/> names the data object's child collection. A
/// <see cref="TreeViewItem"/> generated for a data object pulls its children from that path and templates each child
/// the same way (the by-type <see cref="DataTemplate"/> chain recurses for homogeneous trees; set the owning
/// control's <see cref="ItemsControl.ItemTemplate"/> to this template, or register it in resources keyed by
/// <see cref="DataTemplate.DataType"/>).
/// </summary>
public class HierarchicalDataTemplate : DataTemplate
{
    /// <summary>The property path on the data item to its child collection (e.g. <c>"Children"</c>; dotted paths walk
    /// properties). <c>null</c>/empty ⇒ a leaf node (no children).</summary>
    public string? ItemsSourcePath { get; set; }
}

/// <summary>Shared HierarchicalDataTemplate application for <see cref="TreeView"/> + <see cref="TreeViewItem"/>
/// (both generate <see cref="TreeViewItem"/> containers for data items).</summary>
internal static class TreeHierarchy
{
    /// <summary>Templates a generated <paramref name="container"/> for a data <paramref name="item"/>: the header is
    /// the item rendered via its (hierarchical) template, and the children come from the template's
    /// <see cref="HierarchicalDataTemplate.ItemsSourcePath"/>.</summary>
    // Realization is eager (the whole shown data tree builds at once — virtualization is the v2 item, #80). To keep a
    // cyclic or pathologically-deep data graph from recursing into an uncatchable StackOverflow, a node stops
    // recursing (renders as a leaf) when its data item already appears among its ancestors (a back-reference / cycle)
    // or its depth hits the cap.
    private const int MaxRealizationDepth = 128;

    public static void Apply(ItemsControl owner, TreeViewItem container, object? item)
    {
        var template = owner.ItemTemplate ?? ContentRealization.FindImplicitTemplate(owner, item);

        container.SetCurrentValue(HeaderedItemsControl.HeaderProperty, item);
        container.SetCurrentValue(HeaderedItemsControl.HeaderTemplateProperty, template);
        container.SetCurrentValue(ItemsControl.ItemTemplateProperty, owner.ItemTemplate); // propagate for recursion

        var children = template is HierarchicalDataTemplate { ItemsSourcePath: { Length: > 0 } path }
            ? ResolveChildren(item, path)
            : null;
        if (children is not null && (IsAncestorData(container, item) || DepthAtLeast(container, MaxRealizationDepth)))
            children = null; // cycle / too deep ⇒ stop the eager recursion (render as a leaf)

        container.SetCurrentValue(ItemsControl.ItemsSourceProperty, children);
    }

    /// <summary>Unhooks the templated state when a container is recycled / cleared.</summary>
    public static void Clear(TreeViewItem container)
    {
        // ItemsSource FIRST: it tears the children down once. Clearing ItemTemplate first would fire ResetContainers
        // while ItemsSource is still set, re-realizing the whole (deep) subtree only to drop it again (audit CD-P2H-1).
        container.ClearValue(ItemsControl.ItemsSourceProperty);
        container.ClearValue(HeaderedItemsControl.HeaderProperty);
        container.ClearValue(HeaderedItemsControl.HeaderTemplateProperty);
        container.ClearValue(ItemsControl.ItemTemplateProperty);
    }

    // True when the container's data item already appears on an ancestor (a cyclic / back-referencing graph).
    private static bool IsAncestorData(TreeViewItem container, object? item)
    {
        for (UIElement? node = container.LogicalParent; node is not null; node = node.LogicalParent)
            if (node is TreeViewItem ancestor && ReferenceEquals(ancestor.DataContext, item))
                return true;
        return false;
    }

    private static bool DepthAtLeast(TreeViewItem container, int depth)
    {
        var count = 0;
        for (UIElement? node = container.LogicalParent; node is not null; node = node.LogicalParent)
            if (node is TreeViewItem && ++count >= depth)
                return true;
        return false;
    }

    // Resolve a dotted property path on the data item to its child collection (a string is NOT a child collection).
    private static IEnumerable? ResolveChildren(object? item, string path)
    {
        var current = item;
        foreach (var segment in path.Split('.'))
        {
            if (current is null)
                return null;
            var property = current.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
                return null;
            current = property.GetValue(current);
        }

        return current is string ? null : current as IEnumerable;
    }
}
