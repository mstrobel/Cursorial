using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.UI.Controls;

/// <summary>
/// The one parse/format cache for the text presenters (UNIFIED-TEXT-SCOPING Scope A): owns the
/// cache slots, the freshness key, the capability subscriptions, and the <see cref="ResolveBounds"/>
/// arithmetic that <see cref="TextBlock"/>, <see cref="RichTextPresenter"/>, and
/// <see cref="FigletPresenter"/> previously each hand-rolled — with the FigletPresenter copy's
/// freshness key degraded (doc defect 3: no ResourceVersion/Variant terms, so a theme flip served
/// the pre-flip parse forever). Two tiers:
/// <list type="bullet">
/// <item><b>Document tier</b> — a cached parse (<see cref="RichText"/>), fresh only for the
/// <c>(ResourceVersion, ActualThemeVariant)</c> it was resolved against (design doc §11.6/CD16:
/// resolution is static-per-parse; sealed dictionaries never pulse, so freshness is PULL-based
/// through these key terms). Used by <see cref="RichTextPresenter"/>, whose parse is sticky.</item>
/// <item><b>Layout tier</b> — the formatted <see cref="FormattedText"/>, keyed on source
/// identity/equality (+ the markup-lane bit), columns, wrap/trim/alignment, ResourceVersion,
/// ActualThemeVariant, and output capabilities, with the row budget checked through
/// <see cref="HeightCompatible"/> (the grow-back rule: a layout the cap actually truncated is
/// valid ONLY at that exact budget — reusing it for a taller slot is how text used to stay
/// trimmed forever after the space it needed came back).</item>
/// </list>
/// The shared key computes the pull terms (version/variant/caps) ITSELF from the host — an adopter
/// cannot forget them, which is exactly how the FigletPresenter photocopy regressed. Push
/// invalidation (property-changed callbacks) stays with the presenters via <see cref="Invalidate"/>.
/// </summary>
internal sealed class FormattedTextCache
{
    private readonly UIElement _host;
    private readonly Action _onCapabilitiesInvalidated;

    private UIApplication? _subscribedApp;

    // Document (parse) tier.
    private RichText? _document;
    private ParseFreshness _parseFreshness;

    // Layout tier.
    private FormattedText? _layout;
    private object? _layoutSource;
    private LayoutKey _layoutKey;
    private int? _layoutMaxRows;

    /// <param name="host">The presenter whose text this cache serves (the resource/variant scope
    /// and, for <see cref="ResolveBounds"/>, the layout-state source).</param>
    /// <param name="onCapabilitiesInvalidated">Invoked AFTER a capability change cleared the cache —
    /// the host's re-layout hook (InvalidateMeasure/InvalidateVisual and, for the drawn presenters,
    /// the placeholder re-evaluation that defeats the measure-cache early-out — CD-P2K-1).</param>
    public FormattedTextCache(UIElement host, Action onCapabilitiesInvalidated)
    {
        _host = host;
        _onCapabilitiesInvalidated = onCapabilitiesInvalidated;
    }

    /// <summary>
    /// The freshness terms a cached PARSE rides — the same <c>(resource version, ActualThemeVariant)</c>
    /// cache-key contract the layout tier folds (design doc §11.6/CD16). Parsing resolves resource
    /// brushes through <see cref="ResourceBrushResolver"/> (and flattens the theme-reactive
    /// <c>Foreground</c> default) and BAKES the results, so without these terms a variant flip
    /// repaints the pre-flip ink forever. No dictionary subscription — sealed dictionaries never
    /// pulse (CD16).
    /// </summary>
    private readonly record struct ParseFreshness(int ResourceVersion, ThemeVariant? Variant);

    // The value terms of the layout key (source identity rides beside it — see SourceEquals).
    private readonly record struct LayoutKey(
        bool MarkupLane,
        int Columns,
        WrapMode Wrap,
        TextAlignment Alignment,
        TextTrimming Trim,
        BrushedStyle Carrier,
        TextIndicator? Indicator,
        bool FillBounds,
        int ResourceVersion,
        ThemeVariant? Variant,
        OutputCapabilities? Capabilities);

