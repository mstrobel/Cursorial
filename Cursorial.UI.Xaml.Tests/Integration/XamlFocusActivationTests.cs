using Cursorial.Input;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// Regression for the XAML control-tree keyboard/focus bug (assignment §"XAML-loaded tree not
/// keyboard/focus-functional"): a tree whose first focusable sits behind a template / content-presenter
/// boundary (<c>ScrollViewer.Content → Border → StackPanel → Button</c>) is shown <b>before</b> its first
/// layout pass, so <see cref="FocusManager.OnWindowActivated"/>'s first-tab-stop auto-focus found nothing
/// (the subtree is realized only when the first measure applies templates). Auto-focus reported
/// <c>focus: none</c>; the demo's status line showed it and no focus visuals appeared.
///
/// <para>
/// Root cause (verified by a hand-built-vs-loader diff: BOTH trees were identically broken pre-fix and
/// are identically fixed now — the fault was activation TIMING, not the loader's tree construction): the
/// activation auto-focus ran synchronously inside the <c>RootElement</c> set, ahead of Phase 5 layout.
/// The fix parks the activation when no target is reachable and retries it at the post-layout boundary
/// (<see cref="FocusManager.CompletePendingActivationFocus"/>, driven by the frame loop after the first
/// <c>RunLayoutPass</c>).
/// </para>
///
/// These tests assert the deliverable spine: a XAML-loaded tree auto-focuses its first control on the
/// first idle frame, is Tab-navigable, and routes keys to a root-level handler — and the hand-built
/// equivalent does the same (proving the fix is layer-correct, not loader-local).
/// </summary>
public sealed class XamlFocusActivationTests
{
    private const string Ns =
        " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static readonly XamlLoader Loader = new();

    static XamlFocusActivationTests()
    {
        XamlSchemaContext.Default.RegisterAssembly(typeof(Phase6XamlEndToEndTests).Assembly);
        XamlSchemaContext.Default.RegisterDefaultNamespace(typeof(XamlTestBrush).Namespace!);
    }

    // The offending shape, the demo's spine: focusables behind ScrollViewer.Content → Border → StackPanel.
    private const string Doc =
        "<DockPanel" + Ns + ">" +
          "<ScrollViewer VerticalScrollBarVisibility=\"Auto\">" +
            "<Border BorderPen=\"Light\" Padding=\"1\">" +
              "<StackPanel Spacing=\"1\" Margin=\"1\">" +
                "<Button x:Name=\"Ok\"     Content=\"_OK\"/>" +
                "<Button x:Name=\"Cancel\" Content=\"_Cancel\"/>" +
                "<Button x:Name=\"Apply\"  Content=\"_Apply\"/>" +
              "</StackPanel>" +
            "</Border>" +
          "</ScrollViewer>" +
        "</DockPanel>";

    private static UIElement HandBuilt()
    {
        var root = new DockPanel();
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var border = new Border { BorderPen = Drawing.Media.Pens.Light, Padding = new Rendering.Margins(1) };
        var panel = new StackPanel { Spacing = 1, Margin = new Rendering.Margins(1) };
        panel.Children.Add(new Button { Name = "Ok", Content = "_OK" });
        panel.Children.Add(new Button { Name = "Cancel", Content = "_Cancel" });
        panel.Children.Add(new Button { Name = "Apply", Content = "_Apply" });
        border.Child = panel;
        sv.Content = border;
        root.Children.Add(sv);
        return root;
    }

    private static string Content(UIElement? e)
        => (e as ContentControl)?.Content as string ?? e?.GetType().Name ?? "<none>";

    [Fact]
    public void XamlLoadedTree_AutoFocusesFirstControl_IsTabNavigable_AndRoutesKeysToRoot()
    {
        using var host = UITestHost.Create();

        var root = Loader.Load<DockPanel>(Doc, source: null);

        var rootKeyHits = 0;
        root.AddHandler(UIElement.KeyDownEvent, (_, _) => rootKeyHits++);

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var focus = host.Application.FocusManager;

        // (1) Auto-focus on the first idle frame: the activation, parked because the first tab stop sat
        // behind the not-yet-realized ScrollViewer/ContentControl boundary, completed after layout and
        // placed focus on the FIRST tab-ordered focusable (the OK button) — not "focus: none".
        Assert.NotNull(focus.FocusedElement);
        Assert.Equal("_OK", Content(focus.FocusedElement));

        // (2) Tab cycles through the buttons (the visual subtree the navigator walks is now built).
        host.SendKey(Key.Tab);
        host.RunUntilIdle();
        Assert.Equal("_Cancel", Content(focus.FocusedElement));

        host.SendKey(Key.Tab);
        host.RunUntilIdle();
        Assert.Equal("_Apply", Content(focus.FocusedElement));

        // (3) A root-level KeyDown handler receives routed keys (the demo's shortcut path). Three sends
        // reach the root (Tab, Tab, and the character below).
        host.SendText("q");
        host.RunUntilIdle();
        Assert.Equal(3, rootKeyHits);
    }

    [Fact]
    public void HandBuiltTree_SameShape_AutoFocusesIdentically()
    {
        // The hand-built equivalent proves the fix is layer-correct (activation timing), not loader-local:
        // the identical nesting auto-focuses the first control on the first idle frame too.
        using var host = UITestHost.Create();

        var root = HandBuilt();
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var focus = host.Application.FocusManager;
        Assert.NotNull(focus.FocusedElement);
        Assert.Equal("_OK", Content(focus.FocusedElement));

        host.SendKey(Key.Tab);
        host.RunUntilIdle();
        Assert.Equal("_Cancel", Content(focus.FocusedElement));
    }

    [Fact]
    public void ParkedActivation_DoesNotOverride_FocusTheUserMovedBeforeLayout()
    {
        // If the application moves focus explicitly before the parked activation resolves, the post-layout
        // retry must NOT clobber it — the retry only fills EMPTY focus, never overrides a real one.
        using var host = UITestHost.Create();

        var root = Loader.Load<DockPanel>(Doc, source: null);
        host.Application.RootElement = root; // activation parks here (pre-layout, nothing reachable yet)

        var scope = NameScope.GetNameScope(root)!;
        // Content set members realize at layout; force one layout so the named parts exist, then move focus
        // explicitly to the LAST button before the parked first-focus would otherwise pick the first.
        host.RunFrame();
        var apply = (Button)scope.Find("Apply")!;
        Assert.True(host.Application.FocusManager.SetFocus(apply, FocusNavigationMethod.Programmatic));

        Assert.True(host.RunUntilIdle());

        // The explicit focus survived; the parked activation became a no-op (focus was no longer empty).
        Assert.Equal("_Apply", Content(host.Application.FocusManager.FocusedElement));
    }
}
