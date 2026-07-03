using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Cursorial.Input;
using Cursorial.UI.Xaml;

namespace Cursorial.UI.Input;

/// <summary>
/// The abstract gesture an <see cref="InputBinding"/> triggers on. <see cref="KeyGesture"/> is the
/// only concrete kind in v1 (<c>MouseBinding</c> is deferred — design doc §7.12); the base exists
/// so the binding surface (<see cref="InputBinding.Gesture"/>) does not source-break when other
/// gesture kinds arrive.
/// </summary>
[TypeConverter(typeof(InputGestureConverter))]
public abstract record InputGesture
{
    /// <summary>
    /// Whether <paramref name="e"/> triggers this gesture — the polymorphic entry the bindings
    /// sweep uses; args of a foreign event family never match.
    /// </summary>
    public abstract bool Matches(RoutedEventArgs e);
}

/// <summary>
/// A keyboard shortcut (design doc §7.9): a <see cref="Key"/> plus an exact modifier set. Printable
/// keys are <b>character gestures</b> — <see cref="Cursorial.Input.Key.Character"/> with the
/// <see cref="Character"/> text — because the identity of a printable key is <c>(Key, Text)</c>,
/// never <see cref="Cursorial.Input.Key"/> alone (the input-map gotcha; matrix ND10).
/// </summary>
/// <remarks>
/// Matching (ND14): <see cref="KeyEventArgs.Modifiers"/> — lock-free, never
/// <c>ExtendedModifiers</c> — must equal <see cref="Modifiers"/> <b>exactly</b>; named keys match
/// by <see cref="Key"/> equality; character gestures by ordinal case-insensitive comparison of
/// <see cref="Character"/> against the event text. One gesture matches every wire encoding of the
/// same chord (legacy C0, Kitty CSI-u, ESC-prefix Alt) because the interpreter normalizes them all
/// to the same <c>(Key, Text, Modifiers)</c> shape.
/// </remarks>
public sealed record KeyGesture : InputGesture
{
    /// <summary>Creates a gesture.</summary>
    /// <param name="key">The key; <see cref="Cursorial.Input.Key.Character"/> declares a character gesture.</param>
    /// <param name="modifiers">The exact modifier set the event must carry.</param>
    /// <param name="character">
    /// The printable text for character gestures — required for
    /// <see cref="Cursorial.Input.Key.Character"/>, forbidden for named keys.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A character gesture without <paramref name="character"/>, or a named key with one (doc §7.9).
    /// </exception>
    public KeyGesture(Key key, KeyModifiers modifiers = KeyModifiers.None, string? character = null)
    {
        if (key == Key.Character)
        {
            if (string.IsNullOrEmpty(character))
                throw new ArgumentException("A Key.Character gesture must carry its printable text in Character.", nameof(character));
        }
        else if (character is not null)
        {
            throw new ArgumentException($"A named-key gesture ({key}) must not carry Character — character gestures use Key.Character.", nameof(character));
        }

        Key = key;
        Modifiers = modifiers;
        Character = character;
    }

    /// <summary>The key. <see cref="Cursorial.Input.Key.Character"/> means "printable — see <see cref="Character"/>".</summary>
    public Key Key { get; }

    /// <summary>The exact modifier set (lock bits never participate — ND14).</summary>
    public KeyModifiers Modifiers { get; }

    /// <summary>The printable text of a character gesture; null for named keys.</summary>
    public string? Character { get; }

