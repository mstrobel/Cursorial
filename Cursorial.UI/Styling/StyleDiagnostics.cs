using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI.Data;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// One armed rule on an element, as reported by <see cref="StyleDiagnostics.MatchedRules"/>
/// (design doc §3.9): the flattened canonical selector text (<c>(explicit)</c> for selector-less
/// explicit styles), the channel layer, the packed sort key, and whether the rule is currently
/// active (armed-but-inactive rules are listed — activation is a flip, not a re-match).
/// </summary>
/// <param name="SelectorText">The flattened canonical selector, or <c>(explicit)</c>.</param>
/// <param name="Layer">The channel layer the arming scope assigned.</param>
/// <param name="Key">The full packed <see cref="StyleSortKey"/>.</param>
/// <param name="IsActive">Whether the rule's frame currently contributes values.</param>
public readonly record struct MatchedRuleInfo(string SelectorText, StyleLayer Layer, StyleSortKey Key, bool IsActive);


public readonly record struct StyleExplanation(
    string TargetDescription,
    ValueSourceKind Kind,
    BindingPriority Priority,
    BindingPriority BasePriority,
    bool IsAnimated,
    ImmutableArray<StyleFrameExplanation> Frames)
{
    /// <summary>Whether any expression is tracked for the pair.</summary>
    public bool HasFrames => !Frames.IsDefaultOrEmpty;
}

public readonly record struct StyleFrameExplanation(StyleLayer Layer,
                                                    string SelectorDescription,
                                                    StyleSortKey SortKey,
                                                    bool IsActive,
                                                    bool HasValue,
                                                    object? LastProducedValue,
                                                    string Status);

/// <summary>
/// The styling engine's diagnostics surface (design doc §3.9): <see cref="Explain"/> renders every
/// contributing setter with its full sort-key derivation in one line per contributor (the §3.9
/// acceptance format, pinned bit-for-bit by style matrix SD13); <see cref="MatchedRules"/> dumps
/// the armed frames + activation state.
/// </summary>
public static class StyleDiagnostics
{
    /// <summary>
    /// The one-line-per-contributor derivation of a property's value (SD13), strongest first,
    /// newline-joined. Style lines render
    /// <c>&lt;Property&gt; = &lt;value&gt; &lt;- &lt;Layer&gt;(&lt;n&gt;) "&lt;selector&gt;"
    /// names=&lt;n&gt; classLike=&lt;n&gt; types=&lt;n&gt; depth=&lt;n&gt; order=&lt;n&gt;
    /// key=0x&lt;16-hex&gt; -- winning|shadowed</c> with the entry value's invariant-culture
    /// <c>ToString</c> (<c>(unset)</c> for valueless A8 entries), the flattened canonical selector
    /// (<c>(explicit)</c> for selector-less explicit styles), and the full upper-case packed key.
    /// When a non-Style lane holds the effective value the first line is
    /// <c>&lt;Property&gt; = &lt;value&gt; &lt;- &lt;Lane&gt;</c>
    /// (<c>LocalValue</c>/<c>Animation</c>/<c>Inherited</c>/<c>Default</c>) and every style line is
    /// <c>shadowed</c>. Only <b>active</b> contributors render — armed-inactive rules appear in
    /// <see cref="MatchedRules"/>, not here. Cold path.
    /// </summary>
    public static string Explain(UIElement element, UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);

        var builder = new StringBuilder();
        var source = element.GetValueSource(property);
        var styleWins = source.Priority == BindingPriority.Style;

        if (!styleWins)
        {
            // The stronger-lane line: the effective value did not come from the Style slot.
            builder.Append(property.Name).Append(" = ")
                   .Append(FormatValue(property.GetValueUntyped(element)))
                   .Append(" <- ").Append(source.Priority);
        }

        var contributors = new List<(ValueFrame Frame, IValueEntry Entry)>();
        element.DebugValueStore?.AppendActiveStyleContributors(property, contributors);

