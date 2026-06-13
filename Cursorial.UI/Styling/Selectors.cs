// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// Fluent builders for code-first selectors (the Avalonia <c>Selectors</c> kinship; design doc
/// §3.1). Builder chains are structurally equal to their parsed equivalents and produce identical
/// canonical <see cref="Selector.ToString"/> text: <c>Selectors.OfType&lt;Pane&gt;().Class("toolbar")
/// .Child().OfType&lt;Widget&gt;()</c> ≡ <c>Selector.Parse("Pane.toolbar &gt; Widget")</c>.
/// </summary>
public static class Selectors
{
    /// <summary>
    /// Starts a selector with an exact-type compound (<c>Widget</c> — the bare-type token). The
    /// token is the type's <b>simple</b> name (SD2): when that simple name is ambiguous under the
    /// resolver used at parse time (two known element types named alike), the built selector
    /// matches its CLR type correctly but its <see cref="Selector.ToString"/> text will not
    /// round-trip through <see cref="Selector.Parse"/> with that resolver (the SD1 ambiguity
    /// error) — disambiguate with a custom <see cref="ISelectorTypeResolver"/>.
    /// </summary>
    public static Selector OfType<T>() where T : UIElement => OfType(null, typeof(T));

    /// <summary>
    /// Starts a selector with an assignable-type compound (<c>:is(Widget)</c> — T or derived).
    /// The token is the type's simple name — see <see cref="OfType{T}()"/> for the
    /// ambiguous-simple-name round-trip caveat.
    /// </summary>
    public static Selector Is<T>() where T : UIElement => Is(null, typeof(T));

    /// <summary>Starts a selector with the <c>^</c> nesting anchor (valid in <c>Style.Children</c> and explicit styles).</summary>
    public static Selector Nesting() => new([
                                                new SelectorBranch([
                                                                       new SelectorCompound(
                                                                           isNesting: true, type: null, isAssignableType: false, typeName: null, simples: [])
                                                                   ], [])
                                            ]);

    /// <summary>Continues (or starts, when <paramref name="previous"/> is null) with an exact-type compound.</summary>
    public static Selector OfType<T>(this Selector? previous) where T : UIElement => OfType(previous, typeof(T));

    /// <summary>Continues (or starts) with an exact-type compound for <paramref name="type"/>.</summary>
    public static Selector OfType(this Selector? previous, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Append(previous, static (compound, state) => compound.SetType((Type) state!, isAssignable: false), type, startsCompound: true);
    }

    /// <summary>Continues (or starts) with an assignable-type compound (<c>:is(T)</c>).</summary>
    public static Selector Is<T>(this Selector? previous) where T : UIElement => Is(previous, typeof(T));

    /// <summary>Continues (or starts) with an assignable-type compound for <paramref name="type"/>.</summary>
    public static Selector Is(this Selector? previous, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Append(previous, static (compound, state) => compound.SetType((Type) state!, isAssignable: true), type, startsCompound: true);
    }

    /// <summary>Adds a <c>.class</c> simple to the current compound (or starts a class-only selector).</summary>
    public static Selector Class(this Selector? previous, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Append(previous, static (compound, state) => compound.AddSimple(SimpleSelectorKind.Class, (string) state!), name, startsCompound: false);
    }

    /// <summary>Adds a <c>#name</c> simple to the current compound (or starts a name-only selector).</summary>
    public static Selector Name(this Selector? previous, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Append(previous, static (compound, state) => compound.AddSimple(SimpleSelectorKind.Name, (string) state!), name, startsCompound: false);
    }

    /// <summary>
    /// Adds a <c>:pseudo</c> simple to the current compound (or starts a pseudo-only selector).
    /// The leading colon is optional — <c>"focus"</c> and <c>":focus"</c> are equivalent.
    /// </summary>
    public static Selector PseudoClass(this Selector? previous, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var bare = name[0] == ':' ? name[1..] : name;
        ArgumentException.ThrowIfNullOrEmpty(bare, nameof(name));
        return Append(previous, static (compound, state) => compound.AddSimple(SimpleSelectorKind.PseudoClass, (string) state!), bare, startsCompound: false);
    }

