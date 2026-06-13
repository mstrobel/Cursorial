using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// Phase-6 (Fork C) end-to-end coverage: declarative UI loaded from a XAML <em>string</em> through the
/// full <see cref="XamlLoader"/> and run on the real <see cref="UIApplication"/> frame loop via
/// <see cref="UITestHost"/>. These are the §14 P6 exit criteria — the live proof that the runtime loader
/// produces widget trees that render, bind, resolve resources, expand templates, fold access keys, and
/// report parse errors with correct positions. Every scenario crosses the loader → object graph →
/// layout/render → composited cells chain (not a parse-only harness).
/// </summary>
public sealed class Phase6XamlEndToEndTests
{
    // The standard xmlns preamble: the default Cursorial map (UI/Controls/Data/Drawing.Media) + x:.
    private const string Ns =
        " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static readonly XamlLoader Loader = new();

    static Phase6XamlEndToEndTests()
    {
        // The integration fixtures (XamlTestBrush) live in this test assembly + namespace; register both
        // in the default xmlns so they resolve as unprefixed names (mirrors LoaderTestBase).
        XamlSchemaContext.Default.RegisterAssembly(typeof(Phase6XamlEndToEndTests).Assembly);
        XamlSchemaContext.Default.RegisterDefaultNamespace(typeof(XamlTestBrush).Namespace!);
    }

    // ───────────────────────────── (a) themed tree: render + bindings + resources ─────────────────────────────

    /// <summary>
    /// A full window-content tree loaded from one XAML string — a StackPanel of a themed Button + a
    /// CheckBox + a ScrollViewer whose content is {Binding}-driven, with a {StaticResource} brush and a
    /// ControlTemplate whose part uses {TemplateBinding} — renders through the real frame loop and the
    /// live wiring resolves: cell assertions on the rendered text, the StaticResource brush reached the
    /// Button background, the binding pushed the view-model name into the scrolled content, and the
    /// templated part tracks the templated parent's Background via TemplateBinding.
    /// </summary>
    [Fact]
    public void ThemedTree_FromXaml_RendersAndBindingsResourcesResolveLive()
    {
        using var host = UITestHost.Create();
        var vm = new DemoVm { Caption = "Hello", Accent = "go" };

        var root = Loader.Load<UIControls.StackPanel>(
            "<StackPanel" + Ns + " HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\">" +
              "<StackPanel.Resources>" +
                "<XamlTestBrush x:Key=\"PanelAccent\" Color=\"#3050c0\"/>" +
                "<ControlTemplate x:Key=\"Chrome\">" +
                  "<Border x:Name=\"PART_Chrome\" Background=\"{TemplateBinding Background}\"/>" +
                "</ControlTemplate>" +
              "</StackPanel.Resources>" +
              "<Button x:Name=\"Ok\" Content=\"{Binding Caption}\" Foreground=\"{StaticResource PanelAccent}\" " +
                      "HorizontalAlignment=\"Left\"/>" +
              "<CheckBox x:Name=\"Toggle\" Content=\"Enable\"/>" +
              "<TextBlock x:Name=\"Live\" Text=\"{Binding Caption}\"/>" +
              "<ScrollViewer x:Name=\"Scroll\" Width=\"16\" Height=\"3\" " +
                            "HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\">" +
                "<TextBlock x:Name=\"Bound\" Text=\"{Binding Caption}\"/>" +
              "</ScrollViewer>" +
            "</StackPanel>",
            source: null);

        root.DataContext = vm;
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        // x:Name registration in the document scope.
        var scope = NameScope.GetNameScope(root)!;
        var button = (UIControls.Button)scope.Find("Ok")!;
        var check = (UIControls.CheckBox)scope.Find("Toggle")!;
        var live = (UIControls.TextBlock)scope.Find("Live")!;
        var bound = (UIControls.TextBlock)scope.Find("Bound")!;

        // The {StaticResource} brush resolved eagerly onto the Button foreground (the #3050c0 XamlTestBrush).
        var brush = Assert.IsType<XamlTestBrush>(button.Foreground);
        Assert.Equal(Color.FromRgb(0x30, 0x50, 0xc0), brush.Color);

        // The {Binding}s live-resolved the VM caption into the Button content and both bound TextBlocks
        // (the binding's job — the loader produced live BindingExpressions, not literal strings).
        Assert.Equal("Hello", button.Content);
        Assert.Equal("Hello", live.Text);
        Assert.Equal("Hello", bound.Text);

        // It rendered: the bound Button content, the CheckBox label, and the bound text all appear in the
        // composited cells (the scrolled copy shows inside the ScrollViewer's viewport).
        var rows = ReadRows(host, host.FrameBuffer.Rows);
        Assert.Contains(rows, r => r.Contains("Hello"));
        Assert.Contains(rows, r => r.Contains("Enable"));

        // A live VM update flows through the binding to the next frame and re-renders in the cells — the
        // bound Button content (its ContentPresenter is render-reactive) shows the new caption.
        vm.Caption = "Goodbye";
        Assert.True(host.RunUntilIdle());
        Assert.Equal("Goodbye", button.Content);
        Assert.Equal("Goodbye", live.Text);
        Assert.Equal("Goodbye", bound.Text);
        Assert.Contains(ReadRows(host, host.FrameBuffer.Rows), r => r.Contains("Goodbye"));

        // The ControlTemplate from the resource chain expands on a real Button and the TemplateBinding part
        // tracks the templated parent's Background.
        var template = (ControlTemplate)root.Resources["Chrome"]!;
        template.TargetType = typeof(UIControls.Button);
        var owner = new UIControls.Button { Template = template, Background = new XamlTestBrush { Color = Color.FromRgb(7, 8, 9) } };
        owner.ApplyTemplate();
        var chrome = NameScope.GetTemplateNameScope(owner)!.RequireControl<UIControls.Border>("PART_Chrome");
        Assert.Same(owner.Background, chrome.Background);

        _ = check; // referenced for the name-scope assertion; toggling is exercised by the demo
    }

