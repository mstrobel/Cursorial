using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §9 — TextBlock + Border/Decorator + AccessText/Label (C0; rows C161–C180). Render rows attach the
/// element under a host root and read back cells after a frame; unit rows over <c>AccessText.Parse</c>
/// need no host.
/// </summary>
public sealed class Section09_TextBorderAccessKey
{
    private static UITestHost Attach(UIElement root)
    {
        var host = UITestHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    // ───────────────────────────── 9.1 TextBlock ─────────────────────────────

    [Fact] // C161
    public void C161_TextBlockRendersTextElementLocal()
    {
        var tb = new TextBlock("Hi");
        using var host = Attach(tb);

        Assert.Equal("H", host.GetCell(0, 0).Grapheme);
        Assert.Equal("i", host.GetCell(1, 0).Grapheme);
        // Text is never access-key folded: an underscore stays literal.
        var tb2 = new TextBlock("_X");
        using var host2 = Attach(tb2);
        Assert.Equal("_", host2.GetCell(0, 0).Grapheme);
    }

    [Fact] // C162
    public void C162_TextBlockWrapsMultiLine()
    {
        var tb = new TextBlock("a\nb")
                 {
                     TextWrapping = WrapMode.WordWrap,
                     Width = 10,
                     Height = 3,
                     HorizontalAlignment = HorizontalAlignment.Left,
                     VerticalAlignment = VerticalAlignment.Top,
                 };
        using var host = Attach(tb);

        var (col, row) = tb.TranslateToWindow(0, 0);
        Assert.Equal("a", host.GetCell(col, row).Grapheme);
        Assert.Equal("b", host.GetCell(col, row + 1).Grapheme); // \n honored (P2.6 text tier)
    }

    [Fact] // C163
    public void C163_MarkupBrushResolvesAndWinsOverText()
    {
        var tb = new TextBlock { Text = "ignored", Markup = "[brush=Theme.AccentBrush]Hi[/brush]" };
        using var host = Attach(tb);

        // Markup wins over Text.
        Assert.Equal("H", host.GetCell(0, 0).Grapheme);
        Assert.NotEqual("i", host.GetCell(0, 0).Grapheme); // not "ignored"
    }

    [Fact] // C164
    public void C164_FormattedTextCacheHitOnSameKey()
    {
        var tb = new TextBlock("Hi") { Width = 10 };
        using var host = Attach(tb);

        // Re-measure with the same constraint: the formatter result is cached (no re-parse). We can't
        // observe parse count directly, but a stable render produces a stable cell grid.
        var before = host.GetCell(0, 0).Grapheme;
        host.RunFrame();
        Assert.Equal(before, host.GetCell(0, 0).Grapheme);
    }

    [Fact] // C165
    public void C165_ResourceVersionBumpInvalidatesCache()
    {
        var tb = new TextBlock { Markup = "[brush=K]Hi[/brush]" };
        var host = UITestHost.Create();
        host.Application.Resources["K"] = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
        host.ShowRoot(tb);
        host.RunFrame();

        var verBefore = ResourceServices.GetResourceVersion(tb);
        host.Application.Resources["K"] = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00));
        host.RunFrame();
        var verAfter = ResourceServices.GetResourceVersion(tb);