    /// <summary>
    /// An access-key-style indicator as PIPELINE data (maintainer ruling M2 addendum): the
    /// grapheme-cluster index that carries the indicator, plus the style delta it wears. On the
    /// rich path the cluster becomes its own run wearing <paramref name="Delta"/> as its carrier
    /// (<see cref="BuildIndicatorText"/>); the fast path composes the equivalent runs directly.
    /// The delta composes OVER the document-default carrier — the cue algebra's order — so a
    /// toggle (double-reverse-video) and an imposed weight behave exactly as the hand-rolled
    /// second draw did. Brushes stated by the delta sample the mnemonic run's own strip (the
    /// run-carrier scope rule); brushes the delta does not state fall through to the document
    /// rung, which samples the document's extent.
    /// </summary>
    internal readonly record struct TextIndicator(int Cluster, BrushedStyle Delta);

    /// <summary>The presenter-supplied half of the layout key; the shared pull terms
    /// (ResourceVersion/Variant/Capabilities) are appended by the cache itself.
    /// <paramref name="Carrier"/> is the document-default carrier the layout is built with —
    /// a KEY TERM for adopters whose carrier varies with element state (AccessTextPresenter's
    /// <c>BrushedStyle.FromElement</c>); adopters with a constant carrier may leave it default.
    /// <paramref name="Indicator"/> is the optional indicator declaration, likewise keyed.
    /// <paramref name="FillBounds"/> is the adopter's <see cref="FormattedText.FillEntireBounds"/>
    /// request (RichTextPresenter/FigletPresenter forward their property here) — a key term, and
    /// threaded to <see cref="TextFormatter.Format"/> so the produced layout carries the flag.</summary>
    internal readonly record struct LayoutRequest(
        object? Source,
        bool MarkupLane,
        int Columns,
        int? MaxRows,
        WrapMode Wrap,
        TextAlignment Alignment,
        TextTrimming Trim,
        BrushedStyle Carrier = default,
        TextIndicator? Indicator = null,
        bool FillBounds = false);

    /// <summary>The application whose capability events this cache is subscribed to (set while the
    /// host is attached to a tree).</summary>
    public UIApplication? SubscribedApp => _subscribedApp;

    /// <summary>The output capabilities formatting resolves against — the EFFECTIVE fold (FB-5
    /// overrides included), from the subscribed application or the ambient one.</summary>
    public OutputCapabilities? OutputCapabilities
        => (_subscribedApp ?? UIApplication.Current)?.EffectiveCapabilities.Output;

    // ─────────────────────────────── capability subscriptions ───────────────────────────────

    /// <summary>Subscribes to capability renegotiation/override events (call from
    /// <c>OnAttachedToTree</c>). A caps change invalidates both tiers and pulses the host callback,
    /// so the measure-cache early-out cannot serve a layout formatted under the old terminal.</summary>
    public void Attach()
    {
        if (UIApplication.Current is { } app)
        {
            app.EffectiveCapabilitiesChanged += OnEffectiveCapabilitiesChanged;
            app.CapabilityOverridesChanged += OnCapabilityOverridesChanged; // FB-5: forced-off caps take effect live
            _subscribedApp = app;
        }
    }

    /// <summary>Unsubscribes (call from <c>OnDetachedFromTree</c>).</summary>
    public void Detach()
    {
        if (_subscribedApp is { } app)
        {
            app.EffectiveCapabilitiesChanged -= OnEffectiveCapabilitiesChanged;
            app.CapabilityOverridesChanged -= OnCapabilityOverridesChanged;
            _subscribedApp = null;
        }
    }

    private void OnEffectiveCapabilitiesChanged(object? sender, CapabilitiesChangedEventArgs e)
        => InvalidateForCapabilities();

    private void OnCapabilityOverridesChanged(object? sender, EventArgs e)
        => InvalidateForCapabilities();

    private void InvalidateForCapabilities()
    {
        Invalidate();
        _onCapabilitiesInvalidated();
    }

    // ─────────────────────────────────── invalidation ───────────────────────────────────

    /// <summary>Drops both tiers — the push half of freshness (property-changed callbacks and the
    /// arrange-time row-budget reset call this; the pull terms handle everything resources do).</summary>
    public void Invalidate()
    {
        _document = null;
        _layout = null;
        _layoutSource = null;
    }