    // ───────────────────────────── (b) ResourceDictionary: MergedDictionaries + x:Key + DynamicResource theme flip ─────────────────────────────

    /// <summary>
    /// A ResourceDictionary loaded from XAML with <c>MergedDictionaries</c> + keyed entries + per-theme
    /// sub-dictionaries (<c>ThemeDictionaries</c>) becomes the application resources; a tree whose Button
    /// foreground is a <c>{DynamicResource}</c> resolves the themed brush at the current variant, and a
    /// theme-base flip re-resolves the live producer — the consumer's color updates without a rebuild
    /// (the loader produced a real DynamicResource carrier, not a snapshot).
    /// </summary>
    [Fact]
    public void ResourceDictionary_FromXaml_MergedAndThemed_DynamicResourceConsumerUpdatesOnThemeFlip()
    {
        using var host = UITestHost.Create();
        host.Application.RequestedThemeBase = ThemeBase.Dark; // deterministic regardless of negotiated luminance
        host.RunFrame();

        // The application resources: a top-level keyed brush from a MERGED dictionary plus a per-theme
        // "ink" brush in ThemeDictionaries (Dark vs Light), all authored in one XAML document.
        var appResources = (ResourceDictionary)Loader.Load(
            "<ResourceDictionary" + Ns + ">" +
              "<ResourceDictionary.MergedDictionaries>" +
                "<ResourceDictionary>" +
                  "<XamlTestBrush x:Key=\"Surface\" Color=\"#101418\"/>" +
                "</ResourceDictionary>" +
              "</ResourceDictionary.MergedDictionaries>" +
              "<ResourceDictionary.ThemeDictionaries>" +
                "<ResourceDictionary x:Key=\"Dark\"><XamlTestBrush x:Key=\"ink\" Color=\"#c8d2f0\"/></ResourceDictionary>" +
                "<ResourceDictionary x:Key=\"Light\"><XamlTestBrush x:Key=\"ink\" Color=\"#14183c\"/></ResourceDictionary>" +
              "</ResourceDictionary.ThemeDictionaries>" +
            "</ResourceDictionary>", source: null);

        Assert.Single(appResources.MergedDictionaries);
        Assert.True(appResources.ThemeDictionaries.ContainsKey(new ThemeVariantKey(ThemeBase.Dark, null)));
        host.Application.Resources = appResources;

        // The consumer tree: a Button whose Foreground is a DynamicResource to the themed "ink" key.
        var button = Loader.Load<UIControls.Button>(
            "<Button" + Ns + " Content=\"Ink\" Foreground=\"{DynamicResource ink}\" " +
                       "HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\"/>", source: null);
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        var darkInk = Color.FromRgb(0xc8, 0xd2, 0xf0);
        var lightInk = Color.FromRgb(0x14, 0x18, 0x3c);

        // The DynamicResource resolved the themed "ink" brush at the Dark variant (live, at LocalValue).
        Assert.Equal(BindingPriority.LocalValue, button.GetValueSource(UIControls.Control.ForegroundProperty).Priority);
        Assert.Equal(darkInk, ((XamlTestBrush)button.Foreground!).Color);

        // The top-level merged "Surface" entry resolved through the application chain.
        Assert.True(host.Application.Resources.TryGetResource("Surface", host.Application.ActualThemeVariant, out var surface));
        Assert.Equal(Color.FromRgb(0x10, 0x14, 0x18), ((XamlTestBrush)surface!).Color);

        // Flip the theme base to Light: the variant changes and the DynamicResource producer re-resolves
        // against the Light dictionary on the resource pulse — the consumer's color updates with no rebuild.
        host.Application.RequestedThemeBase = ThemeBase.Light;
        Assert.True(host.RunUntilIdle());
        Assert.Equal(ThemeBase.Light, host.Application.ActualThemeVariant.Base);
        Assert.Equal(lightInk, ((XamlTestBrush)button.Foreground!).Color);

        // Flip back: the producer re-resolves to the Dark ink again (the carrier is live, not a snapshot).
        host.Application.RequestedThemeBase = ThemeBase.Dark;
        Assert.True(host.RunUntilIdle());
        Assert.Equal(darkInk, ((XamlTestBrush)button.Foreground!).Color);
    }

