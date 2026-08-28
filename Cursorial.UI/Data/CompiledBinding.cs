using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace Cursorial.UI.Data;

/// <summary>
/// The non-generic discovery anchor for the compiled lane (design doc §6.7). <see cref="From"/> is a
/// sibling of <see cref="Binding.Compiled"/> placed on the type the author is creating, so IDE
/// autocomplete on <c>CompiledBinding.</c> surfaces the factory.
/// </summary>
public static class CompiledBinding
{
    /// <summary>
    /// Analyzes <paramref name="path"/> into a <see cref="CompiledBinding{TSource,TValue}"/> — the
    /// same lambda-analysis factory as <see cref="Binding.Compiled"/>, exposed on
    /// <see cref="CompiledBinding"/> for discoverability. The lambda is the SOLE path source (Fork C
    /// contract). Each call re-analyzes the tree — cache the result in a <c>static readonly</c> field.
    /// </summary>
    /// <seealso cref="Binding.Compiled{TSource,TValue}"/>
    public static CompiledBinding<TSource, TValue> From<TSource, TValue>(
        Expression<Func<TSource, TValue>> path,
        BindingMode mode = BindingMode.Default,
        object? source = null,
        string? elementName = null,
        IValueConverter? converter = null,
        object? converterParameter = null,
        CultureInfo? converterCulture = null,
        string? stringFormat = null,
        object? fallbackValue = null,
        RelativeSource? relativeSource = null,
        object? targetNullValue = null,
        UpdateSourceTrigger updateSourceTrigger = UpdateSourceTrigger.Default,
        bool trace = false)
    {
        ArgumentNullException.ThrowIfNull(path);

        return CompiledBindingFactory.Analyze(path,
                                              mode,
                                              source,
                                              elementName,
                                              converter,
                                              converterParameter,
                                              converterCulture,
                                              stringFormat,
                                              fallbackValue,
                                              relativeSource,
                                              targetNullValue,
                                              updateSourceTrigger,
                                              trace);
    }
    
    /// <summary>
    /// Builds the descriptor for a single styled/attached-property read on the source — directly,
    /// with no expression tree and no reflection: the getter closes over
    /// <paramref name="property"/> and reads through <c>GetValue</c> (the always-GetValue rule —
    /// no trust in CLR wrappers), the step carries the registration for observer subscription, and
    /// a writable property gets a <c>SetValue</c> write-back (LocalValue — the durable data push).
    /// Fully AOT-native: nothing here compiles at runtime or needs trim analysis.
    /// </summary>
    public static CompiledBinding<UIObject, TValue> For<TValue>(
        StyledProperty<TValue> property,
        BindingMode mode = BindingMode.Default,
        object? source = null,
        string? elementName = null,
        IValueConverter? converter = null,
        object? converterParameter = null,
        CultureInfo? converterCulture = null,
        string? stringFormat = null,
        object? fallbackValue = null,
        RelativeSource? relativeSource = null,
        object? targetNullValue = null,
        UpdateSourceTrigger updateSourceTrigger = UpdateSourceTrigger.Default,
        bool trace = false)
    {
        ArgumentNullException.ThrowIfNull(property);

        // Setter presence is decided by WRITABILITY, not by predicting the effective mode: Default
        // resolves per the TARGET property's BindsTwoWayByDefault at install (ResolveEffectiveMode
        // consults the SetBinding target, which this factory cannot see), so a writable source keeps
        // its setter for every mode that could use one — an unused setter is harmless, a missing one
        // silently strips write-back. Read-only registrations get null (BD10/B152 wire-time degrade).
        var needsSetter = !property.IsReadOnly && mode is not (BindingMode.OneWay or BindingMode.OneTime);

        return Build<UIObject, TValue>(
                   o => o.GetValue(property),
                   needsSetter ? (o, v) => o.SetValue(property, v) : null,
                   mode: mode,
                   source: source,
                   elementName: elementName,
                   converter: converter,
                   converterParameter: converterParameter,
                   fallbackValue: fallbackValue, // raw — Build() resolves ONCE (double-resolve turned NullValue into UnsetValue)
                   relativeSource: relativeSource,
                   converterCulture: converterCulture,
                   stringFormat: stringFormat,
                   targetNullValue: targetNullValue,
                   updateSourceTrigger: updateSourceTrigger,
                   trace: trace)
              .Step(property)
              .Build();
    }

