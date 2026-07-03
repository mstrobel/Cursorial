using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// #141 — the drop-opener close-focus behavior must be CONSISTENT across BarPopupButton and BarSplitButton: entering a
// non-retaining Toolbar via an access-key/pointer entry, opening a dropdown, and invoking an item returns focus to the
// OPENER (the Popup W4 restore-to-face), NOT to the pre-entry element. Root cause: the returning-scope walk from a
// dropdown item must hit a retaining IsFocusScope barrier before the Toolbar. BarPopupButton's whole-control barrier is
// an ancestor of the popup; a BarSplitButton's ▾-zone barrier is a SIBLING of the popup (never on the walk), so the fix
// makes the shared dropdown CONTENT presenter the barrier — an ancestor of every item for both controls.
public sealed class DropDownFocusReturnTests
{
    private sealed record Harness(UITestHost Host, Button Editor, Toolbar Toolbar, BarDropDownButton Opener, BarButton Item)
    {
        public FocusManager Focus => Host.Application.FocusManager;
    }

    private static Harness Build(BarDropDownButton opener)
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(48, 8),
            Capabilities = TestCapabilities.KittyTruecolor,
        });

        var item = new BarButton { Content = "Paste Special" };
        var menu = new StackPanel { Orientation = Orientation.Vertical };
        menu.Children.Add(item);
        opener.DropDownContent = menu;

        var toolbar = new Toolbar { VerticalAlignment = VerticalAlignment.Top };
        toolbar.Items.Add(opener);

        var editor = new Button { Content = "Editor" };

        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(toolbar); // row 0
        root.Children.Add(editor);  // row 1
        host.ShowRoot(root);
        host.RunUntilIdle();
        return new Harness(host, editor, toolbar, opener, item);
    }

    // Enter the bar via access key onto the opener, open the (Bottom) dropdown, focus the first item, and invoke it.
    private static void EnterOpenAndInvoke(Harness h)
    {
        h.Editor.Focus();
        h.Host.RunUntilIdle();
        h.Opener.Focus(FocusNavigationMethod.AccessKey); // auto-returnable entry — the return target is the editor
        h.Host.RunUntilIdle();
        Assert.Same(h.Opener, h.Focus.FocusedElement);

        h.Host.SendKey(Key.DownArrow); // Bottom placement: open, focus parked on the face
        h.Host.RunUntilIdle();
        h.Host.SendKey(Key.DownArrow); // enter the dropdown — focus the first item
        h.Host.RunUntilIdle();
        Assert.True(h.Item.IsFocused);

        h.Host.SendKey(Key.Enter); // invoke the item → OnDropDownItemClick defers the close → Popup W4 restore
        h.Host.RunUntilIdle();
    }

    [Fact] // BarSplitButton: invoking a dropdown item returns focus to the split button, NOT the pre-entry editor (#141)
    public void SplitButton_DropDownItemInvoke_ReturnsToButton_NotPreEntry()
    {
        var h = Build(new BarSplitButton { Content = "Paste" });
        using var _ = h.Host;

        EnterOpenAndInvoke(h);

        Assert.Same(h.Opener, h.Focus.FocusedElement); // restored to the split button (was: the editor — the bug)
    }

    [Fact] // BarPopupButton parity: same scenario returns focus to the popup button (the fix leaves this unchanged)
    public void PopupButton_DropDownItemInvoke_ReturnsToButton()
    {
        var h = Build(new BarPopupButton { Content = "Align" });
        using var _ = h.Host;

        EnterOpenAndInvoke(h);

        Assert.Same(h.Opener, h.Focus.FocusedElement);
    }

    [Fact] // the fix must NOT regress the split button's PRIMARY label action: it still auto-returns to the pre-entry
           // element (the content-presenter barrier is not on the primary path; the split button itself is no scope)
    public void SplitButton_PrimaryInvoke_StillAutoReturnsToPreEntry()
    {
        var h = Build(new BarSplitButton { Content = "Paste" });
        using var _ = h.Host;

        h.Editor.Focus();
        h.Host.RunUntilIdle();
        h.Opener.Focus(FocusNavigationMethod.AccessKey);
        h.Host.RunUntilIdle();
        Assert.Same(h.Opener, h.Focus.FocusedElement);

        h.Host.SendKey(Key.Enter); // activate the PRIMARY action (dropdown closed) — a BarButton-like auto-return
        h.Host.RunUntilIdle();

        Assert.Same(h.Editor, h.Focus.FocusedElement); // returned to the editor (primary auto-returns, unlike the dropdown)
    }
}
