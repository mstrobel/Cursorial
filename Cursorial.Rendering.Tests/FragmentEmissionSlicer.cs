using System.Collections.Immutable;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// Slices the DECSC (<c>ESC 7</c>) … DECRC (<c>ESC 8</c>) brackets that <c>FrameRenderer</c> wraps every
/// fragment emission in. Only fragments save the cursor, so a bracket isolates one fragment's backdrop SGR
/// and payload from the cell pass around it.
/// </summary>
/// <remarks>
/// Lifted out of <c>FrameRendererDefaultColorTests</c> so the text-pipeline characterization harness
/// in <c>Cursorial.Drawing.Tests</c> (which compiles this file by link) slices brackets exactly the way the
/// existing default-color tests do, rather than restating the rule a second time.
/// </remarks>
internal static class FragmentEmissionSlicer
{
    // Composed rather than written as an escape: C#'s \x is variable-length and would swallow the
    // trailing digit of "ESC 7" into the character code.
    private static readonly string s_decsc = (char) 0x1B + "7";
    private static readonly string s_decrc = (char) 0x1B + "8";

    /// <summary>
    /// Every bracketed fragment emission in <paramref name="output"/>, in emission order, each spanning
    /// DECSC (inclusive) up to but excluding its DECRC. An unclosed trailing bracket is dropped — the
    /// renderer always closes what it opens, so one can only appear in a truncated capture.
    /// </summary>
    public static ImmutableArray<string> All(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var emissions = ImmutableArray.CreateBuilder<string>();
        int search = 0;

        while (true)
        {
            int start = output.IndexOf(s_decsc, search, StringComparison.Ordinal);
            if (start < 0) break;

            int end = output.IndexOf(s_decrc, start, StringComparison.Ordinal);
            if (end < 0) break;

            emissions.Add(output[start..end]);
            search = end + s_decrc.Length;
        }

        return emissions.ToImmutable();
    }

    /// <summary>
    /// The bytes of the one fragment emission in <paramref name="output"/>. Fails the calling test when no
    /// bracket is present, or it is left unclosed.
    /// </summary>
    public static string Single(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        int start = output.IndexOf(s_decsc, StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected a bracketed fragment emission (DECSC) in the output.");
        int end = output.IndexOf(s_decrc, start, StringComparison.Ordinal);
        Assert.True(end > start, "Expected the fragment emission to be closed by DECRC.");
        return output[start..end];
    }
}