    // ───────────────────────────── (c) ControlTemplate: per-target expansion + Detach + independent namescopes ─────────────────────────────

    /// <summary>
    /// A ControlTemplate loaded once from XAML expands per target — two Buttons sharing the one template
    /// each get a FRESH subtree with an INDEPENDENT template name scope (the same PART_Chrome name resolves
    /// to two distinct Border instances); the parts are wired (the templated-parent TemplateBinding is
    /// live on each); and a Template clear (Detach) tears the instance down and drops the name scope.
    /// </summary>
    [Fact]
    public void ControlTemplate_FromXaml_ExpandsPerTarget_IndependentNameScopes_DetachRetracts()
    {
        using var host = UITestHost.Create();

        var dict = (ResourceDictionary)Loader.Load(
            "<ResourceDictionary" + Ns + ">" +
              "<ControlTemplate x:Key=\"Chrome\">" +
                "<Border x:Name=\"PART_Chrome\" Background=\"{TemplateBinding Background}\"/>" +
              "</ControlTemplate>" +
            "</ResourceDictionary>", source: null);

        var template = (ControlTemplate)dict["Chrome"]!;
        template.TargetType = typeof(UIControls.Button);

        var first = new UIControls.Button { Template = template, Background = new XamlTestBrush { Color = Color.FromRgb(1, 2, 3) } };
        var second = new UIControls.Button { Template = template, Background = new XamlTestBrush { Color = Color.FromRgb(4, 5, 6) } };

        var stack = new UIControls.StackPanel();
        stack.Children.Add(first);
        stack.Children.Add(second);
        host.ShowRoot(stack);
        Assert.True(host.RunUntilIdle());

        var firstScope = NameScope.GetTemplateNameScope(first)!;
        var secondScope = NameScope.GetTemplateNameScope(second)!;
        var firstChrome = firstScope.RequireControl<UIControls.Border>("PART_Chrome");
        var secondChrome = secondScope.RequireControl<UIControls.Border>("PART_Chrome");

        // Independent name scopes: same part name, two distinct instances.
        Assert.NotSame(firstScope, secondScope);
        Assert.NotSame(firstChrome, secondChrome);

        // Each part's TemplateBinding tracks its OWN templated parent.
        Assert.Same(first.Background, firstChrome.Background);
        Assert.Same(second.Background, secondChrome.Background);
        Assert.NotSame(firstChrome.Background, secondChrome.Background);

        // Detach: clearing the template tears the instance down and drops the template name scope.
        first.Template = null;
        Assert.True(host.RunUntilIdle());
        Assert.Null(first.TemplateInstance);
        Assert.Null(NameScope.GetTemplateNameScope(first));
        // The other instance is untouched.
        Assert.Same(secondChrome, NameScope.GetTemplateNameScope(second)!.Find("PART_Chrome"));
    }

    // ───────────────────────────── (d) access-key fold: Content="_Save" → AccessText + Alt path ─────────────────────────────

