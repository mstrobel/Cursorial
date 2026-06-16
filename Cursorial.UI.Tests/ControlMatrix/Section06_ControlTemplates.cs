using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §6 — Control base + ControlTemplate + TemplateInstance + parts (C0; rows C118–C140). One test per
/// row, namespace per the §17 contract. Builds run on the calling (UI) thread via
/// <see cref="UITestHost"/>; template-only rows attach a control under a host root and step a frame.
/// </summary>
public sealed class Section06_ControlTemplates
{
    // ───────────────────────────── helpers ─────────────────────────────

    private sealed class TestControl : ContentControl
    {
        public int ApplyCount;
        public int DetachCount;
        public Action<TemplateInstance>? DetachingHook;
        public Action? ApplyHook;

        protected override void OnApplyTemplate()
        {
            ApplyCount++;
            ApplyHook?.Invoke();
        }

        protected override void OnTemplateDetaching(TemplateInstance old)
        {
            DetachCount++;
            DetachingHook?.Invoke(old);
        }

        public new T? GetTemplatePart<T>(string name) where T : UIElement => base.GetTemplatePart<T>(name);
    }

    [TemplatePart("PART_T", typeof(Border), IsRequired = true)]
    private sealed class RequiredPartControl : Control
    {
    }

    [TemplatePart("PART_Opt", typeof(Border))]
    private sealed class OptionalPartControl : Control
    {
        public Border? Part => GetTemplatePart<Border>("PART_Opt");
    }

    private sealed class TargetTypedControl : Control { }

    private static ControlTemplate BorderWithPresenter() => new(ctx =>
    {
        var border = new Border();
        var presenter = new ContentPresenter();
        ctx.RegisterName("PART_Presenter", presenter);
        border.Child = presenter;
        return border;
    });

    private static UITestHost AttachControl(Control control)
    {
        var host = UITestHost.Create();
        host.ShowRoot(control);
        host.RunFrame();
        return host;
    }

    // ───────────────────────────── 6.1 Control properties + ControlThemeKey ─────────────────────────────

    [Fact] // C118
    public void C118_ControlPropertyMetadata()
    {
        Assert.True(Control.TemplateProperty.GetEffects(typeof(Control)).HasFlag(PropertyEffects.AffectsMeasure));
        Assert.True(Control.BackgroundProperty.GetEffects(typeof(Control)).HasFlag(PropertyEffects.AffectsRender));
        Assert.False(Control.BackgroundProperty.Inherits);
        Assert.True(Control.ForegroundProperty.Inherits);
        Assert.True(Control.ForegroundProperty.GetEffects(typeof(Control)).HasFlag(PropertyEffects.AffectsRender));
        Assert.True(Control.BorderPenProperty.GetEffects(typeof(Control)).HasFlag(PropertyEffects.AffectsRender));
        Assert.True(Control.PaddingProperty.GetEffects(typeof(Control)).HasFlag(PropertyEffects.AffectsMeasure));
    }

    [Fact] // C119
    public void C119_TextElementForegroundInheritsDownTree()
    {
        var root = new TestControl { Template = BorderWithPresenter() };
        var child = new TextBlock("hi");
        // Place the TextBlock under the control's content presenter via Content.
        root.Content = child;

        using var host = AttachControl(root);

        TextElement.SetForeground(root, ControlMatrixFixture.Vbrush);
        host.RunFrame();

        Assert.Same(ControlMatrixFixture.Vbrush, child.GetValue(TextElement.ForegroundProperty));
    }

    [Fact] // C120
    public void C120_ControlThemeKeyDefaultsToRuntimeType()
    {
        var control = new TestControl();
        IControlThemeHost host = control;
        Assert.Equal(typeof(TestControl), host.ControlThemeKey);
    }

    // ───────────────────────────── 6.2 Instantiation + namescope + stamping ─────────────────────────────

    [Fact] // C121
    public void C121_InstantiateBuildsSubtreeAsVisualChild()
    {
        var control = new TestControl { Template = BorderWithPresenter() };
        using var host = AttachControl(control);

        Assert.NotNull(control.TemplateInstance);
        var root = control.TemplateInstance!.Root;
        Assert.IsType<Border>(root);
        Assert.Same(control, root.VisualParent);   // visual child
        Assert.Null(root.LogicalParent);            // NOT a logical child
        Assert.True(control.ApplyCount >= 1);       // OnApplyTemplate ran (after attach)
    }

