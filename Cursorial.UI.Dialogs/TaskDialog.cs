using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Dialogs.Themes;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Dialogs;

public static class TaskDialog
{
    /// <summary>
    /// Shows the dialog modally and completes with the activated button when it closes.
    /// </summary>
    /// <param name="application">The application for which the dialog is to be shown.</param>
    /// <param name="request">The dialog in the FB-12 shape.</param>
    /// <param name="cancellationToken">
    /// Force-closes the dialog when canceled. Cancellation never throws through this call — it
    /// completes with a dismissed result (<see cref="TaskDialogResult.IsDismissed"/>), the same
    /// outcome as a window-chrome close.
    /// </param>
    public static async Task<TaskDialogResult> ShowAsync(UIApplication application,
                                                         TaskDialogRequest request,
                                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<TaskDialogButton> buttons =
            request.Buttons is { Count: > 0 } ? request.Buttons : [TaskDialogButton.Close];

        var chosen = await ShowCoreAsync(application, request, cancellationToken).ConfigureAwait(false);

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
            return await application.Dispatcher
                                    .InvokeAsync(() => ShowOnUIThreadAsync(request,
                                                                           viaMarshal: true,
                                                                           cancellationToken))
                                    .ConfigureAwait(false);
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

        var application = UIApplication.Current!;

        var root = new Grid
                   {
                       MaxWidth = (wm?.ScreenSize.Columns + 1) / 2 ?? 40,
                       MaxHeight = wm?.ScreenSize.Rows ?? LayoutMath.Unbounded
                   };

        var children = new List<UIElement>();
        
        const int mainInstructionRow = 0;
        const int messageRow = mainInstructionRow + 1;
        const int progressRow = messageRow + 1;
        const int commandLinksRow = progressRow + 1;
        const int footerRow = commandLinksRow + 1;
        const int expanderToggleRow = footerRow + 1;
        const int expandedContentRow = expanderToggleRow + 1;
        
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star(), MaxWidth = root.MaxWidth});

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Main Instruction
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Message
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Progress Bar
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Command Links
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Expander Toggle
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Expanded Content
        
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);
        // Up/Down/Left/Right cycle the buttons (Tab order falls out of the window root's Cycle trap).
        KeyboardNavigation.SetDirectionalNavigation(root, DirectionalNavigationMode.Cycle);

        var mainInstruction = new ContentPresenter
                              {
                                  Content = request.MainInstruction,
                                  ShowTrimmedContentInToolTip = true,
                                  Margin = new Margins(2, 1, 2, 1)
                              };
        
        mainInstruction.SetValue(TextElement.TextWeightProperty, TextWeight.Bold);
        mainInstruction.SetValue(TextElement.TextWrappingProperty, WrapMode.WordWrap);
        mainInstruction.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.AccentBrush);
        
        Grid.SetRow(mainInstruction, mainInstructionRow);
        children.Add(mainInstruction);
        
        if (request.Content is {} message)
        {
            var content = new ContentPresenter
                          {
                              Content = message,
                              Margin = new Margins(2, 0, 2, 1),
                              ShowTrimmedContentInToolTip = true
                          };
            content.SetValue(TextElement.TextWrappingProperty, WrapMode.WordWrap);
            Grid.SetRow(content, messageRow);
            root.RowDefinitions[messageRow].Height = GridLength.Star();
            children.Add(content);
        }

        var window = new Window
                     {
                         Content = root,
                         Title = request.Title ?? string.Empty,
                         CanClose = false,
                         CanResize = false,
                         Padding = Margins.Zero,
                         SizeToContent = SizeToContent.WidthAndHeight,
                         SizeToContentMode = SizeToContentMode.Always,
                         AutoFitToViewport = true, // an expanded dialog stays on-screen (dialog-specific; the framework default is the fit badge)
                         WindowStartupLocation = WindowStartupLocation.CenterScreen,
                         Shadow = WindowShadow.Default,
                         Resources = { MergedDictionaries = { CursorialDialogThemes.BuiltIn } }
                     };

        var themeClass = request.Severity switch
                         {
                             TaskDialogSeverity.Information => ThemeClass.Info,
                             TaskDialogSeverity.Question    => ThemeClass.Cool,
                             TaskDialogSeverity.Warning     => ThemeClass.Warning,
                             TaskDialogSeverity.Error       => ThemeClass.Danger,
                             TaskDialogSeverity.Success     => ThemeClass.Success,
                             _                              => null
                         };

        if (themeClass is not null)
            window.Classes.Add(themeClass);
        
        window.SetResourceReference(Control.BackgroundProperty, ThemeKeys.ElevationDialog);

        Button? focusTarget = null;
        ProgressBar? progressBar = null;

        window.ContentRendered += OnWindowContentRendered;

        [SuppressMessage("ReSharper", "AccessToModifiedClosure")]
        void OnWindowContentRendered(object? sender, EventArgs e)
        {
            window.ContentRendered -= OnWindowContentRendered;
            focusTarget?.Focus();
        }

        var buttons = request.Buttons;

        if (buttons.Count == 0)
            buttons = [TaskDialogButton.Close with { IsDefault = true, IsCancel = true}];
        
        List<Button>? commandLinkButtons = null;
        List<Button>? standardButtons = null;
        
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

                if (definition.Class is {} buttonClass)
                    button.Classes.Add(buttonClass);

                standardButtons ??= new List<Button>();
                standardButtons.Add(button);
            }

            if (buttons.Count == 1)
            {
                // If there's only one button, make it the default.
                button.IsDefault = true;

                // If it happens to be 'Close', make it the cancel button too.
                if (definition == TaskDialogButton.Close)
                    button.IsCancel = true;
            }

            var index = i;

            button.Click += (_, _) => window.Close(dialogResult: index);

            if (button.IsDefault)
                focusTarget = button;

            if (button.IsCancel)
                window.CanClose = true;
        }

        
        DockPanel? footerPanel = null;
        CheckBox? verificationBox = null;

        if (request.VerificationText is {} verificationText)
        {
            verificationBox = new CheckBox
                              {
                                  Content = verificationText,
                                  Margin = new Margins(0, 0, 1, 0),
                                  IsChecked = request.VerificationChecked
                              };

            verificationBox.SetBinding(ToggleButton.IsCheckedProperty,
                                       new Binding(nameof(TaskDialogRequest.VerificationChecked)) { Source = request });

            DockPanel.SetDock(verificationBox, Dock.Left);

            footerPanel ??= new DockPanel();
            footerPanel.Children.Add(verificationBox);

        }

        if (request.ExpandedInformation is {} expandedContent)
        {
            var toggle = new ToggleButton
                         {
                             Content = "⌄",
                             Width = 3,
                             Height = 1,
                             Padding = new Margins(1, 0),
                             HorizontalAlignment = HorizontalAlignment.Left
                         };


            if (verificationBox is not null)
            {
                toggle.Margin = new Margins(2, 0, 1, 1);
                Grid.SetRow(toggle, expanderToggleRow);
                children.Add(toggle);
            }
            else
            {
                DockPanel.SetDock(toggle, Dock.Left);
                footerPanel ??= new DockPanel();
                footerPanel.Children.Add(toggle);
            }

            var footerPresenter = new ContentPresenter
                                  {
                                      Content = expandedContent,
                                      RecognizesMarkup = request.ExpandedInformationContainsMarkup,
                                      ShowTrimmedContentInToolTip = true
                                  };

            footerPresenter.SetValue(TextElement.TextWrappingProperty, WrapMode.WordWrap);

            var expandedContentHost = new Border
                                      {
                                          Child = footerPresenter,
                                          Padding = new Margins(2, 0, 2, 1),
                                          Visibility = toggle.IsChecked is true 
                                                           ? Visibility.Visible 
                                                           : Visibility.Collapsed
                                      };
            
            void OnToggleIsCheckedChanged(object? o, RoutedEventArgs routedEventArgs)
            {
                expandedContentHost.Visibility = toggle.IsChecked is true
                                                     ? Visibility.Visible
                                                     : Visibility.Collapsed;

                toggle.Content = toggle.IsChecked is true ? "⌃" : "⌄";
            }

            toggle.IsCheckedChanged += OnToggleIsCheckedChanged;

            Grid.SetRow(expandedContentHost, expandedContentRow);
            children.Add(expandedContentHost);
        }

        if (standardButtons is { Count: > 0 })
        {
            var buttonsPanel = new StackPanel
                               {
                                   Orientation = Orientation.Horizontal,
                                   HorizontalAlignment = HorizontalAlignment.Right,
                                   Spacing = 1
                               };

            foreach (var button in standardButtons)
                buttonsPanel.Children.Add(button);

            DockPanel.SetDock(buttonsPanel, Dock.Right);
            
            footerPanel ??= new DockPanel();
            footerPanel.Children.Add(buttonsPanel);
        }

        if (footerPanel is not null || verificationBox is not null)
        {
            var footer = new Border();

            footer.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.ElevationWell);

            Grid.SetRow(footer, footerRow);
            Grid.SetRowSpan(footer, 2);

            root.Children.Add(footer); // add first, before `children`.

            if (footerPanel is not null)
            {
                footerPanel.Margin = new Margins(2, 1);
                Grid.SetRow(footerPanel, footerRow);
                children.Add(footerPanel);
            }
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

            Grid.SetRow(border, commandLinksRow);

            children.Add(border);
        }
        
        children.Sort((e1, e2) => Grid.GetRow(e1).CompareTo(Grid.GetRow(e2)));

        foreach (var child in children)
            root.Children.Add(child);
        
        if (focusTarget is not null)
        {
            // Window.Shown is only raised on the modeless Show() path (never by ShowDialogAsync), so
            // initial focus rides the first activation — raised synchronously while the manager shows
            // the dialog, after its content is attached and provisionally measured.
            FocusManager.SetFocusedElement(window, focusTarget);
        }
        
        request.ProgressChanged += OnRequestProgressChanged;

        void OnRequestProgressChanged(int? progress)
        {
            if (application.Dispatcher.CheckAccess() is false)
            {
                application.Dispatcher.Post(progress, OnRequestProgressChanged);
                return;
            }

            var pb = progressBar;

            if (pb is null)
            {
                progressBar = pb = new ProgressBar
                                   {
                                       Minimum = 0d,
                                       Maximum = 100d,
                                       Margin = new Margins(2, 0, 2, 1)
                                   };
                Grid.SetRow(pb, progressRow);
                root.Children.Add(pb);

            }

            if (progress is {} current)
            {
                pb.Value = current;

                if (pb.IsIndeterminate)
                    pb.IsIndeterminate = false;
            }
            else if (pb.IsIndeterminate is false)
            {
                pb.IsIndeterminate = true;
            }
        }

        try
        {
            var result = await window.ShowDialogAsync(cancellationToken) is int chosen ? chosen : -1;
            request.ProgressChanged -= OnRequestProgressChanged;
            return result;

        }
        catch (OperationCanceledException)
        {
            return -1; // dismissal-by-cancellation: the forced close carries no chosen button
        }
    }
}