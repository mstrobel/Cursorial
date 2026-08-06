// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;
using Cursorial.UI.Themes.Default;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// #19 — the caps-* theme-styles authored in <c>Cursorial.UI.Themes/Themes/Styles.xaml</c> and consumed
/// from <c>UIApplication.Theme</c> at <c>Theme(2)</c> (the R2/B13 channel). Proves: the loader populates the
/// <c>&lt;ResourceDictionary.Styles&gt;</c> slot (selector LISTS + dotted/attached Setters + a
/// <c>GlyphSetCarrier</c> Setter.Value element); the data theme reproduces BuiltIn's caps-unicode glyph +
/// caps-nocolor reverse-video looks byte-for-byte; and an app-theme caps-style actually OVERRIDES BuiltIn's
/// (the channel is genuinely consumed, not silently shadowed by BuiltIn's identical rule).
/// </summary>
public sealed class XamlThemeStylesTests
{
    private const string Ns = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // The loader capability behind it: a dotted/attached Setter resolves with NO enclosing Style TargetType.
    public void DottedSetter_WithoutTargetType_Resolves()
    {
        // BuiltIn caps-nocolor rules span multiple types with no single TargetType — the dotted setter owner
        // (TextElement) is resolved from the property name, so the Style needs no TargetType.
        var style = new XamlLoader().Load<Cursorial.UI.Style>(
            "<Style" + Ns + " Selector=\".caps-nocolor Button:focus\">" +
              "<Setter Property=\"TextElement.Inverse\" Value=\"True\"/>" +
            "</Style>");

        Assert.NotNull(style.Selector);
        // The dotted Setter resolved to the REAL TextElement.Inverse attached UIProperty (not a placeholder).
        Assert.Equal("Inverse", Assert.Single(style.Setters).Property.Name);
    }

    [Fact] // LoadStyles populates the Styles slot with all five theme rules (the caps-nocolor inverse is a 7-branch list).
    public void LoadStyles_PopulatesAllThemeStyles()
    {
        var builtin = CursorialTheme.BuiltIn.Styles!;
        var dict = CursorialDefaultTheme.LoadStyles();

        Assert.NotNull(dict.Styles);
        Assert.Equal(builtin.Count, dict.Styles!.Count);
        // The nocolor interactive-state rule (re-pinned, caps-mechanism review): the .caps-* compounds and
        // the doubled self-form leg are gone — one structural selector + RequiresCapabilities carries the
        // gate (and the exact class-like specificity the dropped compound contributed).
        var inverseRule = Assert.Single(dict.Styles!, s => s.Selector!.ToString() == ":is(ButtonBase)" && s.RequiresCapabilities == StyleCapabilities.NoColor);
        var inverseText1 = inverseRule.Children[0].Selector!.ToString();
        var inverseText2 = inverseRule.Children[1].Selector!.ToString();
        Assert.Contains("^:focus", inverseText1);
        Assert.Contains("^:pointerover", inverseText1);
        Assert.Contains("^:pressed", inverseText2);
        Assert.Equal(UIControls.TextElement.InverseProperty, inverseRule.Children[0].Setters[0].Property);
        Assert.Equal(true, inverseRule.Children[0].Setters[0].Value);
        Assert.Equal(UIControls.TextElement.InverseProperty, inverseRule.Children[1].Setters[0].Property);
        Assert.Equal(false, inverseRule.Children[1].Setters[0].Value);
        // The rest are single-branch (two caps-unicode glyph rules + the caps-nocolor disabled rule + AccessKeyCue).
        Assert.Equal(builtin.Count(s => s.Selector is { Branches.Length: 1 }),
                     dict.Styles!.Count(s => s.Selector is { Branches.Length: 1 }));

        // The ported AccessKeyCue rule's Setter resolved its PREFIXED owner (input:AccessKeyManager, in
        // Cursorial.UI.Input — outside the default xmlns map) to the real ShowUnderline attached UIProperty via
        // the captured-namespace path (#22). A failed prefix resolution would have been CUR2002 at load.
        var cue = Assert.Single(dict.Styles!, s => s.Selector is { } sel && sel.ToString().Contains("access-keys"));
        var cueSetter = Assert.Single(cue.Setters);
        Assert.Equal(Cursorial.UI.Input.AccessKeyManager.ShowUnderlineProperty, cueSetter.Property);
        Assert.Equal(true, cueSetter.Value);
    }

    [Theory] // The data theme reproduces BuiltIn byte-for-byte AND genuinely matches (non-fallback) — not vacuous.
    [InlineData(true)]  // checked CheckBox under caps-unicode (default) → "[✓]" either way
    [InlineData(false)] // focused Button under NoColor → reverse-video (Inverse) either way
    public void XamlTheme_CapsStyles_RenderIdenticallyToBuiltIn(bool checkBoxCase)
    {
        var builtin = RenderCaps(xaml: false, checkBoxCase);
        var xaml = RenderCaps(xaml: true, checkBoxCase);

        // Parity: the data theme reproduces BuiltIn (the dogfood).
        Assert.Equal(builtin, xaml);

        // Non-vacuity: prove the caps rule actually MATCHED (the descendant selector resolved) and produced the
        // NON-fallback result — ruling out the "both render the ASCII/no-Inverse fallback" false-pass.
        if (checkBoxCase)
            Assert.Contains(xaml, c => c.Glyph == "✓");                          // caps-unicode checked mark (not ASCII 'x')
        else
            Assert.Contains(xaml, c => c.Attrs.HasFlag(TextAttributes.Inverse)); // caps-nocolor reverse-video
    }

