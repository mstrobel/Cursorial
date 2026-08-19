using System.Reflection;

namespace Cursorial.UI.Controls;

/// <summary>
/// The WPF-style type-ahead facility for <see cref="ItemsControl"/>s (design doc §12.6): pressing printable keys
/// accumulates a prefix (reset after a short idle) and moves the current item to the first match, repeating a single
/// character cycling among items that start with it. The match text per item is, in order, the per-item
/// <see cref="TextProperty"/>, else the control's <see cref="TextPathProperty"/> evaluated against the item, else the
/// item's <c>ToString()</c>. Enabled by default (<see cref="ItemsControl.IsTextSearchEnabled"/>), case-insensitive by
/// default (<see cref="ItemsControl.IsTextSearchCaseSensitive"/>).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global - attached-property owner used only as a generic type argument
public sealed class TextSearch
{
    private TextSearch()
    {
    }

    /// <summary>The property path on each item to match against (e.g. <c>"Name"</c>; dotted paths walk properties). Set on the <see cref="ItemsControl"/>.</summary>
    public static readonly AttachedProperty<string?> TextPathProperty =
        UIProperty.RegisterAttached<TextSearch, ItemsControl, string?>("TextPath");

    /// <summary>A per-item match-text override. Set on a generated container (or an own-container item) to give it an explicit search string.</summary>
    public static readonly AttachedProperty<string?> TextProperty =
        UIProperty.RegisterAttached<TextSearch, UIElement, string?>("Text");

    /// <summary>Reads <see cref="TextPathProperty"/>.</summary>
    public static string? GetTextPath(ItemsControl element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TextPathProperty);
    }

    /// <summary>Sets <see cref="TextPathProperty"/>.</summary>
    public static void SetTextPath(ItemsControl element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TextPathProperty, value);
    }

    /// <summary>Reads <see cref="TextProperty"/>.</summary>
    public static string? GetText(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TextProperty);
    }

    /// <summary>Sets <see cref="TextProperty"/>.</summary>
    public static void SetText(UIElement element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TextProperty, value);
    }

    /// <summary>Resolves a dotted property path against <paramref name="item"/> (the <see cref="TextPathProperty"/> lookup).</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "TextPath evaluates dotted members against LIVE items by runtime type; a member the trimmer " +
                        "removed yields null — the item simply contributes no searchable text.")]
    internal static string? EvaluatePath(object? item, string path)
    {
        var current = item;
        foreach (var segment in path.Split('.'))
        {
            if (current is null)
                return null;

            var property = current.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
                return null;

            current = property.GetValue(current);
        }

        return current?.ToString();
    }
}

/// <summary>The pure prefix matcher (extracted for unit testing — no state, no clock).</summary>
internal static class TextSearchMatcher
{
    /// <summary>
    /// Returns the index of the first item (from <paramref name="currentIndex"/>, wrapping) whose match text starts
    /// with <paramref name="prefix"/>, or −1. A <paramref name="repeat"/> search (a single character re-pressed)
    /// starts <i>after</i> the current item so it cycles; an extension starts <i>at</i> the current item so it can stay.
    /// </summary>
    public static int FindMatchIndex(int count, Func<int, string?> getText, int currentIndex, string prefix, bool repeat, bool caseSensitive)
    {
        if (count == 0 || prefix.Length == 0)
            return -1;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var anchor = currentIndex < 0 ? 0 : currentIndex;
        var start = repeat ? (anchor + 1) % count : anchor;

        for (var k = 0; k < count; k++)
        {
            var i = (start + k) % count;
            if (getText(i) is { } text && text.StartsWith(prefix, comparison))
                return i;
        }

        return -1;
    }
}

/// <summary>Per-control type-ahead state: the accumulated prefix plus the idle-reset timer.</summary>
internal sealed class TextSearchController
{
    private static readonly TimeSpan ResetDelay = TimeSpan.FromSeconds(1);

    private string _prefix = "";
    private UITimer? _resetTimer;

    /// <summary>
    /// Processes <paramref name="typed"/> against the items and returns the matched index, or −1 for no move.
    /// Extends the prefix on a new character (re-searching from the current item); cycles on a re-pressed single
    /// character. A leading space is ignored so it stays available as a command (e.g. ListBox toggle).
    /// </summary>
    public int Handle(string typed, int count, Func<int, string?> getText, int currentIndex, bool caseSensitive)
    {
        if (_prefix.Length == 0 && typed == " ")
            return -1; // a leading space is a command, not the start of a search

        RestartTimer();

        // Decide repeat with the SAME folding the matcher uses (OrdinalIgnoreCase by default), not ToUpperInvariant —
        // the two disagree for characters like U+017F long-s, which would mis-classify a distinct key as a repeat.
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var repeat = typed.Length == 1 && _prefix.Length >= 1 && IsAllChar(_prefix, typed[0], comparison);
        var candidate = repeat ? typed : _prefix + typed;

        var match = TextSearchMatcher.FindMatchIndex(count, getText, currentIndex, candidate, repeat, caseSensitive);
        if (match >= 0)
            _prefix = candidate; // adopt the prefix only on a hit (a miss leaves the buffer for the next keystroke)

        return match;
    }

    /// <summary>Clears the accumulated prefix (idle timeout, focus loss, or detach).</summary>
    public void Reset()
    {
        _prefix = "";
        _resetTimer?.Stop();
        _resetTimer = null;
    }

    private void RestartTimer()
    {
        _resetTimer?.Stop();
        // Frame-aligned idle reset; absent a scheduler (detached / no app) the prefix simply persists until Reset.
        _resetTimer = AnimationScheduler.CurrentOrNull is not null ? UITimer.Start(ResetDelay, Reset) : null;
    }

    private static bool IsAllChar(string text, char c, StringComparison comparison)
    {
        Span<char> one = stackalloc char[1];
        one[0] = c;
        Span<char> probe = stackalloc char[1];
        foreach (var ch in text)
        {
            probe[0] = ch;
            if (!MemoryExtensions.Equals(probe, one, comparison)) // exact same folding as the prefix match
                return false;
        }

        return true;
    }
}