    // ─────────────────────────────────── document tier ───────────────────────────────────

    /// <summary>The cached parse, or <see langword="null"/> when absent or stale for the current
    /// <c>(ResourceVersion, Variant)</c> — staleness also drops the layout tier, which was built
    /// from the stale parse.</summary>
    public RichText? GetDocument()
    {
        var freshness = new ParseFreshness(ResourceServices.GetResourceVersion(_host),
                                           UIApplication.Current?.ActualThemeVariant);

        if (_parseFreshness != freshness)
        {
            _parseFreshness = freshness;
            Invalidate();
        }

        return _document;
    }

    /// <summary>Stores a parse under the freshness <see cref="GetDocument"/> last observed.</summary>
    public void StoreDocument(RichText document) => _document = document;

    // ──────────────────────────────────── layout tier ────────────────────────────────────

    /// <summary>Whether the cached layout is fresh for <paramref name="request"/> — source
    /// identity/equality, the value terms (columns/wrap/alignment/trim/lane), the pull terms
    /// (ResourceVersion/Variant/Capabilities), and the row-budget compatibility rule.</summary>
    public bool TryGetLayout(in LayoutRequest request, [NotNullWhen(true)] out FormattedText? layout)
    {
        layout = null;

        if (_layout is not { } cached)
            return false;

        if (!SourceEquals(_layoutSource, request.Source))
            return false;

        if (_layoutKey != KeyFor(in request))
            return false;

        if (!HeightCompatible(cached, _layoutMaxRows, request.MaxRows))
            return false;

        layout = cached;
        return true;
    }

    /// <summary>Formats <paramref name="document"/> under <paramref name="request"/> (the shared
    /// formatter construction the presenters previously duplicated) and stores it as the cached
    /// layout. The document tier is left alone — a width change re-formats a still-fresh parse.</summary>
    public FormattedText FormatAndStore(in LayoutRequest request, RichText document)
    {
        var layout = Format(document, request.Columns, request.MaxRows,
                            request.Alignment, request.Trim, request.Wrap,
                            fillEntireBounds: request.FillBounds);
        StoreLayout(in request, layout);
        return layout;
    }

    /// <summary>Stores an already-formatted layout under <paramref name="request"/> plus the pull
    /// terms observed NOW (single-threaded with the format that produced it).</summary>
    public void StoreLayout(in LayoutRequest request, FormattedText layout)
    {
        _layout = layout;
        _layoutSource = request.Source;
        _layoutKey = KeyFor(in request);
        _layoutMaxRows = request.MaxRows;
    }

    /// <summary>One formatting funnel (the D5 twins): presenter-supplied options, cache-supplied
    /// capabilities unless the caller states its own.</summary>
    public FormattedText Format(RichText document, int columns, int? maxRows,
                                TextAlignment alignment, TextTrimming trim, WrapMode wrap,
                                OutputCapabilities? capabilities = null,
                                bool fillEntireBounds = false)
    {
        if (document.IsEmpty)
            return FormattedText.Empty;

        FullFormatCount++;

        var formatter = new TextFormatter
                        {
                            Alignment = alignment,
                            Trim = trim,
                            Wrap = wrap
                        };

        return formatter.Format(document, columns, maxRows, capabilities ?? OutputCapabilities,
                                fillEntireBounds);
    }

    // ─────────────────────────────── the plain-text fast path (M2) ───────────────────────────────

    /// <summary>Layouts built by <see cref="TryFormatPlainTextFast"/>. The route taken is a
    /// behaviour-free choice (the fast path is byte-identical by construction, and
    /// <c>FormattedTextFastPathTests</c> proves the equality cell-for-cell), so these counters exist
    /// to make the ROUTING observable: an eligibility regression shows up as the wrong counter
    /// moving, not as a frame difference.</summary>
    internal int FastPathFormatCount { get; private set; }

    /// <summary>Non-empty layouts built by the full RichText pipeline (<see cref="Format"/>).</summary>
    internal int FullFormatCount { get; private set; }

