namespace Cursorial.UI.Data;

/// <summary>The lifecycle status of a <see cref="BindingExpressionBase"/> (design doc §6.2).</summary>
public enum BindingStatus : byte
{
    /// <summary>Created but not yet activated.</summary>
    Inactive,

    /// <summary>Resolved and producing values.</summary>
    Active,

    /// <summary>Activated but a path step failed to resolve.</summary>
    PathError,

    /// <summary>Activated but the anchor/source could not be resolved (parked; retry on attach).</summary>
    SourceMissing,

    /// <summary>Disposed.</summary>
    Detached
}
