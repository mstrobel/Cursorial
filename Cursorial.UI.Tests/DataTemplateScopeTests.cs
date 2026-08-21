using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI;

/// <summary>
/// Data-template name-scope chaining (PD24): template content is authoring-equivalent to inline
/// elements, so its scope chains LAZILY to the host's enclosing scopes on a local miss — while a
/// plain (control-template) scope stays isolated (BD21). The chain is what lets ElementName /
/// x:Reference anchors inside data templates reach page-level names.
/// </summary>
public class DataTemplateScopeTests
{
    [Fact] // local hit wins; local miss chains through the host to the document scope
    public void DataTemplateScope_ChainsToEnclosingScopes()
    {
        var root = new StackPanel();
        var docScope = new NameScopeDictionary();
        NameScope.SetNameScope(root, docScope);
        var outer = new TextBox();
        docScope.Register("Outer", outer);
        root.Children.Add(outer);

        var host = new ContentPresenter();
        root.Children.Add(host);

        TextBlock? inner = null;
        var template = new DataTemplate
        {
            Content = new FuncTemplateContent(ctx =>
            {
                inner = new TextBlock();
                ctx.RegisterName("Inner", inner);
                return inner;
            })
        };

        var built = template.Build(data: null, host: host);
        var scope = NameScope.GetNameScope(built);
        Assert.NotNull(scope);
        Assert.Same(inner, scope.Find("Inner"));  // local first — nearest scope wins
        Assert.Same(outer, scope.Find("Outer"));  // miss chains: host → enclosing document scope
        Assert.Null(scope.Find("Nowhere"));
    }

    [Fact] // without a host the scope stays isolated — the pre-chain contract is unchanged
    public void DataTemplateScope_WithoutHost_StaysIsolated()
    {
        var root = new StackPanel();
        var docScope = new NameScopeDictionary();
        NameScope.SetNameScope(root, docScope);
        docScope.Register("Outer", new TextBox());

        var template = new DataTemplate { Content = new FuncTemplateContent(_ => new TextBlock()) };
        var built = template.Build(data: null);
        Assert.Null(NameScope.GetNameScope(built)!.Find("Outer"));
    }
}
