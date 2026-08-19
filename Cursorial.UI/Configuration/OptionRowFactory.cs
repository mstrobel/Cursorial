using System.Globalization;

using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Configuration;

/// <summary>
/// Builds the code-first row visuals both the settings dialog and the wizard share: a toggle or
/// choice control bound to the shared option view-model, the tri-state affordances (inheritance
/// badge + reset-to-inherited), and the dimmed description line. One factory so the two surfaces
/// cannot drift.
/// </summary>
internal static class OptionRowFactory
{
    internal static UIElement BuildRow(OptionViewModel option, ref int nextTabIndex)
    {
        var row = new StackPanel { Margin = new Margins(0, 0, 0, 1) };
        var header = new DockPanel { LastChildFill = true };

        // Reset-to-inherited (docked right; visible only while the current layer sets the key).
        var reset = new Button
        {
            Content = "↺ inherit",
            Padding = new Margins(1, 0),
            Visibility = option.IsSetInCurrentScope ? Visibility.Visible : Visibility.Collapsed
        };
        reset.Click += (_, _) => option.ClearToInherited();
        DockPanel.SetDock(reset, Dock.Right);
        header.Children.Add(reset);

        // Inheritance badge (docked right, dimmed): "inherited from global" / "default".
        var badge = new TextBlock { Margin = new Margins(1, 0), Opacity = 0.6 };
        badge.SetBinding(TextBlock.TextProperty,
                         CompiledBinding.From<OptionViewModel, string>(source => source.InheritanceLabel));
        DockPanel.SetDock(badge, Dock.Right);
        header.Children.Add(badge);

        var editor = BuildEditor(option, ref nextTabIndex);
        header.Children.Add(editor);
        row.Children.Add(header);

        row.Children.Add(new TextBlock
        {
            Text = option.Description,
            TextWrapping = WrapMode.WordWrap,
            Opacity = 0.6,
            Margin = new Margins(4, 0, 0, 0)
        });

        // The reset affordance tracks the tri-state live (scope switches, external writes).
        option.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(OptionViewModel.IsSetInCurrentScope))
                reset.Visibility = option.IsSetInCurrentScope ? Visibility.Visible : Visibility.Collapsed;
        };

        row.DataContext = option;
        reset.TabIndex = nextTabIndex++;
        return row;
    }

    private static UIElement BuildEditor(OptionViewModel option, ref int nextTabIndex)
    {
        switch (option)
        {
            case BooleanOptionViewModel boolean:
            {
                var check = new CheckBox { Content = option.DisplayName, TabIndex = nextTabIndex++ };
                check.DataContext = boolean;
                check.SetBinding(ToggleButton.IsCheckedProperty,
                                 CompiledBinding.From((BooleanOptionViewModel vm) => vm.IsChecked, BindingMode.TwoWay));
                return check;
            }

            case ChoiceOptionViewModel choice:
            {
                var panel = new DockPanel { LastChildFill = true };
                var label = new TextBlock { Markup = option.DisplayName + ":", Margin = new Margins(0, 0, 1, 0) };
                DockPanel.SetDock(label, Dock.Left);
                panel.Children.Add(label);

                var combo = new ComboBox { DataContext = choice, MinWidth = 24, TabIndex = nextTabIndex++ };
                combo.SetBinding(ItemsControl.ItemsSourceProperty, 
                                 CompiledBinding.From((ChoiceOptionViewModel vm) => vm.Items));
                combo.SetBinding(SelectingItemsControl.SelectedIndexProperty,
                                 CompiledBinding.From((ChoiceOptionViewModel vm) => vm.SelectedIndex, BindingMode.TwoWay));
                panel.Children.Add(combo);
                return panel;
            }

            default:
                return new TextBlock { Markup = option.DisplayName };
        }
    }

    /// <summary>
    /// The Advanced tab's warning banner + timed-test controls (dialog-only; the wizard has no
    /// Advanced page by design).
    /// </summary>
    internal static UIElement BuildAdvancedHeader(UserOptionsViewModel model)
    {
        var panel = new StackPanel { Margin = new Margins(0, 0, 0, 1) };

        var warning = new TextBlock
        {
            Text = "⚠ These override terminal capability detection. Forcing a capability the terminal "
                 + "does not support can corrupt the entire display — run the timed test first; it "
                 + "reverts automatically.",
            TextWrapping = WrapMode.WordWrap
        };
        warning.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.WarningBrush);
        panel.Children.Add(warning);

        var testRow = new DockPanel { LastChildFill = false, Margin = new Margins(0, 1, 0, 0) };

        var testButton = new Button { Content = "_Test for 5 seconds", IsEnabled = model.CanBeginTest };
        testButton.Click += (_, _) => model.BeginTest();
        testRow.Children.Add(testButton);

        var countdown = new TextBlock { Margin = new Margins(2, 0, 0, 0), Visibility = Visibility.Collapsed };
        testRow.Children.Add(countdown);

        var endEarly = new Button
        {
            Content = "_Revert now",
            Margin = new Margins(2, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        endEarly.Click += (_, _) => model.EndTest();
        testRow.Children.Add(endEarly);

        model.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(UserOptionsViewModel.CanBeginTest):
                    testButton.IsEnabled = model.CanBeginTest;
                    break;

                case nameof(UserOptionsViewModel.IsTestRunning):
                    countdown.Visibility = model.IsTestRunning ? Visibility.Visible : Visibility.Collapsed;
                    endEarly.Visibility = countdown.Visibility;
                    break;

                case nameof(UserOptionsViewModel.TestSecondsRemaining):
                    countdown.Text = string.Create(
                        CultureInfo.InvariantCulture, $"testing… reverts in {model.TestSecondsRemaining}s");
                    break;
            }
        };

        panel.Children.Add(testRow);
        return panel;
    }
}
