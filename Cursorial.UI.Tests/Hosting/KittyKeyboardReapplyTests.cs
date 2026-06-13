// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — see TeardownAndExceptionTests for the rationale.
#pragma warning disable xUnit1031

using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// The application re-applies the negotiated screen-local opt-ins (notably the per-screen-buffer
/// Kitty keyboard flag push) after it enters the alternate screen — otherwise key-up / repeat
/// reporting (and with it the access-key Alt gate, ND23) would be stranded on the main screen where
/// negotiation ran. Proven via <c>SyntheticTerminalHost.ReapplyScreenLocalOptInsCount</c>; the
/// byte-level push/pop symmetry is proven at the negotiator level (VtTerminalNegotiatorTests).
/// </summary>
public sealed class KittyKeyboardReapplyTests
{
    [Fact]
    public void Startup_OnAltScreen_ReAppliesScreenLocalOptInsExactlyOnce()
    {
        using var host = UITestHost.Create(); // UseAlternateScreen defaults true; KittyTruecolor caps
        host.ShowRoot(new Probe(4, 1) { FillGlyph = "X" });
        Assert.True(host.RunUntilIdle());

        // Exactly one re-apply, performed at startup right after entering the alt screen — NOT per frame.
        Assert.Equal(1, host.Terminal.ReapplyScreenLocalOptInsCount);
    }

    [Fact]
    public void Startup_NoAltScreen_DoesNotReApply()
    {
        // No alt-screen entry ⇒ negotiation's main-screen push is still active ⇒ nothing to re-apply.
        using var host = UITestHost.Create(new UITestHostOptions { UseAlternateScreen = false });
        host.ShowRoot(new Probe(4, 1) { FillGlyph = "X" });
        Assert.True(host.RunUntilIdle());

        Assert.Equal(0, host.Terminal.ReapplyScreenLocalOptInsCount);
    }
}
