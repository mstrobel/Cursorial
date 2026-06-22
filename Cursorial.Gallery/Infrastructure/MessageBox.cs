using Cursorial.Rendering.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.Gallery.Infrastructure;

[Flags]
public enum MessageBoxButton : byte
{
    Ok = 1,
    Cancel = 2,
    Yes = 4,
    No = 8,
    Close = 16
}

public static class MessageBox
{
    private static string ButtonText(MessageBoxButton button)
        => button switch
           {
               MessageBoxButton.Close => "C_lose",
               _                      => button.ToString()
           };

    public static async Task<MessageBoxButton?> ShowAsync(string message,
                                                          string? title = null,
                                                          MessageBoxButton? buttons = null,
                                                          MessageBoxButton? focusedButton = null,
                                                          MessageBoxButton? defaultButton = null,
                                                          MessageBoxButton? cancelButton = null)

    {
        var buttonPanel = new StackPanel
                          {
                              Orientation = Orientation.Horizontal,
                              HorizontalAlignment = HorizontalAlignment.Right,
                              Spacing = 1,
                              Margin = new(0, 1, 0, 0)
                          };

        var actualButtons = Enum.GetValues<MessageBoxButton>().Where(o => buttons?.HasFlag(o) is true).ToArray();

        DockPanel.SetDock(buttonPanel, Dock.Bottom);

        var window = new Window
                     {
                         Content = new DockPanel
                                   {
                                       LastChildFill = true,
                                       Children =
                                       {
                                           buttonPanel,
                                           new TextBlock { Text = message, TextWrapping = WrapMode.WordWrap }
                                       }
                                   },
                         Title = title ?? string.Empty,
                         MaxWidth = 40,
                         CanResize = false,
                         Padding = new(2, 1),
                         SizeToContent = SizeToContent.WidthAndHeight,
                         WindowStartupLocation = WindowStartupLocation.CenterScreen
                     };

        foreach (var button in actualButtons)
        {
            if (buttons?.HasFlag(button) is true)
            {
                var b = new Button
                        {
                            Content = ButtonText(button),
                            IsDefault = defaultButton == button,
                            IsCancel = cancelButton == button
                        };

                var button1 = button;
                b.Click += (_, _) => window.Close(dialogResult: button1);

                buttonPanel.Children.Add(b);

                if (button == focusedButton || focusedButton is null && button == defaultButton)
                {
                    window.Shown += OnWindowShown;

                    void OnWindowShown(object? sender, RoutedEventArgs e)
                    {
                        window.Shown -= OnWindowShown;
                        b.Focus();
                    }
                }
            }
        }

        try
        {
            return await window.ShowDialogAsync<MessageBoxButton?>();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}