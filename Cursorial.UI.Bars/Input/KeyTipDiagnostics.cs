using System.Diagnostics;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// DEBUG diagnostics for the KeyTip overlay (keytips-design §4/§5): collision drops, non-derivable targets, and
/// parked-level build failures. Emissions are compiled out of Release builds (the <see cref="ConditionalAttribute"/>);
/// a test / tool can subscribe <see cref="MessageLogged"/> to observe them.
/// </summary>
public static class KeyTipDiagnostics
{
    /// <summary>Raised for each DEBUG diagnostic (message text). Subscribe in tests to assert a collision/skip fired.</summary>
    public static event Action<string>? MessageLogged;

    /// <summary>Logs a KeyTip diagnostic (DEBUG only).</summary>
    [Conditional("DEBUG")]
    public static void Warning(string message) => MessageLogged?.Invoke(message);
}
