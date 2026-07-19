using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.Gallery.Pages;

/// <summary>
/// The Event Routing page: interactive scenarios laid out over the hierarchy relations the event route can
/// traverse — plain visual nesting, template chrome, presented content (logical reparenting), and the two
/// popup→owner bridges (a context menu's <c>PlacementTarget</c> hop, an in-tree <c>Popup</c>'s logical hop).
/// Every mouse-down / key-down whose target lies within the scenario column is captured via
/// <c>InputDispatcher.PreProcessInput</c> (before any routing runs) and its route — the same
/// <c>VisualParent ?? UIParent</c> walk <c>EventRoute.Build</c> performs — is rendered node by node:
/// element type, template chrome (dimmed, with its owner), template boundaries, and surface/bridge hops.
/// </summary>
/// <remarks>
/// A <see cref="UserControl"/> so the observer can (un)hook cleanly on tree attach/detach and the view root
/// scopes the capture filter: a mixed-chain ancestry walk from the scenario column (see
/// <c>IsOnRouteChain</c>) admits popup content anchored to scenario elements (it lives on a separate
/// surface) while excluding the route log, the nav shell, and every other page. The pane is an observation
/// instrument for the input-routing design review: each ══ bridge line marks exactly where a visual-only
/// route would end.
/// </remarks>
internal sealed class EventRoutingView : UserControl
{
    private const int MaxRenderedNodes = 40; // a route deeper than this is truncated (defensive; never expected)

    private readonly StackPanel _scenarios = new() { Orientation = Orientation.Vertical };
    private readonly StackPanel _mouseLog = new() { Orientation = Orientation.Vertical };
    private readonly StackPanel _keyLog = new() { Orientation = Orientation.Vertical };
    private readonly ScrollViewer _mouseScroll;
    private readonly ScrollViewer _keyScroll;

    private InputDispatcher? _dispatcher; // the dispatcher we subscribed (held for symmetric detach)

    /// <summary>
    /// The in-view filter — and it must follow the SAME chain the event route walks
    /// (<c>VisualParent ?? UIParent</c>): popup content anchored to a scenario element is connected only
    /// through that MIXED chain (visual hops within the popup surface, one <c>UIParent</c> hop at its root,
    /// visual hops through the owner). <see cref="UIElement.IsAncestorOf"/> is deliberately not used here:
    /// it tests the pure-logical OR pure-visual chain, and neither alone spans the popup seam — the
    /// pure-visual walk stops at the popup surface root, and the pure-logical walk breaks inside template
    /// chrome whose parts carry no logical/templated link of their own.
    /// </summary>
    private static bool IsOnRouteChain(UIElement ancestor, UIElement element)
    {
        for (var node = (UIElement?)element; node is not null; node = node.VisualParent ?? node.UIParent)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
        }