    /// <summary>
    /// The plain-text fast path (maintainer ruling M2): a single line of markup-free text that fits
    /// the requested bounds needs no <see cref="RichText"/> model — the full pipeline's output for
    /// that subset is one paragraph holding one line of tokenizer-shaped runs, so the cache
    /// constructs that <see cref="FormattedText"/> directly and skips RichTextBuilder,
    /// tokenization, line packing, trimming, and alignment entirely. <b>Byte-identical by
    /// construction</b>: the produced runs replicate the tokenizer's exact output for the eligible
    /// subset — maximal non-space fragments split at ASCII spaces, each space its own run,
    /// per-run cumulative <see cref="FormattedTextRun.LogicalStart"/>, the identity glyph source,
    /// the identity carrier — so the paint walk runs over the same values either way (proven
    /// cell-for-cell by <c>FormattedTextFastPathTests</c>' twin-buffer matrix).
    /// </summary>
    /// <remarks>
    /// Eligibility is CONSERVATIVE — anything doubtful returns <see langword="null"/> and the
    /// caller takes the full pipeline unchanged:
    /// <list type="bullet">
    /// <item>the markup lane, a non-string source, or empty text;</item>
    /// <item>any control character (hard breaks and tabs included), DEL/C1, a soft hyphen, or any
    /// whitespace other than the ASCII space (the tokenizer's Unicode breaking-space set and NBSP
    /// stay on the full path);</item>
    /// <item>a LEADING space — the packer keeps it under <see cref="WrapMode.NoWrap"/> and drops it
    /// under every other mode, so it is the one wrap-mode-dependent shape in the subset;</item>
    /// <item>a host-level font or non-normal sizing (the adopters' plain lanes attach a
    /// <see cref="GlyphSource"/> for those — such runs measure through the face);</item>
    /// <item>text wider than the column budget — wrap and trim engage there.</item>
    /// </list>
    /// An indicator declaration (<see cref="LayoutRequest.Indicator"/>) IS part of the subset (M2:
    /// the fast path composes the access-key presenter's two draw operations): the indicator
    /// cluster is emitted as its own run wearing the delta, replicating the tokenizer's output for
    /// the pre / mnemonic / post three-source-run document <see cref="BuildIndicatorText"/> builds
    /// — per-region <see cref="FormattedTextRun.LogicalStart"/> restarting at each source-run
    /// boundary, the mnemonic piece at logical 0 of its own run. An out-of-range cluster index
    /// formats as plain text, exactly as the rich path does.
    /// <paramref name="documentDefault"/> is the adopter's document-default carrier — the exact
    /// value its full pipeline hands <see cref="RichTextBuilder"/> — so the fast layout carries the
    /// same document rung (<see cref="FormattedText.DefaultCarrier"/>) and resolved
    /// <see cref="FormattedText.DefaultCarrier"/>. Passing it as a parameter (rather than assuming a
    /// host type) keeps the cache usable by any class, inside or outside a presenter hierarchy
    /// (ruling M1 groundwork).
    /// </remarks>
    public FormattedText? TryFormatPlainTextFast(in LayoutRequest request, in BrushedStyle documentDefault)
    {
        if (request.MarkupLane || request.Source is not string text || text.Length == 0)
            return null;

        // A FillBounds request produces a layout carrying FormattedText.FillEntireBounds (the
        // surround fill + vertical re-centring at paint); the fast layout below never sets it, so
        // decline conservatively.
        if (request.FillBounds)
            return null;

        if (request.Columns <= 0 || request.MaxRows is < 1)
            return null;

        // A leading space is wrap-mode-dependent (kept under NoWrap, dropped otherwise) — decline.
        if (text[0] == ' ')
            return null;

        foreach (var c in text)
        {
            // Control characters (\r/\n/\t included), DEL, C1, soft hyphens, and any whitespace
            // other than the ASCII space take the full pipeline — conservatively, whether or not
            // the formatter would special-case them.
            if (c < ' ' || c is >= '\u007F' and <= '\u009F' || c == TextFormatter.SoftHyphen)
                return null;

            if (c != ' ' && char.IsWhiteSpace(c))
                return null;
        }

        // A host-level font or sizing attaches a GlyphSource in the adopters' plain lanes
        // (TextBlock.BuildPlainText): such runs measure and paint through the face. Declined
        // conservatively — even where an unsupported sizing would resolve back to the identity.
        if (TextElement.GetFont(_host) is {} font && font != MonospaceFont.Default)
            return null;

        if (!TextElement.GetSizing(_host).IsNormal)
            return null;

        // Split into the runs the tokenizer would emit: maximal non-space fragments, each ASCII
        // space its own run, LogicalStart = the cumulative cell offset within the source run.
        // Identity metrics (Advance == ClusterWidth, kerning-free) — the same measure the
        // tokenizer resolves for a source-less run. An indicator splits the text into THREE
        // source runs (pre / mnemonic / post), so the logical accounting restarts at each region
        // boundary — exactly what the tokenizer does for BuildIndicatorText's document.
        var metrics = GlyphSource.Default.Metrics;
        var runs = ImmutableArray.CreateBuilder<FormattedRun>();

        var width = 0;          // the line's total cell width
        var offset = 0;         // the next run's LogicalStart, within the CURRENT source region
        var fragmentStart = -1; // char index of the open fragment (-1 = none)
        var fragmentWidth = 0;

        var clusterIndex = 0;
        var indicatorCluster = request.Indicator?.Cluster ?? -1;

        var enumerator = text.GetGraphemeEnumerator();

        while (enumerator.MoveNext())
        {
            var grapheme = enumerator.Current;

            if (clusterIndex++ == indicatorCluster)
            {
                // The indicator cluster: its own source run wearing the delta. Close the PRE
                // region's open fragment; both the mnemonic run and the POST region restart
                // their logical accounting at 0 (per-source-run, as the tokenizer spells it).
                if (fragmentStart >= 0)
                {
                    runs.Add(new FormattedTextRun(text[fragmentStart..enumerator.ElementIndex],
                                                  BrushedStyle.Identity) { LogicalStart = offset });
                    fragmentStart = -1;
                    fragmentWidth = 0;
                }

                var indicatorWidth = metrics.ClusterWidth(grapheme);
                width += indicatorWidth;

                if (width > request.Columns)
                    return null; // does not fit — wrapping/trimming engage on the full path

                runs.Add(new FormattedTextRun(grapheme.ToString(), request.Indicator!.Value.Delta)
                         {
                             LogicalStart = 0,
                             Indicator = true // structural parity with BuildIndicatorText's run
                                              // (the fast path never trims, so it is inert here)
                         });
                offset = 0;
                continue;
            }

            if (grapheme.Length == 1 && grapheme[0] == ' ')
            {
                if (fragmentStart >= 0)
                {
                    runs.Add(new FormattedTextRun(text[fragmentStart..enumerator.ElementIndex],
                                                  BrushedStyle.Identity) { LogicalStart = offset });
                    offset += fragmentWidth;
                    fragmentStart = -1;
                    fragmentWidth = 0;
                }

                var spaceWidth = metrics.ClusterWidth(grapheme);
                width += spaceWidth;

                if (width > request.Columns)
                    return null; // does not fit — wrapping/trimming engage on the full path

                runs.Add(new FormattedTextRun(" ", BrushedStyle.Identity) { LogicalStart = offset });
                offset += spaceWidth;
                continue;
            }

            if (fragmentStart < 0)
                fragmentStart = enumerator.ElementIndex;

            var clusterWidth = metrics.ClusterWidth(grapheme);
            fragmentWidth += clusterWidth;
            width += clusterWidth;

            if (width > request.Columns)
                return null;
        }

        if (fragmentStart >= 0)
            runs.Add(new FormattedTextRun(text[fragmentStart..], BrushedStyle.Identity) { LogicalStart = offset });

        // The single fitting line, exactly as FormatParagraphCore leaves it: alignment never
        // rewrites a fitting single line (Justify collapses to Left on a paragraph's last line),
        // so the paragraph merely RECORDS the alignment for paint-time anchoring.
        var line = new FormattedLine(runs.ToImmutable(), width, Trimmed: false);

        var paragraph = new FormattedParagraph(ImmutableArray.Create(line), new Size(width, 1),
                                               request.Alignment, TrimmedLines: false)
                        {
                            VerticalAlignment = VerticalTextAlignment.Bottom
                        };

        FastPathFormatCount++;

        return new FormattedText(ImmutableArray.Create<FormattedBlock>(paragraph), new Size(width, 1),
                                 request.Columns)
               {
                   DefaultCarrier = documentDefault
               };
    }

