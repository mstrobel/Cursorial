using System.Collections.Immutable;

using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Rendering.Text;

/// <summary>
/// Fluent builder for <see cref="RichText"/> documents. Tracks an open <see cref="TextParagraph"/>
/// and a stack of styles + glyph maps + hyperlinks that combine for each appended
/// <see cref="TextRun"/>; closes the open paragraph automatically when a block-level transition
/// (<see cref="Text.HorizontalRule"/>, <see cref="Figlet"/>, etc.) is invoked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Style scoping.</b> <see cref="Push(in CellStyle)"/> pushes a style onto an inheritance stack;
/// each child layer merges with its parent (child wins where set). <see cref="PushMap"/> and
/// <see cref="PushHyperlink"/> apply per-run attributes similarly. The returned
/// <see cref="StyleScope"/> pops the corresponding layer on disposal — use
/// <c>using var _ = builder.Push(...)</c> for lexical scoping that mirrors markup nesting.
/// </para>
/// <para>
/// <b>Paragraph lifecycle.</b> Inline calls (<c>Run</c>, <c>LineBreak</c>, <c>Hyperlink</c>,
/// <c>InlineContent</c>) all append to the currently-open paragraph, lazily creating one if
/// none exists. Block calls (<c>Paragraph</c>, <c>HorizontalRule</c>, <c>Figlet</c>,
/// <c>SizedText</c>, <c>Content</c>) close the open paragraph before appending the new block.
/// </para>
/// </remarks>
public sealed class RichTextBuilder
{
    private readonly ImmutableArray<Block>.Builder _blocks = ImmutableArray.CreateBuilder<Block>();
    private readonly CellStyle _defaultStyle;
    private readonly GlyphSource? _defaultGlyphSource;
    private readonly TextTrimming _defaultTrimming;
    private readonly WrapMode _defaultWrap;

    private ImmutableArray<Inline>.Builder? _openInlines;

    // Per-paragraph configuration captured at the moment the paragraph is opened. Fields are
    // initialized to the documented defaults so an implicitly-opened paragraph (one created by
    // a Run/LineBreak/etc. without a preceding Paragraph() call) gets sensible behavior — in
    // particular, WrapMode.NoWrap is NOT the desired default. FlushOpenParagraph resets these
    // to the same values after closing a paragraph.
    private TextAlignment? _openAlignment;
    private WrapMode _openWrap;
    private TextTrimming _openTrim;
    private int? _openMaxLines;
    private Margins _openMargin;

    // Style / map / hyperlink stacks. The TOP of each stack is the active value applied to
    // appended runs that don't supply their own.
    private readonly Stack<CellStyle> _styles = new();
    private readonly Stack<IGlyphMap?> _maps = new();
    private readonly Stack<string?> _hyperlinks = new();
    private readonly Stack<object?> _tags = new();

    public RichTextBuilder(CellStyle defaultStyle = default,
                           TextTrimming? defaultTrimming = null,
                           WrapMode? defaultWrap = null,
                           GlyphSource? defaultGlyphSource = null)
    {
        _defaultStyle = defaultStyle;
        _defaultGlyphSource = defaultGlyphSource;
        _defaultTrimming = defaultTrimming ?? TextTrimming.None;
        _defaultWrap = defaultWrap ?? WrapMode.WordWrap;
        _openWrap = _defaultWrap;
        _openTrim = _defaultTrimming;
    }

    /// <summary>
    /// Optionally initialize the builder with a default trimming and wrapping mode. 
    /// </summary>
    /// <param name="trimming">The new default text trimming mode, or <c>null</c> to leave it unchanged.</param>
    /// <param name="wrapping">The new default text wrapping mode, or <c>null</c> to leave it unchanged.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if any content has already been added to this builder.
    /// </exception>
    internal void Initialize(TextTrimming? trimming, WrapMode? wrapping)
    {
        if (HasContent)
            throw new InvalidOperationException($"{nameof(Initialize)} must be called before adding any content.");

        if (trimming is {} t)
            _openTrim = t;

        if (wrapping is {} w)
            _openWrap = w;
    }

    internal bool HasContent => _blocks.Count > 0 || _openInlines?.Count > 0;
    
    /// <summary>
    /// Push <paramref name="style"/> onto the style stack. The pushed layer is merged with the
    /// previous top (child wins per <see cref="StyleExtensions.Compose"/>); subsequent
    /// <see cref="Run(string)"/> calls inherit the resulting style. Dispose the returned
    /// <see cref="StyleScope"/> to pop.
    /// </summary>
    public StyleScope Push(in CellStyle style)
    {
        var current = _styles.Count > 0 ? _styles.Peek() : _defaultStyle;
        _styles.Push(current.Compose(in style));
        return new StyleScope(this, StyleScope.Layer.Style);
    }

