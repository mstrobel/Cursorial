namespace Cursorial.UI.Input;

/// <summary>
/// Args for the <c>PreviewTextInput</c>/<c>TextInput</c> events (design doc §7.2 / §7.5 step 7):
/// composed text synthesized from an unhandled printable key-down, or a whole bracketed paste in
/// one event (<see cref="FromPaste"/>). There is no separate paste event in the UI vocabulary.
/// </summary>
public sealed class TextInputEventArgs : RoutedEventArgs
{
    private ReadOnlyMemory<char> _text;
    private bool _fromPaste;

    /// <summary>Creates an empty caller-owned args (also the pooled construction path).</summary>
    public TextInputEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args.</summary>
    /// <param name="routedEvent">The text-input event to raise.</param>
    /// <param name="source">The dispatch target.</param>
    /// <param name="text">The composed text (or whole paste).</param>
    /// <param name="fromPaste">Whether the text arrived as a bracketed paste.</param>
    public TextInputEventArgs(RoutedEvent<TextInputEventArgs> routedEvent, UIElement source, ReadOnlyMemory<char> text, bool fromPaste = false)
        : base(routedEvent, source)
    {
        _text = text;
        _fromPaste = fromPaste;
    }

    /// <summary>The composed text or whole paste (event-owned memory; safe to retain).</summary>
    public ReadOnlyMemory<char> Text
    {
        get
        {
            ThrowIfStale();
            return _text;
        }
    }

    /// <summary>
    /// Whether the text arrived via bracketed paste — a text control keys newline flattening and
    /// any paste-specific handling on this flag (without bracketed paste, pastes are
    /// indistinguishable from typing and arrive as ordinary key events).
    /// </summary>
    public bool FromPaste
    {
        get
        {
            ThrowIfStale();
            return _fromPaste;
        }
    }

    /// <summary>Framework-dispatch payload initialization.</summary>
    internal void InitializeText(ReadOnlyMemory<char> text, bool fromPaste)
    {
        _text = text;
        _fromPaste = fromPaste;
    }

    /// <inheritdoc/>
    internal override void ResetPooledState()
    {
        _text = default;
        _fromPaste = false;
    }
}
