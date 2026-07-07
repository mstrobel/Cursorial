namespace Cursorial.UI.Configuration;

/// <summary>
/// An immutable point-in-time copy of both <see cref="UserOptionsStore"/> layers — what the
/// options UI captures at open and restores on Reset/Cancel. Opaque by design: consumers only
/// hand it back to <see cref="UserOptionsStore.RestoreSnapshot"/>.
/// </summary>
public sealed class UserOptionsSnapshot
{
    internal UserOptionsSnapshot(Dictionary<string, string> global, Dictionary<string, string> application)
    {
        Global = global;
        Application = application;
    }

    internal Dictionary<string, string> Global { get; }

    internal Dictionary<string, string> Application { get; }
}