    /// <summary>
    /// Push a per-run <see cref="IGlyphMap"/> for grapheme remapping. Subsequent
    /// <see cref="Run(string)"/> calls inherit the map. Dispose the returned scope to pop.
    /// </summary>
    public StyleScope PushMap(IGlyphMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        _maps.Push(map);
        return new StyleScope(this, StyleScope.Layer.Map);
    }

    /// <summary>
    /// Push an OSC 8 hyperlink target. Subsequent runs are wrapped by the renderer in the
    /// hyperlink open/close pair. Dispose the returned scope to pop.
    /// </summary>
    public StyleScope PushHyperlink(string uri)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        _hyperlinks.Push(uri);
        return new StyleScope(this, StyleScope.Layer.Hyperlink);
    }

    /// <summary>
    /// Push an opaque <paramref name="tag"/> onto the tag stack. Subsequent <see cref="Run(string)"/> calls
    /// inherit it. An inline-scoped <see cref="ScopedBrush"/> is translated into each run's own
    /// <see cref="TextRun.Style"/> carrier; any other tag is preserved through layout onto every derived
    /// <see cref="FormattedTextRun"/> for a higher layer to read — e.g. a block/document-scoped
    /// <c>[brush=…]</c> markup tag. Dispose to pop.
    /// </summary>
    public StyleScope PushTag(object? tag)
    {
        _tags.Push(tag);
        return new StyleScope(this, StyleScope.Layer.Tag);
    }

    // ---- Inline appends ----

    /// <summary>Append a text run inheriting the current style / map / hyperlink / tag stacks.</summary>
    public RichTextBuilder Run(string text)
    {
        return Run(text, source: null, CurrentStyle, CurrentTag);
    }

    /// <summary>Append a text run with an explicit style, ignoring the style stack (but inheriting the tag).</summary>
    public RichTextBuilder Run(string text, in CellStyle style)
    {
        return Run(text, source: null, style, CurrentTag);
    }

    /// <summary>
    /// Append a text run with an explicit style and an opaque <paramref name="tag"/>. An inline-scoped
    /// <see cref="ScopedBrush"/> translates into the run's own <see cref="TextRun.Style"/> carrier; any
    /// other tag is preserved through layout onto every derived <see cref="FormattedTextRun"/> for a higher
    /// layer to read.
    /// </summary>
    public RichTextBuilder Run(string text, in CellStyle style, object? tag)
    {
        return Run(text, source: null, style, tag);
    }

    /// <summary>
    /// Append a text run rendered through <paramref name="source"/> (proposal-glyph-runs): an OSC 66
    /// sizing, a FIGlet face, or both (sizing with the face as fallback). The run is a first-class
    /// participant in the paragraph flow — it wraps, trims, and aligns like plain text, at the
    /// source's per-cluster cell advances.
    /// </summary>
    public RichTextBuilder Run(string text, GlyphSource? source, in CellStyle style = default, object? tag = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return this;

        // The run's carrier is the resolved style restated whole (BrushedStyle.Restate — the background
        // sentinel decides stamp vs box downstream). An INLINE-scoped ScopedBrush tag translates into the
        // carrier here — the declaration site IS the run, so its scope is implied and the tag has nothing
        // left to say. Block/document scopes keep riding the tag: the carrier has no scope channel, and
        // widening it is a later phase's decision.
        var carrier = BrushedStyle.Restate(DefaultStyle(style));
        var effectiveTag = tag ?? CurrentTag;

        if (effectiveTag is ScopedBrush { Scope: DeclarationScope.Inline } inlineBrush)
        {
            carrier = carrier.WithForeground(inlineBrush.Foreground);
            effectiveTag = null;
        }

        AppendInline(new TextRun(text, carrier, CurrentMap, CurrentHyperlink, effectiveTag)
                     {
                         Source = source ?? _defaultGlyphSource
                     });
        return this;
    }

    /// <summary>Append a text run at the given OSC 66 <paramref name="sizing"/> — shorthand for a
    /// <see cref="GlyphSource"/> with no fallback face.</summary>
    public RichTextBuilder Run(string text, in TextSizing sizing, in CellStyle style = default)
        => Run(text, new GlyphSource(null, sizing), style);

    /// <summary>Append a hard line break inside the current paragraph.</summary>
    public RichTextBuilder LineBreak()
    {
        AppendInline(Text.LineBreak.Instance);
        return this;
    }

    /// <summary>
    /// Append a one-shot hyperlinked run. Convenience for the common case where the link
    /// target's scope matches a single run's text.
    /// </summary>
    public RichTextBuilder Hyperlink(string text, string uri, in CellStyle style = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(uri);
        if (text.Length == 0) return this;

        var effectiveStyle = DefaultStyle(style);
        var resolvedStyle = effectiveStyle == default ? CurrentStyle : CurrentStyle.Compose(in effectiveStyle);
        AppendInline(new TextRun(text, BrushedStyle.Restate(resolvedStyle), CurrentMap, uri));
        return this;
    }

    /// <summary>Embed a single-row <see cref="IContent"/> inside the current paragraph flow.</summary>
    public RichTextBuilder InlineContent(IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        AppendInline(new InlineContent(content));
        return this;
    }

    // ---- Block transitions ----

    /// <summary>
    /// Close the currently-open paragraph (if any) without starting a new one. Subsequent
    /// inline appends will implicitly open a fresh paragraph using the formatter defaults.
    /// </summary>
    public RichTextBuilder EndParagraph()
    {
        FlushOpenParagraph();
        return this;
    }

    /// <summary>
    /// Close the currently-open paragraph (if any) and start a fresh one with the supplied
    /// per-paragraph options. Subsequent inline appends accumulate into the new paragraph.
    /// </summary>
    public RichTextBuilder Paragraph(
        WrapMode? wrap = null,
        TextAlignment? alignment = null,
        TextTrimming? trim = null,
        int? maxLines = null,
        Margins? margin = null)
    {
        FlushOpenParagraph();
        _openInlines = ImmutableArray.CreateBuilder<Inline>();
        _openWrap = wrap ?? _defaultWrap;
        _openAlignment = alignment;
        _openTrim = trim ?? _defaultTrimming;
        
        _openMaxLines = maxLines;
        _openMargin = margin ?? TextParagraph.DefaultMargins;
        return this;
    }

    /// <summary>Append a <see cref="Text.HorizontalRule"/> using the supplied glyph + style.</summary>
    public RichTextBuilder HorizontalRule(
        // ReSharper disable once MethodOverloadWithOptionalParameter
        string glyph = "─",
        in CellStyle style = default,
        TextAlignment? alignment = null,
        Margins? margin = null)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        FlushOpenParagraph();

        _blocks.Add(new HorizontalRule(glyph, BrushedStyle.Restate(DefaultStyle(style)))
                    {
                        Alignment = alignment,
                        Margin = margin ?? Text.HorizontalRule.DefaultMargins
                    });

        return this;
    }

    /// <summary>Append a <see cref="Text.HorizontalRule.Light">light horizontal rule</see> using the
    /// supplied glyph + style.</summary>
    public RichTextBuilder HorizontalRule() => HorizontalRule(Text.HorizontalRule.Light);

    /// <summary>Append a <see cref="Text.HorizontalRule"/> using the supplied glyph + style.</summary>
    public RichTextBuilder HorizontalRule(HorizontalRule standardRule)
    {
        ArgumentNullException.ThrowIfNull(standardRule);
        FlushOpenParagraph();

        // A preset rule carries no style of its own; the builder's default steps in, restated the same
        // way a run's would be. A rule that already states something keeps it.
        if (standardRule.Style.IsIdentity && _defaultStyle != default)
            standardRule = standardRule with { Style = BrushedStyle.Restate(_defaultStyle) };

        _blocks.Add(standardRule);

        return this;
    }

    /// <summary>Append a <see cref="FigletBlock"/>.</summary>
    public RichTextBuilder Figlet(
        string text, IGlyphFont face,
        in CellStyle style = default,
        TextAlignment? alignment = null,
        Margins margin = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(face);
        FlushOpenParagraph();

        _blocks.Add(new FigletBlock(text, face, BrushedStyle.Restate(DefaultStyle(style))) { Alignment = alignment, Margin = margin });

        return this;
    }

    /// <summary>Append a <see cref="SizedTextBlock"/> using the supplied OSC 66 sizing parameters.</summary>
    public RichTextBuilder SizedText(
        string text, TextSizing sizing,
        in CellStyle style = default,
        IGlyphFont? fallback = null,
        TextAlignment? alignment = null,
        Margins margin = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        FlushOpenParagraph();

        _blocks.Add(new SizedTextBlock(text, sizing, BrushedStyle.Restate(DefaultStyle(style)))
                    {
                        Fallback = fallback,
                        Alignment = alignment,
                        Margin = margin
                    });

        return this;
    }

    /// <summary>Append a block-level <see cref="IContent"/> embedding.</summary>
    public RichTextBuilder Content(
        IContent content,
        TextAlignment? alignment = null,
        Margins margin = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        FlushOpenParagraph();
        _blocks.Add(new BlockContent(content) { Alignment = alignment, Margin = margin });
        return this;
    }

    /// <summary>Materialize the accumulated content into an immutable <see cref="RichText"/>.</summary>
    public RichText Build()
    {
        FlushOpenParagraph();
        return new RichText(_blocks.ToImmutable()) { DefaultStyle = BrushedStyle.Restate(_defaultStyle) };
    }

    // ---- Internals ----

    private CellStyle CurrentStyle => _styles.Count > 0 ? _styles.Peek() : _defaultStyle;
    private IGlyphMap? CurrentMap => _maps.Count > 0 ? _maps.Peek() : null;
    private string? CurrentHyperlink => _hyperlinks.Count > 0 ? _hyperlinks.Peek() : null;
    private object? CurrentTag => _tags.Count > 0 ? _tags.Peek() : null;

    private void AppendInline(Inline inline)
    {
        _openInlines ??= ImmutableArray.CreateBuilder<Inline>();
        _openInlines.Add(inline);
    }

    private void FlushOpenParagraph()
    {
        if (_openInlines is null) return;

        _blocks.Add(new TextParagraph(_openInlines.ToImmutable())
                    {
                        Wrap = _openWrap,
                        Alignment = _openAlignment,
                        Trim = _openTrim,
                        MaxLines = _openMaxLines,
                        Margin = _openMargin
                    });

        _openInlines = null;
        _openWrap = _defaultWrap;
        _openAlignment = TextAlignment.Left;
        _openTrim = _defaultTrimming;
        _openMaxLines = null;
        _openMargin = default;
    }

    private CellStyle DefaultStyle(CellStyle style) => style == default ? _defaultStyle : style;

    internal void PopLayer(StyleScope.Layer layer)
    {
        switch (layer)
        {
            case StyleScope.Layer.Style when _styles.Count > 0:        _styles.Pop(); break;
            case StyleScope.Layer.Map when _maps.Count > 0:            _maps.Pop(); break;
            case StyleScope.Layer.Hyperlink when _hyperlinks.Count > 0: _hyperlinks.Pop(); break;
            case StyleScope.Layer.Tag when _tags.Count > 0:            _tags.Pop(); break;
        }
    }
}

