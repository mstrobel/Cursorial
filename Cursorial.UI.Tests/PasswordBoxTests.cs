using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI;

/// <summary>
/// The <see cref="PasswordBox"/> (#114): a masked <see cref="TextBox"/>. The display is masked per grapheme
/// cluster while the model stays plaintext; the caret/selection track the mask; clipboard Copy/Cut are suppressed
/// (no password ever reaches OSC 52); <see cref="PasswordBox.RevealPassword"/> shows the plaintext.
/// </summary>
public class PasswordBoxTests
{
    private static TerminalCapabilities WithClipboard()
    {
        var caps = HeadlessCapabilities.KittyTruecolor;
        return caps with { Output = caps.Output with { Protocol = caps.Output.Protocol with { ClipboardWrite = true } } };
    }

    private static (UIHeadlessHost Host, PasswordBox Box) Shown(string password = "", int width = 12, bool focus = true,
                                                            TerminalCapabilities? capabilities = null)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(30, 4),
            Capabilities = capabilities ?? HeadlessCapabilities.KittyTruecolor,
            CaptureFrameBytes = true,
        });
        var box = new PasswordBox
        {
            Password = password, Width = width, Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(box);
        host.RunUntilIdle();
        if (focus)
        {
            box.Focus();
            host.RunUntilIdle();
        }
        return (host, box);
    }

    // The TextBox chrome pads content by one column (Border Padding 1,0); masked content has no internal spaces,
    // so trimming both ends isolates the rendered glyphs cleanly.
    private static string Row0(UIHeadlessHost host) => host.GetRowText(0).Trim();

    [Fact]
    public void Masks_EachCharacter_WithTheMaskGlyph_NotThePlaintext()
    {
        var (host, box) = Shown("secret");
        using var _ = host;

        var row = Row0(host);
        Assert.StartsWith("●●●●●●", row);          // six masked glyphs
        Assert.DoesNotContain("secret", row);       // the plaintext never renders
        Assert.Equal("secret", box.Password);       // ...but the model holds it
        Assert.Equal("secret", box.Text);           // Password is an alias for Text
    }

    [Fact]
    public void CustomPasswordChar_IsUsed()
    {
        var (host, box) = Shown("abcd");
        using var _ = host;
        box.PasswordChar = '#';
        host.RunUntilIdle();

        Assert.StartsWith("####", Row0(host));
    }

    [Fact]
    public void RevealPassword_ShowsPlaintext_ThenReMasks()
    {
        var (host, box) = Shown("hunter2");
        using var _ = host;

        box.RevealPassword = true;
        host.RunUntilIdle();
        Assert.StartsWith("hunter2", Row0(host));

        box.RevealPassword = false;
        host.RunUntilIdle();
        Assert.StartsWith("●●●●●●●", Row0(host));
        Assert.DoesNotContain("hunter2", Row0(host));
    }

    [Fact]
    public void Typing_InsertsIntoTheModel_AndMasksDisplay()
    {
        var (host, box) = Shown();
        using var _ = host;

        host.SendText("pw");
        host.RunUntilIdle();

        Assert.Equal("pw", box.Password);
        Assert.Equal(2, box.CaretIndex); // caret advances per character
        Assert.StartsWith("●●", Row0(host));
    }

    [Fact]
    public void Copy_IsSuppressed_NoOsc52_EvenWithClipboardCapabilityAndSelection()
    {
        var (host, box) = Shown("topsecret", capabilities: WithClipboard());
        using var _ = host;
        Assert.True(host.Application.Clipboard.CanWrite);

        box.SelectAll();
        host.SendKey(Key.Character, KeyModifiers.Control, "c");

        // The password must NEVER reach the clipboard (OSC 52) — the whole point of the control.
        Assert.False(ContainsSequence(CollectFrames(host), "]52;"u8));
        Assert.Equal("topsecret", box.Password); // copy doesn't mutate
    }

    [Fact]
    public void Cut_IsSuppressed_NoOsc52_AndDoesNotDelete()
    {
        var (host, box) = Shown("topsecret", capabilities: WithClipboard());
        using var _ = host;

        box.SelectAll();
        host.SendKey(Key.Character, KeyModifiers.Control, "x");

        Assert.False(ContainsSequence(CollectFrames(host), "]52;"u8));
        Assert.Equal("topsecret", box.Password); // cut is a no-op (WPF parity), text retained
    }

    [Fact] // WPF parity: a focused PasswordBox does NOT consume Ctrl+C/Ctrl+X (Copy/Cut are no-ops), so the chord
           // bubbles to ancestors (an app KeyBinding may still claim it) — and neither mutates the password.
    public void CopyCutChords_BubblePastTheControl_Unconsumed()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 4) });
        using var _ = host;
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var box = new PasswordBox { Password = "secret", Width = 12, Height = 1 };
        panel.Children.Add(box);
        host.ShowRoot(panel);
        host.RunUntilIdle();
        box.Focus();
        box.SelectAll();
        host.RunUntilIdle();

        var bubbled = 0;
        panel.KeyDown += (_, e) => { if (e.Key == Key.Character) bubbled++; };

        host.SendKey(Key.Character, KeyModifiers.Control, "c");
        host.RunUntilIdle();
        host.SendKey(Key.Character, KeyModifiers.Control, "x");
        host.RunUntilIdle();

        Assert.Equal(2, bubbled);               // both chords reached the ancestor unconsumed
        Assert.Equal("secret", box.Password);   // and neither copied or deleted
    }

    [Fact]
    public void Paste_StillInsertsIntoThePassword()
    {
        var (host, box) = Shown();
        using var _ = host;

        // The terminal's own paste arrives as a PasteEvent ⇒ TextInput{FromPaste} — the supported inbound path.
        host.SendInput(new PasteEvent { Text = "pasted".AsMemory(), Timestamp = default });
        host.RunUntilIdle();

        Assert.Equal("pasted", box.Password);
        Assert.StartsWith("●●●●●●", Row0(host));
    }

    [Fact]
    public void Backspace_DeletesFromTheModel()
    {
        var (host, box) = Shown("abc");
        using var _ = host;
        box.CaretIndex = 3;
        host.RunUntilIdle();

        host.SendKey(Key.Backspace);
        host.RunUntilIdle();

        Assert.Equal("ab", box.Password);
        Assert.StartsWith("●●", Row0(host));
    }

    [Fact] // the hard case: a wide/multi-char cluster masks to ONE width-1 glyph; cluster-level editing still works
    public void WideCluster_MasksToSingleGlyph_AndEditsByCluster()
    {
        var (host, box) = Shown("a\U0001F600b"); // 'a', an emoji (1 cluster, 2 chars, width 2), 'b' — 3 clusters
        using var _ = host;

        // Three clusters → three width-1 mask glyphs (the emoji collapses to ONE '*', not two columns).
        Assert.Equal("●●●", Row0(host).TrimEnd());

        box.CaretIndex = box.Text.Length; // model end (char index 4: 1 + 2 + 1)
        host.RunUntilIdle();

        host.SendKey(Key.Backspace); // deletes the 'b' cluster
        host.RunUntilIdle();
        Assert.Equal("a\U0001F600", box.Password);
        Assert.Equal("●●", Row0(host).TrimEnd());

        host.SendKey(Key.Backspace); // deletes the whole emoji cluster (both chars), not a half
        host.RunUntilIdle();
        Assert.Equal("a", box.Password);
        Assert.Equal("●", Row0(host).TrimEnd());
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty)
            return true;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return true;
        return false;
    }

    private static byte[] CollectFrames(UIHeadlessHost host, int frames = 4)
    {
        var all = new List<byte>();
        for (var i = 0; i < frames; i++)
        {
            host.RunFrame();
            all.AddRange(host.LastFrameBytes.ToArray());
        }
        return [.. all];
    }
}