    /// <summary>
    /// The trimmed-content tooltip payload. The wrap and trimming modes are STATED by the caller
    /// (maintainer ruling M4) instead of forked per copy: the defaults are the most common
    /// spelling — the CharacterEllipsis/CharacterWrap heritage of TextBlock and ContentPresenter's
    /// access-text payload — while RichTextPresenter and FigletPresenter pass their
    /// <see cref="TextTrimming.None"/> heritage explicitly. Under <see cref="WrapMode.CharacterWrap"/>
    /// the trim mode is expected to be unobservable (nothing overflows a character-wrapped line) —
    /// which is exactly why the difference is now an argument each site states, not a fork that can
    /// drift. Deliberately UNCAPPED rows: this payload's whole job is to reveal what the
    /// presenter's own bounds hid — capping it at those bounds would re-hide exactly the lines the
    /// user hovered to see. Display limits belong to the tooltip.
    /// </summary>
    public string? FormatUntrimmedPlainText(RichText document, int maxWidth,
                                            TextTrimming trim = TextTrimming.CharacterEllipsis,
                                            WrapMode wrap = WrapMode.CharacterWrap)
    {
        var formatter = new TextFormatter
                        {
                            Alignment = TextAlignment.Left,
                            Trim = trim,
                            Wrap = wrap
                        };

        return formatter.FormatPlainText(document, maxWidth, maxRows: null);
    }

