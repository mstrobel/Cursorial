using System.Globalization;
using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// Derives a control's KeyTip badge letter (keytips-design §4). The ladder, applied lazily at level-build time:
/// <list type="number">
/// <item>an <see cref="IKeyTipProvider.ResolveKeyTip"/> override,</item>
/// <item>an explicit <c>KeyTip.Key</c>,</item>
/// <item>nothing when <c>KeyTip.AutoAssign</c> is <see langword="false"/>,</item>
/// <item>the control's access-key letter (a <see cref="ContentControl"/> that folds one),</item>
/// <item>the control's <see cref="BarCommand.Text"/> mnemonic (or its first letter),</item>
/// <item>the first letter of the display text,</item>
/// <item>nothing derivable (a DEBUG diagnostic).</item>
/// </list>
/// Results 1–2 are <c>explicit</c> (never dropped on a collision); 4–6 are auto (dropped when their letter clashes).
/// </summary>
public static class KeyTipModel
{
    /// <summary>Resolves the badge letter for <paramref name="target"/>. <c>KeyTip</c> is <see langword="null"/> when
    /// the control has no badge (auto-assign off / nothing derivable). <c>Explicit</c> distinguishes an author-pinned
    /// letter (collision-immune) from an auto-derived one.</summary>
    public static (string? KeyTip, bool Explicit) Resolve(UIElement target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // 1 — a per-control provider override.
        if (target is IKeyTipProvider provider && Clean(provider.ResolveKeyTip()) is { } custom)
            return (custom, true);

        // 2 — an explicit KeyTip.Key.
        if (Clean(KeyTip.GetKey(target)) is { } explicitKey)
            return (explicitKey, true);

        // 3 — opted out of a badge.
        if (!KeyTip.GetAutoAssign(target))
            return (null, false);

        // 4 — the control's access-key mnemonic.
        if (target is ContentControl content)
        {
            var access = content.GetAccessTextInternal();
            if (access.HasKey)
                return (char.ToUpperInvariant(access.Key).ToString(), false);
        }

        // 5 — the bound BarCommand's text mnemonic, else its first letter.
        if (CommandOf(target) is BarCommand { Text: { Length: > 0 } commandText })
        {
            var parsed = AccessText.Parse(commandText);
            if (parsed.HasKey)
                return (char.ToUpperInvariant(parsed.Key).ToString(), false);
            if (FirstLetter(parsed.Text) is { } commandLetter)
                return (commandLetter, false);
        }

        // 6 — the first letter of the display text (Header / Content).
        if (FirstLetter(DisplayText(target)) is { } letter)
            return (letter, false);

        // 7 — nothing derivable.
        KeyTipDiagnostics.Warning($"No KeyTip letter could be derived for {target.GetType().Name}; it is omitted from the overlay.");
        return (null, false);
    }

    private static string? Clean(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToUpperInvariant();

    // The BarCommand a bar control is bound to (ButtonBase covers every bar button; MenuItem covers menu rows).
    private static BarCommand? CommandOf(UIElement target)
        => target switch
        {
            ButtonBase button => button.Command as BarCommand,
            MenuItem menuItem => menuItem.Command as BarCommand,
            _ => null,
        };

    private static string? DisplayText(UIElement target)
        => target switch
        {
            HeaderedContentControl headered => headered.Header?.ToString(),
            HeaderedItemsControl headered => headered.Header?.ToString(),   // RibbonGroup — badge from its group name
            ContentControl content => content.Content?.ToString(),
            _ => null,
        };

    // The first LETTER of a display string, uppercased (skips leading whitespace/punctuation/DIGITS). Digits are
    // excluded on purpose: the QAT registers explicit digit badges (1..9), so a digit-leading header must not
    // auto-derive a digit that would collide with them (keytips-design §4 step 5/6 — "first non-whitespace letter").
    private static string? FirstLetter(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
                return char.ToUpperInvariant(ch).ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }
}
