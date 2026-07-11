using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

namespace Cursorial.Tests.UI.Bars;

// The BarLabel access-key forwarding spine (WIP review follow-up): a "_Edit:" caption folds its mnemonic (the
// inherited ContentControl ParsesAccessKeyLiterals machinery), registers with the AccessKeyManager, and on
// activation forwards focus to the next control to its RIGHT — as a ONE-SHOT AccessKey entry (so a subsequent invoke
// auto-returns), guarding multi-match (ND18) and the DirectionalNavigation=Cycle trailing-label wrap.
public sealed class BarLabelAccessKeyTests
{
    private static KeyEvent KeyEvt(Key key, KeyModifiers modifiers = KeyModifiers.None, string? text = null,
                                   KeyEventKind kind = KeyEventKind.Down)
        => new()
        {
            Key = key,
            Modifiers = modifiers,
            Kind = kind,
            Text = (text ?? string.Empty).AsMemory(),
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    // Activate a folded mnemonic through the manager — a fresh Alt-down opens the cue window before the char
    // (a bare char with Alt held would trip the manager's stale-Alt inference). Mirrors Section19/Section12.
    private static void ActivateAccessKey(UIHeadlessHost host, char mnemonic)
    {
        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(KeyEvt(Key.LeftAlt, KeyModifiers.Alt));
        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, mnemonic.ToString()));
        host.RunUntilIdle();
    }

    private sealed record Harness(UIHeadlessHost Host, Button Editor, BarLabel Label, BarButton Cut, Toolbar Toolbar)
    {
        public FocusManager Focus => Host.Application.FocusManager;
    }

    // A "_Edit:" caption titling [Cut][Copy], with an editor below the bar as the pre-bar focus origin.
    private static Harness Build()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(48, 6),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var label = new BarLabel { Content = "_Edit:" };
        var cut = new BarButton { Content = "Cut" };
        var toolbar = new Toolbar { VerticalAlignment = VerticalAlignment.Top };
        toolbar.Items.Add(label);
        toolbar.Items.Add(cut);
        toolbar.Items.Add(new BarButton { Content = "Copy" });

        var editor = new Button { Content = "Editor" };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(toolbar);  // row 0
        root.Children.Add(editor);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return new Harness(host, editor, label, cut, toolbar);
    }

    [Fact] // Alt+<mnemonic> on a BarLabel forwards focus to the next control to its RIGHT (the cluster it titles)
    public void Mnemonic_ForwardsFocusToNextRightControl()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Host.RunUntilIdle();

        ActivateAccessKey(h.Host, 'E'); // "_Edit:" folds to 'E'
        Assert.Same(h.Cut, h.Focus.FocusedElement); // forwarded right into the cluster
    }

    [Fact] // the forward is a ONE-SHOT AccessKey entry: a subsequent invoke inside the bar auto-returns to the editor
    public void Mnemonic_IsOneShot_SubsequentInvokeAutoReturns()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        ActivateAccessKey(h.Host, 'E');
        Assert.Same(h.Cut, h.Focus.FocusedElement);

        h.Host.SendKey(Key.Enter); // invoke Cut — AccessKey entry armed the non-retaining Toolbar's auto-return
        h.Host.RunUntilIdle();
        Assert.Same(h.Editor, h.Focus.FocusedElement); // returned to the pre-bar editor (would STRAND on Cut if Directional)
    }

    [Fact] // ND18: two captions sharing a mnemonic form a multi-match — the manager owns cycling, the label never forwards
    public void Mnemonic_MultiMatch_DoesNotForward()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(48, 6),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });
        using var _ = host;

        var l1 = new BarLabel { Content = "_Edit:" };   // folds 'E'
        var l2 = new BarLabel { Content = "_Extra:" };  // also folds 'E' → collision
        var toolbar = new Toolbar { VerticalAlignment = VerticalAlignment.Top };
        toolbar.Items.Add(l1);
        toolbar.Items.Add(new BarButton { Content = "Cut" });
        toolbar.Items.Add(l2);

        var editor = new Button { Content = "Editor" };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(toolbar);
        root.Children.Add(editor);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var focus = host.Application.FocusManager;
        editor.Focus();
        ActivateAccessKey(host, 'E'); // multi-match — the manager focus-cycles (a no-op on the non-focusable labels)

        Assert.Same(editor, focus.FocusedElement); // the label did NOT forward (ND18 guard); focus unchanged
    }

    [Fact] // a TRAILING label (nothing focusable to its right) is a no-op — not a Cycle-wrap to the leftmost control
    public void TrailingMnemonic_DoesNotWrapToLeftmost()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(48, 6),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });
        using var _ = host;

        var cut = new BarButton { Content = "Cut" };
        var view = new BarLabel { Content = "_View:" }; // trailing — nothing focusable to its right
        var toolbar = new Toolbar { VerticalAlignment = VerticalAlignment.Top };
        toolbar.Items.Add(cut);
        toolbar.Items.Add(view);

        var editor = new Button { Content = "Editor" };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(toolbar);
        root.Children.Add(editor);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var focus = host.Application.FocusManager;
        editor.Focus();
        ActivateAccessKey(host, 'V');

        Assert.Same(editor, focus.FocusedElement); // no-op: the rightward query wrapped to Cut, the guard rejected it
    }

    [Fact] // a disabled BarLabel is access-key-ineligible — its mnemonic never forwards
    public void DisabledLabel_DoesNotForward()
    {
        var h = Build();
        using var _ = h.Host;

        h.Label.IsEnabled = false;
        h.Host.RunUntilIdle();

        h.Editor.Focus();
        ActivateAccessKey(h.Host, 'E');

        Assert.Same(h.Editor, h.Focus.FocusedElement); // ineligible (IsEffectivelyEnabled false): no match, no forward
    }
}