    /// <summary>Closes the current compound with the child combinator (<c>&gt;</c>); the next part starts a new compound.</summary>
    public static Selector Child(this Selector previous) => WithCombinator(previous, SelectorCombinator.Child);

    /// <summary>Closes the current compound with the descendant combinator (whitespace).</summary>
    public static Selector Descendant(this Selector previous) => WithCombinator(previous, SelectorCombinator.Descendant);

    /// <summary>Closes the current compound with the template combinator (<c>/template/</c> — one stamp edge, SD8).</summary>
    public static Selector Template(this Selector previous) => WithCombinator(previous, SelectorCombinator.Template);

    /// <summary>
    /// Combines two complete selectors into a selector list (<c>,</c>) — each member compiles to
    /// its own rule with its own specificity, sharing the style's setters (doc §3.1).
    /// </summary>
    public static Selector Or(this Selector previous, Selector other)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(other);

        if (previous.PendingCombinator is not null || other.PendingCombinator is not null)
            throw new InvalidOperationException("A selector-list member must be a complete selector (a combinator is still open).");

        return new Selector([.. previous.Branches, .. other.Branches]);
    }

    // ───────────────────────────── builder mechanics ─────────────────────────────

    private static Selector WithCombinator(Selector previous, SelectorCombinator combinator)
    {
        ArgumentNullException.ThrowIfNull(previous);

        if (previous.Branches.Length == 0 || previous.PendingCombinator is not null)
            throw new InvalidOperationException("A combinator must follow a completed compound.");

        return new Selector(previous.Branches, combinator);
    }

    private static Selector Append(Selector? previous, Action<CompoundBuilder, object?> apply, object? state, bool startsCompound)
    {
        var builder = new CompoundBuilder();

        if (previous is null || previous.Branches.Length == 0)
        {
            apply(builder, state);
            return new Selector([new SelectorBranch([builder.Build()], [])]);
        }

        var branches = previous.Branches;
        var last = branches[^1];

        if (previous.PendingCombinator is {} pending)
        {
            // Start a new compound joined by the pending combinator.
            apply(builder, state);
            var compounds = new SelectorCompound[last.Compounds.Length + 1];
            last.Compounds.CopyTo(compounds, 0);
            compounds[^1] = builder.Build();
            var combinators = new SelectorCombinator[last.Combinators.Length + 1];
            last.Combinators.CopyTo(combinators, 0);
            combinators[^1] = pending;
            return new Selector([.. branches[..^1], new SelectorBranch(compounds, combinators)]);
        }

        // Extend the current (last) compound.
        var subject = last.Compounds[^1];

        if (startsCompound && subject.TypeName is not null)
            throw new InvalidOperationException("The compound already has a type test; types must begin a compound.");

        builder.LoadFrom(subject);
        apply(builder, state);

        var extended = new SelectorCompound[last.Compounds.Length];

        last.Compounds.CopyTo(extended, 0);
        extended[^1] = builder.Build();

        return new Selector([.. branches[..^1], new SelectorBranch(extended, last.Combinators)]);
    }

    private sealed class CompoundBuilder
    {
        private bool _isNesting;
        private Type? _type;
        private bool _isAssignable;
        private string? _typeName;
        private List<SimpleSelector>? _simples;

        internal void LoadFrom(SelectorCompound compound)
        {
            _isNesting = compound.IsNesting;
            _type = compound.Type;
            _isAssignable = compound.IsAssignableType;
            _typeName = compound.TypeName;
            _simples = [.. compound.Simples];
        }

        internal void SetType(Type type, bool isAssignable)
        {
            if (_typeName is not null || _simples is { Count: > 0 })
                throw new InvalidOperationException("A type test must be the first part of a compound (after '^').");

            _type = type;
            _isAssignable = isAssignable;
            _typeName = type.Name;
        }

        internal void AddSimple(SimpleSelectorKind kind, string value)
            => (_simples ??= []).Add(new SimpleSelector(kind, value));

        internal SelectorCompound Build()
            => new(_isNesting, _type, _isAssignable, _typeName, _simples is null ? [] : [.. _simples]);
    }
}