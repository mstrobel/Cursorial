using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The classic modal message box (implementation-plan M1.WP10; ported from the Cursorial Gallery's
/// app-local implementation onto a public, app-agnostic API). A themed <see cref="Window"/> shown via
/// <see cref="Window.ShowDialogAsync{TResult}(CancellationToken)"/>: word-wrapped message, a
/// right-aligned button row, and the standard dialog keyboard grammar — Tab and Left/Right arrows move
/// between buttons (arrows cycle), Enter activates the focused button (falling back to the default
/// button), Esc activates the cancel button. All colors resolve through the theme's
/// <c>ThemeKeys</c> spine (window/button control themes) — nothing is hardcoded here.
/// </summary>
/// <remarks>
/// This type lives in the promotable FB-12 dialog suite: it references Cursorial only — no editor
/// coupling — so post-v1 promotion to a <c>Cursorial.UI.Dialogs</c>-style extensions package stays a
/// mechanical move (architecture §2.1; enforced by <c>ArchitectureTests</c>).
/// </remarks>
public static class MessageBox
{
    // The single button registry, in presentation order (affirmative → negative → dismissive).
    // One row per flag — caption (framework access-key convention: '_' marks the mnemonic) and
    // result live together so a future flag cannot be half-registered across parallel structures;
    // an unmapped flag fails loudly at the registry rather than silently reporting None.
    private static readonly (MessageBoxButton Flag, string Caption, MessageBoxResult Result)[] Buttons =
    [
        (MessageBoxButton.Ok,       "_OK",         MessageBoxResult.Ok),
        (MessageBoxButton.Yes,      "_Yes",        MessageBoxResult.Yes),
        (MessageBoxButton.Save,     "_Save",       MessageBoxResult.Save),
        (MessageBoxButton.No,       "_No",         MessageBoxResult.No),
        (MessageBoxButton.DontSave, "Do_n't Save", MessageBoxResult.DontSave),
        (MessageBoxButton.Cancel,   "_Cancel",     MessageBoxResult.Cancel)
    ];

    /// <summary>
    /// Shows a modal message box over <paramref name="application"/>'s window manager and completes
    /// with the chosen button when it closes.
    /// </summary>
    /// <param name="application">
    /// The owning application. When called off the UI thread, the call marshals to
    /// <paramref name="application"/>'s dispatcher.
    /// </param>
    /// <param name="message">The message body (word-wrapped; <c>\n</c> produces hard line breaks).</param>
    /// <param name="title">The window title (empty when null).</param>
    /// <param name="buttons">The buttons to show — at least one flag required.</param>
    /// <param name="focusedButton">
    /// The initially focused button; defaults to the effective default button.
    /// </param>
    /// <param name="defaultButton">
    /// The Enter-activated default button; defaults to the first shown button in presentation order.
    /// </param>
    /// <param name="cancelButton">
    /// The Esc-activated cancel button; defaults to <see cref="MessageBoxButton.Cancel"/> when shown,
    /// else none (Esc then does nothing).
    /// </param>
    /// <param name="cancellationToken">
    /// Force-closes the box when canceled. Cancellation is handled internally (the underlying
    /// <see cref="OperationCanceledException"/> never propagates) and yields
    /// <see cref="MessageBoxResult.None"/>.
    /// </param>
    /// <returns>
    /// The chosen button, or <see cref="MessageBoxResult.None"/> when the box was dismissed without a
    /// choice (cancellation or window-chrome close) — treat as cancel.
    /// </returns>
    public static async Task<MessageBoxResult> ShowAsync(UIApplication application,
                                                         string message,
                                                         string? title = null,
                                                         MessageBoxButton buttons = MessageBoxButton.Ok,
                                                         MessageBoxButton? focusedButton = null,
                                                         MessageBoxButton? defaultButton = null,
                                                         MessageBoxButton? cancelButton = null,
                                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(message);

        var shown = Array.FindAll(Buttons, b => buttons.HasFlag(b.Flag));

        if (shown.Length == 0)
            throw new ArgumentException("At least one button flag is required.", nameof(buttons));

        ValidatePick(focusedButton, buttons, nameof(focusedButton));
        ValidatePick(defaultButton, buttons, nameof(defaultButton));
        ValidatePick(cancelButton, buttons, nameof(cancelButton));

        var effectiveDefault = defaultButton ?? shown[0].Flag;
        var effectiveCancel = cancelButton
                              ?? (buttons.HasFlag(MessageBoxButton.Cancel) ? MessageBoxButton.Cancel : null);
        var effectiveFocused = focusedButton ?? effectiveDefault;

        var definitions = new MessageBoxButtonDefinition[shown.Length];

        for (var i = 0; i < shown.Length; i++)
            definitions[i] = new MessageBoxButtonDefinition(shown[i].Caption,
                                                            IsDefault: shown[i].Flag == effectiveDefault,
                                                            IsCancel: shown[i].Flag == effectiveCancel);

        var chosen = await ShowCoreAsync(application,
                                         message,
                                         title,
                                         definitions,
                                         Array.FindIndex(shown, b => b.Flag == effectiveFocused),
                                         cancellationToken).ConfigureAwait(false);

        return chosen >= 0 ? shown[chosen].Result : MessageBoxResult.None;
    }

    /// <summary>
    /// The label-driven core the public flags API and <see cref="MessageBoxTaskDialogService"/> both
    /// funnel through: shows the box with arbitrary button captions and completes with the index of
    /// the chosen button, or <c>-1</c> on dismissal without a choice (cancellation handled here — the
    /// <see cref="OperationCanceledException"/> a canceled dialog throws never escapes).
    /// </summary>
    internal static async Task<int> ShowCoreAsync(UIApplication application,
                                                  string message,
                                                  string? title,
                                                  IReadOnlyList<MessageBoxButtonDefinition> buttons,
                                                  int focusedIndex,
                                                  CancellationToken cancellationToken)
    {
        // On the UI thread the show runs unguarded (viaMarshal: false): a missing WindowManager
        // there is programmer error (show before RunAsync composed it) and must fail loudly with
        // ShowDialogAsync's InvalidOperationException, never be silently dismissed.
        if (application.Dispatcher.CheckAccess())
        {
            return await ShowOnUIThreadAsync(message,
                                             title,
                                             buttons,
                                             focusedIndex,
                                             viaMarshal: false,
                                             cancellationToken)
                      .ConfigureAwait(false);

        }

        try
        {
            return await application.Dispatcher.InvokeAsync(
                () => ShowOnUIThreadAsync(message,
                                          title,
                                          buttons,
                                          focusedIndex,
                                          true,
                                          cancellationToken)
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

    private static async Task<int> ShowOnUIThreadAsync(string message,
                                                       string? title,
                                                       IReadOnlyList<MessageBoxButtonDefinition> buttons,
                                                       int focusedIndex,
                                                       bool viaMarshal,
                                                       CancellationToken cancellationToken)
    {
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
        if (viaMarshal && UIApplication.Current?.WindowManager is null)
            return -1;

        var buttonPanel = new StackPanel
                          {
                              Orientation = Orientation.Horizontal,
                              HorizontalAlignment = HorizontalAlignment.Right,
                              Spacing = 1,
                              Margin = new(0, 1, 0, 0)
                          };

        // Left/Right cycle the button row (Tab order falls out of the window root's Cycle trap).
        KeyboardNavigation.SetDirectionalNavigation(buttonPanel, DirectionalNavigationMode.Cycle);
        DockPanel.SetDock(buttonPanel, Dock.Bottom);

        var window = new Window
                     {
                         Content = new DockPanel
                                   {
                                       MaxWidth = 40,
                                       LastChildFill = true,
                                       Children =
                                       {
                                           buttonPanel,
                                           new TextBlock { Text = message, TextWrapping = WrapMode.WordWrap }
                                       }
                                   },
                         Title = title ?? string.Empty,
                         CanResize = false,
                         Padding = new(2, 1),
                         SizeToContent = SizeToContent.WidthAndHeight,
                         WindowStartupLocation = WindowStartupLocation.CenterScreen,
                         Shadow = WindowShadow.Default
                     };

        Button? focusTarget = null;

        for (var i = 0; i < buttons.Count; i++)
        {
            var definition = buttons[i];

            var button = new Button
                         {
                             Content = definition.Caption,
                             IsDefault = definition.IsDefault,
                             IsCancel = definition.IsCancel
                         };

            var index = i;
            button.Click += (_, _) => window.Close(dialogResult: index);

            buttonPanel.Children.Add(button);

            if (i == focusedIndex)
                focusTarget = button;
        }

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

    private static void ValidatePick(MessageBoxButton? pick, MessageBoxButton buttons, string paramName)
    {
        if (pick is not { } picked)
            return;

        if (Array.FindIndex(Buttons, b => b.Flag == picked) < 0)
            throw new ArgumentException($"'{picked}' is not a single button flag.", paramName);

        if (!buttons.HasFlag(picked))
            throw new ArgumentException($"'{picked}' is not among the shown buttons.", paramName);
    }
}

/// <summary>
/// One button of the label-driven <see cref="MessageBox"/> core: the access-key caption plus the
/// window-key roles (<see cref="IsDefault"/> → Enter, <see cref="IsCancel"/> → Esc).
/// </summary>
internal readonly record struct MessageBoxButtonDefinition(string Caption, bool IsDefault, bool IsCancel);
