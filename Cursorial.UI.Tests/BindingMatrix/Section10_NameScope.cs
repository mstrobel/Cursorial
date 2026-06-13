using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Testing;
using Xunit;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>
/// Binding matrix §10 — ElementName, NameScope, and FindName (B121–B130). <c>ElementName</c> resolves
/// through <see cref="NameScope.FindEnclosing"/>; <see cref="UIElement.FindName"/> is S2-owned. Scope
/// producers (Fork C / S8 / DataTemplate) are stubbed with manual <see cref="NameScope.SetNameScope"/>
/// and template-scope stamping.
/// </summary>
public class Section10_NameScope
{
    public Section10_NameScope()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    [Fact]
    public void B121_ElementName_ResolvesViaScope_PathAgainstNamedElement()
    {
        using var host = UITestHost.Create();
        var root = new StackPanel();
        var a = new BindWidget();
        var b = new BindWidget { Num = 17 };
        root.Children.Add(a);
        root.Children.Add(b);

        var scope = new NameScopeDictionary();
        scope.Register("editor", b);
        NameScope.SetNameScope(root, scope);

        host.ShowRoot(root);
        host.RunFrame();

        Assert.Same(b, NameScope.FindEnclosing(a)?.Find("editor"));

        a.SetBinding(BindWidget.TextProperty, new Binding("Num") { ElementName = "editor" });
        Assert.Equal("17", a.Text);
    }

    [Fact]
    public void B122_ElementName_NotAttached_ParksThenResolvesOnAttach()
    {
        using var host = UITestHost.Create();
        var root = new StackPanel();
        var b = new BindWidget { Num = 5 };
        root.Children.Add(b);

        var scope = new NameScopeDictionary();
        scope.Register("editor", b);
        NameScope.SetNameScope(root, scope);

        var a = new BindWidget();
        // Install while `a` is not yet attached: parks SourceMissing, no trace yet.
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        var expr = a.SetBinding(BindWidget.TextProperty, new Binding("Num") { ElementName = "editor" });
        Assert.Equal(BindingStatus.SourceMissing, expr.Status);

        host.ShowRoot(root);
        root.Children.Add(a); // attach
        host.RunFrame();

        Assert.Equal(BindingStatus.Active, expr.Status);
        Assert.Equal("5", a.Text);
    }

    [Fact]
    public void B123_ElementName_NeverRegistered_TracesAfterAttach()
    {
        using var host = UITestHost.Create();
        var root = new StackPanel();
        var a = new BindWidget();
        root.Children.Add(a);
        NameScope.SetNameScope(root, new NameScopeDictionary()); // empty scope

        host.ShowRoot(root);
        host.RunFrame();

        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        var expr = a.SetBinding(BindWidget.TextProperty, new Binding("Num") { ElementName = "ghost" });

        Assert.Equal(BindingStatus.PathError, expr.Status);
        Assert.Contains(BindingDiagnostics.RecentEvents, e => e.Kind == BindingFailureKind.NameNotFound);
    }

    [Fact]
    public void B124_GuardedWalk_TemplatePart_SeesTemplateNames()
    {
        // A template part (a.TemplatedParent == ctrl) consults the TEMPLATE scope on ctrl first.
        var ctrl = new BindWidget();
        var templateScope = new NameScopeDictionary();
        var partTarget = new BindWidget();
        templateScope.Register("partName", partTarget);
        NameScope.SetTemplateNameScope(ctrl, templateScope);

        var documentScope = new NameScopeDictionary();
        var documentTarget = new BindWidget();
        documentScope.Register("partName", documentTarget);
        NameScope.SetNameScope(ctrl, documentScope);

        var part = new BindWidget();
        part.StampTemplatedParent(ctrl);
        ctrl.AddChild(part);

        Assert.Same(partTarget, NameScope.FindEnclosing(part)?.Find("partName")); // template names visible to parts
    }

    [Fact]
    public void B125_GuardedWalk_DocumentChild_FailsGuard_ResolvesDocumentNames()
    {
        // A document content child (d.LogicalParent == ctrl but d.TemplatedParent != ctrl) fails the
        // guard at ctrl ⇒ the walk continues to the DOCUMENT scope. Part names invisible to content.
        var ctrl = new BindWidget();
        var templateScope = new NameScopeDictionary();
        templateScope.Register("partName", new BindWidget());
        NameScope.SetTemplateNameScope(ctrl, templateScope);

        var documentScope = new NameScopeDictionary();
        var documentTarget = new BindWidget();
        documentScope.Register("partName", documentTarget);
        NameScope.SetNameScope(ctrl, documentScope);

        var d = new BindWidget(); // d.TemplatedParent stays null ⇒ document content child
        ctrl.AddChild(d);

        Assert.Same(documentTarget, NameScope.FindEnclosing(d)?.Find("partName")); // document name, never the part
    }