    /// <summary>
    /// Builds the descriptor for a single direct-property read on the source. A direct property's
    /// wrapper IS the implementation (the standing exemption from the always-GetValue rule), so the
    /// registration's own <see cref="DirectProperty{TOwner,T}.Getter"/> and
    /// <see cref="DirectProperty{TOwner,T}.Setter"/> delegates serve verbatim as the descriptor's
    /// accessors — a registered setter makes the binding two-way capable. No expression tree, no
    /// reflection; fully AOT-native.
    /// </summary>
    public static CompiledBinding<TSource, TValue> For<TSource, THost, TValue>(
        DirectProperty<THost, TValue> property,
        BindingMode mode = BindingMode.Default,
        object? source = null,
        string? elementName = null,
        IValueConverter? converter = null,
        object? converterParameter = null,
        CultureInfo? converterCulture = null,
        string? stringFormat = null,
        object? fallbackValue = null,
        RelativeSource? relativeSource = null,
        object? targetNullValue = null,
        UpdateSourceTrigger updateSourceTrigger = UpdateSourceTrigger.Default,
        bool trace = false) where THost : UIObject where TSource : THost
    {
        ArgumentNullException.ThrowIfNull(property);

        // The registration's OWN setter is the write-back (the direct-wrapper exemption); a
        // getter-only DirectProperty yields null and the engine degrades TwoWay→OneWay at wire
        // (BD10/B152). Mode only gates whether a setter could ever be used — never the TARGET-side
        // Default resolution, which this factory cannot see.
        var setter = mode is BindingMode.OneWay or BindingMode.OneTime ? null : property.Setter;

        return Build<TSource, TValue>(
                   property.Getter,
                   setter,
                   mode: mode,
                   source: source,
                   elementName: elementName,
                   converter: converter,
                   converterParameter: converterParameter,
                   fallbackValue: fallbackValue, // raw — Build() resolves ONCE (double-resolve turned NullValue into UnsetValue)
                   relativeSource: relativeSource,
                   converterCulture: converterCulture,
                   stringFormat: stringFormat,
                   targetNullValue: targetNullValue,
                   updateSourceTrigger: updateSourceTrigger,
                   trace: trace)
              .Step(property)
              .Build();
    }

    /// <summary>
    /// Builds the descriptor for a single direct-property read on the source. A direct property's
    /// wrapper IS the implementation (the standing exemption from the always-GetValue rule), so the
    /// registration's own <see cref="DirectProperty{TOwner,T}.Getter"/> and
    /// <see cref="DirectProperty{TOwner,T}.Setter"/> delegates serve verbatim as the descriptor's
    /// accessors — a registered setter makes the binding two-way capable. No expression tree, no
    /// reflection; fully AOT-native.
    /// </summary>
    public static CompiledBinding<THost, TValue> For<THost, TValue>(
        DirectProperty<THost, TValue> property,
        BindingMode mode = BindingMode.Default,
        object? source = null,
        string? elementName = null,
        IValueConverter? converter = null,
        object? converterParameter = null,
        CultureInfo? converterCulture = null,
        string? stringFormat = null,
        object? fallbackValue = null,
        RelativeSource? relativeSource = null,
        object? targetNullValue = null,
        UpdateSourceTrigger updateSourceTrigger = UpdateSourceTrigger.Default,
        bool trace = false) where THost : UIObject
    {
        ArgumentNullException.ThrowIfNull(property);

        // The registration's OWN setter is the write-back (the direct-wrapper exemption); a
        // getter-only DirectProperty yields null and the engine degrades TwoWay→OneWay at wire
        // (BD10/B152). Mode only gates whether a setter could ever be used — never the TARGET-side
        // Default resolution, which this factory cannot see.
        var setter = mode is BindingMode.OneWay or BindingMode.OneTime ? null : property.Setter;

        return Build(property.Getter,
                     setter,
                     mode: mode,
                     source: source,
                     elementName: elementName,
                     converter: converter,
                     converterParameter: converterParameter,
                     fallbackValue: fallbackValue, // raw — Build() resolves ONCE (double-resolve turned NullValue into UnsetValue)
                     relativeSource: relativeSource,
                     converterCulture: converterCulture,
                     stringFormat: stringFormat,
                     targetNullValue: targetNullValue,
                     updateSourceTrigger: updateSourceTrigger,
                     trace: trace)
              .Step(property)
              .Build();
    }