        var winnerPending = styleWins;
        foreach (var (frame, entry) in contributors)
        {
            if (builder.Length > 0)
                builder.Append('\n');

            // The winning contributor is the strongest active VALUE-BEARING entry — exactly the
            // store's arbitration (valueless A8 entries are promoted past and render shadowed).
            var winning = winnerPending && entry.HasValue;
            if (winning)
                winnerPending = false;

            AppendStyleLine(builder, property, frame, entry, winning);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The one-line-per-contributor derivation of a property's value (SD13), strongest first,
    /// newline-joined. Style lines render
    /// <c>&lt;Property&gt; = &lt;value&gt; &lt;- &lt;Layer&gt;(&lt;n&gt;) "&lt;selector&gt;"
    /// names=&lt;n&gt; classLike=&lt;n&gt; types=&lt;n&gt; depth=&lt;n&gt; order=&lt;n&gt;
    /// key=0x&lt;16-hex&gt; -- winning|shadowed</c> with the entry value's invariant-culture
    /// <c>ToString</c> (<c>(unset)</c> for valueless A8 entries), the flattened canonical selector
    /// (<c>(explicit)</c> for selector-less explicit styles), and the full upper-case packed key.
    /// When a non-Style lane holds the effective value the first line is
    /// <c>&lt;Property&gt; = &lt;value&gt; &lt;- &lt;Lane&gt;</c>
    /// (<c>LocalValue</c>/<c>Animation</c>/<c>Inherited</c>/<c>Default</c>) and every style line is
    /// <c>shadowed</c>. Only <b>active</b> contributors render — armed-inactive rules appear in
    /// <see cref="MatchedRules"/>, not here. Cold path.
    /// </summary>
    public static StyleExplanation ExplainDetails(UIElement element, UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);

        var targetDescription = DescribeTarget(element, property);
        var source = element.GetValueSource(property);
        var styleWins = source.Priority == BindingPriority.Style;
        
        object? value = null;

        if (!styleWins)
        {
            // The stronger-lane line: the effective value did not come from the Style slot.
            value = property.GetValueUntyped(element);
        }

        var contributors = new List<(ValueFrame Frame, IValueEntry Entry)>();

        element.DebugValueStore?.AppendActiveStyleContributors(property, contributors);

        var frameExplanations = new StyleFrameExplanation[contributors.Count];
        var winnerPending = styleWins;

        for (var i = 0; i < contributors.Count; i++)
        {
            var (frame, entry) = contributors[i];

            // The winning contributor is the strongest active VALUE-BEARING entry — exactly the
            // store's arbitration (valueless A8 entries are promoted past and render shadowed).
            var winning = winnerPending && entry.HasValue;

            if (winning)
                winnerPending = false;
            
            var frameExplanation = ExplainFrame(property, frame, entry, winning);

            if (winning && value is null)
                value = property.GetEntryValueBoxed(entry);

            frameExplanations[i] = frameExplanation;
        }

        return new StyleExplanation(targetDescription,
                                    source.Kind,
                                    source.Priority,
                                    source.BasePriority,
                                    source.IsAnimated,
                                    Frames: [..frameExplanations]);
    }

    private static void AppendStyleLine(
        StringBuilder builder, UIProperty property, ValueFrame frame, IValueEntry entry, bool winning)
    {
        var key = frame.SortKey;
        var selector = frame is StyleRuleFrame styleFrame
            ? styleFrame.Rule.SelectorText.Length == 0 ? "(explicit)" : styleFrame.Rule.SelectorText
            : "(frame)"; // a non-engine ValueFrame in the Style slot (conformance kit / future producers)
        var layer = frame is StyleRuleFrame ruleFrame ? ruleFrame.Layer : key.Layer;

        builder.Append(property.Name).Append(" = ")
               .Append(entry.HasValue ? FormatValue(property.GetEntryValueBoxed(entry)) : "(unset)")
               .Append(" <- ").Append(layer).Append('(').Append((int)layer).Append(") \"")
               .Append(selector).Append("\" names=").Append(key.Names)
               .Append(" classLike=").Append(key.ClassLike)
               .Append(" types=").Append(key.Types)
               .Append(" depth=").Append(key.ScopeDepth)
               .Append(" order=").Append(key.Order)
               .Append(" key=0x").Append(key.Packed.ToString("X16", CultureInfo.InvariantCulture))
               .Append(" -- ").Append(winning ? "winning" : "shadowed");
    }

    private static StyleFrameExplanation ExplainFrame(UIProperty property,
                                                               ValueFrame frame,
                                                               IValueEntry entry,
                                                               bool winning)
    {
        var key = frame.SortKey;
        var selector = frame is StyleRuleFrame styleFrame
            ? styleFrame.Rule.SelectorText.Length == 0 ? "(explicit)" : styleFrame.Rule.SelectorText
            : "(frame)"; // a non-engine ValueFrame in the Style slot (conformance kit / future producers)
        var layer = frame is StyleRuleFrame ruleFrame ? ruleFrame.Layer : key.Layer;
        
        return new StyleFrameExplanation(Layer: layer,
                                         SelectorDescription: selector,
                                         SortKey: frame.SortKey,
                                         IsActive: frame.IsActive,
                                         HasValue: entry.HasValue,
                                         LastProducedValue: entry.HasValue ? FormatValue(property.GetEntryValueBoxed(entry)) : null,
                                         Status: winning ? "Winning" : "Shadowed");

    }

