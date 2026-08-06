namespace Cursorial.Text;

/// <summary>
/// The text weight axis (proposal-TextAttributes-decomposition §1). One axis, three values — Bold
/// and Faint share the terminal's SGR 22 reset, so they are alternatives on a single dial, not
/// independent flags: mutual exclusion by construction, and a weight conflict ("disabled says
/// Faint, heading says Bold") arbitrates deterministically through the lattice like any
/// single-valued property. The axis of WPF's <c>FontWeight</c> / CSS <c>font-weight</c> — not the
/// type (no font-object model, no 100–900 numeric weights; the deviated name signals the deviated
/// domain, the design doc's "no font types" pin refined).
/// </summary>
public enum TextWeight : byte
{
    /// <summary>No weight attribute (neither SGR 1 nor 2; the shared reset 22 state).</summary>
    Normal = 0,

    /// <summary>SGR 2 — faint / dim.</summary>
    Faint,

    /// <summary>SGR 1 — bold / increased intensity.</summary>
    Bold,
}