    public static CompiledPathStep Step(string clrMemberName) => new(clrMemberName);

    public static CompiledPathStep Step(UIProperty property)
        => new(property.IsAttached || property.IsDirect ? $"({property.OwnerType.Name}.{property.Name})" : property.Name)
           { UIProperty = property };
    
    /// <summary>
    /// Produces a compiled binding builder using the provided getter and setter closures directly.
    /// Every step in the property access chain must be described with a <c>Step()</c> that includes
    /// the step's corresponding <see cref="UIProperty"/> descriptor or the name of the CLR member.
    /// </summary>
    /// <seealso cref="Binding.Compiled{TSource,TValue}"/>
    /// <seealso cref="From{TSource,TValue}"/>
    /// <remarks>
    /// It is generally far more convenient to use the <seealso cref="From{TSource,TValue}"/> method,
    /// which does not require manually describing steps in the access chain. Reserve the use of this
    /// method only when performance is critical: when runtime code generation is unavailable (e.g., AOT)
    /// and the cost of the LINQ expression interpreter is too high. Useful for control authors and
    /// extremely binding-heavy applications, but likely a premature optimization for most.
    /// </remarks>
    public static CompiledBindingBuilder<TSource, TValue> Build<TSource, TValue>(
        Func<TSource, TValue> getter,
        Action<TSource, TValue>? setter = null,
        BindingMode mode = BindingMode.Default,
        object? source = null,
        string? elementName = null,
        IValueConverter? converter = null,
        object? converterParameter = null,
        CultureInfo? converterCulture = null,
        string? stringFormat = null,
        object? fallbackValue = null,
        RelativeSource? relativeSource = null,
        object? targetNullValue = null,
        UpdateSourceTrigger updateSourceTrigger = UpdateSourceTrigger.Default,
        bool trace = false)
    {
        ArgumentNullException.ThrowIfNull(getter);

        // No setter validation here: a null setter under TwoWay/OneWayToSource degrades to OneWay at
        // wire (BD10/B152) — the engine-wide contract; throwing at build would give the builder lane a
        // third behavior distinct from both Analyze and the wire-time degrade.

        return new CompiledBindingBuilder<TSource, TValue>(getter,
                                                           setter,
                                                           mode,
                                                           source,
                                                           elementName,
                                                           converter,
                                                           converterParameter,
                                                           converterCulture,
                                                           stringFormat,
                                                           fallbackValue,
                                                           relativeSource,
                                                           targetNullValue,
                                                           updateSourceTrigger,
                                                           trace);
    }
}