    [Fact] // C122
    public void C122_TemplatedParentStampedAndNameScopeSet()
    {
        var control = new TestControl { Template = BorderWithPresenter() };
        using var host = AttachControl(control);

        var root = control.TemplateInstance!.Root;
        var presenter = control.GetTemplatePart<ContentPresenter>("PART_Presenter");
        Assert.NotNull(presenter);
        Assert.Same(control, root.TemplatedParent);
        Assert.Same(control, presenter.TemplatedParent);
        // The template name scope is set on the control.
        Assert.Same(control.TemplateInstance!.NameScope, NameScope.GetTemplateNameScope(control));
    }

    [Fact] // C123
    public void C123_ForeignTemplatedParentThrows()
    {
        var alien = new Border();
        alien.SetTemplatedParent(new TestControl()); // a foreign stamp (shared-subtree misuse)

        var control = new TestControl
        {
            Template = new ControlTemplate(_ => alien)
        };

        var host = UITestHost.Create();
        host.ShowRoot(control);
        Assert.Throws<InvalidOperationException>(host.RunFrame);
    }

    [Fact] // C124
    public void C124_TargetTypeMismatchThrows()
    {
        var template = new ControlTemplate(_ => new Border()) { TargetType = typeof(Button) };
        var control = new TargetTypedControl { Template = template };

        var host = UITestHost.Create();
        host.ShowRoot(control);
        Assert.Throws<InvalidOperationException>(host.RunFrame);
    }

    [Fact] // C125
    public void C125_SealedTemplateArmsResources()
    {
        var template = BorderWithPresenter();
        template.Resources["K"] = ControlMatrixFixture.Vbrush;
        Assert.False(template.IsSealed);

        var control = new TestControl { Template = template };
        using var host = AttachControl(control);

        Assert.True(template.IsSealed);          // instantiation sealed it
        Assert.True(template.Resources.IsSealed); // resources sealed as part of the template seal
    }

    [Fact] // C126
    public void C126_GetTemplatePartNullBeforeExpansionThenResolves()
    {
        var control = new TestControl { Template = BorderWithPresenter() };

        // Before any expansion: null.
        Assert.Null(control.GetTemplatePart<ContentPresenter>("PART_Presenter"));

        using var host = AttachControl(control);

        // After expansion: resolves in the template namescope.
        Assert.NotNull(control.GetTemplatePart<ContentPresenter>("PART_Presenter"));
    }

    [Fact] // C127
    public void C127_RequireControlPresentAndAbsent()
    {
        var control = new TestControl { Template = BorderWithPresenter() };
        using var host = AttachControl(control);
        var scope = control.TemplateInstance!.NameScope;

        Assert.NotNull(scope.RequireControl<ContentPresenter>("PART_Presenter"));
        Assert.Throws<InvalidOperationException>(() => scope.RequireControl<ContentPresenter>("PART_Absent"));
    }

    // ───────────────────────────── 6.3 [TemplatePart] validation ─────────────────────────────

    [Fact] // C128
    public void C128_RequiredPartCorrectTypeValidatesOk()
    {
        var control = new RequiredPartControl
        {
            Template = new ControlTemplate(ctx =>
            {
                var border = new Border();
                ctx.RegisterName("PART_T", border);
                return border;
            })
        };

        using var host = AttachControl(control);
        Assert.NotNull(control.TemplateInstance);
    }

    [Fact] // C129
    public void C129_WrongTypePartThrows()
    {
        var control = new RequiredPartControl
        {
            Template = new ControlTemplate(ctx =>
            {
                var wrong = new TextBlock("x");
                ctx.RegisterName("PART_T", wrong); // declared Border, provided TextBlock
                return wrong;
            })
        };

        var host = UITestHost.Create();
        host.ShowRoot(control);
        var ex = Assert.Throws<InvalidOperationException>(host.RunFrame);
        Assert.Contains("PART_T", ex.Message);
        Assert.Contains("Border", ex.Message);
        Assert.Contains("TextBlock", ex.Message);
    }