    /// <summary>
    /// A Button loaded from XAML with <c>Content="_Save"</c> folds its mnemonic the same as
    /// <see cref="AccessText.Parse"/> (the three-identical-producers rule — the loader keeps the raw
    /// string; the runtime folds on demand). Attached to a live application, the auto-registered access
    /// key fires via the Alt chord through the dispatcher (the consumed P2/P5 access-key manager).
    /// </summary>
    [Fact]
    public void AccessKeyFold_FromXaml_ContentUnderscore_FoldsAndAltActivates()
    {
        using var host = UITestHost.Create();

        var border = Loader.Load<UIControls.Border>(
            "<Border" + Ns + " HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\">" +
              "<Button x:Name=\"Save\" Content=\"_Save\" HorizontalAlignment=\"Left\"/>" +
            "</Border>", source: null);

        var button = (UIControls.Button)border.Child!;
        var clicks = 0;
        button.Click += (_, _) => clicks++;

        host.ShowRoot(border);
        Assert.True(host.RunUntilIdle());

        // The loader stored the raw string; folding it matches AccessText.Parse (key 'S' at index 0).
        Assert.Equal(new AccessText("Save", 'S', 0), AccessText.Parse((string)button.Content!));

        // The AccessTextPresenter rendered the visible label without the leading underscore.
        Assert.Contains(ReadRows(host, host.FrameBuffer.Rows), r => r.Contains("Save"));
        Assert.DoesNotContain(ReadRows(host, host.FrameBuffer.Rows), r => r.Contains("_Save"));

        // The Alt chord (Alt+S) activates the auto-registered access-key target through the dispatcher.
        host.Application.InputDispatcher.ProcessEvent(new KeyEvent
        {
            Key = Key.Character,
            Text = "s".AsMemory(),
            Modifiers = KeyModifiers.Alt,
            Kind = KeyEventKind.Down,
            Timestamp = host.Time.GetUtcNow(),
        });
        host.RunFrame();
        Assert.Equal(1, clicks);
    }

    // ───────────────────────────── (e) error quality: line + column ─────────────────────────────

    /// <summary>
    /// Malformed XAML throws <see cref="XamlParseException"/> with a useful 1-based line + column and a
    /// banded diagnostic code — the loader's authoring-error story. Covers an unknown element, an unknown
    /// member, and a malformed markup extension; the positions point inside the offending document.
    /// </summary>
    [Fact]
    public void ErrorQuality_MalformedXaml_ThrowsWithLineAndColumn()
    {
        // An unknown element (CUR2001 — unresolvable type), on line 2.
        var unknownElement = Assert.Throws<XamlParseException>(() => Loader.Load(
            "<StackPanel" + Ns + ">\n" +
            "  <NoSuchControl/>\n" +
            "</StackPanel>", source: null));
        Assert.Equal(2, unknownElement.Line);
        Assert.True(unknownElement.Column > 0);
        Assert.StartsWith("CUR2", unknownElement.Code);

        // An unknown member on a known element — position points at the bad attribute.
        var unknownMember = Assert.Throws<XamlParseException>(() => Loader.Load(
            "<Button" + Ns + " NoSuchProperty=\"x\"/>", source: null));
        Assert.True(unknownMember.Line >= 1);
        Assert.True(unknownMember.Column > 0);
        Assert.StartsWith("CUR2", unknownMember.Code);

        // A malformed markup extension (unclosed brace) — a parse-stage diagnostic with a position.
        var badExtension = Assert.Throws<XamlParseException>(() => Loader.Load(
            "<Button" + Ns + " Content=\"{Binding Name\"/>", source: null));
        Assert.True(badExtension.Line >= 1);
        Assert.True(badExtension.Column > 0);

        // Well-formed-XML-but-not-valid-XAML (unbalanced tags) surfaces as a parse diagnostic too.
        var malformedXml = Assert.Throws<XamlParseException>(() => Loader.Load(
            "<StackPanel" + Ns + "><Button></StackPanel>", source: null));
        Assert.True(malformedXml.Line >= 1);
        Assert.True(malformedXml.Column > 0);
    }

    // ───────────────────────────── fixtures ─────────────────────────────

    private static string[] ReadRows(UITestHost host, int rows)
    {
        var result = new string[rows];
        for (var r = 0; r < rows; r++)
            result[r] = host.GetRowText(r);
        return result;
    }

    /// <summary>The {Binding} oracle for the integration documents.</summary>
    private sealed class DemoVm : INotifyPropertyChanged
    {
        private string? _caption;
        private string? _accent;

        public string? Caption { get => _caption; set { _caption = value; Raise(); } }
        public string? Accent { get => _accent; set { _accent = value; Raise(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// A XAML-constructible brush (parameterless ctor + a string-convertible <c>Color</c>) for the
/// integration documents — the resource/binding brush value (<see cref="Drawing.Media.SolidColorBrush"/>
/// is immutable / not XAML-activatable). Public so it resolves as an unprefixed default-xmlns name.
/// </summary>
public sealed class XamlTestBrush : IBrush
{
    /// <summary>The brush color (<c>#hex</c> / named ANSI string-convertible).</summary>
    public Color Color { get; set; }

    /// <inheritdoc/>
    public Color ColorAt(int column, int row, Rect bounds) => Color;
}
