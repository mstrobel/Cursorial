using System.Globalization;
using System.Security.Cryptography;

using Cursorial.Rendering.Text;
using Cursorial.UI.Data;

namespace Cursorial.UI.Controls;

/// <summary>
/// The shared DataTemplate lookup + content realization chain (design doc §12.3 / CD22), reused by
/// <see cref="ContentPresenter"/> (and, at P9, <c>ItemsControl</c>). The chain:
/// ① explicit <c>ContentTemplate</c> → ② implicit <c>DataTemplateKey(t)</c> walk (runtime type then
/// each base up to but excluding <see cref="object"/>) → ③ <see cref="UIElement"/> passthrough →
/// ④ <see cref="AccessText"/> ⇒ <see cref="AccessTextPresenter"/> (extended to plain strings when
/// <c>RecognizesAccessKey</c>) → ⑤ fallback <see cref="TextBlock"/>(<c>Convert.ToString(content)</c>).
/// </summary>
internal static class ContentRealization
{
    /// <summary>
    /// Resolves the implicit <see cref="DataTemplate"/> for <paramref name="content"/> (chain ②): the
    /// runtime type then each base class via <c>DataTemplateKey</c> through the resource chain
    /// (the templated-parent hop is built into the chain walk). Returns null when nothing matches or
    /// the content is a string / <see cref="UIElement"/> / <see cref="AccessText"/> (those take chains
    /// ③/④/⑤). Interfaces are deferred (CD22).
    /// </summary>
    internal static DataTemplate? FindImplicitTemplate(UIElement scope, object? content)
    {
        if (content is null or string or UIElement or AccessText)
            return null;

        for (var type = content.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            if (scope.TryFindResource(new DataTemplateKey(type), out var resolved) && resolved is DataTemplate template)
                return template;
        }

        return null;
    }

    /// <summary>
    /// Realizes <paramref name="content"/> into a visual element via the chain (CD22). A resolved
    /// <paramref name="template"/> (explicit or implicit) wins (chains ①/②); otherwise the content
    /// type selects chains ③/④/⑤.
    /// </summary>
    /// <param name="host">The presenter (for the templated-parent logical adoption of element content).</param>
    /// <param name="content">The content object.</param>
    /// <param name="template">The resolved template, or null.</param>
    /// <param name="recognizesAccessKey">Whether a plain string is parsed for an access-key mnemonic (chain ④ extension).</param>
    /// <param name="recognizesMarkup"></param>
    /// <param name="stringFormat">A composite format applied to the text fallbacks (chains ④-string/⑤); ignored when a template handles the content (WPF parity).</param>
    internal static UIElement? Realize(ContentPresenter host, object? content, DataTemplate? template,
                                       bool recognizesAccessKey, bool recognizesMarkup, string? stringFormat = null)
    {
        var realized = RealizeCore(host, content, template, recognizesAccessKey, recognizesMarkup, stringFormat);
        realized?.SetInheritanceParent(host);
        return realized;
    }

    internal static UIElement? RealizeCore(ContentPresenter host, object? content, DataTemplate? template,
                                           bool recognizesAccessKey, bool recognizesMarkup, string? stringFormat = null)
    {
        // ①/② a resolved template builds the data subtree (DataContext = content, TemplatedParent null). The
        // string format applies only to the TEXT fallbacks below — a template owns its own formatting (WPF parity).
        if (template is not null)
            return ForwardTextAttributeAxes(host, template.Build(content/*, host.TemplatedParent*/));

        switch (content)
        {
            case null:
            {
                // Null content resolves as null.
                return null;
            }

            case DeferredContent dc:
            {
                var realized = dc.Realize();
                AdoptElementContent(host, ForwardTextAttributeAxes(host, realized));
                return realized;
            }

            // ③ a UIElement passes through: it is a logical child of the templated parent (so it
            // inherits the host's DataContext) and a visual child of the presenter. When the presenter
            // is a template part, the templated parent owns the logical adoption (the ContentControl);
            // a freestanding presenter adopts it itself.
            case UIElement element:
            {
                AdoptElementContent(host, ForwardTextAttributeAxes(host, element));
                // Icon-as-content gets the Inverse cue (§2.1), but the forward is a framework binding on
                // BORROWED content — the presenter must own its lifecycle (it is torn down on un-host, else
                // the source-anchored observer leaks the Icon; audit fix 2026-07-13). So the installation is
                // driven by ContentPresenter.RebuildChild, not here.
                return element;
            }

            // ④ an AccessText value always becomes an AccessTextPresenter, regardless of RecognizesAccessKey.
            case AccessText accessText:
            {
                var atp = new AccessTextPresenter(accessText);
                return ForwardTextAttributeAxes(host, atp);
            }

            // ④ extension: a plain string under RecognizesAccessKey folds to an AccessText (doc §12.5). The access
            // key is parsed from the RAW content — the same text ContentControl registers with the AccessKeyManager
            // — so the underlined mnemonic always matches the active gesture (a ContentStringFormat never injects
            // or moves a mnemonic, and never disagrees with the registration).
            case string s when recognizesAccessKey:
            {
                var atp = new AccessTextPresenter(AccessText.Parse(s));
                return ForwardTextAttributeAxes(host, atp);
            }

            // ⑤ RichText content renders as RichTextPresenter.
            case RichText rt:
            {
                var (hAlign, _) = host.EffectiveContentAlignment();

                TextAlignment? textAlignment = hAlign switch
                                               {
                                                   HorizontalAlignment.Stretch => null,
                                                   HorizontalAlignment.Left => TextAlignment.Left,
                                                   HorizontalAlignment.Center => TextAlignment.Center,
                                                   HorizontalAlignment.Right => TextAlignment.Right,
                                                   _ => throw new ArgumentOutOfRangeException()
                                               };

                var rtp = new RichTextPresenter { Source = rt };
                
                if (textAlignment is not null)
                    rtp.TextAlignment = textAlignment.Value;

                return ForwardTextAttributeAxes(host, rtp);
            }

            // ⑥ fallback: any other content (incl. a plain string) when `recognizesMarkup` is true renders
            // as TextBlock { Markup = SafeFormat(content) } with CurrentCulture (CD22), through the string format
            // if provided.
            case var o when recognizesMarkup:
            {
                var stb = new TextBlock { Markup = SafeFormat(host, stringFormat, o) };
                return ForwardTextAttributeAxes(host, stb);
            }

            // ⑦ fallback: any other content (incl. a plain string without RecognizesAccessKey) renders
            // as TextBlock { Text = SafeFormat(content) } with CurrentCulture (CD22), through the string format
            // if provided.
            default:
            {
                var dtb = new TextBlock(SafeFormat(host, stringFormat, content));
                return ForwardTextAttributeAxes(host, dtb);
            }
        }
    }

