using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// <c>Widget</c> — the style matrix's primary fixture element (§0.2): a plain <see cref="UIElement"/>
/// carrying the shared fixture properties, the <c>flip</c>/<c>pseudo</c> test shims over the
/// protected interaction surface, an optional rigid size (<c>Widget(w×h)</c>), and an optional
/// render of <see cref="P"/> for same-frame pixel rows. Registrations are static so they are
/// idempotent across test classes (dense ids are process-global).
/// </summary>
public class Widget : UIElement
{
    /// <summary><c>P</c> — int, default 0, <c>AffectsRender</c> (the workhorse property).</summary>
    public static readonly StyledProperty<int> P = UIProperty.Register<Widget, int>("P");

    /// <summary><c>Q</c> — int, default 0, <c>AffectsRender</c> (the second property for batch rows).</summary>
    public static readonly StyledProperty<int> Q = UIProperty.Register<Widget, int>("Q");

    /// <summary><c>Pi</c> — int, default 0, inherits.</summary>
    public static readonly StyledProperty<int> Pi = UIProperty.Register<Widget, int>("Pi", inherits: true);

    /// <summary><c>Pd</c> — double, default 0.0 (conversion rows).</summary>
    public static readonly StyledProperty<double> Pd = UIProperty.Register<Widget, double>("Pd");

    /// <summary><c>Pcmp</c> — string?, metadata Comparer = OrdinalIgnoreCase.</summary>
    public static readonly StyledProperty<string?> Pcmp = UIProperty.Register<Widget, string?>(
        "Pcmp", new PropertyMetadata<string?>(Comparer: StringComparer.OrdinalIgnoreCase));

    /// <summary><c>Kro</c> — the write key of the read-only <see cref="Pro"/>.</summary>
    public static readonly UIPropertyKey<int> Kro = UIProperty.RegisterReadOnly<Widget, int>("Pro");

    /// <summary><c>Pro</c> — the read-only styled property behind <see cref="Kro"/> (SD10 rows).</summary>
    public static readonly StyledProperty<int> Pro = Kro.Property;

    static Widget() => AffectsRender<Widget>(P, Q); // §0.2: P/Q carry AffectsRender (S109/S176 routing)

    /// <summary>A measure-flexible widget (desired size 0×0 — most rows never lay out).</summary>
    public Widget()
    {
    }

    /// <summary>The §0.2 rigid form: <c>Widget(w×h)</c>.</summary>
    public Widget(int columns, int rows) => Natural = new Size(columns, rows);

    /// <summary>The rigid natural size (0×0 when default-constructed).</summary>
    public Size Natural { get; set; }

    /// <summary>When set, <c>Render</c> fills the bounds with <see cref="P"/>'s last decimal digit (S109-style pixel rows).</summary>
    public bool PaintP { get; set; }

    /// <summary>Optional hook run at the start of <c>MeasureOverride</c> (S149's layout-raised flips).</summary>
    public Action<Widget>? OnMeasuring { get; set; }

    /// <summary>The matrix's <c>flip(e, Bit, on)</c> shim over the protected interaction sink.</summary>
    public void Flip(InteractionState state, bool active) => SetInteractionState(state, active);

    /// <summary>The matrix's <c>batch { … }</c> shim.</summary>
    public InteractionUpdateScope Batch() => BeginInteractionUpdate();

    /// <summary>The matrix's <c>pseudo(e, ":x", on)</c> shim over the protected pseudo set.</summary>
    public bool SetPseudo(string pseudoClass, bool active) => PseudoClasses.Set(pseudoClass, active);

    /// <summary>Whether the pseudo-class is set (custom set, or the interaction bit for §0.3 names).</summary>
    public bool HasPseudo(string pseudoClass) => PseudoClasses.Contains(pseudoClass);

