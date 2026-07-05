using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Dialogs.Themes;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Dialogs;

public sealed class TaskDialogService(UIApplication application) : ITaskDialogService
{
    private readonly UIApplication _application = application ?? throw new ArgumentNullException(nameof(application));

    /// <inheritdoc/>
    public async Task<TaskDialogResult> ShowAsync(TaskDialogRequest request,
                                                  CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<TaskDialogButton> buttons =
            request.Buttons is { Count: > 0 } ? request.Buttons : [TaskDialogButton.Ok];

        var chosen = await ShowCoreAsync(_application, request, cancellationToken).ConfigureAwait(false);

        return new TaskDialogResult(chosen >= 0 ? buttons[chosen] : null, request.VerificationChecked);
    }
    
    /// <summary>
    /// The label-driven core the public flags API and <see cref="MessageBoxTaskDialogService"/> both
    /// funnel through: shows the box with arbitrary button captions and completes with the index of
    /// the chosen button, or <c>-1</c> on dismissal without a choice (cancellation handled here — the
    /// <see cref="OperationCanceledException"/> a canceled dialog throws never escapes).
    /// </summary>
    internal static async Task<int> ShowCoreAsync(UIApplication application,
                                                  TaskDialogRequest request,
                                                  CancellationToken cancellationToken)
    {
        // On the UI thread the show runs unguarded (viaMarshal: false): a missing WindowManager
        // there is programmer error (show before RunAsync composed it) and must fail loudly with
        // ShowDialogAsync's InvalidOperationException, never be silently dismissed.
        if (application.Dispatcher.CheckAccess())
            return await ShowOnUIThreadAsync(request, viaMarshal: false, cancellationToken).ConfigureAwait(false);

        try
        {
            return await application.Dispatcher.InvokeAsync(
                () => ShowOnUIThreadAsync(request, viaMarshal: true, cancellationToken)
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A shut-down dispatcher returns a canceled task without ever running the delegate, so
            // ShowOnUIThreadAsync's own OCE handling never engages — the no-throw contract maps this
            // to dismissal, the same as an in-dialog cancellation.
            return -1;
        }
    }
        
    private static async Task<int> ShowOnUIThreadAsync(TaskDialogRequest request,
                                                       bool viaMarshal,
                                                       CancellationToken cancellationToken)
    {
        var wm = UIApplication.Current?.WindowManager;

        // Shutdown race — the MARSHALED path only: a marshaled show can be dispatched while (or
        // after) teardown removes the window manager on this same UI thread — Window.ShowDialogAsync
        // would throw InvalidOperationException. The no-throw contract maps a dialog requested
        // against a dying application to dismissal. This mirrors ShowDialogAsync's own resolution
        // chain and is synchronous with the show below (single UI thread), so the check cannot go
        // stale. Scoping the check to the marshaled branch keeps a direct on-UI-thread show against
        // a not-yet-started application failing loudly (programmer error) instead of silently
        // dismissing. Accepted edge: a MARSHALED show dispatched after Build but before RunAsync
        // composes the WindowManager also maps to dismissal — indistinguishable from the teardown
        // race at this seam.
        if (viaMarshal && wm is null)
            return -1;

        var root = new DockPanel { LastChildFill = true, MaxWidth = (wm?.ScreenSize.Columns + 1) / 2 ?? 40 };
        
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);

        var mainInstruction = new TextBlock
                              {
                                  Text = request.MainInstruction,
                                  TextWrapping = WrapMode.WordWrap,
                                  Margin = new Margins(2, 1, 2, 1)
                              };
        
        mainInstruction.SetValue(TextElement.TextAttributesProperty, TextAttributes.Bold);
        mainInstruction.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.AccentBrush);
        
        UIElement mainChild = mainInstruction;
        
        if (request.Content is {} message)
        {
            var content = new ContentPresenter { Content = message, Margin = new Margins(2, 0, 2, 1)};
            DockPanel.SetDock(mainInstruction, Dock.Top);
            root.Children.Add(mainInstruction);
            mainChild = content;
        }