    // ─────────────────────────────── the indicator document (M2) ───────────────────────────────

    /// <summary>
    /// The rich-path form of an indicator-bearing plain text (M2 addendum: the indicator is
    /// handled by the rich text path, not only the fast one): pre / mnemonic / post runs in one
    /// paragraph, the mnemonic cluster's run wearing <see cref="TextIndicator.Delta"/> as its
    /// carrier. Hard line breaks fold through <see cref="HardLineBreaks"/> in each region (the
    /// D12 contract); an absent or out-of-range cluster yields the plain document. Shared by
    /// AccessTextPresenter's full pipeline and the fast-path equivalence tests' full lane, so the
    /// two cannot drift.
    /// </summary>
    internal static RichText BuildIndicatorText(string text, in BrushedStyle documentDefault,
                                                TextIndicator? indicator,
                                                TextTrimming trim, WrapMode wrap)
    {
        var builder = new RichTextBuilder(documentDefault, defaultTrimming: trim, defaultWrap: wrap);

        if (indicator is { } declared && TryGetClusterRange(text, declared.Cluster, out var start, out var length))
        {
            // IndicatorRun, not Run: the delta marks exactly the mnemonic's cluster, so a trim
            // ellipsis cut in immediately after it stays PLAIN (ruling 2026-08-11 #3) instead of
            // joining the cue run's style.
            var delta = declared.Delta;
            AppendPlainSegments(builder, text, 0, start);
            builder.IndicatorRun(text.Substring(start, length), in delta);
            AppendPlainSegments(builder, text, start + length, text.Length - (start + length));
        }
        else
        {
            AppendPlainSegments(builder, text, 0, text.Length);
        }

        return builder.Build();
    }

    // Appends a region of plain text as Run/LineBreak sequences (the TextBlock.BuildPlainText
    // fold, region-scoped). Breaks are emitted only BETWEEN a region's own segments — the
    // region↔mnemonic junctions never carry one (the mnemonic cluster is never a break), so a
    // region ending in "\n" emits its trailing break via its own empty final segment, and a
    // region that merely continues the line emits none.
    private static void AppendPlainSegments(RichTextBuilder builder, string text, int start, int length)
    {
        if (length <= 0)
            return;

        var region = text.Substring(start, length);
        var first = true;

        foreach (var range in HardLineBreaks.EnumerateLines(region))
        {
            if (!first)
                builder.LineBreak();

            first = false;

            if (range.End.Value > range.Start.Value)
                builder.Run(region[range]);
        }
    }