public sealed class CompiledBindingBuilder<TSource, TValue>(Func<TSource, TValue> getter,
                                                            Action<TSource, TValue>? setter,
                                                            BindingMode mode,
                                                            object? source,
                                                            string? elementName,
                                                            IValueConverter? converter,
                                                            object? converterParameter,
                                                            CultureInfo? converterCulture,
                                                            string? stringFormat,
                                                            object? fallbackValue,
                                                            RelativeSource? relativeSource,
                                                            object? targetNullValue,
                                                            UpdateSourceTrigger updateSourceTrigger,
                                                            bool trace)
{
    private readonly List<CompiledPathStep> _steps = [];


    public CompiledBindingBuilder<TSource, TValue> Step(string clrMemberName)
    {
        _steps.Add(new(clrMemberName));
        return this;
    }

    /// <summary>
    /// A CLR-member hop with an explicit object-typed accessor. Required for every hop EXCEPT the
    /// last: rewiring materializes each intermediate through its step to subscribe the hop below it,
    /// and a bare name cannot read a value (the leaf is exempt — the typed whole-chain getter reads
    /// it, so a leaf step only needs its name for INPC matching).
    /// </summary>
    public CompiledBindingBuilder<TSource, TValue> Step(string clrMemberName, Func<object?, object?> getStep)
    {
        ArgumentNullException.ThrowIfNull(getStep);
        _steps.Add(new(clrMemberName, getStep));
        return this;
    }

    public CompiledBindingBuilder<TSource, TValue> Step(UIProperty property)
    {
        if (property.IsAttached || property.IsDirect)
            _steps.Add(new($"({property.OwnerType.Name}.{property.Name})") { UIProperty = property });
        else
            _steps.Add(new(property.Name) { UIProperty = property });

        return this;
    }

    public CompiledBinding<TSource, TValue> Build()
    {
        // Every INTERIOR hop must be materializable (a closure or a carried UIProperty): rewiring
        // walks step-by-step to subscribe each hop's host, and an accessor-less interior hop would
        // silently kill change tracking for everything below it (stale UI, no error).
        for (int i = 0; i < _steps.Count - 1; i++)
        {
            if (_steps[i] is { GetStep: null, UIProperty: null })
                throw new InvalidOperationException(
                    $"Interior step '{_steps[i].MemberName}' has no accessor — use Step(name, getStep) or " +
                    "Step(UIProperty) so the chain below it can re-subscribe on change.");
        }

        return new CompiledBinding<TSource, TValue>(
                   getter,
                   setter,
                   _steps.ToArray(),
                   _steps.Aggregate(new StringBuilder(),
                                    (sb, step) => (sb.Length > 0 ? sb.Append('.') : sb).Append(step.MemberName),
                                    sb => sb.ToString()))
               {
                   Converter = converter,
                   ConverterParameter = converterParameter,
                   ConverterCulture = converterCulture,
                   StringFormat = stringFormat,
                   UpdateSourceTrigger = updateSourceTrigger,
                   FallbackValue = BindingBase.ResolveFallbackValue(fallbackValue),
                   Mode = mode,
                   Source = source,
                   ElementName = elementName,
                   TargetNullValue = BindingBase.ResolveFallbackValue(targetNullValue),
                   Trace = trace,
                   RelativeSource = relativeSource
               };
    }
}

/// <summary>
/// One hop of a compiled chain (design doc §6.1/§6.7): the member name matched against
/// <c>PropertyChangedEventArgs.PropertyName</c> for INPC re-wiring, and an object-typed step getter
/// used <em>only</em> for subscription rewiring (the typed whole-chain getter does the value
/// reads). A constant-index indexer hop carries <see cref="MemberName"/> <c>"Item[]"</c> (the INPC
/// convention) and <see cref="GetStep"/> applies the captured index.
/// </summary>
/// <param name="MemberName">The member name (or <c>"Item[]"</c> for indexer hops).</param>
/// <param name="GetStep">An object-typed reader for subscription rewiring; <see langword="null"/>
/// for a descriptor-only hop, materialized through <see cref="UIProperty"/> instead (the
/// <c>HostType</c> guard reproduces the closure's receiver pattern, including a direct property's
/// owner constraint).</param>
public readonly record struct CompiledPathStep(string MemberName, Func<object?, object?>? GetStep)
{
    /// <summary>
    /// A descriptor-only hop: no step closure at all — subscription rewiring reads the intermediate
    /// through the carried <see cref="UIProperty"/> (property-store dispatch, no reflection, and no
    /// boxing for the reference-typed intermediates a UIObject chain produces).
    /// </summary>
    public CompiledPathStep(string memberName) : this(memberName, null) {}

    /// <summary>
    /// The property-system identity for a <c>GetValue(SomeClass.SomeProperty)</c> hop: subscription
    /// observes it directly, no registry lookup. Hops written through CLR wrappers carry only
    /// <see cref="MemberName"/> — the expression's runtime name-based registry probe covers those.
    /// </summary>
    public UIProperty? UIProperty { get; init; }
}

