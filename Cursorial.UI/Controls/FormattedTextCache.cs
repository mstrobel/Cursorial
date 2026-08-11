using System.Diagnostics.CodeAnalysis;

using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
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
        int ResourceVersion,
        ThemeVariant? Variant,
        OutputCapabilities? Capabilities);

    /// <summary>The presenter-supplied half of the layout key; the shared pull terms
    /// (ResourceVersion/Variant/Capabilities) are appended by the cache itself.</summary>
    internal readonly record struct LayoutRequest(
        object? Source,
        bool MarkupLane,
        int Columns,
        int? MaxRows,
        WrapMode Wrap,
        TextAlignment Alignment,
        TextTrimming Trim);

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
                            request.Alignment, request.Trim, request.Wrap);
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
                                OutputCapabilities? capabilities = null)
    {
        if (document.IsEmpty)
            return FormattedText.Empty;

        var formatter = new TextFormatter
                        {
                            Alignment = alignment,
                            Trim = trim,
                            Wrap = wrap
                        };

        return formatter.Format(document, columns, maxRows, capabilities ?? OutputCapabilities);
    }

    /// <summary>
    /// The trimmed-content tooltip payload for the presenters whose untrimmed spelling is
    /// <c>Trim=None</c> (RichTextPresenter/FigletPresenter — TextBlock keeps its own
    /// CharacterEllipsis spelling; unifying THAT choice is Mike-gated M4). Deliberately UNCAPPED
    /// rows: this payload's whole job is to reveal what the presenter's own bounds hid — capping it
    /// at those bounds would re-hide exactly the lines the user hovered to see. Display limits
    /// belong to the tooltip.
    /// </summary>
    public string? FormatUntrimmedPlainText(RichText document, int maxWidth)
    {
        var formatter = new TextFormatter
                        {
                            Alignment = TextAlignment.Left,
                            Trim = TextTrimming.None,
                            Wrap = WrapMode.CharacterWrap
                        };

        return formatter.FormatPlainText(document, maxWidth, maxRows: null);
    }

    private LayoutKey KeyFor(in LayoutRequest request)
        => new(request.MarkupLane, request.Columns, request.Wrap, request.Alignment, request.Trim,
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
    /// <see cref="TextBlock.IsTrimmedPropertyKey"/> when the layout trimmed, clear it when this
    /// advertisement was the only contributor, and return the measured size.
    /// </summary>
    public static Size MeasureAndAdvertiseTrimmed(UIElement host, FormattedText? layout)
    {
        var wasMarkedTrimmed = host.GetValueSource(TextBlock.IsTrimmedProperty) is
                               {
                                   Kind: ValueSourceKind.Default,
                                   IsCurrentValue: true
                               };

        if (layout is not null)
        {
            if (layout.HasTrimmedLines)
                host.SetCurrentValue(TextBlock.IsTrimmedPropertyKey, true);
            else if (wasMarkedTrimmed)
                host.ClearValue(TextBlock.IsTrimmedPropertyKey);

            return layout.Size;
        }

        if (wasMarkedTrimmed)
            host.ClearValue(TextBlock.IsTrimmedPropertyKey);

        return Size.Empty;
    }
}

/// <summary>
/// A text element that advertises trimming through <see cref="TextBlock.IsTrimmedProperty"/> and
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