    [Fact] // The XAML caps-unicode glyph style is genuinely CONSUMED + OVERRIDES BuiltIn (a unique glyph wins).
    public void XamlThemeStyles_OverrideBuiltIn_CapsUnicodeGlyph()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(8, 1) });

        // A custom theme whose .caps-unicode CheckBox rule sets a UNIQUE checked mark "(!)" (not BuiltIn's "[✓]"),
        // authored through the same FillStyles + GlyphSetCarrier string-converter path Styles.xaml uses.
        host.Application.Theme = new XamlLoader().Load<ResourceDictionary>(
            "<ResourceDictionary" + Ns + ">" +
              "<ResourceDictionary.Styles>" +
                "<Style Selector=\".caps-unicode CheckBox\">" +
                  "<Setter Property=\"ToggleGlyph.Glyphs\" Value=\"( )|(!)|(-)\"/>" +
                "</Style>" +
              "</ResourceDictionary.Styles>" +
            "</ResourceDictionary>");

        // The GlyphSetCarrier value resolved through the converter.
        var carrier = Assert.IsType<GlyphSetCarrier>(host.Application.Theme!.Styles!.Single().Setters.Single().Value);
        Assert.Equal("(!)", carrier.Checked);

        // The CheckBox is a CHILD of the root, so `.caps-unicode CheckBox` (a descendant selector) matches —
        // caps-unicode is stamped on the root, the CheckBox is its descendant.
        var panel = new UIControls.StackPanel { Name = "Root" };
        var checkBox = new UIControls.CheckBox { Content = "x", IsChecked = true };
        panel.Children.Add(checkBox);
        host.ShowRoot(panel);
        Assert.True(host.RunUntilIdle());

        // The effective ToggleGlyph.Glyphs is the app-theme's override — proving the app.Theme caps-unicode rule
        // armed at Theme(2) and BEAT BuiltIn's identical-selector rule (the order-base override, R2/B13).
        var effective = checkBox.GetValue(ToggleGlyph.GlyphsProperty);
        Assert.Equal("(!)", Assert.IsType<GlyphSetCarrier>(effective).Checked);

        // The checked glyph renders the app-theme's unique mark.
        Assert.Equal("(", host.GetCell(0, 0).Grapheme);
        Assert.Equal("!", host.GetCell(1, 0).Grapheme);
        Assert.Equal(")", host.GetCell(2, 0).Grapheme);
    }

    [Fact] // P3 audit: the XAML overlay's ListBoxItem row FACE (not just the label) inverts under the NoColor per-axis cue
    public void XamlTheme_NoColorListRow_FaceInverts_ViaPerAxisForward()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(12, 3) });
        host.Application.Theme = CursorialDefaultTheme.LoadTheme();
        host.Application.RequestedColorTier = ColorDepth.NoColor;

        var list = new UIControls.ListBox { ItemsSource = new[] { "alpha" } };
        var root = new UIControls.StackPanel { Name = "Root" };
        root.Children.Add(list);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var item = (UIControls.ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)!;
        item.IsSelected = true; // .caps-nocolor ListBoxItem:selected → TextElement.Inverse (control-level)
        Assert.True(host.RunUntilIdle());

        // The row's per-axis Inverse forward (Controls.xaml ListBoxItemTemplate) inverts the WHOLE bar,
        // not just the label — the audit regression (the XAML twin shipped label-only) is closed.
        var (col, r) = item.TranslateToWindow(0, 0);
        Assert.True(host.GetCell(col, r).Style.Attributes.HasFlag(TextAttributes.Inverse));      // the row-padding face cell
        Assert.True(host.GetCell(col + 1, r).Style.Attributes.HasFlag(TextAttributes.Inverse));  // the 'a' label cell
    }

    private static (string Glyph, Color Fg, Color Bg, TextAttributes Attrs)[] RenderCaps(bool xaml, bool checkBoxCase)
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(12, 3) });
        if (xaml)
            host.Application.Theme = CursorialDefaultTheme.LoadTheme();

        // Both cases use a panel root so the styled control is a DESCENDANT of the caps-* stamped root — the
        // caps selectors (`.caps-unicode CheckBox` / `.caps-nocolor Button:focus`) are descendant selectors.
        var root = new UIControls.StackPanel { Name = "Root" };
        if (checkBoxCase)
        {
            // A checked CheckBox under caps-unicode (stamped by default) — exercises the caps-unicode glyph opt-up.
            root.Children.Add(new UIControls.CheckBox { Content = "x", IsChecked = true });
        }
        else
        {
            // A focused Button under NoColor — exercises the caps-nocolor reverse-video. The child Button is the
            // first tab stop (auto-focused on activation); RequestedColorTier=NoColor stamps caps-nocolor.
            host.Application.RequestedColorTier = ColorDepth.NoColor;
            root.Children.Add(new UIControls.Button { Content = "OK" });
        }

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var cells = new List<(string, Color, Color, TextAttributes)>(12 * 3);
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 12; c++)
        {
            var cell = host.GetCell(c, r);
            cells.Add((cell.Grapheme ?? string.Empty, cell.Style.Foreground, cell.Style.Background, cell.Style.Attributes));
        }
        return cells.ToArray();
    }
}