/// <summary>
/// Disposable scope returned by <see cref="RichTextBuilder.Push(in CellStyle)"/>,
/// <see cref="RichTextBuilder.PushMap"/>, and <see cref="RichTextBuilder.PushHyperlink"/>.
/// Disposing pops the corresponding layer off the builder's stack. Intended for
/// <c>using var _ = builder.Push(...);</c> lexical scoping.
/// </summary>
public readonly struct StyleScope : IDisposable
{
    internal enum Layer { Style, Map, Hyperlink, Tag }

    private readonly RichTextBuilder? _builder;
    private readonly Layer _layer;

    internal StyleScope(RichTextBuilder builder, Layer layer)
    {
        _builder = builder;
        _layer = layer;
    }

    /// <summary>Pops the pushed layer off the builder.</summary>
    public void Dispose() => _builder?.PopLayer(_layer);
}

/// <summary>Helpers used by <see cref="RichTextBuilder"/> for style inheritance.</summary>
internal static class StyleExtensions
{
    /// <summary>
    /// Compose two styles, child winning on every field that's non-default. Used to merge a
    /// pushed style with its parent on the builder's style stack.
    /// </summary>
    public static CellStyle Compose(this in CellStyle parent, in CellStyle child)
    {
        var result = parent;
        if (!child.Foreground.IsDefault) result = result.WithForeground(child.Foreground);
        if (!child.Background.IsDefault) result = result.WithBackground(child.Background);
        if (child.Attributes != default) result = result.WithAttributes(parent.Attributes | child.Attributes);
        if (child.UnderlineStyle != default) result = result.WithUnderlineStyle(child.UnderlineStyle);
        if (!child.UnderlineColor.IsDefault) result = result.WithUnderlineColor(child.UnderlineColor);
        return result;
    }
}