        return false;
    }

    public EventRoutingView()
    {
        _mouseScroll = new ScrollViewer { Content = _mouseLog };
        _keyScroll = new ScrollViewer { Content = _keyLog };
        BuildScenarios();
        Content = BuildLayout();
    }

    // ───────────────────────────── observer lifecycle ─────────────────────────────

    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(e);

        if (UIApplication.Current is { } app && _dispatcher is null)
        {
            _dispatcher = app.InputDispatcher;
            _dispatcher.PreProcessInput += OnPreProcessInput;
        }
    }

    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (_dispatcher is { } dispatcher)
        {
            dispatcher.PreProcessInput -= OnPreProcessInput;
            _dispatcher = null;
        }

        base.OnDetachedFromTree(e);
    }

    // PreProcessInput fires once per input event, BEFORE the route is built or walked — the sender is the
    // element the dispatcher resolved as the route target (hit element for mouse, focused element for keys).
    // Observation only: this handler must never set Handled. A prior subscriber (the KeyTip controller
    // subscribes at startup, so it runs first) may have consumed the event; that is captured as a note.
    private void OnPreProcessInput(object? sender, InputEventArgs e)
    {
        if (sender is not UIElement target || !IsOnRouteChain(_scenarios, target))
            return; // outside the scenario column

        if (ReferenceEquals(e.RoutedEvent, UIElement.PreviewMouseDownEvent) && e is MouseButtonEventArgs mouse)
            RenderRoute(_mouseLog, _mouseScroll, DescribeMouse(mouse), target, e.Handled, surfaceScoped: true);
        else if (ReferenceEquals(e.RoutedEvent, UIElement.PreviewKeyDownEvent) && e is KeyEventArgs { IsRepeat: false } key)
            RenderRoute(_keyLog, _keyScroll, DescribeKey(key), target, e.Handled, surfaceScoped: false);
    }

    private static string DescribeMouse(MouseButtonEventArgs e)
        => $"MouseDown · {e.Button} @ ({e.ScreenPosition.Column},{e.ScreenPosition.Row})";

    private static string DescribeKey(KeyEventArgs e)
    {
        var key = e.Key == Key.Character && !e.Text.IsEmpty ? $"'{e.Text}'" : e.Key.ToString();
        return e.Modifiers == KeyModifiers.None ? $"KeyDown · {key}" : $"KeyDown · {e.Modifiers}+{key}";
    }

    // ───────────────────────────── route rendering ─────────────────────────────

    /// <summary>
    /// Renders the route the dispatcher will build for <paramref name="target"/> — the same
    /// <c>VisualParent ?? UIParent</c> walk as <c>EventRoute.Build</c> (tunnel runs it root→target, bubble
    /// target→root). Nodes above this view are summarized in one line: the shell chrome is identical for
    /// every route and would drown the interesting part. A route that crossed a surface seam is scrolled to
    /// its OWNER end — a popup route is deep enough that the ══ bridge lines (the pane's headline) would
    /// otherwise sit below the log viewport; a plain route stays scrolled to its target end.
    /// </summary>
    private void RenderRoute(StackPanel log, ScrollViewer scroll, string header, UIElement target,
                             bool consumedPreRoute, bool surfaceScoped)
    {
        log.Children.Clear();
        AddLine(log, consumedPreRoute ? header + "  (consumed pre-route)" : header, ThemeKeys.AccentBrush);

        var hasBridge = RenderRouteNodes(log, target, surfaceScoped);

        // The presenter coerces the offset against the post-layout extent, so an over-large value pins the
        // view to the content's bottom once this frame's layout runs.
        scroll.VerticalOffset = hasBridge ? int.MaxValue : 0;
    }

    private bool RenderRouteNodes(StackPanel log, UIElement target, bool surfaceScoped)
    {
        var index = 0;
        var hasBridge = false;

        var route = EventRoute.Rent();

        try
        {
            route.Build(target, surfaceScoped);

            for (var i = 0; i < route.Count; i++)
            {
                var node = route[i];

                if (index >= MaxRenderedNodes)
                {
                    AddLine(log, "     ⋯ truncated", ThemeKeys.MutedBrush);
                    return hasBridge;
                }

                var isChrome = node.TemplatedParent is not null;
                AddLine(log, NodeLine(index, node), isChrome ? ThemeKeys.TextDimBrush : ThemeKeys.TextBrush);
                index++;

                if (ReferenceEquals(node, this))
                {
                    // The rest of the walk is the gallery shell — same for every route; one summary line.
                    var remaining = 0;

                    for (var above = node.VisualParent ?? node.UIParent;
                         above is not null;
                         above = above.VisualParent ?? above.UIParent)
                        remaining++;

                    if (remaining > 0)
                        AddLine(log, $"     ⋯ +{remaining} ancestors to root (gallery shell)", ThemeKeys.MutedBrush);

                    return hasBridge;
                }

                // The hop annotation describes how the walk reaches the NEXT node. A visual hop needs no line
                // unless it crosses a template boundary; a null-VisualParent hop is a surface/bridge crossing —
                // exactly where a visual-only route would stop.
                if (node.VisualParent is {} visual)
                {
                    if (ReferenceEquals(visual, node.TemplatedParent))
                        AddLine(log, "     ── template boundary ──", ThemeKeys.InfoBrush);
                }
                else if (node.UIParent is {} bridge)
                {
                    var kind = ReferenceEquals(bridge, node.LogicalParent) ? "logical bridge"
                               : ReferenceEquals(bridge, node.TemplatedParent) ? "templated bridge"
                               : "placement-target bridge";

                    AddLine(log, $"     ══ surface → owner · {kind} ══", ThemeKeys.WarningBrush);
                    hasBridge = true;
                }
            }
        }
        finally
        {
            EventRoute.Return(route);
        }

        return hasBridge;
    }

    /// <summary>One route node: index, type, name, and relation badges (template chrome owner; a logical
    /// parent that diverges from the visual chain — presented content's reparenting made visible).</summary>
    private static string NodeLine(int index, UIElement node)
    {
        var line = $"{index,3}  {node.GetType().Name}";

        if (!string.IsNullOrEmpty(node.Name))
            line += $" \"{node.Name}\"";

        if (node.TemplatedParent is { } owner)
            line += $"  ⟨chrome of {owner.GetType().Name}⟩";
        else if (node.LogicalParent is { } logical && !ReferenceEquals(logical, node.VisualParent ?? node.UIParent))
            line += $"  ⟨logical: {logical.GetType().Name}⟩";

        return line;
    }

    private static void AddLine(StackPanel log, string text, string brushKey)
    {
        var line = new TextBlock { Text = text };
        line.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        log.Children.Add(line);
    }

    // ───────────────────────────── scenario column ─────────────────────────────

    private void BuildScenarios()
    {
        // 1 · Plain nesting + the template chrome every control click passes through.
        _scenarios.Children.Add(Group("1 · Nesting + template chrome",
            new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children =
                {
                    new Button { Name = "ClickMe", Content = "Click Me" },
                    new TextBox { Name = "Input", Text = "Focus me, type keys" },
                },
            }));

        // 2 · Presented content: the Button's LogicalParent is the ContentControl, but its VISUAL parent is
        // the ContentPresenter inside the ContentControl's chrome — the route follows the visual chain, and
        // the ⟨logical: …⟩ badge shows the divergence.
        _scenarios.Children.Add(Group("2 · Presented content (logical reparenting)",
            new ContentControl
            {
                Name = "Presenter",
                Content = new Button { Name = "Presented", Content = "Presented Button" },
            }));

        // 3 · Context menu: the menu's popup surface bridges back to the owner via the Popup's
        // PlacementTarget (the menu is attached to nothing else) — the placement-target bridge. Focusable so
        // a keyboard-only user can Tab here and open the menu with the Menu key (then arrow/type inside —
        // key routes from the items cross the same bridge).
        var contextHost = new ContentControl
        {
            Name = "RightClickMe",
            Content = "Right-click here (or ▤ Menu key)",
            Focusable = true,
            ContextMenu = new ContextMenu
            {
                Items =
                {
                    new MenuItem { Header = "Cu_t" },
                    new MenuItem { Header = "_Copy" },
                    new MenuItem { Header = "_Paste" },
                },
            },
        };
        _scenarios.Children.Add(Group("3 · Context menu · placement bridge", contextHost));

        // 4 · An in-tree Popup: its Child roots a separate surface, but the Child's LogicalParent is the
        // Popup element sitting HERE in the host tree — the logical bridge (contrast with scenario 3).
        var inPopupButton = new Button { Name = "InPopup", Content = "Inside Popup" };
        var popup = new Popup
        {
            Name = "TreePopup",
            Placement = PlacementMode.Bottom,
            // The drop-toggle convention (ComboBox/DatePicker/Toolbar): an anchor press must route to the
            // toggle's own Click (which closes), not light-dismiss first — otherwise the release's Click
            // re-checks the toggle and the popup instantly REOPENS (the dismiss/toggle race).
            KeepOpenOnAnchorPress = true,
            Child = new Border
            {
                Padding = new Margins(1, 0),
                Child = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children =
                    {
                        new TextBlock { Text = "A separate surface." },
                        inPopupButton,
                    },
                },
            },
        };
        popup.Child.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationHighest);

        var toggle = new ToggleButton { Name = "PopupToggle", Content = "Show Popup" };
        popup.PlacementTarget = toggle;
        toggle.IsCheckedChanged += (_, _) => popup.IsOpen = toggle.IsChecked == true;
        popup.Closed += (_, _) => toggle.IsChecked = false; // light-dismiss/Esc writes back to the toggle
        popup.Opened += (_, _) => UIApplication.Current?.FocusManager.SetFocus(inPopupButton); // keyboard-only:
        // key routes from popup content are producible without a pointer; close restores focus to the toggle

        _scenarios.Children.Add(Group("4 · Popup · logical bridge",
            new StackPanel { Orientation = Orientation.Vertical, Children = { toggle, popup } }));
    }

    private static Border Group(string caption, UIElement content)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        var header = new TextBlock { Text = caption };
        header.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextDimBrush);
        panel.Children.Add(header);
        panel.Children.Add(content);

        var group = new Border { Padding = new Margins(1, 0), Margin = new Margins(0, 0, 0, 1), Child = panel };
        group.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationWell);
        return group;
    }

    // ───────────────────────────── page layout ─────────────────────────────

    private UIElement BuildLayout()
    {
        var scenarioColumn = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = 38,
            Margin = new Margins(0, 0, 2, 0),
            Children = { _scenarios },
        };

        var legend = new TextBlock { Text = "dim = template chrome · ── template boundary · ══ surface/bridge hop (a visual-only route stops there)" };
        legend.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.MutedBrush);

        var logColumn = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(legend, Dock.Top);
        logColumn.Children.Add(legend);

        var logs = new Grid
        {
            RowDefinitions = { new RowDefinition { Height = GridLength.Star(1) }, new RowDefinition { Height = GridLength.Star(1) } },
        };
        logs.Children.Add(LogSection("Mouse-down route", _mouseScroll, row: 0));
        logs.Children.Add(LogSection("Key-down route", _keyScroll, row: 1));
        logColumn.Children.Add(logs);

        var root = new DockPanel { LastChildFill = true, Margin = new Margins(1, 0) };
        DockPanel.SetDock(scenarioColumn, Dock.Left);
        root.Children.Add(scenarioColumn);
        root.Children.Add(logColumn);
        return root;
    }

    private static UIElement LogSection(string title, ScrollViewer scroll, int row)
    {
        var header = new TextBlock { Text = title };
        header.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextDimBrush);

        var section = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        section.Children.Add(header);
        section.Children.Add(scroll);

        Grid.SetRow(section, row);
        return section;
    }
}