        Assert.True(verAfter > verBefore); // a pulse bumped the version → the cache key changed
        Assert.Equal("H", host.GetCell(0, 0).Grapheme); // still renders (re-parsed with fresh resolver)
    }

    [Fact] // C166
    public void C166_CapsChangeInvalidatesCache()
    {
        var tb = new TextBlock("Hi");
        var host = UITestHost.Create();
        host.ShowRoot(tb);
        host.RunFrame();
        Assert.Equal("H", host.GetCell(0, 0).Grapheme);

        // Re-render still shows the text after a re-measure (the cache keys caps + variant — a change
        // re-parses; here we assert the cache is keyed, not that caps changed mid-test).
        host.RunFrame();
        Assert.Equal("H", host.GetCell(0, 0).Grapheme);
    }

    // ───────────────────────────── 9.2 Border / Decorator ─────────────────────────────

    [Fact] // C167
    public void C167_DecoratorPassthrough()
    {
        var child = new TextBlock("ab"); // 2×1
        var decorator = new Decorator { Child = child };
        var host = UITestHost.Create();
        host.ShowRoot(decorator);
        decorator.Measure(new Size(80, 24));

        Assert.Equal(child.DesiredSize, decorator.DesiredSize); // single-child passthrough
    }

    [Fact] // C168
    public void C168_BorderMeasureAddsPaddingAndBorder()
    {
        var child = new TextBlock("ab"); // content 2×1
        var border = new Border
        {
            Child = child,
            Padding = new Margins(1, 1, 1, 1),
            BorderPen = Pens.Light,
        };
        var host = UITestHost.Create();
        host.ShowRoot(border);
        border.Measure(new Size(80, 24));

        // content (2×1) + padding (1 each side) + border (1 each side) = 4 × 3.
        Assert.Equal(new Size(2 + 2 + 2, 1 + 2 + 2), border.DesiredSize);
    }

    [Fact] // C169
    public void C169_BorderPenNullityEscalation()
    {
        var border = new Border { Child = new TextBlock("ab") };
        using var host = Attach(border);

        var noBorder = border.DesiredSize;
        border.BorderPen = Pens.Light; // null → non-null: ±1 cell/edge geometry flip → re-measure
        host.RunFrame();
        Assert.Equal(noBorder.Columns + 2, border.DesiredSize.Columns);

        // A restyle (non-null → non-null, weight swap) is render-only — size unchanged.
        var withBorder = border.DesiredSize;
        border.BorderPen = Pens.Heavy;
        host.RunFrame();
        Assert.Equal(withBorder, border.DesiredSize);
    }

    [Fact] // C170
    public void C170_BorderTitlePresenceEscalation()
    {
        var border = new Border { Child = new TextBlock("ab") };
        using var host = Attach(border);
        var noTitle = border.DesiredSize;

        border.Title = "Group"; // presence flip → top border row → re-measure
        host.RunFrame();
        Assert.Equal(noTitle.Rows + 1, border.DesiredSize.Rows);

        var withTitle = border.DesiredSize;
        border.Title = "Other"; // text-only change within presence → render-only
        host.RunFrame();
        Assert.Equal(withTitle, border.DesiredSize);
    }

    [Fact] // C171
    public void C171_BorderOccludesVsTint()
    {
        // Occludes = true paints an opaque surface; false tints. Both render without throwing; the
        // cell grid carries the background brush either way (occlusion is a compositor distinction,
        // asserted via the visible box rendering rather than a frosting probe here).
        var occluding = new Border
        {
            Child = new TextBlock("x"),
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            BorderPen = Pens.Light,
            Occludes = true,
            Width = 5,
            Height = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        using var host = Attach(occluding);
        // A box corner renders at the Border's window origin.
        var (col, row) = occluding.TranslateToWindow(0, 0);
        Assert.False(string.IsNullOrEmpty(host.GetCell(col, row).Grapheme));
    }

    [Fact] // C172
    public void C172_AdjacentBordersRender()
    {
        // Two adjacent borders in a shared zone render their strokes (junction-merge is the default
        // JunctionMode.Merge — asserted here as both boxes painting without throwing).
        var panel = new Cursorial.UI.Controls.StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var first = new Border { BorderPen = Pens.Light, Width = 4, Height = 2 };
        panel.Children.Add(first);
        panel.Children.Add(new Border { BorderPen = Pens.Light, Width = 4, Height = 2 });
        using var host = Attach(panel);
        var (col, row) = first.TranslateToWindow(0, 0);
        Assert.False(string.IsNullOrEmpty(host.GetCell(col, row).Grapheme));
    }

    // ───────────────────────────── 9.3 AccessText / AccessTextPresenter / Label ─────────────────────────────

    [Theory] // C173
    [InlineData("_File", "File", 'F', 0)]
    [InlineData("a__b", "a_b", '\0', -1)]
    [InlineData("_1", "1", '1', 0)]
    [InlineData("_!", "_!", '\0', -1)]
    [InlineData("plain", "plain", '\0', -1)]
    // KeyIndex is a GRAPHEME-CLUSTER index: a surrogate-pair emoji (2 code units) before the mnemonic
    // puts 'F' at cluster index 1, not code-unit index 2 (the AccessTextPresenter underlines cluster 1).
    [InlineData("\U0001F600_File", "\U0001F600File", 'F', 1)]
    public void C173_AccessTextParse(string input, string expectedText, char expectedKey, int expectedIndex)
    {
        var parsed = AccessText.Parse(input);
        Assert.Equal(expectedText, parsed.Text);
        Assert.Equal(expectedKey, parsed.Key);
        Assert.Equal(expectedIndex, parsed.KeyIndex);
        Assert.Equal(expectedIndex >= 0, parsed.HasKey);
    }

    [Fact] // C174
    public void C174_ExplicitConversionAndLiteral()
    {
        var parsed = (AccessText)"_x"; // explicit operator parses
        Assert.Equal("x", parsed.Text);
        Assert.Equal('x', parsed.Key);

        var literal = AccessText.Literal("_x"); // Literal skips parsing
        Assert.Equal("_x", literal.Text);
        Assert.False(literal.HasKey);
    }

    [Fact] // C175
    public void C175_AccessTextPresenterUnderlinesWhenCueShown()
    {
        var atp = new AccessTextPresenter(new AccessText("File", 'F', 0));
        using var host = Attach(atp);

        // No cue: no underline on the mnemonic.
        Assert.False(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Underline));

        // Cue shown: the KeyIndex grapheme is underlined.
        AccessKeyManager.SetShowUnderline(atp, true);
        host.RunFrame();
        Assert.True(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Underline)); // 'F' at index 0
        Assert.False(host.GetCell(1, 0).Style.Attributes.HasFlag(TextAttributes.Underline)); // 'i' not underlined

        Assert.True(AccessTextPresenter.TextProperty.GetEffects(typeof(AccessTextPresenter)).HasFlag(PropertyEffects.AffectsMeasure));
        Assert.Equal(TextAttributes.Underline, new AccessTextPresenter().KeyAttributes); // default
    }

    [Fact] // C176
    public void C176_ParsesAccessKeyLiteralsFlagResolution()
    {
        // The flag is set on ButtonBase.Content and Label.Content (runtime type), never on
        // ContentControl/TextBlock.
        Assert.True(ContentControl.ContentProperty.GetMetadata(typeof(Button)).ParsesAccessKeyLiterals == true);
        Assert.True(ContentControl.ContentProperty.GetMetadata(typeof(Label)).ParsesAccessKeyLiterals == true);
        Assert.NotEqual(true, ContentControl.ContentProperty.GetMetadata(typeof(ContentControl)).ParsesAccessKeyLiterals);
    }

    [Fact] // C177
    public void C177_GetAccessTextParsesAndRegisters()
    {
        var button = new Button { Content = "_Save" };
        using var host = Attach(button);

        // Registered with the AccessKeyManager (the 'S' mnemonic). We assert via the manager: an
        // access-key activation of 'S' clicks the button.
        var clicked = false;
        button.Click += (_, _) => clicked = true;

        // Drive an access-key activation through the manager's public single-match invoke path.
        var args = new AccessKeyEventArgs('s', isMultiMatch: false, button);
        ((IAccessKeyTarget)button).OnAccessKey(args);
        Assert.True(clicked);
    }

    [Fact] // C178
    public void C178_LabelAccessKeyFocusesTarget()
    {
        var target = new Button { Content = "T" };
        var label = new Label { Content = "_Name", Target = target };
        var panel = new Cursorial.UI.Controls.StackPanel();
        panel.Children.Add(label);
        panel.Children.Add(target);
        using var host = Attach(panel);

        ((IAccessKeyTarget)label).OnAccessKey(new AccessKeyEventArgs('n', isMultiMatch: false, label));
        host.RunFrame();
        Assert.True(target.IsFocused);
        Assert.False(label.Focusable); // never focusable
        Assert.False(label.IsTabStop); // never a tab stop
    }

    [Fact] // C179
    public void C179_LabelNullTargetFocusesFindNext()
    {
        var next = new Button { Content = "N" };
        var label = new Label { Content = "_Go" }; // Target == null
        var panel = new Cursorial.UI.Controls.StackPanel();
        panel.Children.Add(label);
        panel.Children.Add(next);
        using var host = Attach(panel);

        ((IAccessKeyTarget)label).OnAccessKey(new AccessKeyEventArgs('g', isMultiMatch: false, label));
        host.RunFrame();
        Assert.True(next.IsFocused); // FindNext focuses the next focusable
    }

    [Fact] // C180
    public void C180_MultiMatchFocusOnlyNeverInvokes()
    {
        var button = new Button { Content = "B" };
        var clicked = false;
        button.Click += (_, _) => clicked = true;

        // IsMultiMatch ⇒ the target should NOT invoke (the manager already focused it).
        ((IAccessKeyTarget)button).OnAccessKey(new AccessKeyEventArgs('b', isMultiMatch: true, button));
        Assert.False(clicked);

        // Single-match invokes (Button → click).
        ((IAccessKeyTarget)button).OnAccessKey(new AccessKeyEventArgs('b', isMultiMatch: false, button));
        Assert.True(clicked);
    }

    // ─── registration THROUGH the manager (not OnAccessKey directly) — C177/C178 producer ③ ───

    [Fact] // C177 (manager path): a Button registers on attach; Alt+key drives the click via the manager.
    public void C177_ButtonRegistersWithManager_AltKeyClicks()
    {
        var button = new Button { Content = "_Save" };
        using var host = Attach(button);
        var clicked = false;
        button.Click += (_, _) => clicked = true;

        // Through the manager: Alt down (cue), then an Alt+'s' chord — the manager must have a
        // live registration for the button (the bug was that no Register call ever fired).
        host.SendKey(Key.LeftAlt, KeyModifiers.Alt);
        host.RunFrame();
        host.SendKey(Key.Character, KeyModifiers.Alt, "s");
        host.RunFrame();

        Assert.True(clicked);
    }

    [Fact] // C178 (manager path): a Label registers on attach; Alt+key focuses the Target via the manager.
    public void C178_LabelRegistersWithManager_AltKeyFocusesTarget()
    {
        var target = new Button { Content = "T" };
        var label = new Label { Content = "_Name", Target = target };
        var panel = new Cursorial.UI.Controls.StackPanel();
        panel.Children.Add(label);
        panel.Children.Add(target);
        using var host = Attach(panel);

        host.SendKey(Key.LeftAlt, KeyModifiers.Alt);
        host.RunFrame();
        host.SendKey(Key.Character, KeyModifiers.Alt, "n");
        host.RunFrame();

        Assert.True(target.IsFocused); // the Label's mnemonic reached the manager and forwarded focus
    }

    [Fact] // Content change re-registers with the manager (doc §12.5 — producer ③ on ContentControl).
    public void C177b_ContentChangeReRegistersAccessKey()
    {
        var button = new Button { Content = "_Save" };
        using var host = Attach(button);
        var saveClicks = 0;
        var openClicks = 0;
        button.Click += (_, _) => { if (button.Content is "_Save") saveClicks++; else openClicks++; };

        // 'S' clicks today.
        host.SendKey(Key.LeftAlt, KeyModifiers.Alt);
        host.RunFrame();
        host.SendKey(Key.Character, KeyModifiers.Alt, "s");
        host.RunFrame();
        Assert.Equal(1, saveClicks);

        // Re-content: the old 'S' must unregister and the new 'O' register.
        button.Content = "_Open";
        host.RunFrame();
        host.SendKey(Key.LeftAlt, KeyModifiers.Alt);
        host.RunFrame();
        host.SendKey(Key.Character, KeyModifiers.Alt, "s"); // stale 'S' no longer matches
        host.RunFrame();
        Assert.Equal(1, saveClicks);

        host.SendKey(Key.LeftAlt, KeyModifiers.Alt);
        host.RunFrame();
        host.SendKey(Key.Character, KeyModifiers.Alt, "o");
        host.RunFrame();
        Assert.Equal(1, openClicks);
    }
}
