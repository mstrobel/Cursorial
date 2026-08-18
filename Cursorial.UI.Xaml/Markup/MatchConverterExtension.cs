using Cursorial.UI.Data;

namespace Cursorial.UI.Xaml.Markup;

public sealed class MatchConverterExtension : MarkupExtension
{
    public MatchConverterExtension() {}

    public MatchConverterExtension(object? valueToMatch, object? ifMatched, object? ifUnmatched)
    {
        ValueToMatch = valueToMatch;
        IfMatched = ifMatched;
        IfUnmatched = ifUnmatched;
    }

    public MatchConverterExtension(object? valueToMatch, object? ifMatched, object? ifUnmatched, object? fallbackValue)
    {
        ValueToMatch = valueToMatch;
        IfMatched = ifMatched;
        IfUnmatched = ifUnmatched;
        FallbackValue = fallbackValue;
    }

    /// <inheritdoc cref="MatchConverter.ValueToMatch"/>
    [ConstructorArgument("valueToMatch")]
    public object? ValueToMatch { get; set; }

    /// <inheritdoc cref="MatchConverter.IfMatched"/>
    [ConstructorArgument("ifMatched")]
    public object? IfMatched { get; set; }

    /// <inheritdoc cref="MatchConverter.IfUnmatched"/>
    [ConstructorArgument("ifUnmatched")]
    public object? IfUnmatched { get; set; }

    /// <inheritdoc cref="MatchConverter.FallbackValue"/>
    [ConstructorArgument("fallbackValue")]
    public object? FallbackValue { get; set; } = UIProperty.UnsetValue; // the converter's own default sentinel — an UNSPECIFIED fallback must stay UnsetValue, not become null

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new MatchConverter
           {
               ValueToMatch = ValueToMatch,
               IfMatched = IfMatched,
               IfUnmatched = IfUnmatched,
               FallbackValue = FallbackValue
           };
}