using Cursorial.Input.Parsing;

namespace Cursorial.Tests.Input.Parsing;

/// <summary>
/// Test sink that records every classifier callback as a strongly-typed record so individual
/// assertions can compare against expected token sequences. All <see cref="ReadOnlySpan{T}"/>
/// arguments are copied at capture time.
/// </summary>
internal sealed class RecordingTokenSink : IVtSequenceTokenSink
{
    public List<RecordedToken> Tokens { get; } = [];

    public void OnPrint(ReadOnlySpan<byte> bytes)
        => Tokens.Add(new RecordedToken.Print(bytes.ToArray()));

    public void OnExecute(byte controlChar)
        => Tokens.Add(new RecordedToken.Execute(controlChar));

    // Recorded distinctly from Execute (rather than leaning on the interface's default forward)
    // so tests can prove the classifier framed `ESC <C0>` as Alt+<key> and not as a bare control.
    public void OnAltExecute(byte controlChar)
        => Tokens.Add(new RecordedToken.AltExecute(controlChar));

    public void OnEscDispatch(ReadOnlySpan<byte> intermediates, byte final)
        => Tokens.Add(new RecordedToken.EscDispatch(intermediates.ToArray(), final));

    public void OnCsiDispatch(byte privatePrefix, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> intermediates, byte final)
        => Tokens.Add(new RecordedToken.CsiDispatch(privatePrefix, parameters.ToArray(), intermediates.ToArray(), final));

    public void OnOscDispatch(ReadOnlySpan<byte> body)
        => Tokens.Add(new RecordedToken.OscDispatch(body.ToArray()));

    public void OnDcsHook(byte privatePrefix, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> intermediates, byte final)
        => Tokens.Add(new RecordedToken.DcsHook(privatePrefix, parameters.ToArray(), intermediates.ToArray(), final));

    public void OnDcsPut(ReadOnlySpan<byte> bytes)
        => Tokens.Add(new RecordedToken.DcsPut(bytes.ToArray()));

    public void OnDcsUnhook()
        => Tokens.Add(new RecordedToken.DcsUnhook());

    public void OnX10MouseDispatch(byte cb, byte cx, byte cy)
        => Tokens.Add(new RecordedToken.X10MouseDispatch(cb, cx, cy));
}

internal abstract record RecordedToken
{
    public sealed record Print(byte[] Bytes) : RecordedToken;
    public sealed record Execute(byte ControlChar) : RecordedToken;
    public sealed record AltExecute(byte ControlChar) : RecordedToken;
    public sealed record EscDispatch(byte[] Intermediates, byte Final) : RecordedToken;
    public sealed record CsiDispatch(byte PrivatePrefix, byte[] Parameters, byte[] Intermediates, byte Final) : RecordedToken;
    public sealed record OscDispatch(byte[] Body) : RecordedToken;
    public sealed record DcsHook(byte PrivatePrefix, byte[] Parameters, byte[] Intermediates, byte Final) : RecordedToken;
    public sealed record DcsPut(byte[] Bytes) : RecordedToken;
    public sealed record DcsUnhook : RecordedToken;
    public sealed record X10MouseDispatch(byte Cb, byte Cx, byte Cy) : RecordedToken;
}