    [Fact] // C130
    public void C130_RequiredPartMissingThrows()
    {
        var control = new RequiredPartControl
        {
            Template = new ControlTemplate(_ => new Border()) // PART_T omitted
        };

        var host = UITestHost.Create();
        host.ShowRoot(control);
        var ex = Assert.Throws<InvalidOperationException>(host.RunFrame);
        Assert.Contains("PART_T", ex.Message);
    }

    [Fact] // C131
    public void C131_OptionalPartMissingDegrades()
    {
        var control = new OptionalPartControl
        {
            Template = new ControlTemplate(_ => new Border()) // PART_Opt omitted (optional)
        };

        using var host = AttachControl(control);
        Assert.Null(control.Part); // null, no throw — degrades gracefully
    }

    [Fact] // C132
    public void C132_ValidationBeforeOnApplyTemplateAndAttach()
    {
        var applyRan = false;
        var control = new TestControlWithApplyFlag(() => applyRan = true)
        {
            Template = new ControlTemplate(ctx =>
            {
                var wrong = new TextBlock("x");
                ctx.RegisterName("PART_T", wrong); // wrong type for the required part
                return wrong;
            })
        };

        var host = UITestHost.Create();
        host.ShowRoot(control);
        Assert.Throws<InvalidOperationException>(host.RunFrame);
        Assert.False(applyRan);                   // OnApplyTemplate never ran (throw before step 6)
        Assert.Null(control.TemplateInstance?.Root.VisualParent); // never visually attached
    }

    [TemplatePart("PART_T", typeof(Border), IsRequired = true)]
    private sealed class TestControlWithApplyFlag(Action onApply) : Control
    {
        protected override void OnApplyTemplate() => onApply();
    }

    // ───────────────────────────── 6.4 OnApplyTemplate / re-application / Detach ─────────────────────────────

    [Fact] // C133
    public void C133_TemplateChangeRunsDetachSequence()
    {
        var control = new TestControl { Template = BorderWithPresenter() };
        using var host = AttachControl(control);

        var firstInstance = control.TemplateInstance!;
        var firstRoot = firstInstance.Root;
        var applyBefore = control.ApplyCount;

        control.Template = BorderWithPresenter(); // T2
        host.RunFrame();

        Assert.Equal(1, control.DetachCount);               // OnTemplateDetaching ran
        Assert.True(firstInstance.IsDetached);              // old.Detach() ran
        Assert.Null(firstRoot.VisualParent);                // old root removed
        Assert.NotSame(firstInstance, control.TemplateInstance);
        Assert.True(control.ApplyCount > applyBefore);      // OnApplyTemplate re-ran for T2
    }

    [Fact] // C134
    public void C134_ReentrantTemplateSetDefersNoRecurse()
    {
        var reentered = false;
        var control = new TestControl
                      {
                          Template = BorderWithPresenter()
                      };
        control.ApplyHook = () =>
        {
            if (!reentered)
            {
                reentered = true;
                control.Template = BorderWithPresenter(); // re-set from inside OnApplyTemplate
            }
        };

        using var host = AttachControl(control);
        host.RunUntilIdle();

        // No stack overflow; the deferred re-expansion settled to a valid instance.
        Assert.NotNull(control.TemplateInstance);
        Assert.True(reentered);
    }

    [Fact] // C135
    public void C135_NullTemplateNoVisualChild()
    {
        var diagnostics = new List<ControlDiagnosticEvent>();
        ControlDiagnostics.DiagnosticRaised += Record;
        try
        {
            var control = new TestControl { Template = null };
            using var host = AttachControl(control);

            Assert.Null(control.TemplateInstance);
            Assert.Empty(control.VisualChildrenForTemplateStamp);
#if DEBUG
            Assert.Contains(diagnostics, d => d.Kind == ControlDiagnosticKind.NoTemplate);
#endif
        }
        finally
        {
            ControlDiagnostics.DiagnosticRaised -= Record;
        }

        void Record(ControlDiagnosticEvent e) => diagnostics.Add(e);
    }

