// xUnit1031 disabled: UITestHost is single-thread-affine (blocking drains stay on the UI thread).
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI;

/// <summary>
/// The OSC 52 clipboard READ round-trip (<see cref="IClipboardService.TryGetTextAsync"/>): the query rides
/// the out-of-band channel, the terminal's reply surfaces as a <c>DeviceResponseKind.Clipboard</c> device
/// response, and the pair correlates under a frame-aligned timeout — null on no-reply/denied/unsupported,
/// never a hang. Every injected response drains the parser pump BEFORE the observing frame (the SendBytes
/// contract), blocking so the test thread stays the UI thread.
/// </summary>
public sealed class ClipboardReadTests
{
    private static TerminalCapabilities WithClipboardRead(TerminalCapabilities caps) => caps with
    {
        Output = caps.Output with
        {
            Protocol = caps.Output.Protocol with { ClipboardRead = true, ClipboardWrite = true }
        }
    };

    private static UITestHost NewHost(bool canRead = true)
    {
        var caps = canRead ? WithClipboardRead(TestCapabilities.KittyTruecolor) : TestCapabilities.KittyTruecolor;
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(40, 10),
            Capabilities = caps,
            CaptureFrameBytes = true
        });
        host.ShowRoot(new Border());
        host.RunUntilIdle();
        return host;
    }

    private static void Reply(UITestHost host, ReadOnlySpan<byte> bytes)
    {
        host.SendBytes(bytes);
        host.DrainParsedInputAsync().GetAwaiter().GetResult(); // blocking — stays on the UI thread
        host.RunUntilIdle();
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return true;
        return false;
    }

    [Fact] // the round-trip: the query is emitted out-of-band, and the reply completes the read decoded
    public void Read_ResponseArrives_CompletesWithText()
    {
        using var host = NewHost();

        var task = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();

        host.RunFrame(); // Phase 6 emits the query
        Assert.True(ContainsSequence(host.LastFrameBytes.Span, "\x1b]52;c;?\x1b\\"u8));
        Assert.False(task.IsCompleted); // no reply yet

        Reply(host, "\x1b]52;c;aGVsbG8=\x1b\\"u8); // the terminal answers "hello"

        Assert.True(task.IsCompleted);
        Assert.Equal("hello", task.Result);
    }

    [Fact] // an unsupporting/denying terminal never replies: the read times out to null (never hangs)
    public void Read_NoReply_TimesOutToNull()
    {
        using var host = NewHost();

        var task = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();
        host.RunFrame();
        Assert.False(task.IsCompleted);

        host.AdvanceTime(TimeSpan.FromSeconds(3));
        host.RunUntilIdle();

        Assert.True(task.IsCompleted);
        Assert.Null(task.Result);
    }

    [Fact] // a reply landing after the timeout is dropped (the read already completed null; no crash, no leak)
    public void Read_LateReply_AfterTimeout_IsDropped()
    {
        using var host = NewHost();

        var task = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();
        host.RunFrame();
        host.AdvanceTime(TimeSpan.FromSeconds(3));
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.Null(task.Result);

        Reply(host, "\x1b]52;c;aGVsbG8=\x1b\\"u8); // too late

        Assert.Null(task.Result); // unchanged — the waiter unregistered at completion
    }

    [Fact] // no negotiated read capability ⇒ complete null immediately, no query emitted
    public void Read_WithoutCapability_CompletesNullImmediately()
    {
        using var host = NewHost(canRead: false);
        Assert.False(host.Application.Clipboard.CanRead);

        var task = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();

        Assert.True(task.IsCompleted);
        Assert.Null(task.Result);

        host.RunFrame();
        Assert.False(ContainsSequence(host.LastFrameBytes.Span, "\x1b]52;"u8)); // nothing went out
    }

    [Fact] // an empty selection replies with an empty base64 field ⇒ empty string (distinct from null)
    public void Read_EmptySelection_CompletesWithEmptyString()
    {
        using var host = NewHost();

        var task = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();
        host.RunFrame();
        Reply(host, "\x1b]52;c;\x1b\\"u8);

        Assert.True(task.IsCompleted);
        Assert.Equal(string.Empty, task.Result);
    }

    [Fact] // a terminal that ECHOES the query (a literal '?' data field) is "no data", not the string "?"
    public void Read_EchoedQuery_CompletesWithNull()
    {
        using var host = NewHost();

        var task = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();
        host.RunFrame();
        Reply(host, "\x1b]52;c;?\x1b\\"u8);

        Assert.True(task.IsCompleted);
        Assert.Null(task.Result);
    }

    [Fact] // UTF-8 round-trips through the base64 payload
    public void Read_Utf8Payload_Decodes()
    {
        using var host = NewHost();

        var task = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();
        host.RunFrame();
        Reply(host, "\x1b]52;c;aMOpbGxv\x1b\\"u8); // "héllo" (UTF-8 base64)

        Assert.True(task.IsCompleted);
        Assert.Equal("héllo", task.Result);
    }

    [Fact] // malformed base64 completes null rather than throwing into the frame loop
    public void Read_MalformedBase64_CompletesWithNull()
    {
        using var host = NewHost();

        var task = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();
        host.RunFrame();
        Reply(host, "\x1b]52;c;!!not-base64!!\x1b\\"u8);

        Assert.True(task.IsCompleted);
        Assert.Null(task.Result);
    }

    [Fact] // a whitespace-wrapped, unpadded reply (tmux/line-wrapping terminals) still decodes — not a false null
    public void Read_WhitespaceWrappedUnpaddedBase64_Decodes()
    {
        using var host = NewHost();

        var task = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();
        host.RunFrame();
        // "hello" is aGVsbG8= — strip the padding and inject a CRLF wrap; the decoder must tolerate both.
        Reply(host, "\x1b]52;c;aGVs\r\nbG8\x1b\\"u8);

        Assert.True(task.IsCompleted);
        Assert.Equal("hello", task.Result);
    }

    [Fact] // a multi-target reply ("pc;<data>") splits on the FIRST ';' — the target list isn't part of the payload
    public void Read_MultiTargetReply_Decodes()
    {
        using var host = NewHost();

        var task = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();
        host.RunFrame();
        Reply(host, "\x1b]52;pc;aGVsbG8=\x1b\\"u8);

        Assert.Equal("hello", task.Result);
    }

    [Fact] // two concurrent reads: one reply completes BOTH with the same text (OSC 52 carries no request id) —
           // documents the fan-out the TextBox in-flight guard exists to avoid duplicating into the model
    public void Read_TwoConcurrentReads_BothCompleteFromOneReply()
    {
        using var host = NewHost();

        var a = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();
        var b = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(2)).AsTask();
        host.RunFrame();
        Reply(host, "\x1b]52;c;aGVsbG8=\x1b\\"u8);

        Assert.Equal("hello", a.Result);
        Assert.Equal("hello", b.Result);
    }

    [Fact] // app shutdown completes a pending read null — the timer that would fire the timeout dies with the loop
    public void Read_Shutdown_CompletesPendingReadWithNull()
    {
        var host = NewHost();

        var task = host.Application.Clipboard.TryGetTextAsync(TimeSpan.FromSeconds(30)).AsTask();
        host.RunFrame();
        Assert.False(task.IsCompleted);

        host.Dispose(); // teardown raises BeginShutdown

        Assert.True(task.IsCompleted);
        Assert.Null(task.Result);
    }
}
