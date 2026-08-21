using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §7 — ContentControl + ContentPresenter + auto-aliasing (C0; rows C141–C152). A control whose
/// template hosts a <see cref="ContentPresenter"/> exercises the auto-alias read-through and the
/// content-realization chain.
/// </summary>
public sealed class Section07_ContentPresenter
{
    // A ContentControl whose template hosts an auto-aliasing presenter (neither Content nor
    // ContentTemplate set on the presenter).
    private sealed class HostControl : ContentControl
    {
        public ContentPresenter? Presenter => GetTemplatePart<ContentPresenter>("PART_CP");

        public HostControl()
        {
            Template = new ControlTemplate(ctx =>
            {
                var cp = new ContentPresenter();
                ctx.RegisterName("PART_CP", cp);
                return cp;
            });
        }
    }

    private static UIHeadlessHost Attach(UIElement root)
    {
        var host = UIHeadlessHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    [Fact] // C141
    public void C141_ContentControlAndPresenterMetadata()
    {
        Assert.True(ContentControl.ContentProperty.GetEffects(typeof(ContentControl)).HasFlag(PropertyEffects.AffectsMeasure));
        Assert.True(ContentControl.ContentTemplateProperty.GetEffects(typeof(ContentControl)).HasFlag(PropertyEffects.AffectsMeasure));
        Assert.True(ContentPresenter.ContentProperty.GetEffects(typeof(ContentPresenter)).HasFlag(PropertyEffects.AffectsMeasure));
        Assert.True(ContentPresenter.ContentTemplateProperty.GetEffects(typeof(ContentPresenter)).HasFlag(PropertyEffects.AffectsMeasure));
        Assert.False(new ContentPresenter().RecognizesAccessKey); // default false
    }

    [Fact] // C142
    public void C142_AutoAliasReadsThroughTemplatedParentContent()
    {
        var control = new HostControl { Content = "hello" };
        using var host = Attach(control);

        var cp = control.Presenter!;
        Assert.False(cp.IsSet(ContentPresenter.ContentProperty)); // no installed binding, no frame
        var text = Assert.IsType<TextBlock>(cp.Child);
        Assert.Equal("hello", text.Text);
    }

    [Fact] // C143
    public void C143_AutoAliasObserverReRealizesOnParentContentChange()
    {
        var control = new HostControl { Content = "hello" };
        using var host = Attach(control);

        control.Content = "world";
        host.RunFrame();

        var text = Assert.IsType<TextBlock>(control.Presenter!.Child);
        Assert.Equal("world", text.Text);
    }

    [Fact] // C144
    public void C144_ExplicitPresenterValueStopsReadThrough()
    {
        var control = new HostControl { Content = "hello" };
        using var host = Attach(control);
        var cp = control.Presenter!;

        cp.Content = "explicit"; // a local presenter value → IsSet flips, read-through stops
        host.RunFrame();
        Assert.True(cp.IsSet(ContentPresenter.ContentProperty));
        Assert.Equal("explicit", Assert.IsType<TextBlock>(cp.Child).Text);

        control.Content = "world"; // a later parent change no longer reaches the presenter
        host.RunFrame();
        Assert.Equal("explicit", Assert.IsType<TextBlock>(cp.Child).Text);
    }

    [Fact] // C145
    public void C145_AutoAliasObserverTornDownOnReTemplate()
    {
        var control = new HostControl { Content = "hello" };
        using var host = Attach(control);
        var oldPresenter = control.Presenter!;

        // Re-template: the old presenter detaches; its auto-alias observers tear down with the instance.
        control.Template = new ControlTemplate(ctx =>
        {
            var cp = new ContentPresenter();
            ctx.RegisterName("PART_CP", cp);
            return cp;
        });
        host.RunFrame();

        Assert.Null(oldPresenter.VisualParent); // old presenter gone
        Assert.NotSame(oldPresenter, control.Presenter);
        // The new presenter reads through to the still-set Content.
        Assert.Equal("hello", Assert.IsType<TextBlock>(control.Presenter!.Child).Text);
    }

    [Fact] // C146
    public void C146_HeaderedContentControlShape()
    {
        var hcc = new HeaderedContentControl { Header = "H", Content = "C" };
        Assert.IsAssignableFrom<ContentControl>(hcc);
        Assert.Equal("H", hcc.Header);
        Assert.True(HeaderedContentControl.HeaderProperty.GetEffects(typeof(HeaderedContentControl)).HasFlag(PropertyEffects.AffectsMeasure));
    }

    [Fact] // C147
    public void C147_RecursionGuard()
    {
        // A DataTemplate whose Build re-enters the presenter's realization (degenerate self-reference)
        // must not infinitely expand — the recursion guard short-circuits the nested EnsureChild.
        ContentPresenter? cp = null;
        var template = new DataTemplate
        {
            DataType = typeof(object),
            // ReSharper disable AccessToModifiedClosure
            Content = new FuncTemplateContent(_ =>
            {
                // Re-enter realization during the build (the degenerate path the guard protects).
                cp!.InvalidateMeasure();
                return new TextBlock("leaf");
            })
            // ReSharper restore AccessToModifiedClosure
        };
        cp = new ContentPresenter { Content = new object(), ContentTemplate = template };

        var host = UIHeadlessHost.Create();
        host.ShowRoot(cp);
        host.RunFrame(); // must not stack-overflow — the guard caps re-entry
        Assert.IsType<TextBlock>(cp.Child);
    }

    [Fact] // C148
    public void C148_ElementContentPassthrough()
    {
        var element = new TextBlock("direct");
        var cp = new ContentPresenter { Content = element };
        using var host = Attach(cp);

        Assert.Same(element, cp.Child);         // the element itself
        Assert.Same(cp, element.VisualParent);  // visual child of the presenter
        Assert.Same(cp, element.LogicalParent); // logical child (free-standing presenter adopts)
    }

    [Fact] // C149
    public void C149_PlainStringNoAccessKeyFold()
    {
        var cp = new ContentPresenter { RecognizesAccessKey = false, Content = "snake_case.txt" };
        using var host = Attach(cp);

        var text = Assert.IsType<TextBlock>(cp.Child);
        Assert.Equal("snake_case.txt", text.Text); // underscores literal, not folded
    }

    [Fact] // C150
    public void C150_PlainStringRecognizesAccessKeyFolds()
    {
        var cp = new ContentPresenter { RecognizesAccessKey = true, Content = "_Save" };
        using var host = Attach(cp);

        var atp = Assert.IsType<AccessTextPresenter>(cp.Child);
        Assert.Equal("Save", atp.Text.Text);
        Assert.Equal('S', atp.Text.Key);
    }

    [Fact] // C151
    public void C151_AccessTextContentAlwaysAccessTextPresenter()
    {
        var cp = new ContentPresenter { RecognizesAccessKey = false, Content = new AccessText("File", 'F', 0) };
        using var host = Attach(cp);

        var atp = Assert.IsType<AccessTextPresenter>(cp.Child);
        Assert.Equal("File", atp.Text.Text);
    }

    [Fact] // C152
    public void C152_NonStringFallsBackToTextBlock()
    {
        var cp = new ContentPresenter { Content = 42 };
        using var host = Attach(cp);

        var text = Assert.IsType<TextBlock>(cp.Child);
        Assert.Equal("42", text.Text); // Convert.ToString(42), CurrentCulture
    }

    [Fact] // ContentStringFormat applies to the text fallback (explicit on the presenter)
    public void ContentStringFormat_FormatsTextFallback()
    {
        var cp = new ContentPresenter { Content = 42, ContentStringFormat = "V={0}" };
        using var host = Attach(cp);

        Assert.Equal("V=42", Assert.IsType<TextBlock>(cp.Child).Text);
    }

    [Fact] // ContentStringFormat auto-aliases from the templated ContentControl (the default-theme path)
    public void ContentStringFormat_AutoAliasesFromTemplatedParent()
    {
        var control = new HostControl { Content = 42, ContentStringFormat = "V={0}" };
        using var host = Attach(control);

        var cp = control.Presenter!;
        Assert.False(cp.IsSet(ContentPresenter.ContentStringFormatProperty)); // read-through, no installed value
        Assert.Equal("V=42", Assert.IsType<TextBlock>(cp.Child).Text);
    }

    [Fact] // a live ContentStringFormat change on the templated parent re-renders the fallback
    public void ContentStringFormat_LiveChange_ReRenders()
    {
        var control = new HostControl { Content = 42, ContentStringFormat = "V={0}" };
        using var host = Attach(control);
        Assert.Equal("V=42", Assert.IsType<TextBlock>(control.Presenter!.Child).Text);

        control.ContentStringFormat = "[{0}]";
        host.RunFrame();
        Assert.Equal("[42]", Assert.IsType<TextBlock>(control.Presenter!.Child).Text);
    }

    [Fact] // a MALFORMED ContentStringFormat degrades to the unformatted text — never a thrown FormatException
           // escaping MeasureOverride (which would kill the frame loop — audit finding)
    public void ContentStringFormat_Malformed_DegradesInsteadOfThrowing()
    {
        var cp = new ContentPresenter { Content = 42, ContentStringFormat = "Total: {0" }; // unbalanced brace
        string? text = null;
        var ex = Record.Exception(() =>
        {
            using var host = Attach(cp);
            text = Assert.IsType<TextBlock>(cp.Child).Text;
        });
        Assert.Null(ex);                                                  // no crash
        Assert.Equal("42", text);     // fell back to the unformatted content
    }

    [Fact] // ContentStringFormat is ignored when a DataTemplate handles the content (WPF parity)
    public void ContentStringFormat_IgnoredUnderTemplate()
    {
        var cp = new ContentPresenter
        {
            Content = 42,
            ContentStringFormat = "V={0}",
            ContentTemplate = new DataTemplate { Content = new FuncTemplateContent(_ => new TextBlock { Text = "templated" }) },
        };
        using var host = Attach(cp);

        Assert.Equal("templated", Assert.IsType<TextBlock>(cp.Child).Text); // the template wins; format unused
    }

#if DEBUG
    [Fact] // regression: a fallback TextBlock's live TextWrapping binding (Source = the presenter) must be torn down
           // when the presenter rebuilds its child on a content change — else each discarded TextBlock leaks a strong
           // observer onto the presenter (an unbounded per-change leak on any presenter showing changing string content).
    public void FallbackChildRebuild_TearsDownDiscardedChildBindings()
    {
        var cp = new ContentPresenter { Content = "seed" };
        using var host = Attach(cp);

        Cursorial.UI.Data.BindingLeakTracker.ResetForTests();
        for (var i = 0; i < 8; i++)
        {
            cp.Content = $"c{i}"; // new string identity ⇒ the fallback TextBlock is rebuilt (a fresh binding installed)
            host.RunFrame();
        }

        // Swap to null content so the LAST fallback TextBlock is discarded too. Every rebuilt fallback child's
        // TextWrapping binding was disposed on discard — none leak onto the presenter.
        cp.Content = null;
        host.RunFrame();

        Assert.DoesNotContain(Cursorial.UI.Data.BindingLeakTracker.Sweep(), l => l.Path.Contains("TextWrapping"));
        Cursorial.UI.Data.BindingLeakTracker.ResetForTests();
    }
#endif
}