    protected override Size MeasureOverride(Size availableSize)
    {
        OnMeasuring?.Invoke(this);
        return Natural;
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void Render(RenderContext context)
    {
        if (!PaintP)
            return;

        var glyph = ((char)('0' + Math.Abs(GetValue(P)) % 10)).ToString();
        for (var row = 0; row < context.Size.Rows; row++)
        for (var column = 0; column < context.Size.Columns; column++)
            context.Set(column, row, glyph, default);
    }
}

/// <summary><c>FancyWidget : Widget</c> — the derived branch for exact-vs-assignable type rows.</summary>
public class FancyWidget : Widget
{
    public FancyWidget()
    {
    }

    public FancyWidget(int columns, int rows)
        : base(columns, rows)
    {
    }
}

/// <summary><c>Card : UIElement</c> — the unrelated element branch (hosts children for combinator rows).</summary>
public class Card : UIElement
{
    /// <summary>Adopts a child (visual + logical), exposing the protected tree surface to the matrix rows.</summary>
    public void Add(UIElement child) => AdoptChild(child, -1);
}

/// <summary>The §0.2 fixture surface shared by every style-matrix section file.</summary>
public static class StyleMatrixFixture
{
    private static int _uniqueCounter;

    /// <summary>
    /// The fixture <see cref="ISelectorTypeResolver"/>: maps the §0.2 type tokens
    /// (<c>Pane</c> = <see cref="StackPanel"/>). Grammar rows pass it explicitly except where
    /// testing the SD1 default resolver.
    /// </summary>
    public static readonly ISelectorTypeResolver Resolver = new FixtureResolver();

    /// <summary>A resolver that knows nothing — the S17 fixture.</summary>
    public static readonly ISelectorTypeResolver NullResolver = new NullTypeResolver();

    /// <summary>
    /// A namespace-aware resolver: a <c>prefix|Local</c> token resolves <c>Local</c> via the simple-name map
    /// (any prefix accepted — it stands in for an xmlns-binding resolver like the XAML loader's), proving the
    /// grammar carries the qualified token through to the resolver. Bare names resolve as the fixture resolver.
    /// </summary>
    public static readonly ISelectorTypeResolver QualifiedResolver = new QualifiedTypeResolver();

    /// <summary>Forces the fixture property registrations (static field touch) for default-resolver rows.</summary>
    public static void EnsureRegistered()
    {
        _ = Widget.P;
        _ = ChipVendorAlpha.Chip.AlphaP;
        _ = ChipVendorBeta.Chip.BetaP;
    }

    /// <summary>A process-unique property name for rows that need row-local registrations (mapping rows).</summary>
    public static string UniqueName(string prefix) => $"{prefix}_{Interlocked.Increment(ref _uniqueCounter)}";

    /// <summary>The matrix's <c>R(sel){P=v, …}</c> shorthand: a style with parsed selector and constant setters.</summary>
    public static Style R(string selector, params (UIProperty Property, object? Value)[] setters)
    {
        var style = new Style(selector, Resolver);
        foreach (var (property, value) in setters)
            style.Setters.Add(new Setter(property, value));
        return style;
    }

    /// <summary>A selector-less style with constant setters (explicit-attachment rows).</summary>
    public static Style Explicit(params (UIProperty Property, object? Value)[] setters)
    {
        var style = new Style();
        foreach (var (property, value) in setters)
            style.Setters.Add(new Setter(property, value));
        return style;
    }

    /// <summary>
    /// The §0.2 default tree on a fresh host: <c>Root</c> (StackPanel) → <c>paneA</c> (StackPanel)
    /// → leaves <c>a</c>, <c>b</c> (Widgets); <c>paneB</c> → <c>c</c>. Shown via
    /// <see cref="UIHeadlessHost.ShowRoot"/>; the caller owns disposal of the returned host.
    /// </summary>
    public static StyleTree ShowTree(UIHeadlessHostOptions? options = null, bool show = true)
    {
        var host = UIHeadlessHost.Create(options);
        var tree = new StyleTree
        {
            Host = host,
            Root = new StackPanel(),
            PaneA = new StackPanel(),
            PaneB = new StackPanel(),
            A = new Widget(),
            B = new Widget(),
            C = new Widget()
        };

        tree.PaneA.Children.Add(tree.A);
        tree.PaneA.Children.Add(tree.B);
        tree.PaneB.Children.Add(tree.C);
        tree.Root.Children.Add(tree.PaneA);
        tree.Root.Children.Add(tree.PaneB);

        if (show)
            host.ShowRoot(tree.Root);

        return tree;
    }

