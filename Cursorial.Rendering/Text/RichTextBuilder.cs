using System.Collections.Immutable;

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
/// <b>Style scoping.</b> <see cref="Push(in BrushedStyle)"/> pushes a style DELTA onto an
/// inheritance stack; each child layer composes over its parent (child wins per channel,
/// <see cref="BrushedStyle.Then"/>), and channels no layer states fall through to the document
/// default at paint. <see cref="PushMap"/> and
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
    private readonly BrushedStyle _defaultStyle;
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
    private BrushedStyle _openStyle;

    // Style / map / hyperlink stacks. The TOP of each stack is the active value applied to
    // appended runs that don't supply their own. The style stack holds DELTAS, pre-composed on
    // push — the document default is NOT its floor; it rides Build()'s carrier and folds under
    // every run at paint.
    private readonly Stack<BrushedStyle> _styles = new();
    private readonly Stack<IGlyphMap?> _maps = new();
    private readonly Stack<string?> _hyperlinks = new();

    public RichTextBuilder(CellStyle defaultStyle = default,
                           TextTrimming? defaultTrimming = null,
                           WrapMode? defaultWrap = null,
                           GlyphSource? defaultGlyphSource = null)
        : this(BrushedStyle.FromStated(defaultStyle), defaultTrimming, defaultWrap, defaultGlyphSource)
    {
    }

    /// <summary>
    /// The carrier form of the document default, for a caller that already holds one — or one that
    /// wants to declare a brushed channel (a gradient document foreground) no <see cref="CellStyle"/>
    /// can spell. The markup parser hands <see cref="TextMarkupOptions.DefaultStyle"/> through here
    /// instead of flattening it.
    /// </summary>
    public RichTextBuilder(in BrushedStyle defaultStyle,
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
    /// Push <paramref name="style"/>'s STATED channels onto the style stack — sugar for
    /// <see cref="Push(in BrushedStyle)"/> through <see cref="BrushedStyle.FromStated"/>, so an
    /// all-default style pushes the identity.
    /// </summary>
    public StyleScope Push(in CellStyle style) => Push(BrushedStyle.FromStated(style));

    /// <summary>
    /// Push a style delta onto the style stack. The pushed layer composes over the previous top
    /// (child wins per channel, <see cref="BrushedStyle.Then"/>); subsequent <see cref="Run(string)"/>
    /// calls carry the composed delta, and channels no layer states fall through to the document
    /// default at paint. Dispose the returned <see cref="StyleScope"/> to pop.
    /// </summary>
    public StyleScope Push(in BrushedStyle style)
    {
        var current = _styles.Count > 0 ? _styles.Peek() : BrushedStyle.Identity;
        _styles.Push(current.Then(in style));
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

    // ---- Inline appends ----

    /// <summary>Append a text run inheriting the current style / map / hyperlink stacks.</summary>
    public RichTextBuilder Run(string text)
    {
        return Run(text, source: null, CurrentStyle);
    }

    /// <summary>
    /// Append a text run with an explicit style, ignoring the style stack.
    /// The style's STATED channels are the run's own declarations
    /// (<see cref="BrushedStyle.FromStated"/>); channels it leaves at their defaults fall through to
    /// the document default at paint.
    /// </summary>
    public RichTextBuilder Run(string text, in CellStyle style)
    {
        return Run(text, source: null, BrushedStyle.FromStated(style));
    }

    /// <summary>
    /// Append a text run carrying <paramref name="style"/> as given — the brushed form of
    /// <see cref="Run(string, in CellStyle)"/>, for a run that declares a foreground brush (or any
    /// other brushed channel) without markup. Ignores the style stack.
    /// </summary>
    public RichTextBuilder Run(string text, in BrushedStyle style)
    {
        return Run(text, source: null, style);
    }

    /// <summary>
    /// Append a text run rendered through <paramref name="source"/> (proposal-glyph-runs): an OSC 66
    /// sizing, a FIGlet face, or both (sizing with the face as fallback). The run is a first-class
    /// participant in the paragraph flow — it wraps, trims, and aligns like plain text, at the
    /// source's per-cluster cell advances.
    /// </summary>
    public RichTextBuilder Run(string text, GlyphSource? source, in CellStyle style = default)
    {
        return Run(text, source, BrushedStyle.FromStated(style));
    }

    /// <summary>
    /// The brushed form of <see cref="Run(string, GlyphSource?, in CellStyle)"/> — a sourced run that
    /// declares a foreground brush (or any other brushed channel) directly on its own carrier.
    /// </summary>
    public RichTextBuilder Run(string text, GlyphSource? source, in BrushedStyle style)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return this;

        // The run's carrier states exactly what the caller declared (BrushedStyle.FromStated at the
        // flat public boundary); undeclared channels stay absent and fall through to the enclosing
        // rungs at paint, and the background's PRESENCE decides stamp vs box downstream. Scope is
        // inferred from the declaration site: a brush stated here samples the run's own strip.
        AppendInline(new TextRun(text, style, CurrentMap, CurrentHyperlink)
                     {
                         Source = source ?? _defaultGlyphSource
                     });
        return this;
    }

    /// <summary>
    /// Append an INDICATOR run — an access-key mnemonic (or similar cue) wearing
    /// <paramref name="style"/> as its carrier over exactly its own clusters. It formats and
    /// paints like <see cref="Run(string, in BrushedStyle)"/>, but a trim indicator the
    /// formatter synthesizes immediately after it does not inherit its style (maintainer ruling
    /// 2026-08-11 #3): the ellipsis joins the nearest preceding non-indicator run instead,
    /// falling back to the document default.
    /// </summary>
    public RichTextBuilder IndicatorRun(string text, in BrushedStyle style)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return this;

        AppendInline(new TextRun(text, style, CurrentMap, CurrentHyperlink)
                     {
                         Source = _defaultGlyphSource,
                         Indicator = true
                     });
        return this;
    }

    /// <summary>Append a text run at the given OSC 66 <paramref name="sizing"/> — shorthand for a
    /// <see cref="GlyphSource"/> with no fallback face.</summary>
    public RichTextBuilder Run(string text, in TextSizing sizing, in CellStyle style = default)
        => Run(text, new GlyphSource(null, sizing), style);

    /// <summary>The brushed form of <see cref="Run(string, in TextSizing, in CellStyle)"/>.</summary>
    public RichTextBuilder Run(string text, in TextSizing sizing, in BrushedStyle style)
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

        // The explicit style's stated channels compose over the stack's delta; an all-default style is
        // the identity, so the stack's composition carries alone and undeclared channels fall through
        // to the document default at paint.
        AppendInline(new TextRun(text, CurrentStyle.Then(BrushedStyle.FromStated(style)), CurrentMap, uri));
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
    /// <paramref name="style"/> is the paragraph's own declaration set (<see cref="TextParagraph.Style"/>)
    /// — the ladder's block rung; a foreground brush declared here samples the block's 2-D rect.
    /// </summary>
    public RichTextBuilder Paragraph(
        WrapMode? wrap = null,
        TextAlignment? alignment = null,
        TextTrimming? trim = null,
        int? maxLines = null,
        Margins? margin = null,
        in BrushedStyle style = default)
    {
        FlushOpenParagraph();
        _openInlines = ImmutableArray.CreateBuilder<Inline>();
        _openWrap = wrap ?? _defaultWrap;
        _openAlignment = alignment;
        _openTrim = trim ?? _defaultTrimming;

        _openMaxLines = maxLines;
        _openMargin = margin ?? TextParagraph.DefaultMargins;
        _openStyle = style;
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

        _blocks.Add(new HorizontalRule(glyph, BrushedStyle.FromStated(style))
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

        // A preset rule states no style of its own; its identity carrier falls through to the document
        // default at paint — the same answer the builder used to restate onto it here. A rule that
        // already states something keeps it.
        _blocks.Add(standardRule);

        return this;
    }

    /// <summary>Append a <see cref="FigletBlock"/>.</summary>
    public RichTextBuilder Figlet(
        string text, IGlyphFont face,
        in CellStyle style = default,
        TextAlignment? alignment = null,
        Margins margin = default)
        => Figlet(text, face, BrushedStyle.FromStated(style), alignment, margin);

    /// <summary>
    /// Append a <see cref="FigletBlock"/> whose carrier is stated directly — the brushed sibling of
    /// the <see cref="CellStyle"/> form, mirroring the brushed <c>Run</c> overloads: a gradient (or
    /// any non-uniform brush) rides the block to the FIGlet paint arm's per-cell sampling instead of
    /// being flattened at the boundary. The style is required — passing
    /// <see cref="BrushedStyle.Identity"/> spells "no declaration" explicitly.
    /// </summary>
    public RichTextBuilder Figlet(
        string text, IGlyphFont face,
        in BrushedStyle style,
        TextAlignment? alignment = null,
        Margins margin = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(face);
        FlushOpenParagraph();

        _blocks.Add(new FigletBlock(text, face, style) { Alignment = alignment, Margin = margin });

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

        _blocks.Add(new SizedTextBlock(text, sizing, BrushedStyle.FromStated(style))
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
        // The ctor default rides out as the document rung's CARRIER — the painter folds it under every
        // run and rule, so nothing here restates it onto the content.
        return new RichText(_blocks.ToImmutable()) { DefaultStyle = _defaultStyle };
    }

    // ---- Internals ----

    private BrushedStyle CurrentStyle => _styles.Count > 0 ? _styles.Peek() : BrushedStyle.Identity;
    private IGlyphMap? CurrentMap => _maps.Count > 0 ? _maps.Peek() : null;
    private string? CurrentHyperlink => _hyperlinks.Count > 0 ? _hyperlinks.Peek() : null;

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
                        Margin = _openMargin,
                        Style = _openStyle
                    });

        _openInlines = null;
        _openWrap = _defaultWrap;
        _openAlignment = TextAlignment.Left;
        _openTrim = _defaultTrimming;
        _openMaxLines = null;
        _openMargin = default;
        _openStyle = default;
    }

    internal void PopLayer(StyleScope.Layer layer)
    {
        switch (layer)
        {
            case StyleScope.Layer.Style when _styles.Count > 0:        _styles.Pop(); break;
            case StyleScope.Layer.Map when _maps.Count > 0:            _maps.Pop(); break;
            case StyleScope.Layer.Hyperlink when _hyperlinks.Count > 0: _hyperlinks.Pop(); break;
        }
    }
}

/// <summary>
/// Disposable scope returned by <see cref="RichTextBuilder.Push(in BrushedStyle)"/>,
/// <see cref="RichTextBuilder.PushMap"/>, and <see cref="RichTextBuilder.PushHyperlink"/>.
/// Disposing pops the corresponding layer off the builder's stack. Intended for
/// <c>using var _ = builder.Push(...);</c> lexical scoping.
/// </summary>
public readonly struct StyleScope : IDisposable
{
    internal enum Layer { Style, Map, Hyperlink }

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
