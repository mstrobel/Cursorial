using System.Globalization;
using System.Reflection;
using System.Text;

using Cursorial.Animation;
using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text; // Margins
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;
using Cursorial.UI.Xaml;

// P9.8 — the XAML style-inspector (design doc §3.9 / proposal §2.8 "style inspector overlay"): load a .xaml
// file, render it on the real frame loop, then inspect the element under the cursor — for it, the overlay
// lists every property with a live, non-default contribution (UIObject.GetSetProperties) and its full
// provenance (StyleDiagnostics.Explain: value ← layer/lane + selector + the packed sort key, winning vs
// shadowed). "Debuggability made concrete": the report is exact and cheap because the styling slots are
// objects, not a re-run of a matching algorithm.
//   'o'      open a XAML source (a bundled sample from the list, or a file path you type),
//   hover    inspect the element under the pointer (motion terminals),
//   Tab      move focus through the loaded tree — the focused element is inspected (keyboard fallback),
//   't'/'d'  cycle the color tier / flip dark-light (the loaded tree re-skins; the inspector re-reads),
//   q / Esc  exit.
internal sealed class InspectorDemo : IDemo
{
    public string Name => "inspect";
    public IReadOnlyList<string> Aliases => ["inspector", "xaml"];

    public string Description =>
        "XAML style-inspector (P9.8): open a .xaml (bundled sample or a file path), render it, then hover/Tab an " +
        "element to see every non-default property and its provenance — value ← slot + selector + sort key (StyleDiagnostics.Explain).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("XAML inspector. Opening alt screen — press 'o' to open a XAML source (a bundled sample " +
                          "or a file path), then HOVER or Tab to an element to inspect its non-default properties and " +
                          "their provenance (value ← slot + selector). 't' cycles the color tier, 'd' flips dark/light; " +
                          "q / Esc exits.");

        var app = UIApplication.CreateBuilder().WithFrameRate(60).Build();
        // app.Theme = CursorialXamlTheme.LoadTheme();
        var controller = new Controller(app);

        app.Started += (_, _) => controller.OpenDialog();
        