    /// <summary>The default fixture tree plus its host (disposes the host).</summary>
    public sealed class StyleTree : IDisposable
    {
        public required UIHeadlessHost Host { get; init; }
        public required StackPanel Root { get; init; }
        public required StackPanel PaneA { get; init; }
        public required StackPanel PaneB { get; init; }
        public required Widget A { get; init; }
        public required Widget B { get; init; }
        public required Widget C { get; init; }

        public UIApplication App => Host.Application;

        public void Dispose() => Host.Dispose();
    }

    private sealed class FixtureResolver : ISelectorTypeResolver
    {
        internal static readonly Dictionary<string, Type> Map =
            new(StringComparer.Ordinal)
            {
                ["Widget"] = typeof(Widget),
                ["FancyWidget"] = typeof(FancyWidget),
                ["Card"] = typeof(Card),
                ["Pane"] = typeof(StackPanel),
                ["StackPanel"] = typeof(StackPanel)
            };

        public Type? Resolve(string typeName, out IReadOnlyList<Type>? ambiguousCandidates)
        {
            ambiguousCandidates = null;
            return Map.GetValueOrDefault(typeName);
        }
    }

    private sealed class NullTypeResolver : ISelectorTypeResolver
    {
        public Type? Resolve(string typeName, out IReadOnlyList<Type>? ambiguousCandidates)
        {
            ambiguousCandidates = null;
            return null;
        }
    }

    private sealed class QualifiedTypeResolver : ISelectorTypeResolver
    {
        public Type? Resolve(string typeName, out IReadOnlyList<Type>? ambiguousCandidates)
        {
            ambiguousCandidates = null;
            int pipe = typeName.IndexOf('|');
            string local = pipe >= 0 ? typeName[(pipe + 1)..] : typeName;
            return FixtureResolver.Map.GetValueOrDefault(local);
        }
    }
}

/// <summary>The <c>flip</c> shim for non-Widget fixture elements (panels) — the framework-internal sink write.</summary>
public static class InteractionShims
{
    /// <summary>Flips an interaction bit through the framework-internal commit path.</summary>
    public static void Flip(this UIElement element, InteractionState state, bool active)
        => element.SetInteractionStateInternal(state, active);
}

/// <summary>
/// The §0.2 <c>notify</c>/<c>silent</c> probe over one property on one element: a typed observer
/// recording <c>(old, new, priority)</c> deliveries in order.
/// </summary>
public sealed class StyleProbe<T> : IValueObserver<T>
{
    /// <summary>Deliveries in order.</summary>
    public readonly List<(T Old, T New, BindingPriority Priority)> Deliveries = [];

    private StyleProbe()
    {
    }

    /// <summary>Attaches a probe for <paramref name="property"/> on <paramref name="element"/>.</summary>
    public static StyleProbe<T> Attach(UIObject element, StyledProperty<T> property)
    {
        var probe = new StyleProbe<T>();
        element.AddObserver(property, probe);
        return probe;
    }

    /// <summary>Asserts exactly one delivery of the triple, then clears.</summary>
    public void AssertSingleNotify(T oldValue, T newValue, BindingPriority priority)
    {
        Assert.Equal([(oldValue, newValue, priority)], Deliveries);
        Deliveries.Clear();
    }

    /// <summary>Asserts zero deliveries.</summary>
    public void AssertSilent() => Assert.Empty(Deliveries);

    /// <summary>Clears recorded deliveries.</summary>
    public void Clear() => Deliveries.Clear();

    void IValueObserver<T>.OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority)
        => Deliveries.Add((oldValue, newValue, priority));
}