    /// <summary>The UTF-16 range of the <paramref name="cluster"/>-th grapheme cluster of
    /// <paramref name="text"/>, or <see langword="false"/> when the index is out of range.</summary>
    private static bool TryGetClusterRange(string text, int cluster, out int start, out int length)
    {
        start = 0;
        length = 0;

        if (cluster < 0)
            return false;

        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var index = 0;

        while (enumerator.MoveNext())
        {
            if (index++ == cluster)
            {
                start = enumerator.ElementIndex;
                length = ((string) enumerator.Current).Length;
                return true;
            }
        }

        return false;
    }

    private LayoutKey KeyFor(in LayoutRequest request)
        => new(request.MarkupLane, request.Columns, request.Wrap, request.Alignment, request.Trim,
               request.Carrier, request.Indicator, request.FillBounds,
               ResourceServices.GetResourceVersion(_host),
               UIApplication.Current?.ActualThemeVariant,
               OutputCapabilities);

    // Source identity: strings compare by VALUE (TextBlock's Text/Markup, FigletPresenter's Text),
    // everything else by REFERENCE (RichTextPresenter's parsed/assigned RichText — parses are
    // sticky; equality-by-content would deep-compare on every measure for no freshness gain).
    private static bool SourceEquals(object? cached, object? current)
        => cached is string s ? current is string c && s == c : ReferenceEquals(cached, current);

    /// <summary>
    /// Whether a cached layout built under <paramref name="cachedMaxRows"/> is the SAME layout a
    /// fresh format at <paramref name="maxRows"/> would produce. Equal budgets trivially agree; a
    /// layout whose row cap never bit (or that was built unbounded) is valid for any budget it
    /// fits in. A layout the cap DID truncate is only valid for that exact budget — reusing it
    /// for a taller one is how text used to stay trimmed forever after the space it needed came
    /// back.
    /// </summary>
    private static bool HeightCompatible(FormattedText cached, int? cachedMaxRows, int? maxRows)
    {
        if (cachedMaxRows == maxRows)
            return true;

        return !CapBit(cached, cachedMaxRows) && (maxRows is null || cached.Size.Rows <= maxRows);
    }

    /// <summary>
    /// Whether the finite row budget the layout was formatted under actually changed it. NOT
    /// <c>rows >= cap</c> (the hand-rolled presenters' spelling): a vertically-ATOMIC block — a
    /// figlet line taller than the whole budget — is DROPPED by the cap, leaving FEWER rows than
    /// the cap, and that spelling then reported the capped layout as reusable forever (the
    /// figlet-in-a-two-row-slot shape). <see cref="FormattedText.HasTrimmedLines"/> under a
    /// finite budget is conservative the safe way round: a purely width-trimmed layout also
    /// reports it and merely reformats (to an identical layout) when the row budget changes.
    /// </summary>
    private static bool CapBit(FormattedText cached, int? cachedMaxRows)
        => cachedMaxRows is not null && cached.HasTrimmedLines;

    // ──────────────────────────────── layout-state arithmetic ────────────────────────────────

    /// <summary>
    /// The columns/rows the layout may use (the RTP/FigletPresenter D4 twins, now one copy): an
    /// explicit <paramref name="availableColumns"/> wins; a <see langword="null"/> one (a render or
    /// tooltip pass) is reconstructed from the cached layout's columns (only while its source still
    /// matches <paramref name="currentSource"/>), narrowed by the last measure constraint and the
    /// last arrange rect. Rows come from the host bounds, falling back to the arrange rect.
    /// </summary>
    public Rect ResolveBounds(int? availableColumns, object? currentSource)
    {
        Rect? arrangeRect = _host.HasArrangeRect ? _host.LastArrangeRect : null;

        if (availableColumns is null)
        {
            Size? desiredSize = _host.HasMeasureConstraint ? _host.LastMeasureConstraint : null;

            if (_layout is not null && SourceEquals(_layoutSource, currentSource))
                availableColumns = _layoutKey.Columns;

            if (desiredSize is { Columns: var desiredColumns })
                availableColumns = availableColumns is { } c ? Math.Min(c, desiredColumns) : desiredColumns;

            if (arrangeRect is { Columns: var arrangeColumns })
                availableColumns = availableColumns is { } c ? Math.Min(c, arrangeColumns) : arrangeColumns;
        }

        var bounds = _host.Bounds;
        var rows = bounds.Rows is 0 && _host.HasArrangeRect ? _host.LastArrangeRect.Rows : bounds.Rows;

        return bounds with
               {
                   Columns = Math.Min(availableColumns ?? bounds.Columns, LayoutMath.MaxExtent),
                   Rows = rows
               };
    }