        try
        {
            await app.RunAsync(controller.BuildDesktop);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // Bundled samples — declarative trees with a mix of LOCAL values (Width, explicit Background/BorderPen),
    // THEMED contributions (the control themes' SurfaceBrush/well/etc.), and INHERITED foreground, so the
    // inspector has rich provenance to show. The default xmlns maps the UI/Controls/Drawing.Media types.
    private static readonly (string Label, string Xaml)[] Samples =
    [
        ("Settings panel", """
                           <DockPanel xmlns="https://cursorial.dev/ui"
                                      xmlns:x="https://cursorial.dev/xaml"
                                      Background="{DynamicResource {x:Static ThemeKeys.WindowBackground}}"
                                      TextElement.Foreground="{DynamicResource {x:Static ThemeKeys.TextBrush}}">
                               <TextBlock DockPanel.Dock="Top" Text=" Settings " Margin="1,0"/>
                               <StackPanel Margin="2,1" Spacing="1">
                                   <CheckBox Content="_Airplane mode"/>
                                   <CheckBox Content="_Wi-Fi" IsChecked="True"/>
                                   <CheckBox Content="_Bluetooth"/>
                                   <StackPanel Orientation="Horizontal" Spacing="2" Margin="0,1,0,0">
                                       <Button Content="_OK" IsDefault="True" Width="10"/>
                                       <Button Content="_Cancel"/>
                                   </StackPanel>
                               </StackPanel>
                           </DockPanel>
                           """),
        ("Login form", """
                       <StackPanel xmlns="https://cursorial.dev/ui"
                                   xmlns:x="https://cursorial.dev/xaml"
                                   Background="{DynamicResource {x:Static ThemeKeys.WindowBackground}}"
                                   TextElement.Foreground="{DynamicResource {x:Static ThemeKeys.TextBrush}}"
                                   Margin="2,1" Spacing="1">
                           <Menu>
                             <MenuItem Header="_File">
                               <MenuItem Header="_New"/>
                               <MenuItem Header="_Open"/>
                               <Separator/>
                               <MenuItem Header="_Exit"/>
                             </MenuItem>
                           </Menu>
                           <TextBlock Text="Sign in"/>
                           <Label Content="User _name:"/>
                           <TextBox Placeholder="username" Width="24"/>
                           <Label Content="_Password:"/>
                           <TextBox Placeholder="••••••••" Width="24"/>
                           <CheckBox Content="_Remember me"/>
                           <StackPanel Orientation="Horizontal" Spacing="2" Margin="0,1,0,0">
                               <Button Content="_Sign in" IsDefault="True"/>
                               <Button Content="_Cancel"/>
                           </StackPanel>
                       </StackPanel>
                       """),
        ("Tree view", """
                      <StackPanel xmlns="https://cursorial.dev/ui"
                                  xmlns:x="https://cursorial.dev/xaml"
                                  Background="{DynamicResource {x:Static ThemeKeys.WindowBackground}}"
                                  TextElement.Foreground="{DynamicResource {x:Static ThemeKeys.TextBrush}}"
                                  Margin="2,1" Spacing="1">
                          <TreeView>
                              <TreeViewItem Header="Item 1">
                                  <TreeViewItem Header="Item 1.1" />
                                  <TreeViewItem Header="Item 1.2" />
                              </TreeViewItem>
                              <TreeViewItem Header="Item 2" />
                              <TreeViewItem Header="Item 3" />
                              <TreeViewItem Header="Item 4">
                                  <TreeViewItem Header="Item 4.1">
                                      <TreeViewItem Header="Item 4.1.1" />
                                      <TreeViewItem Header="Item 4.1.2" />
                                  </TreeViewItem>
                                  <TreeViewItem Header="Item 4.2">
                                      <TreeViewItem Header="Item 4.2.1" />
                                      <TreeViewItem Header="Item 4.2.2" />
                                  </TreeViewItem>
                              </TreeViewItem>
                          </TreeView>
                      </StackPanel>
                      """),
        ("List view", """
                      <StackPanel xmlns="https://cursorial.dev/ui"
                                  xmlns:x="https://cursorial.dev/xaml"
                                  Background="{DynamicResource {x:Static ThemeKeys.WindowBackground}}"
                                  TextElement.Foreground="{DynamicResource {x:Static ThemeKeys.TextBrush}}"
                                  Margin="2,1" Spacing="1">
                          <ListBox SelectionMode="Single" VirtualizingPanel.IsVirtualizing="True" MaxHeight="6">
                              <ListBoxItem Content="alpha" />
                              <ListBoxItem Content="bravo" />
                              <ListBoxItem Content="charley" />
                              <ListBoxItem Content="delta" />
                              <ListBoxItem Content="echo" />
                              <ListBoxItem Content="foxtrot" />
                              <ListBoxItem Content="golf" />
                              <ListBoxItem Content="hotel" />
                              <ListBoxItem Content="india" />
                              <ListBoxItem Content="juliett" />
                              <ListBoxItem Content="kilo" />
                              <ListBoxItem Content="lima" />
                              <ListBoxItem Content="mike" />
                              <ListBoxItem Content="november" />
                              <ListBoxItem Content="oscar" />
                              <ListBoxItem Content="papa" />
                              <ListBoxItem Content="quebec" />
                              <ListBoxItem Content="romeo" />
                              <ListBoxItem Content="sierra" />
                              <ListBoxItem Content="tango" />
                              <ListBoxItem Content="uniform" />
                              <ListBoxItem Content="victor" />
                              <ListBoxItem Content="whiskey" />
                              <ListBoxItem Content="x-ray" />
                              <ListBoxItem Content="yankee" />
                              <ListBoxItem Content="zulu" />
                          </ListBox>
                      </StackPanel>
                      """),
    ];

    private sealed class Controller(UIApplication app)
    {
        private TextBlock _status = null!;
        private Border _canvas = null!; // hosts the loaded tree (or the placeholder / error)
        private StackPanel _inspectorContent = null!;
        private UIElement? _lastInspected;
        private UIElement? _lastInspectedParent;
        private UIElement? _lastInspectedRelative;
        private string _loaded = "(nothing)";
        private bool _isInspecting;

        public void ToggleInspection()
        {
            _isInspecting = !_isInspecting;
            _canvas.Cursor = _isInspecting ? MouseCursorShape.Crosshair : MouseCursorShape.Default;
            _canvas.ForceCursor = _isInspecting;
            Refresh();
        }

        private void Refresh(bool reevaluateTarget = true)
        {
            var target = reevaluateTarget 
                             ? app.InputDispatcher.LastHoverTarget 
                             : _lastInspected ?? app.InputDispatcher.LastHoverTarget;

            if (target is not null/* && _canvas.IsAncestorOf(target)*/)
                Inspect(target, forceRefresh: true);
        }

        public UIElement BuildDesktop()
        {
            var root = new DockPanel();
            root.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.WindowBackground);
            root.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextBrush);
            root.AddHandler(UIElement.KeyDownEvent, OnHotkey);

            var header = new Border
                         {
                             Child = new TextBlock(
                                 " Cursorial.UI — P9.8 XAML inspector   ·   o open   ·   hover / Tab to inspect   ·   t tier · d dark/light · q quit")
                         };

            header.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceBrush);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var statusBar = new Border();
            statusBar.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceBrush);
            _status = new TextBlock();
            statusBar.Child = _status;
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            // The inspector panel (docked right): a scrollable provenance report for the element under the cursor.
            _inspectorContent = new StackPanel { Margin = new Margins(1, 0) };

            var inspectorPanel = new Border
                                 {
                                     Width = 50,
                                     BorderPen = Pens.Light,
                                     Title = " inspector ",
                                     Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _inspectorContent },
                                 };

            inspectorPanel.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
            DockPanel.SetDock(inspectorPanel, Dock.Right);
            root.Children.Add(inspectorPanel);

            // The canvas (fills the rest): the loaded tree. Hover / focus over it drives the inspector — the
            // handlers are scoped to the canvas, so hovering the inspector panel never inspects itself.
            _canvas = new Border { Child = Placeholder("Press 'o' to open a XAML source.") };

            HookHandlers(root);

            root.Children.Add(_canvas);

            Inspect(null);
            UpdateStatus();
            return root;
        }

        private static TextBlock Placeholder(string text) => new($"\n   {text}");

        // ───────────────────────────── keys ─────────────────────────────

        private void OnHotkey(object? sender, KeyEventArgs e)
        {
            if ((e.Modifiers & KeyModifiers.Control) != 0)
                return;

            if (e.Key == Key.Escape)
            {
                app.Shutdown();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Character || e.Text.Length == 0 || e.Modifiers is not KeyModifiers.Alt)
                return;

            switch (char.ToLowerInvariant(e.Text.Span[0]))
            {
                case 'q':
                    app.Shutdown();
                    e.Handled = true;
                    break;

                case 'o':
                    OpenDialog();
                    e.Handled = true;
                    break;

                case 'r':
                    Refresh(reevaluateTarget: false);
                    e.Handled = true;
                    break;

                case 't' :
                    app.RequestedColorTier = app.RequestedColorTier switch
                                             {
                                                 null                 => ColorDepth.Ansi256,
                                                 ColorDepth.Truecolor => ColorDepth.Ansi256,
                                                 ColorDepth.Ansi256   => ColorDepth.Ansi16,
                                                 ColorDepth.Ansi16    => ColorDepth.NoColor,
                                                 _                    => ColorDepth.Truecolor,
                                             };

                    ReinspectLast();
                    e.Handled = true;
                    break;

                case 'd':
                    app.RequestedThemeBase = app.ActualThemeVariant.IsDark ? ThemeBase.Light : ThemeBase.Dark;
                    ReinspectLast();
                    e.Handled = true;
                    break;
            }
        }

        // ───────────────────────────── open + load ─────────────────────────────

        internal async void OpenDialog()
        {
            var list = new ListBox { ItemsSource = Samples.Select(s => s.Label).ToArray(), Height = 4 };
            list.SelectedIndex = 0;
            var path = new TextBox { Placeholder = "…or a path to a .xaml file" };

            var open = new Button { Content = "_Open", IsDefault = true };
            var cancel = new Button { Content = "_Cancel", IsCancel = true, Margin = new Margins(1, 0, 0, 0) };

            var buttons = new StackPanel
                          {
                              Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                              Margin = new Margins(0, 1, 0, 0)
                          };

            buttons.Children.Add(open);
            buttons.Children.Add(cancel);

            var body = new StackPanel();
            body.Children.Add(new Label { Content = "_Sample:" });
            body.Children.Add(list);
            body.Children.Add(new TextBlock("\nOr load a file:"));
            body.Children.Add(path);
            body.Children.Add(buttons);

            var dialog = new Window
                         {
                             Title = "Open XAML",
                             Content = body,
                             WindowStartupLocation = WindowStartupLocation.CenterScreen,
                             Width = 50,
                             SizeToContent = SizeToContent.Height,
                             CanResize = false
                         };

            dialog.SetResourceReference(Control.BackgroundProperty, ThemeKeys.PanelBrush);

            open.Click += (_, _) =>
                          {
                              var typed = path.Text.Trim();

                              dialog.Close(typed.Length > 0 ? new OpenChoice(IsFile: true, typed)
                                               : new OpenChoice(IsFile: false, list.SelectedItem as string ?? Samples[0].Label));
                          };

            cancel.Click += (_, _) => dialog.Close(null);

            // HookHandlers(dialog);

            if (await dialog.ShowDialogAsync() is OpenChoice choice)
                LoadAndShow(choice);
        }

        private void HookHandlers(UIElement element)
        {
            element.AddHandler(UIElement.MouseMoveEvent,
                              (_, e) =>
                              {
                                  if (_isInspecting)
                                      Inspect(e.Source);
                              },
                              handledEventsToo: true);

            element.AddHandler(UIElement.PreviewKeyDownEvent,
                              (_, e) =>
                              {
                                  if (e is { Key: Key.F12, Modifiers: KeyModifiers.None })
                                  {
                                      ToggleInspection();
                                      e.Handled = true;
                                  }
                                  else if (e is { Key: Key.Character, Modifiers: KeyModifiers.Alt, Text.Span: "[" })
                                  {
                                      Inspect(_lastInspected, -1);
                                      e.Handled = true;
                                  }
                                  else if (e is { Key: Key.Character, Modifiers: KeyModifiers.Alt, Text.Span: "]" })
                                  {
                                      Inspect(_lastInspected, 1);
                                      e.Handled = true;
                                  }
                              });
        }

        private void LoadAndShow(OpenChoice choice)
        {
            string label, xaml;

            try
            {
                if (choice.IsFile)
                {
                    label = Path.GetFileName(choice.Value);

                    if (Directory.Exists(choice.Value))
                    {
                        ShowError($"\"{choice.Value}\" is a directory, not a .xaml file.");
                        return;
                    }

                    xaml = File.ReadAllText(choice.Value);
                }
                else
                {
                    label = choice.Value;
                    xaml = Samples.First(s => s.Label == choice.Value).Xaml;
                }
            }
            catch (Exception ex) // a bad path / unreadable file
            {
                ShowError($"Could not read \"{choice.Value}\":\n   {ex.Message}");
                return;
            }

            try
            {
                var tree = (UIElement) XamlLoader.Shared.Load(xaml);
                _loaded = label;
                _lastInspected = null;
                _lastInspectedParent = null;
                _lastInspectedRelative = null;
                _canvas.Child = tree; // render the loaded tree
                Inspect(null);
            }
            catch (Exception ex) // XamlParseException (line+col in the message) / type-resolution / cast
            {
                ShowError($"Failed to load \"{label}\":\n\n   {ex.Message}");
            }

            UpdateStatus();
        }

        private void ShowError(string message)
        {
            _loaded = "(load failed)";
            _lastInspected = null;
            _lastInspectedParent = null;
            _lastInspectedRelative = null;
            _canvas.Child = new StackPanel { Margin = new Margins(2, 1) };
            ((StackPanel) _canvas.Child!).Children.Add(new TextBlock($"\n⚠ {message}"));
            Inspect(null);
            UpdateStatus();
        }

        // ───────────────────────────── inspect ─────────────────────────────

        private void ReinspectLast()
        {
            var target = _lastInspected;
            _lastInspected = null; // force a rebuild (provenance changed after a theme flip)
            _lastInspectedParent = null;
            _lastInspectedRelative = null;
            Inspect(target);
        }

        private UIElement? AscendTree(UIElement? anchor, UIElement? current)
        {
            var e = current ?? anchor;
            return e?.VisualParent ?? e?.LogicalParent;
        }

        private UIElement? DescendTree(UIElement? anchor, UIElement? current)
        {
            if (current is null) return anchor;

            UIElement? e;
            UIElement? prev = anchor;

            for (e = anchor; e is not null; e = e.VisualParent ?? e.LogicalParent)
            {
                if (e == current) return prev;
                prev = e;
            }

            return anchor;
        }

        private UIElement? TemplatedParent(UIElement? anchor)
        {
            UIElement? e = anchor;

            while (e is not null)
            {
                if (e.TemplatedParent is not null) return e.TemplatedParent;

                if (e.LogicalParent is null) e = e.VisualParent;
                else break;
            }

            return e ?? anchor;
        }

        private void Inspect(UIElement? element, int direction = 0, bool forceRefresh = false)
        {
            var parent = TemplatedParent(element);

            if (ReferenceEquals(parent, _lastInspectedParent) && direction == 0 && forceRefresh is false)
                return;

            _lastInspected = element;
            _lastInspectedParent = parent;

            while (_inspectorContent.Children.Count > 0)
                _inspectorContent.Children.RemoveAt(_inspectorContent.Children.Count - 1);

            _lastInspectedRelative = direction switch
                                     {
                                         0   => _lastInspectedParent,
                                         > 0 => DescendTree(_lastInspected, _lastInspectedRelative),
                                         < 0 => AscendTree(_lastInspected, _lastInspectedRelative)
                                     };

            var current = _lastInspectedRelative;

            if (current is null)
            {
                _inspectorContent.Children.Add(new TextBlock("\n  Hover or Tab to an element\n  in the loaded tree.\n  " +
                                                             "Use [ and ] to transcend\n  template elements."));

                UpdateStatus();
                return;
            }

            var tree = new TreeView();

            tree.Items.Add(InspectNode(current));

            if (current is Control c)
            {
                var attributes = c.GetType().GetCustomAttributes(typeof(TemplatePartAttribute), true);

                foreach (var attribute in attributes.OfType<TemplatePartAttribute>())
                {
                    if (c.GetTemplatePart<UIElement>(attribute.Name) is {} part)
                        tree.Items.Add(InspectNode(part, attribute.Name));
                }
            }

            _inspectorContent.Children.Add(tree);
            UpdateStatus();
        }

        private TreeViewItem InspectNode(UIElement current, string? name = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                name = BuildElementPath(current);
            else
                name = current.GetType().Name is { Length: > 0 } tName ? $"{tName}#{name}" : name;

            var root = Node(name, NoValue, ThemeKeys.GreenBrush);

            var pseudoClasses = Enum.GetValues<InteractionState>()
                                    .Where(o => current.InteractionStateInternal.HasFlag(o))
                                    .Select(o => InteractionPseudoClasses.TryGetPseudoClass(o, out var c) ? c : null)
                                    .Where(c => c is not null)
                                    .Concat(current.PseudoClasses.CustomClasses)
                                    .Concat(current.Classes)
                                    .Aggregate(new StringBuilder(),
                                               (sb, c) => sb.Append(c).Append(", "),
                                               sb => sb.ToString())
                                    .Trim();

            root.Items.Add(Node("Classes", pseudoClasses));

            var properties = current.GetSetProperties().OrderBy(p => p.Name).ToArray();

            foreach (var property in properties)
            {
                // The winning derivation line (StyleDiagnostics.Explain is one line per contributor, strongest
                // first): "<prop> = <value> <- <Layer>(n) \"<selector>\" … -- winning" (or "<- LocalValue").
                // Guarded: a diagnostic must never crash the thing it inspects — a pathological value ToString()
                // in an arbitrarily-loaded tree degrades to an error line, not an unhandled hover-handler throw.
                StyleExplanation e;
                object? resourceKey;

                try
                {
                    e = StyleDiagnostics.ExplainDetails(current, property);
                    resourceKey = ResourceDiagnostics.GetResourceKey(current, property); // W3 resource-inspector hook
                }
                catch (Exception ex)
                {
                    return new TreeViewItem
                           {
                               Header = new TextBlock
                                        {
                                            Markup = $"[brush {ThemeKeys.RedBrush}][b]{property.Name} " +
                                                     $"Error:[/b]{ex.GetType().Name}[/brush]"
                                        },
                           };
                }

                var item = Node($"{property.OwnerType.Name}.{property.Name}", NoValue);

                item.Items.Add(Node(nameof(e.TargetDescription), e.TargetDescription));
                item.Items.Add(Node("Value", FormatValue(current.GetValue(property))));

                if (resourceKey is not null)
                    item.Items.Add(Node("Resource Key", FormatValue(resourceKey)));

                AttachStyleExplanation(item, e);

                if (BindingDiagnostics.Explain(current, property) is { HasBindings: true } bd)
                    AttachBindingExplanation(bd, item);

                //
                // // Append the within-lane provenance (PD25) and, for a resource-backed value, the resource KEY
                // // it resolved through (W3 — the resource-inspector companion to the style inspector).
                // var line = $"{winning} · {kind}";
                // if (resourceKey is not null)
                //     line += $" ← resource '{resourceKey}'";
                // _inspectorContent.Children.Add(new TextBlock(line));

                root.Items.Add(item);
            }

            return root;
        }

        private static void AttachStyleExplanation(TreeViewItem item, StyleExplanation e)
        {
            item.Items.Add(Node(nameof(e.Kind), e.Kind));
            item.Items.Add(Node(nameof(e.Priority), e.Priority));
            item.Items.Add(Node(nameof(e.BasePriority), e.BasePriority));
            item.Items.Add(Node(nameof(e.IsAnimated), e.IsAnimated));

            if (e.Frames is { Length: > 0 })
            {
                for (var i = 0; i < e.Frames.Length; i++)
                {
                    var frame = e.Frames[i];

                    var frameRoot = Node($"Frames[{i}]", NoValue);

                    foreach (var p in frame.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var value = p.GetValue(frame);

                        if (value is StyleSortKey k)
                        {
                            var keyRoot = Node(nameof(StyleFrameExplanation.SortKey), NoValue);

                            foreach (var fp in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                if (fp.Name == nameof(k.Packed))
                                    keyRoot.Items.Add(Node(fp.Name, k.Packed.ToString("X16")));
                                else
                                    keyRoot.Items.Add(Node(fp.Name, fp.GetValue(k)));
                            }
                        }
                        else
                        {
                            frameRoot.Items.Add(Node(p.Name, value));
                        }
                    }

                    item.Items.Add(frameRoot);
                }
            }
        }

        private static readonly object NoValue = new();

        private static TreeViewItem Node(string? name, object? value, string? brush = ThemeKeys.MutedBrush)
        {
            var hasName = name is not null;
            var type = value?.GetType() ?? typeof(object);

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                type = Nullable.GetUnderlyingType(type);

            var isSimple = type is { IsPrimitive: true } or { IsEnum: true } or { FullName: "System.String" };

            var header = hasName
                             ? isSimple ?
                                   $"[b][brush {brush}]{Sanitize(name)}:[/brush][/b] {FormatValue(value)}"
                                   : $"[b][brush {brush}]{Sanitize(name)}[/brush][/b]"
                             : FormatValue(value);

            var item = new TreeViewItem
                       {
                           IsExpanded = true,
                           Header = new TextBlock { Markup = header, TextWrapping = WrapMode.WordWrap }
                       };

            if (hasName && !isSimple && value != NoValue)
                item.Items.Add(Node(null, FormatValue(value)));

            return item;
        }

        private static string FormatValue(object? value)
        {
            // ReSharper disable once ArrangeRedundantParentheses
            var f = value switch
                    {
                        null     => "(null)",
                        string s => s,
                        Color c  => c.Kind == ColorKind.Rgb ? $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}{c.Alpha:X2}" : c.ToString(),
                        Pen p => $"Pen {{ Brush={FormatValue(p.Brush)}, Weight={p.Weight}, Corners={FormatValue(p.Corners)}, " +
                                 $"Dash={FormatValue(p.Dash)}, EndCap={FormatValue(p.EndCap)}, Junction={FormatValue(p.Junction)}, " +
                                 $"GlyphSet={FormatValue(p.GlyphSet)}, Attributes={FormatValue(p.Attributes)} }}",
                        SolidColorBrush sc => FormatValue(sc.Color),
                        LinearGradientBrush lg => $"linear:({lg.StartPoint.X},{lg.StartPoint.Y}) -> ({lg.EndPoint.X},{lg.EndPoint.Y}, " +
                                                  $"{string.Join(", ", lg.Stops.Select(s => s.Color.ToString()))})",
                        RadialGradientBrush rg => $"radial:({rg.Center.X},{rg.Center.Y}) -> ({rg.RadiusX},{rg.RadiusY}, " +
                                                  $"{string.Join(", ", rg.Stops.Select(s => s.Color.ToString()))})",
                        ConicGradientBrush cb => $"conic:({cb.Center.X},{cb.Center.Y}) -> ({cb.AngleDegrees}º, Center={cb.Center}, " +
                                                 $"{string.Join(", ", cb.Stops.Select(s => s.Color.ToString()))})",
                        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                        GlyphSetCarrier gc       => gc.ToString().Replace("[", "\\["),
                        _                        => HasToStringOverride(value) ? (value.ToString() ?? "(null)") : $"{{{value.GetType().Name}}}"
                    };

            return Sanitize(f);
        }

        private static void AttachBindingExplanation(BindingExplanation bd, TreeViewItem item)
        {
            var rootBindingItem = Node("Binding", NoValue);

            rootBindingItem.Items.Add(Node("Target", bd.TargetDescription));

            for (var i = 0; i < bd.Expressions.Length; i++)
            {
                var be = bd.Expressions[i];
                var bindingItem = Node($"Bindings[{i}]", NoValue);

                foreach (var p in be.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    bindingItem.Items.Add(Node(p.Name, p.GetValue(be)));

                rootBindingItem.Items.Add(bindingItem);
            }

            item.Items.Add(rootBindingItem);
        }

        private static bool HasToStringOverride(object? obj)
        {
            if (obj is null)
                return false;

            var type = obj.GetType();

            var toStringMethod = type.GetMethod(nameof(ToString),
                                                BindingFlags.Public | BindingFlags.Instance, null
                                                , Type.EmptyTypes,
                                                null);

            return toStringMethod is not null &&
                   toStringMethod.DeclaringType != typeof(object);
        }

        private static string Sanitize(object? value)
        {
            return value?.ToString()?.Replace("[", "\\[") ?? "(null)";
        }

        private static string BuildElementPath(UIElement current)
        {
            var tree = new List<UIElement>();
            var sb = new StringBuilder();

            for (UIElement? e = current; e is not null; e = e.VisualParent ?? e.LogicalParent ?? e.TemplatedParent)
                tree.Add(e);

            for (var index = tree.Count - 1; index >= 0; index--)
            {
                var e = tree[index];

                if (sb.Length > 0)
                    sb.Append('.');

                sb.Append(e.GetType().Name);

                if (e.Name is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name))
                    sb.Append($"#{e.Name}");
            }

            return sb.ToString();
        }

        private void UpdateStatus()
        {
            _status.Text = $" loaded: {_loaded} · theme: {app.ActualThemeVariant} — " +
                           "⌥+o open · F12 inspect · ⌥+[, ⌥+] traverse · ⌥+r refresh · ⌥+t tier · ⌥+d dark/light · ⌥+q quit".Replace("[", "\\[");
        }
    }

    private sealed record OpenChoice(bool IsFile, string Value);
}