/// <summary>
/// The second producer of the engine contract (design doc §6.7): typed end-to-end, zero reflection,
/// AOT-clean when generator-produced. Constructible by <c>Binding.Compiled</c> (runtime lambda
/// analysis — the reflective-fallback v1 producer), by the X4 generator, or by hand. Inherits the
/// full <see cref="AnchoredBinding"/> surface — a compiled binding can be Self-, TemplatedParent-,
/// ElementName-, or FindAncestor-anchored; the typed root check (<c>root is TSource</c>) covers
/// anchor/type mismatch.
/// </summary>
/// <seealso cref="Binding.Compiled{TSource,TValue}"/>
/// <seealso cref="CompiledBinding.From{TSource,TValue}"/>
public sealed class CompiledBinding<TSource, TValue> : AnchoredBinding
{
    /// <summary>
    /// Builds a compiled descriptor. A <see langword="null"/> <paramref name="setter"/> makes the
    /// binding one-way only.
    /// </summary>
    public CompiledBinding(
        Func<TSource, TValue> getter,
        Action<TSource, TValue>? setter,
        ReadOnlyMemory<CompiledPathStep> steps,
        string pathText)
    {
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(pathText);
        Getter = getter;
        Setter = setter;
        Steps = steps;
        PathText = pathText;
    }

    /// <summary>The whole-chain typed read (the hot path).</summary>
    public Func<TSource, TValue> Getter { get; }

    /// <summary>The leaf setter; <see langword="null"/> ⇒ one-way only.</summary>
    public Action<TSource, TValue>? Setter { get; }

    /// <summary>Per-hop wiring info for INPC subscription.</summary>
    public ReadOnlyMemory<CompiledPathStep> Steps { get; }

    /// <summary>The path text (diagnostics — e.g. <c>"Customer.Address.City"</c>).</summary>
    public string PathText { get; }

    /// <summary>An optional forward/back converter.</summary>
    public IValueConverter? Converter { get; init; }

    /// <summary>The converter parameter.</summary>
    public object? ConverterParameter { get; init; }

    /// <summary>
    /// When set, this binding is an OPINION-GATED forward: on each produce it retracts (produces
    /// <see cref="UIProperty.UnsetValue"/>, so weaker value-store lanes promote) UNLESS the source has a real
    /// contribution for the bound property at this priority strength or STRONGER (<see cref="UIObject.HasValue{T}"/>).
    /// The per-axis text forwards (<see cref="Cursorial.UI.Controls.TextElement.ForwardAllAxes"/>) set it to
    /// <see cref="BindingPriority.BaseTextStyle"/> so a source with no axis opinion does not shadow the target's
    /// own base look with a forwarded DEFAULT. <see langword="null"/> ⇒ an ordinary always-producing binding.
    /// Set once immediately after construction by the forward helpers (the descriptor is freshly built per forward).
    /// </summary>
    internal BindingPriority? OpinionGateFloor { get; set; }

    internal override BindingExpressionBase CreateExpression(in BindingActivationContext context)
    {
        ValidateAnchors();
        return new CompiledBindingExpression<TSource, TValue>(this, in context);
    }
}