    [Fact]
    public void B126_DataTemplateScope_ItemNames_ShadowOuter()
    {
        // A DataTemplate realization attaches a fresh scope to the item root's DOCUMENT slot; item
        // names are subtree-visible and shadow outer names (no barrier — the document slot on the item root).
        var outer = new BindWidget();
        var outerScope = new NameScopeDictionary();
        outerScope.Register("name", new BindWidget());
        NameScope.SetNameScope(outer, outerScope);

        var itemRoot = new BindWidget();
        var itemScope = new NameScopeDictionary();
        var itemTarget = new BindWidget();
        itemScope.Register("name", itemTarget);
        NameScope.SetNameScope(itemRoot, itemScope);
        outer.AddChild(itemRoot);

        var itemChild = new BindWidget();
        itemRoot.AddChild(itemChild);

        Assert.Same(itemTarget, NameScope.FindEnclosing(itemChild)?.Find("name")); // item names shadow outer
    }

    [Fact]
    public void B127_FindName_DocumentRegistered_ReturnsElement()
    {
        var window = new BindWidget();
        var scope = new NameScopeDictionary();
        var toast = new BindWidget();
        scope.Register("toast", toast);
        NameScope.SetNameScope(window, scope);

        Assert.Same(toast, window.FindName("toast")); // = FindEnclosing(window)?.Find("toast")
    }

    [Fact]
    public void B128_FindName_Unregistered_ReturnsNull()
    {
        var window = new BindWidget();
        NameScope.SetNameScope(window, new NameScopeDictionary());

        Assert.Null(window.FindName("nope")); // lookup miss, not a throw
    }

    [Fact]
    public void B129_ElementName_InsideTemplateInstance_FindsSiblingPart()
    {
        using var host = UITestHost.Create();
        var rootPanel = new StackPanel();
        var ctrl = new BindWidget();
        rootPanel.Children.Add(ctrl);

        // A template scope on ctrl registering two parts; a document scope hiding a different "sibling".
        var templateScope = new NameScopeDictionary();
        var sibling = new BindWidget { Num = 33 };
        templateScope.Register("sibling", sibling);
        NameScope.SetTemplateNameScope(ctrl, templateScope);

        var documentScope = new NameScopeDictionary();
        documentScope.Register("sibling", new BindWidget { Num = 99 });
        NameScope.SetNameScope(rootPanel, documentScope);

        var part = new BindWidget();
        part.StampTemplatedParent(ctrl);
        ctrl.AddChild(part);

        host.ShowRoot(rootPanel);
        host.RunFrame();

        part.SetBinding(BindWidget.TextProperty, new Binding("Num") { ElementName = "sibling" });
        Assert.Equal("33", part.Text); // the template part, not the document name
    }

    [Fact]
    public void B130_ElementName_AnchorReResolvesOnDetach_VanishedName_NoStalePush()
    {
        // The expression re-resolves on the ANCHOR's detach/attach events (WPF — ElementName follows
        // the anchor's tree life, not the named element's). When the anchor leaves the scope that
        // resolved the name, re-resolution finds the name gone ⇒ no stale push.
        using var host = UITestHost.Create();
        var root = new StackPanel();
        var scoped = new StackPanel(); // carries the scope registering "editor"
        var a = new BindWidget();
        var b = new BindWidget { Num = 7 };
        scoped.Children.Add(a);
        scoped.Children.Add(b);
        root.Children.Add(scoped);

        var scope = new NameScopeDictionary();
        scope.Register("editor", b);
        NameScope.SetNameScope(scoped, scope);

        host.ShowRoot(root);
        host.RunFrame();

        a.SetBinding(BindWidget.TextProperty, new Binding("Num") { ElementName = "editor" });
        Assert.Equal("7", a.Text);

        // Reparent the anchor out of the scope: re-resolution finds "editor" unreachable.
        scoped.Children.Remove(a);
        root.Children.Add(a); // root has no scope registering "editor"
        host.RunFrame();

        b.Num = 100; // the old source mutates; the re-resolved binding no longer points at it
        Assert.NotEqual("100", a.Text);
    }
}