    internal static string FormatValue(object? value)
    {
        return value switch
               {
                   null     => "(null)",
                   string s => $"\"{s.Replace("\"", "\\\"")}\"",
                   Color c  => c.Kind == ColorKind.Rgb ? $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}{c.Alpha:X2}" : c.ToString(),
                   Pen p => $"Pen {{ Brush={FormatValue(p.Brush)}, Weight={p.Weight}, Corners={FormatValue(p.Corners)}, " +
                            $"Dash={FormatValue(p.Dash)}, EndCap={FormatValue(p.EndCap)}, Junction={FormatValue(p.Junction)}, " +
                            $"GlyphSet={FormatValue(p.GlyphSet)}, Attributes={FormatValue(p.Attributes)} }}",
                   SolidColorBrush sc => FormatValue(sc.Color),
                   LinearGradientBrush lg => $"linear:({lg.StartPoint.X},{lg.StartPoint.Y}) -> ({lg.EndPoint.X},{lg.EndPoint.Y}, " +
                                             $"{string.Join(", ", lg.Stops.Select(s => s.Color.ToString()))})",
                   RadialGradientBrush rg => $"radial:({rg.Center.X},{rg.Center.Y}) -> ({rg.RadiusX},{rg.RadiusY}, " +
                                             $"{string.Join(", ", rg.Stops.Select(s => s.Color.ToString()))})",
                   ConicGradientBrush cb => $"conic:({cb.Center.X},{cb.Center.Y}) -> ({cb.AngleDegrees}º, Center={cb.Center}, " +
                                            $"{string.Join(", ", cb.Stops.Select(s => s.Color.ToString()))})",
                   IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                   _                        => value.ToString() ?? "(null)"
               };
    }
    
    internal static string DescribeTarget(UIObject target, UIProperty property)
        => BindingRegistry.DescribeTarget(target, property);

    /// <summary>
    /// The element's armed rules, strongest-first (larger keys first; equal keys in arm order).
    /// Empty for detached elements, elements nothing matched, and barrier-skipped template parts
    /// (except their <c>/template/</c>-matched and explicit entries).
    /// </summary>
    public static IReadOnlyList<MatchedRuleInfo> MatchedRules(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element.StyleStateInternal is not { Frames.Length: > 0 } state)
            return [];

        var results = new List<MatchedRuleInfo>(state.Frames.Length);
        foreach (var frame in state.Frames)
        {
            results.Add(new MatchedRuleInfo(
                            frame.Rule.SelectorText.Length == 0 ? "(explicit)" : frame.Rule.SelectorText,
                            frame.Layer, frame.SortKey, frame.IsActive));
        }

        // Strongest first; OrderByDescending is a stable sort, so equal keys keep arm order (cold path).
        return results.OrderByDescending(static info => info.Key.Packed).ToArray();
    }
}

/// <summary>
/// The one-time DEBUG lint channel (style matrix SD23): the engine emits each finding once per
/// offending style/rule, DEBUG builds only. Tests observe through <see cref="DiagnosticEmitted"/>
/// (InternalsVisibleTo); release builds compile the emission sites out.
/// </summary>
internal static class StyleDebugDiagnostics
{
    /// <summary>The SD23-① category: a rule indexed into the universal bucket.</summary>
    internal const string UniversalBucketCategory = "style-universal-bucket";

    /// <summary>The SD23-② category: a rule with more than one ancestor-state compound.</summary>
    internal const string AncestorStateCategory = "style-ancestor-state";

    /// <summary>The §3.3 style-loop category: a frame toggled A→B→A within one activation drain.</summary>
    internal const string StyleLoopCategory = "style-loop";

    /// <summary>The SD23-③ category: a <c>:pointerover</c> rule with no <c>:focus</c> parity (the §3.8 lint).</summary>
    internal const string HoverParityCategory = "style-hover-parity";

    /// <summary>The SD23-④ category: an ancestor-state rule whose chain exceeds the 64-element placement bitmap (greedy fallback).</summary>
    internal const string ChainTruncationCategory = "style-chain-truncation";

