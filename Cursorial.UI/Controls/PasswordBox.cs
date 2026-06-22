namespace Cursorial.UI.Controls;

/// <summary>
/// A single-line masked text field (the WPF/Avalonia <c>PasswordBox</c>): a <see cref="TextBox"/> whose
/// <em>display</em> is masked — every grapheme cluster of the entered text renders as <see cref="PasswordChar"/>
/// (default <c>'●'</c> U+25CF, the design-guide password dot; the renderer's ambiguous-width defense handles wide-rendering terminals) —
/// while the model (caret, selection, navigation, <see cref="TextBox.MaxLength"/>) operates on the real text.
/// The plaintext is the inherited <see cref="TextBox.Text"/>, also surfaced as <see cref="Password"/>.
/// <para>
/// Security posture (terminal-scope v1): clipboard <b>Copy/Cut are suppressed</b> so a password is never written
/// to the clipboard (OSC 52); the rendered glyphs are masked (the caret + selection highlight track per cluster).
/// Plaintext is held in a managed <see cref="string"/> (no <c>SecureString</c>) — adequate for a terminal library;
/// callers that can read <see cref="TextBox.SelectedText"/>/<see cref="Password"/> in process are trusted.
/// <see cref="RevealPassword"/> temporarily shows the plaintext (the common "show password" affordance).
/// </para>
/// </summary>
public class PasswordBox : TextBox
{
    /// <summary>The glyph each cluster is masked with (default <c>'●'</c>, U+25CF — the design-guide password dot;
    /// the renderer's ambiguous-width defense handles terminals that render it wide; set an ASCII char such as
    /// <c>'*'</c> for strictly single-width). Must be a single non-whitespace character: in XAML a
    /// whitespace/empty/multi-char value is rejected at load (the <c>char</c> converter trims), so set those in code.</summary>
    public static readonly StyledProperty<char> PasswordCharProperty =
        UIProperty.Register<PasswordBox, char>(nameof(PasswordChar), defaultValue: '●',
            changed: static (sender, _, _) => Reproject(sender));

    /// <summary>When <see langword="true"/>, the field shows the plaintext instead of the mask (default false).</summary>
    public static readonly StyledProperty<bool> RevealPasswordProperty =
        UIProperty.Register<PasswordBox, bool>(nameof(RevealPassword), defaultValue: false,
            changed: static (sender, _, _) => Reproject(sender));

    static PasswordBox() => AffectsMeasure<PasswordBox>(PasswordCharProperty, RevealPasswordProperty);

    /// <inheritdoc cref="PasswordCharProperty"/>
    public char PasswordChar { get => GetValue(PasswordCharProperty); set => SetValue(PasswordCharProperty, value); }

    /// <inheritdoc cref="RevealPasswordProperty"/>
    public bool RevealPassword { get => GetValue(RevealPasswordProperty); set => SetValue(RevealPasswordProperty, value); }

    /// <summary>The entered text — an alias for the inherited <see cref="TextBox.Text"/> (the WPF name).</summary>
    public string Password { get => Text; set => Text = value; }

    // ───────────────────────────── masked display projection ─────────────────────────────

    /// <inheritdoc/>
    internal override string DisplayText
        => RevealPassword ? Text : new string(PasswordChar, GraphemeLayout.Build(Text).ClusterCount);

    /// <inheritdoc/>
    internal override int ToDisplayIndex(int modelIndex)
        => RevealPassword ? modelIndex : GraphemeLayout.Build(Text).ClusterIndexAtOrBefore(modelIndex);

    /// <inheritdoc/>
    internal override int ToModelIndex(int displayIndex)
        => RevealPassword ? displayIndex : GraphemeLayout.Build(Text).CharIndexOfCluster(displayIndex);

    // ───────────────────────────── security: no clipboard leak ─────────────────────────────

    /// <summary>Suppressed — a password is never copied to the clipboard (the chord bubbles, WPF parity).</summary>
    private protected override bool Copy() => false;

    /// <summary>Suppressed — a password is never cut to the clipboard (no copy, no delete; WPF parity).</summary>
    private protected override bool Cut() => false;

    // A change to the mask glyph or the reveal flag re-projects the display: re-measure (AffectsMeasure) handles
    // layout; nudge the presenter so the caret/scroll/visual refresh the same frame even when the width is unchanged.
    private static void Reproject(UIObject sender)
    {
        var box = (PasswordBox)sender;
        box.Presenter?.RefreshCaretAndScroll();
        box.Presenter?.InvalidateVisual();
    }
}
