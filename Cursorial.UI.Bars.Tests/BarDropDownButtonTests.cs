using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// The bar drop-openers (bars guide): BarPopupButton (whole control opens its dropdown) and BarSplitButton (a label
// primary action + a separate ▾ zone that opens the dropdown). Both host DropDownContent in a Popup; opening ENTERS
// the content; the opener is a retaining focus-scope barrier so a pointer-open never trips a Toolbar's auto-return.
public sealed class BarDropDownButtonTests
{
    private static UITestHost NewHost(int w = 30, int h = 8) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = TestCapabilities.KittyTruecolor });

    private static StackPanel DropContent(out Button firstItem)
    {
        firstItem = new Button { Content = "One", Width = 8, Height = 1 };
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(firstItem);
        panel.Children.Add(new Button { Content = "Two", Width = 8, Height = 1 });
        return panel;
    }

    private static void ClickAt(UITestHost host, UIElement element)
    {
        var origin = element.TranslateToScreen(0, 0);
        host.SendMouseMove(origin.Column + 1, origin.Row); // hover (arms the release-click gate)
        host.RunFrame();
        host.SendClick(origin.Column + 1, origin.Row);
        host.RunUntilIdle();
    }

    [Fact] // BarPopupButton: a click opens the dropdown, focus STAYS on the face; Down then enters the content
    public void PopupButton_ClickOpens_FocusStaysOnFace_DownEnters()
    {
        using var host = NewHost();
        var content = DropContent(out var first);
        var button = new BarPopupButton
        {
            Content = "Align", DropDownContent = content,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        host.RunUntilIdle();

        ClickAt(host, button);
        Assert.True(button.IsDropDownOpen);
        Assert.True(button.IsFocused); // focus stays on the opener (ComboBox model); barrier kept it from yanking

        host.SendKey(Key.DownArrow); // the content is laid out now → Down moves focus into it
        host.RunUntilIdle();
        Assert.True(first.IsFocused);
    }

    [Fact] // BarPopupButton: a second click (on the anchor) closes it (the opener owns the toggle)
    public void PopupButton_SecondClickCloses()
    {
        using var host = NewHost();
        var button = new BarPopupButton
        {
            Content = "Align", DropDownContent = DropContent(out _),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        host.RunUntilIdle();

        ClickAt(host, button);
        Assert.True(button.IsDropDownOpen);

        ClickAt(host, button);
        Assert.False(button.IsDropDownOpen);
    }

    [Fact] // BarPopupButton: Down opens (focus on face), Down again enters; Escape closes + returns focus to the face
    public void PopupButton_DownOpens_DownEnters_EscapeReturnsToFace()
    {
        using var host = NewHost();
        var content = DropContent(out var first);
        var button = new BarPopupButton
        {
            Content = "Align", DropDownContent = content,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        host.RunUntilIdle();

        button.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow); // opens (focus stays on the face)
        host.RunUntilIdle();
        Assert.True(button.IsDropDownOpen);
        Assert.True(button.IsFocused);

        host.SendKey(Key.DownArrow); // enters (content laid out)
        host.RunUntilIdle();
        Assert.True(first.IsFocused);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(button.IsDropDownOpen);
        Assert.True(button.IsFocused); // returned to the face (the Popup W4 restore)
    }

    [Fact] // BarSplitButton: the ▾ zone opens the dropdown; the PRIMARY command does NOT run
    public void SplitButton_ArrowZoneOpens_NotCommand()
    {
        using var host = NewHost();
        var ran = 0;
        var split = new BarSplitButton
        {
            Content = "Paste", Command = new BarCommand(() => ran++), DropDownContent = DropContent(out _),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(split);
        host.RunUntilIdle();

        ClickAt(host, split.DropZoneForTests!); // click the ▾ zone specifically

        Assert.True(split.IsDropDownOpen);
        Assert.Equal(0, ran); // the primary action did NOT fire
    }

    [Fact] // BarSplitButton: the primary (keyboard-activate) runs the command; the dropdown does NOT open
    public void SplitButton_PrimaryRunsCommand_NotDropDown()
    {
        using var host = NewHost();
        var ran = 0;
        var split = new BarSplitButton
        {
            Content = "Paste", Command = new BarCommand(() => ran++), DropDownContent = DropContent(out _),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(split);
        host.RunUntilIdle();

        split.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.Enter); // the primary action

        host.RunUntilIdle();
        Assert.Equal(1, ran);
        Assert.False(split.IsDropDownOpen);
    }

    [Fact] // barrier: a popup button on a non-retaining Toolbar opened by POINTER keeps focus on the opener — the
           // retaining focus-scope barrier stops the Toolbar's auto-return from yanking focus back to the editor
    public void PopupButton_OnToolbar_PointerOpen_DoesNotYankToEditor()
    {
        using var host = NewHost(40, 10);
        var popup = new BarPopupButton { Content = "Align", DropDownContent = DropContent(out _) };
        var toolbar = new Toolbar { VerticalAlignment = VerticalAlignment.Top };
        toolbar.Items.Add(popup);
        var editor = new Button { Content = "Editor", Width = 8, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(toolbar);
        root.Children.Add(editor);
        host.ShowRoot(root);
        host.RunUntilIdle();

        editor.Focus();
        host.RunUntilIdle();

        ClickAt(host, popup); // pointer-open

        Assert.True(popup.IsDropDownOpen);
        Assert.True(popup.IsFocused);            // focus stayed on the opener…
        Assert.NotSame(editor, host.Application.FocusManager.FocusedElement); // …and was NOT yanked back to the editor
    }

    [Fact] // BarSplitButton PRIMARY action on a Toolbar auto-returns focus to the editor (like a BarButton) — only the
           // ▾ zone is a barrier, so the split button itself is NOT, and its primary invoke returns focus normally
    public void SplitButton_OnToolbar_PrimaryInvoke_AutoReturnsToEditor()
    {
        using var host = NewHost(48, 10);
        var ran = 0;
        var split = new BarSplitButton { Content = "Paste", Command = new BarCommand(() => ran++), DropDownContent = DropContent(out _) };
        var toolbar = new Toolbar { VerticalAlignment = VerticalAlignment.Top };
        toolbar.Items.Add(split);
        var editor = new Button { Content = "Editor", Width = 8, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(toolbar);
        root.Children.Add(editor);
        host.ShowRoot(root);
        host.RunUntilIdle();

        editor.Focus();
        host.RunUntilIdle();

        // Click the PRIMARY (label) zone at the split's left edge (the ▾ zone is at the right).
        var origin = split.TranslateToScreen(0, 0);
        host.SendMouseMove(origin.Column + 1, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column + 1, origin.Row);
        host.RunUntilIdle();

        Assert.Equal(1, ran);                    // the primary command ran
        Assert.False(split.IsDropDownOpen);      // the dropdown did NOT open
        Assert.Same(editor, host.Application.FocusManager.FocusedElement); // …and focus auto-returned to the editor
    }

    [Fact] // invoking a dropdown item closes the dropdown (menu-like), and the command still runs
    public void PopupButton_InvokingItemClosesDropDown()
    {
        using var host = NewHost();
        var ran = 0;
        var item = new Button { Content = "One", Width = 8, Height = 1, Command = new BarCommand(() => ran++) };
        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(item);
        var button = new BarPopupButton
        {
            Content = "Align", DropDownContent = content,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        host.RunUntilIdle();

        ClickAt(host, button);
        Assert.True(button.IsDropDownOpen);

        ClickAt(host, item); // invoke a content item
        Assert.Equal(1, ran);                  // the command ran…
        Assert.False(button.IsDropDownOpen);   // …and the dropdown closed (menu-like)
    }

    [Fact] // Down advances through dropdown items (not stuck on the first); Up from the first returns to the opener
    public void PopupButton_DropDownArrowNav()
    {
        using var host = NewHost();
        var one = new Button { Content = "One", Width = 8, Height = 1 };
        var two = new Button { Content = "Two", Width = 8, Height = 1 };
        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(one);
        content.Children.Add(two);
        var button = new BarPopupButton
        {
            Content = "Align", DropDownContent = content,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        host.RunUntilIdle();
        button.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow); // open (focus stays on face)
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // enter → first item
        host.RunUntilIdle();
        Assert.True(one.IsFocused);

        host.SendKey(Key.DownArrow); // advance → second item (NOT stuck on the first)
        host.RunUntilIdle();
        Assert.True(two.IsFocused);

        host.SendKey(Key.UpArrow); // back to the first
        host.RunUntilIdle();
        Assert.True(one.IsFocused);

        host.SendKey(Key.UpArrow); // up from the first → back to the opener face (menu-like)
        host.RunUntilIdle();
        Assert.True(button.IsFocused);
    }

    private sealed class DropVm; // a non-UIElement dropdown content object (realized through an implicit DataTemplate)

    [Fact] // a DataTemplated dropdown (non-UIElement content) is still keyboard-enterable — the entry walks the REALIZED
           // presenter child, not the raw content object (before the fix, `DropDownContent is UIElement` failed the cast)
    public void PopupButton_DataTemplatedContent_DownEnters()
    {
        using var host = NewHost();
        Button? templatedFirst = null;
        host.Application.Resources[new DataTemplateKey(typeof(DropVm))] = new DataTemplate
        {
            Content = new FuncTemplateContent(_ =>
            {
                var panel = new StackPanel { Orientation = Orientation.Vertical };
                templatedFirst = new Button { Content = "Item", Width = 8, Height = 1 };
                panel.Children.Add(templatedFirst);
                return panel;
            }),
        };
        var button = new BarPopupButton
        {
            Content = "Align", DropDownContent = new DropVm(),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        host.RunUntilIdle();

        ClickAt(host, button);
        Assert.True(button.IsDropDownOpen);
        Assert.True(button.IsFocused);

        host.SendKey(Key.DownArrow); // the DataTemplated content is laid out → Down moves focus into it
        host.RunUntilIdle();

        Assert.NotNull(templatedFirst);
        Assert.True(templatedFirst!.IsFocused);
    }

    [Fact] // OnTemplateDetaching unhooks the old parts so a re-template rebinds cleanly to the fresh template (the
           // control still opens/closes through the new PART_Popup — the OnPopupClosed handler is not left stranded)
    public void PopupButton_ReTemplate_RebindsCleanly()
    {
        using var host = NewHost();
        var button = new BarPopupButton
        {
            Content = "Align", DropDownContent = null, // no live element to re-parent across the presenter rebuild
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        host.RunUntilIdle();

        ClickAt(host, button); // open + close once through the original template's PART_Popup
        Assert.True(button.IsDropDownOpen);
        button.IsDropDownOpen = false;
        host.RunUntilIdle();
        Assert.False(button.IsDropDownOpen);

        // Swap in a FRESH theme instance → the control re-templates (OnTemplateDetaching on the old parts, then
        // OnApplyTemplate binds the new ones). The old popup's Closed handler must be unhooked, not stranded.
        var ex = Record.Exception(() =>
        {
            button.SetValue(Control.ThemeProperty, CursorialBarsTheme.BarPopupButtonStyle());
            host.RunUntilIdle();
        });
        Assert.Null(ex);

        // The fresh template's PART_Popup drives open/close correctly.
        ClickAt(host, button);
        Assert.True(button.IsDropDownOpen);
        button.IsDropDownOpen = false;
        host.RunUntilIdle();
        Assert.False(button.IsDropDownOpen);
    }

    [Fact] // detaching the button while its dropdown is open closes it (no leaked popup surface)
    public void Detach_ClosesDropDown()
    {
        using var host = NewHost();
        var button = new BarPopupButton
        {
            Content = "Align", DropDownContent = DropContent(out _),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        host.RunUntilIdle();
        ClickAt(host, button);
        Assert.True(button.IsDropDownOpen);

        var ex = Record.Exception(() =>
        {
            host.ShowRoot(new BarButton { Content = "X" }); // detaches the popup button
            host.RunUntilIdle();
        });
        Assert.Null(ex);
        Assert.False(button.IsDropDownOpen);
    }
}