    /// <summary>
    /// The arrange-time half of the row-budget rule (the D6 twins): reformat when the slot shrank
    /// below the layout — or GREW past a row cap that actually truncated it (see
    /// <see cref="CapBit"/> for why "truncated" is not <c>rows >= cap</c>).
    /// </summary>
    public bool NeedsRowBudgetReformat(int finalRows)
        => _layout is { Size.Rows: var rows } cached &&
           (rows > finalRows ||
            (_layoutMaxRows is { } cap && CapBit(cached, cap) && finalRows > cap));

    // ─────────────────────────────── trimmed-state advertisement ───────────────────────────────

    /// <summary>
    /// The measure-time <c>IsTrimmed</c> advertisement (the D9 twins): stamp
    /// <see cref="TextElement.IsTrimmedPropertyKey"/> when the layout trimmed, clear it when this
    /// advertisement was the only contributor, and return the measured size.
    /// </summary>
    public static Size MeasureAndAdvertiseTrimmed(UIElement host, FormattedText? layout)
    {
        var wasMarkedTrimmed = host.GetValueSource(TextElement.IsTrimmedProperty) is
                               {
                                   Kind: ValueSourceKind.Default,
                                   IsCurrentValue: true
                               };

        if (layout is not null)
        {
            if (layout.HasTrimmedLines)
                host.SetCurrentValue(TextElement.IsTrimmedPropertyKey, true);
            else if (wasMarkedTrimmed)
                host.ClearValue(TextElement.IsTrimmedPropertyKey);

            return layout.Size;
        }

        if (wasMarkedTrimmed)
            host.ClearValue(TextElement.IsTrimmedPropertyKey);

        return Size.Empty;
    }
}

/// <summary>
/// A text element that advertises trimming through <see cref="TextElement.IsTrimmedProperty"/> and
/// can produce the untrimmed payload for the trimmed-content tooltip — the surface
/// <see cref="ContentPresenter"/> previously reached through a hard-coded four-type switch
/// (UNIFIED-TEXT-SCOPING D8/D9's closed set, now open).
/// </summary>
internal interface ITrimmedTextSource
{
    /// <summary>The full (untrimmed) text, formatted to <paramref name="maxWidth"/> columns with
    /// UNCAPPED rows, or <see langword="null"/> when there is nothing to reveal. Each implementation
    /// keeps its own trim spelling (M4 is Mike-gated).</summary>
    string? GetUntrimmedText(int maxWidth);
}

/// <summary>
/// The one hard-line-break splitter (UNIFIED-TEXT-SCOPING D12): <c>\r\n</c> | <c>\n</c> | <c>\r</c>
/// each end a segment, with <c>\r\n</c> folded into ONE break — the P2.6 text-tier contract the
/// matrix pins (C162). Empty segments are yielded (a genuine blank line is content); consumers
/// decide what an empty segment renders as. Previously <see cref="TextBlock"/> hand-rolled this
/// fold while <see cref="FigletPresenter"/> split on the raw char pair — yielding a phantom empty
/// figlet block per CRLF.
/// </summary>
internal static class HardLineBreaks
{
    public static LineEnumerator EnumerateLines(string text) => new(text);

    internal struct LineEnumerator(string text)
    {
        private int _position = 0; // start of the next segment; -1 = exhausted

        public Range Current { get; private set; }

        public readonly LineEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_position < 0)
                return false;

            var start = _position;
            var i = start;

            while (i < text.Length && text[i] is not ('\r' or '\n'))
                i++;

            Current = start..i;

            if (i >= text.Length)
                _position = -1; // the final segment (possibly empty — "a\n" ends with one)
            else
                _position = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n'
                                ? i + 2  // \r\n is ONE break
                                : i + 1;

            return true;
        }
    }
}