    [Fact] // C136
    public void C136_DetachRetractsTemplateLayerFramesByCookie()
    {
        // A Template-layer style frame armed on the part + a TemplateBinding, then re-template.
        var template = new ControlTemplate(ctx =>
        {
            var border = new Border();
            ctx.RegisterName("PART_B", border);
            return border;
        });
        template.Styles.Add(new Style("^ /template/ Border").Set(UIElement.ZIndexProperty, 5));

        var control = new TestControl { Template = template };
        using var host = AttachControl(control);

        var part = control.GetTemplatePart<Border>("PART_B")!;
        Assert.Equal(5, part.ZIndex); // the Template-layer frame armed

        control.Template = BorderWithPresenter(); // re-template → Detach retracts the old frame
        host.RunFrame();

        // The old part detached; its style state dropped (SD15). The new tree carries no ZIndex:5.
        Assert.Null(part.VisualParent);
        Assert.Equal(0, part.ZIndex); // store-owned retraction, never set-back
    }

    [Fact] // C137
    public void C137_UnhookBeforeRewire()
    {
        var order = new List<string>();
        var template = BorderWithPresenter();
        var control = new TestControl { Template = template };
        control.DetachingHook = _ => order.Add("detaching");

        using var host = AttachControl(control);
        var firstInstance = control.TemplateInstance!;

        control.Template = BorderWithPresenter();
        host.RunFrame();

        // OnTemplateDetaching (the unhook) ran before old.Detach() completed and the root was removed.
        Assert.Equal("detaching", order[0]);
        Assert.True(firstInstance.IsDetached);
    }

    // ───────────────────────────── 6.5 Barrier + TemplateBinding ─────────────────────────────

    [Fact] // C138
    public void C138_BarrierSkipsNonTemplateRulesForParts()
    {
        var host = UITestHost.Create();

        var template = new ControlTemplate(ctx =>
        {
            var border = new Border();
            ctx.RegisterName("PART_B", border);
            return border;
        });
        // A /template/ rule matches; a plain Border rule must NOT match the part (barrier, SD8).
        template.Styles.Add(new Style("^ /template/ Border").Set(UIElement.ZIndexProperty, 3));

        var control = new TestControl { Template = template };
        // An app-level plain Border rule (no /template/ hop) — barred for the part.
        host.Application.Styles.Add(new Style("Border").Set(UIElement.ZIndexProperty, 9));

        host.ShowRoot(control);
        host.RunFrame();

        var part = control.GetTemplatePart<Border>("PART_B")!;
        Assert.Equal(3, part.ZIndex); // the /template/ rule won; the plain Border rule was barred
    }

    [Fact] // C139
    public void C139_TemplateBindingTracksTemplatedParent()
    {
        var template = new ControlTemplate(ctx =>
        {
            var border = new Border();
            ctx.RegisterName("PART_B", border);
            // {TemplateBinding ZIndex} — one-way fast path to the templated parent's ZIndex.
            border.SetBinding(UIElement.ZIndexProperty, new TemplateBinding(UIElement.ZIndexProperty));
            return border;
        });

        var control = new TestControl { Template = template };
        using var host = AttachControl(control);

        var part = control.GetTemplatePart<Border>("PART_B")!;
        control.ZIndex = 4;
        host.RunFrame();
        Assert.Equal(4, part.ZIndex); // tracks the templated parent

        // The binding tears down with the template instance (re-template).
        control.Template = BorderWithPresenter();
        host.RunFrame();
        control.ZIndex = 8;
        host.RunFrame();
        Assert.NotEqual(8, part.ZIndex); // the old part no longer tracks (detached)
    }

    [Fact] // C140
    public void C140_DocumentAndTemplateNameScopesAreIsolated()
    {
        var template = new ControlTemplate(ctx =>
        {
            var border = new Border();
            ctx.RegisterName("PART_B", border);
            return border;
        });

        var control = new TestControl { Template = template };
        using var host = AttachControl(control);

        // A document name on the control's logical content.
        var docChild = new TextBlock("x") { Name = "DocName" };
        control.Content = docChild;
        var docScope = new NameScopeDictionary();
        docScope.Register("DocName", docChild);
        NameScope.SetNameScope(control, docScope);
        host.RunFrame();

        var templateScope = control.TemplateInstance!.NameScope;
        // The template scope sees PART_B but never the document name.
        Assert.NotNull(templateScope.Find("PART_B"));
        Assert.Null(templateScope.Find("DocName"));
        // The document scope sees DocName but never the part name.
        Assert.NotNull(docScope.Find("DocName"));
        Assert.Null(docScope.Find("PART_B"));
    }
}