        var window = new Window
                     {
                         Content = root,
                         Title = request.Title ?? string.Empty,
                         CanClose = false,
                         CanResize = false,
                         Padding = Margins.Zero,
                         SizeToContent = SizeToContent.WidthAndHeight,
                         WindowStartupLocation = WindowStartupLocation.CenterScreen,
                         Shadow = WindowShadow.Default,
                         Resources = { MergedDictionaries = { CursorialDialogThemes.BuiltIn } }
                     };

        window.SetResourceReference(Control.BackgroundProperty, ThemeKeys.ElevationDialog);
        
        var buttons = request.Buttons;
        
        Panel? allButtons = null;
        List<Button>? commandLinkButtons = null;
        List<Button>? standardButtons = null;

        Button? focusTarget = null;
        
        for (var i = 0; i < buttons.Count; i++)
        {
            var definition = buttons[i];

            Button button;

            if (definition.Explanation is { Length: > 0 })
            {
                button = new CommandLink{ ButtonDefinition = definition };
                commandLinkButtons ??= new List<Button>();
                commandLinkButtons.Add(button);
            }
            else
            {
                button = new Button
                         {
                             Content = definition.Label,
                             IsDefault = definition.IsDefault,
                             IsCancel = definition.IsCancel
                         };
                standardButtons ??= new List<Button>();
                standardButtons.Add(button);
            }

            var index = i;

            button.Click += (_, _) => window.Close(dialogResult: index);

            if (button.IsDefault)
                focusTarget = button;

            if (button.IsCancel)
                window.CanClose = true;
        }

        if (commandLinkButtons is { Count: > 0 })
        {
            var commandLinks = new StackPanel
                               {
                                   Orientation = Orientation.Vertical,
                                   HorizontalAlignment = HorizontalAlignment.Stretch,
                                   Spacing = 1
                               };

            foreach (var button in commandLinkButtons)
                commandLinks.Children.Add(button);
            
            var border = new Border { Child = commandLinks, Padding = new(2, 0, 2, 1) };
            
            allButtons ??= new StackPanel { Orientation = Orientation.Vertical };
            allButtons.Children.Add(border);
        }

        if (standardButtons is { Count: > 0 })
        {
            var buttonsPanel = new StackPanel
                               {
                                   Orientation = Orientation.Horizontal,
                                   HorizontalAlignment = HorizontalAlignment.Right,
                                   Spacing = 1
                               };

            var border = new Border { Child = buttonsPanel, Padding = new(2, 1) };
            
            border.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.ElevationWell);
            
            foreach (var button in standardButtons)
                buttonsPanel.Children.Add(button);

            allButtons ??= new StackPanel { Orientation = Orientation.Vertical };
            allButtons.Children.Add(border);
        }

        if (allButtons is not null)
        {
            // Up/Down/Left/Right cycle the buttons (Tab order falls out of the window root's Cycle trap).
            KeyboardNavigation.SetDirectionalNavigation(allButtons, DirectionalNavigationMode.Cycle);
            // KeyboardNavigation.SetTabNavigation(allButtons, KeyboardNavigationMode.Once);

            DockPanel.SetDock(allButtons, Dock.Bottom);

            root.Children.Add(allButtons);
        }
        
        root.Children.Add(mainChild);

        if (focusTarget is not null)
        {
            // Window.Shown is only raised on the modeless Show() path (never by ShowDialogAsync), so
            // initial focus rides the first activation — raised synchronously while the manager shows
            // the dialog, after its content is attached and provisionally measured.
            window.Activated += OnActivated;

            void OnActivated(object? sender, EventArgs e)
            {
                window.Activated -= OnActivated;
                focusTarget!.Focus();
            }
        }

        try
        {
            return await window.ShowDialogAsync(cancellationToken) is int chosen ? chosen : -1;
        }
        catch (OperationCanceledException)
        {
            return -1; // dismissal-by-cancellation: the forced close carries no chosen button
        }
    }
}