    // Forwards the per-axis text attributes from the templated parent (the control the theme rules
    // land on) onto a GENERATED leaf (chains ④/⑤; and framework-presentation chain-③ Icons) — never
    // onto DataTemplate/opaque-element content (app content is app-styleable, proposal §2.1/§7.3).
    // The axes are non-inheriting ("flows like Background"), so a control-level cue — the theme's
    // `.caps-nocolor Button:focus → Inverse` — reaches the label through these live forwards.
    private static UIElement ForwardTextAttributeAxes(ContentPresenter host, UIElement leaf)
    {
        // The forward source is the control the theme rules land on (the templated parent); a
        // freestanding presenter forwards its own values (it IS the element the app styles).
        var source = host.ForwardsFromTemplatedParent ? host.TemplatedParent ?? (UIObject)host : host;

        // Install at the Template lane (audit fix 2026-07-13): opening the template-instantiation
        // scope around the binding installs makes the forwarded value the leaf's RESTING truth —
        // a conditional rule targeting the leaf pierces it (StyleTrigger > Template, PD26), while a
        // resting rule does not (re-skin via the container's axes, which the forward mirrors). At
        // LocalValue the forward would occlude even conditional rules (LocalValue > StyleTrigger).
        using (TemplateInstantiationScope.Enter())
        {
            foreach (var axis in TextElement.AllAxisProperties)
            {
                if (host.ForwardTextInverse || axis != TextElement.InverseProperty)
                    leaf.SetBinding(axis, new Binding(axis) { Source = source });
            }

            if (leaf is TextBlock)
            {
                foreach (var property in TextElement.AllFormattingProperties)
                    leaf.SetBinding(property, new Binding(property) { Source = source });
            }
        }

        return leaf;
    }

    // The GLYPH/icon delivery (owner rule 2026-07-13): a symbol carries only the Inverse cue — it
    // swaps in unison with an inverted face; weight/style/underline don't apply to a glyph. Returns the
    // installed expression so the CALLER (ContentPresenter, for borrowed Icon content) owns its
    // teardown — a source-anchored binding on borrowed content would otherwise leak the Icon.
    internal static BindingExpressionBase ForwardInverseOnly(ContentPresenter host, UIElement glyph)
    {
        var source = /*host.TemplatedParent ?? */(UIObject)host;
        using (TemplateInstantiationScope.Enter())
            return glyph.SetBinding(TextElement.InverseProperty,
                             new Binding(TextElement.InverseProperty) { Source = source });
    }

    // Applies a composite format to content (null format ⇒ Convert.ToString, the prior behavior). A MALFORMED
    // format degrades to the unformatted text + a DEBUG diagnostic — never a thrown FormatException, which would
    // escape the measure pass (EnsureChild runs inside MeasureOverride) and kill the frame loop (audit finding).
    private static string? SafeFormat(ContentPresenter host, string? stringFormat, object? content)
    {
        if (stringFormat is null)
            return Convert.ToString(content, CultureInfo.CurrentCulture);

        try
        {
            return string.Format(CultureInfo.CurrentCulture, stringFormat, content);
        }
        catch (FormatException)
        {
            ControlDiagnostics.BadStringFormat(host, stringFormat);
            return Convert.ToString(content, CultureInfo.CurrentCulture);
        }
    }

    private static void AdoptElementContent(ContentPresenter host, UIElement element)
    {
        // If the element already has a logical parent (the ContentControl adopted it on Content change),
        // leave it. Otherwise, the presenter adopts it logically so DataContext inheritance flows.
        if (element.LogicalParent is not null)
            return;

        // A presenter inside a template: the templated parent (the ContentControl) is the logical owner.
        // It adopts the element on its own Content change; the presenter only hosts it visually here.
        if (host.TemplatedParent is ContentControl)
            return;

        // A freestanding presenter (no ContentControl host): adopt logically so inheritance works.
        host.AdoptElementContentLogically(element);
    }
}
