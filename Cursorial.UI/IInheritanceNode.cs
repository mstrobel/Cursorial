namespace Cursorial.UI;

/// <summary>
/// The inheritance-tree seam (design doc §2.3 / proposal §3.5): exposes the parent link the
/// property system's lazy-read walk follows. <see cref="UIObject"/> implements it over the pointer
/// installed by <see cref="UIObject.SetInheritanceParent"/>; the element tree (S1) wires that
/// pointer on every attach / detach / reparent (<c>InheritanceParent = LogicalParent ??
/// VisualParent</c>), and tests build fake trees by chaining plain <see cref="UIObject"/>s.
/// </summary>
public interface IInheritanceNode
{
    /// <summary>The node values inherit from, or <see langword="null"/> at a tree root.</summary>
    UIObject? GetInheritanceParent();
}