    /// <summary>
    /// Parses a gesture string (ND13): <c>'+'</c>-separated, tokens trimmed, case-insensitive.
    /// Modifier tokens: <c>Ctrl|Control</c>, <c>Shift</c>, <c>Alt</c>, <c>Super|Win|Cmd</c>,
    /// <c>Meta</c>, <c>Hyper</c>. The final token: a single character produces a character gesture
    /// with <see cref="Character"/> canonicalized upper-invariant (<c>"ctrl+s"</c> and
    /// <c>"Ctrl+S"</c> produce the same gesture and the same <see cref="ToString"/>; matching is
    /// ordinal case-insensitive regardless — amended ND13);
    /// a multi-character token resolves through the aliases (<c>Esc</c>, <c>Return</c>, <c>Del</c>,
    /// <c>Ins</c>, <c>PgUp</c>/<c>PgDn</c>, <c>Up/Down/Left/Right</c>, <c>Space</c> — the last
    /// compiling to the character gesture <c>" "</c> per ND10) then the <see cref="Key"/> enum
    /// names.
    /// </summary>
    /// <param name="gesture">The gesture string.</param>
    /// <exception cref="FormatException">An empty/unrecognized token (e.g. <c>"Ctrl+"</c>, <c>"Bogus+X"</c>, <c>""</c>).</exception>
    public static KeyGesture Parse(string gesture)
    {
        if (TryParse(gesture, out var result, out var ex) && result is not null)
            return result;

        throw ex!;
    }

    /// <summary>
    /// Parses a gesture string (ND13): <c>'+'</c>-separated, tokens trimmed, case-insensitive.
    /// Modifier tokens: <c>Ctrl|Control</c>, <c>Shift</c>, <c>Alt</c>, <c>Super|Win|Cmd</c>,
    /// <c>Meta</c>, <c>Hyper</c>. The final token: a single character produces a character gesture
    /// with <see cref="Character"/> canonicalized upper-invariant (<c>"ctrl+s"</c> and
    /// <c>"Ctrl+S"</c> produce the same gesture and the same <see cref="ToString"/>; matching is
    /// ordinal case-insensitive regardless — amended ND13);
    /// a multi-character token resolves through the aliases (<c>Esc</c>, <c>Return</c>, <c>Del</c>,
    /// <c>Ins</c>, <c>PgUp</c>/<c>PgDn</c>, <c>Up/Down/Left/Right</c>, <c>Space</c> — the last
    /// compiling to the character gesture <c>" "</c> per ND10) then the <see cref="Key"/> enum
    /// names.
    /// </summary>
    /// <param name="gesture">The gesture string to parse.</param>
    /// <param name="result">The parsed gesture, if successful.</param>
    /// <param name="ex">
    /// A format exception, if parsing failed (an empty/unrecognized token (e.g. <c>"Ctrl+"</c>,
    /// <c>"Bogus+X"</c>, <c>""</c>).).
    /// </param>
    public static bool TryParse(string gesture, out KeyGesture? result, out Exception? ex)
    {
        ArgumentNullException.ThrowIfNull(gesture);

        result = null;
        ex = null;

        var modifiers = KeyModifiers.None;
        var remaining = gesture.AsSpan();

        int plus;

        while ((plus = remaining.IndexOf('+')) >= 0)
        {
            modifiers |= ParseModifier(remaining[..plus].Trim(), gesture);
            remaining = remaining[(plus + 1)..];
        }

        var token = remaining.Trim();

        if (token.IsEmpty)
        {
            ex = new FormatException($"Invalid key gesture '{gesture}': missing the key token.");
            return false;
        }

        if (token.Length == 1)
        {
            result = new KeyGesture(Key.Character, modifiers, char.ToUpperInvariant(token[0]).ToString());
            return true;
        }

        // Multi-character: aliases first (Space pins to the character gesture per ND10), then enum names.
        if (token.Equals("Space", StringComparison.OrdinalIgnoreCase))
        {
            result = new KeyGesture(Key.Character, modifiers, " ");
            return true;
        }

        var key = ResolveAlias(token);

        if (key == Key.None && (char.IsAsciiDigit(token[0]) ||
                                !Enum.TryParse(token, ignoreCase: true, out key) ||
                                key is Key.None or Key.Character))
        {
            ex = new FormatException($"Invalid key gesture '{gesture}': unrecognized key '{token}'.");
            return false;
        }

        result = new KeyGesture(key, modifiers);
        return true;
    }

    /// <inheritdoc/>
    public override bool Matches(RoutedEventArgs e) => e is KeyEventArgs key && Matches(key);

