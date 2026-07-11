using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §8 — DataTemplate lookup chain (the ItemsControl-reused seam) (C0; rows C153–C160). Exercises the
/// explicit-template win, the implicit <c>DataTemplateKey(t)</c> walk (incl. the templated-parent hop
/// and base-class probe), subtree reuse vs rebuild, and <c>DataTemplate.Build</c> directly.
/// </summary>
public sealed class Section08_DataTemplateChain
{
    public class Vm
    {
        public string? Name { get; set; }
    }

    public sealed class DerivedVm : Vm { }

    // A DataTemplate that builds a TextBlock bound to the data's Name (via the root's DataContext).
    private static DataTemplate VmTemplate(string marker) => new()
    {
        DataType = typeof(Vm),
        Content = new FuncTemplateContent(_ => new TextBlock(marker))
    };

    private static UIHeadlessHost Attach(UIElement root)
    {
        var host = UIHeadlessHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    [Fact] // C153
    public void C153_ExplicitContentTemplateWins()
    {
        var vm = new Vm { Name = "Ada" };
        var template = VmTemplate("explicit");
        var cp = new ContentPresenter { Content = vm, ContentTemplate = template };
        using var host = Attach(cp);

        var built = Assert.IsType<TextBlock>(cp.Child);
        Assert.Equal("explicit", built.Text);
        Assert.Same(vm, built.DataContext);   // DataContext == data
        Assert.Null(built.TemplatedParent);   // data content, TemplatedParent null (CD18)
    }

    [Fact] // C154
    public void C154_ImplicitDataTemplateKeyWalkFromAppResources()
    {
        var cp = new ContentPresenter { Content = new Vm { Name = "x" } };
        var host = UIHeadlessHost.Create();
        host.Application.Resources[new DataTemplateKey(typeof(Vm))] = VmTemplate("implicit");
        host.ShowRoot(cp);
        host.RunFrame();

        Assert.Equal("implicit", Assert.IsType<TextBlock>(cp.Child).Text);
    }

    [Fact] // C155
    public void C155_TemplatedParentHopFindsTemplate()
    {
        // A presenter that is a template part (LogicalParent == null, TemplatedParent == owner). The
        // implicit walk hops to the owner and continues up the owner's logical chain.
        var control = new TemplatePartHostControl { Content = new Vm { Name = "x" } };
        // The template resource lives on the owner's Resources (reached via the templated-parent hop).
        control.Resources[new DataTemplateKey(typeof(Vm))] = VmTemplate("hopped");

        using var host = Attach(control);
        Assert.Equal("hopped", Assert.IsType<TextBlock>(control.Presenter!.Child).Text);
    }

    private sealed class TemplatePartHostControl : ContentControl
    {
        public ContentPresenter? Presenter => GetTemplatePart<ContentPresenter>("PART_CP");

        public TemplatePartHostControl()
        {
            Template = new ControlTemplate(ctx =>
            {
                var cp = new ContentPresenter();
                ctx.RegisterName("PART_CP", cp);
                return cp;
            });
        }
    }

    [Fact] // C156
    public void C156_BaseClassProbeFirstHitWins()
    {
        var cp = new ContentPresenter { Content = new DerivedVm { Name = "x" } };
        var host = UIHeadlessHost.Create();
        // Only a Vm template exists; the runtime type DerivedVm probes, then base Vm — finds the Vm one.
        host.Application.Resources[new DataTemplateKey(typeof(Vm))] = VmTemplate("base");
        host.ShowRoot(cp);
        host.RunFrame();

        Assert.Equal("base", Assert.IsType<TextBlock>(cp.Child).Text);
    }

    [Fact] // C157
    public void C157_SameTemplateIdentityReusesSubtree()
    {
        var template = VmTemplate("reused");
        var cp = new ContentPresenter { Content = new Vm { Name = "1" }, ContentTemplate = template };
        using var host = Attach(cp);
        var first = cp.Child;

        cp.Content = new Vm { Name = "2" }; // same type, same resolved template identity
        host.RunFrame();
        Assert.Same(first, cp.Child); // reused — only DataContext updated
        Assert.Equal("2", ((Vm)cp.Child!.DataContext!).Name);
    }

    [Fact] // C158
    public void C158_TemplateIdentityChangeRebuilds()
    {
        var cp = new ContentPresenter { Content = new Vm { Name = "1" }, ContentTemplate = VmTemplate("t1") };
        using var host = Attach(cp);
        var first = cp.Child;

        cp.ContentTemplate = VmTemplate("t2"); // a different DataTemplate resolves
        host.RunFrame();
        Assert.NotSame(first, cp.Child);
        Assert.Equal("t2", Assert.IsType<TextBlock>(cp.Child).Text);
    }

    [Fact] // C159
    public void C159_DataTemplateBuildDirectly()
    {
        var vm = new Vm { Name = "Grace" };
        var template = VmTemplate("direct");
        var root = template.Build(vm);

        Assert.Same(vm, root.DataContext);                          // DataContext = data
        Assert.Null(root.TemplatedParent);                          // stays null
        Assert.NotNull(NameScope.GetNameScope(root));               // a fresh document namescope attached
    }

    [Fact] // C160
    public void C160_DataTemplateKeyAndTypeKeyAreDistinct()
    {
        var dict = new ResourceDictionary();
        dict[new DataTemplateKey(typeof(Vm))] = VmTemplate("dtk");
        dict[typeof(Vm)] = "type-key-value";

        Assert.True(dict.TryGetValue(new DataTemplateKey(typeof(Vm)), out var dtkValue));
        Assert.IsType<DataTemplate>(dtkValue);
        Assert.True(dict.TryGetValue(typeof(Vm), out var typeValue));
        Assert.Equal("type-key-value", typeValue); // collision-free (DataTemplateKey value vs Type reference)
    }
}
