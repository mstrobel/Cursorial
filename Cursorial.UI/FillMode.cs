namespace Cursorial.UI;

public enum FillMode : byte
{
    /// <summary>
    /// Existing content is tinted by the fill brush, but not overwritten. Blended content has no protection from being
    /// overwritten by other fill operations.
    /// </summary>
    Blended,

    /// <summary>
    /// Existing content is tinted by the fill brush, but not overwritten. However, the cells filled will be protected from
    /// being overwritten by other non-<see cref="Occluding"/> fill operations.
    /// </summary>
    Durable,

    /// <summary>
    /// Existing will be replaced by durable whitespace that will be protected from any other non-<see cref="Occluding"/>
    /// fill operations.
    /// </summary>
    Occluding
}