    /// <summary>The #115 category: a multi-compound reach-in rule in an element-addressed (control-theme / explicit) style — it can never match.</summary>
    internal const string ElementAddressedReachInCategory = "style-element-addressed-reach-in";

#if DEBUG
    private static readonly HashSet<CompiledRule> WarnedUniversal = [];
    private static readonly HashSet<CompiledRule> WarnedAncestorState = [];
    private static readonly HashSet<Style> WarnedHoverParity = [];
    private static readonly HashSet<CompiledRule> WarnedChainTruncation = [];
    private static readonly HashSet<CompiledRule> WarnedElementAddressedReachIn = [];
    private static readonly Lock WarnedLock = new();
#endif

    /// <summary>Raised once per finding (category, message). DEBUG builds only — never raised in release.</summary>
    internal static event Action<string, string>? DiagnosticEmitted;

    [Conditional("DEBUG")]
    internal static void WarnUniversalBucketRule(CompiledRule rule)
    {
#if DEBUG
        lock (WarnedLock)
        {
            if (!WarnedUniversal.Add(rule))
                return;
        }

        Emit(UniversalBucketCategory,
             $"Style rule '{rule.SelectorText}' (style '{rule.OwnerStyle.IdentityForDiagnostics}') has no name, class, " +
             "or type discriminator and lands in the universal bucket — it is evaluated against every element in scope (SD23-①).");
#endif
    }

    [Conditional("DEBUG")]
    internal static void WarnMultipleAncestorStateCompounds(CompiledRule rule)
    {
#if DEBUG
        lock (WarnedLock)
        {
            if (!WarnedAncestorState.Add(rule))
                return;
        }

        Emit(AncestorStateCategory,
             $"Style rule '{rule.SelectorText}' (style '{rule.OwnerStyle.IdentityForDiagnostics}') carries " +
             $"{rule.AncestorStateCompounds.Length} ancestor-state compounds — each pays its own dependency " +
             "bookkeeping; consider classes (SD23-②).");
#endif
    }

    [Conditional("DEBUG")]
    internal static void WarnHoverParity(Style style, CompiledRule rule)
    {
#if DEBUG
        lock (WarnedLock)
        {
            if (!WarnedHoverParity.Add(style))
                return;
        }

        Emit(HoverParityCategory,
             $"Style '{style.IdentityForDiagnostics}': rule '{rule.SelectorText}' styles properties on ':pointerover' " +
             "with no ':focus' rule covering any of them — hover-only affordances are invisible to keyboard-only " +
             "terminals (design doc §3.8; SD23-③).");
#endif
    }

    [Conditional("DEBUG")]
    internal static void WarnChainTruncation(CompiledRule rule)
    {
#if DEBUG
        lock (WarnedLock)
        {
            if (!WarnedChainTruncation.Add(rule))
                return;
        }

        Emit(ChainTruncationCategory,
             $"Style rule '{rule.SelectorText}' (style '{rule.OwnerStyle.IdentityForDiagnostics}') has an ancestor-state " +
             "compound on a styling-parent chain longer than 64 elements — the placement bitmap is exceeded, so the " +
             "engine falls back to a single greedy ancestor binding (it may miss an alternative valid placement). " +
             "Flatten the tree or use classes (SD23-④).");
#endif
    }

    [Conditional("DEBUG")]
    internal static void WarnStyleLoop(CompiledRule first, CompiledRule second)
        => Emit(StyleLoopCategory,
                $"Style rules '{first.SelectorText}' and '{second.SelectorText}' toggled each other within one " +
                "activation drain (A→B→A) — a style loop (design doc §3.3).");

    [Conditional("DEBUG")]
    internal static void WarnElementAddressedReachIn(CompiledRule rule, StyleLayer layer)
    {
#if DEBUG
        lock (WarnedLock)
        {
            if (!WarnedElementAddressedReachIn.Add(rule))
                return;
        }

        Emit(ElementAddressedReachInCategory,
             $"Style rule '{rule.SelectorText}' (style '{rule.OwnerStyle.IdentityForDiagnostics}') is a multi-compound " +
             $"reach-in rule in an element-addressed {layer} style — it can never match, because that channel matches the " +
             "subject against the styled element itself. Author '/template/' part rules in ControlTemplate.Styles " +
             "(the Template layer, CD30); the control-theme Style carries only the '^'-rooted self/state rules (#115).");
#endif
    }

    private static void Emit(string category, string message) => DiagnosticEmitted?.Invoke(category, message);
}