    /// <summary>
    /// Whether <paramref name="e"/> is this gesture (ND14): exact lock-free modifiers; named keys
    /// by <see cref="Key"/>; character gestures by ordinal case-insensitive text comparison.
    /// </summary>
    public bool Matches(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Modifiers != Modifiers)
            return false;

        return Key == Key.Character
                   ? e.Key == Key.Character && e.Text.Span.Equals(Character.AsSpan(), StringComparison.OrdinalIgnoreCase)
                   : e.Key == Key;
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{(Modifiers == KeyModifiers.None ? "" : $"{Modifiers}+")}{(Key == Key.Character ? Character : Key.ToString())}";

    private static KeyModifiers ParseModifier(ReadOnlySpan<char> token, string gesture)
    {
        if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            return KeyModifiers.Control;

        if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            return KeyModifiers.Shift;

        if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            return KeyModifiers.Alt;

        if (token.Equals("Super", StringComparison.OrdinalIgnoreCase)
            || token.Equals("Win", StringComparison.OrdinalIgnoreCase)
            || token.Equals("Cmd", StringComparison.OrdinalIgnoreCase))
        {
            return KeyModifiers.Super;
        }

        if (token.Equals("Meta", StringComparison.OrdinalIgnoreCase))
            return KeyModifiers.Meta;

        if (token.Equals("Hyper", StringComparison.OrdinalIgnoreCase))
            return KeyModifiers.Hyper;

        throw new FormatException($"Invalid key gesture '{gesture}': unrecognized modifier '{token}'.");
    }

    private static Key ResolveAlias(ReadOnlySpan<char> token)
    {
        if (token.Equals("Esc", StringComparison.OrdinalIgnoreCase))
            return Key.Escape;

        if (token.Equals("Return", StringComparison.OrdinalIgnoreCase))
            return Key.Enter;

        if (token.Equals("Del", StringComparison.OrdinalIgnoreCase))
            return Key.Delete;

        if (token.Equals("Ins", StringComparison.OrdinalIgnoreCase))
            return Key.Insert;

        if (token.Equals("PgUp", StringComparison.OrdinalIgnoreCase))
            return Key.PageUp;

        if (token.Equals("PgDn", StringComparison.OrdinalIgnoreCase))
            return Key.PageDown;

        if (token.Equals("Up", StringComparison.OrdinalIgnoreCase))
            return Key.UpArrow;

        if (token.Equals("Down", StringComparison.OrdinalIgnoreCase))
            return Key.DownArrow;

        if (token.Equals("Left", StringComparison.OrdinalIgnoreCase))
            return Key.LeftArrow;

        if (token.Equals("Right", StringComparison.OrdinalIgnoreCase))
            return Key.RightArrow;

        return Key.None;
    }
}

/// <summary>
/// An <see cref="InputGesture"/> <see cref="ITypeConverter">type converter</see>.
/// Currently only handles <see cref="KeyGesture"/>, but will eventually handle
/// mouse/pointer gestures.
/// </summary>
public sealed class InputGestureConverter : TypeConverter, ITypeConverter
{
    public static readonly InputGestureConverter Instance = new();

    // KeyGesture.Parse needs no services and never touches the tree — context-free.
    public bool IsContextFree => true;

    public object ConvertFromString(string text, in XamlValueContext ctx)
    {
        if (KeyGesture.TryParse(text, out KeyGesture? result, out var ex) && result is not null)
            return result;

        throw ex!;
    }

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        if (sourceType == typeof(string))
            return true;

        return base.CanConvertFrom(context, sourceType);
    }

    public override bool CanConvertTo(ITypeDescriptorContext? context, [NotNullWhen(true)] Type? destinationType)
    {
        if (destinationType == typeof(string))
            return true;

        return base.CanConvertTo(context, destinationType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string text)
            return ConvertFromString(text, ctx: default);

        return base.ConvertFrom(context, culture, value);
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (destinationType == typeof(string) && value is KeyGesture gesture)
            return gesture.ToString();

        return base.ConvertTo(context, culture, value, destinationType);
    }
}