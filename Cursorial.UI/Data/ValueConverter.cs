using System.Globalization;

namespace Cursorial.UI.Data;

/// <summary>
/// A delegate representing a callback function used to convert a value during a binding operation.
/// This can be used for transforming source values to target values in the context of data bindings.
/// </summary>
/// <param name="value">The value produced by the binding source.</param>
/// <param name="targetType">The type of the binding target property.</param>
/// <param name="parameter">The optional parameter to use in the conversion logic.</param>
/// <param name="culture">The culture information to use during conversion.</param>
/// <returns>The converted value to be passed to the binding target, or null if the conversion fails.</returns>
public delegate object? ConvertCallback(object? value, Type targetType, object? parameter, CultureInfo culture);

/// <summary>
/// A delegate representing a callback function used to convert a value back during a binding operation,
/// typically transforming target values to source values in two-way data bindings.
/// </summary>
/// <param name="value">The value produced by the binding target.</param>
/// <param name="targetType">The type of the binding source property.</param>
/// <param name="parameter">The optional parameter to use in the conversion logic.</param>
/// <param name="culture">The culture information to use during the conversion process.</param>
/// <returns>The converted value to be passed to the binding source, or null if the conversion fails.</returns>
public delegate object? ConvertBackCallback(object? value, Type targetType, object? parameter, CultureInfo culture);

/// <summary>
/// Provides functionality for transforming values in data-binding operations.
/// It supports custom conversion logic to adapt values between binding sources
/// and binding targets, including two-way bindings.
/// </summary>
public static class ValueConverter
{
    /// <summary>
    /// Creates an implementation of the <see cref="IValueConverter"/> interface using the specified conversion logic.
    /// </summary>
    /// <param name="convert">A delegate that defines the logic for forward conversion.</param>
    /// <param name="convertBack">
    /// An optional delegate that defines the logic for reverse conversion. This can be null if no reverse
    /// conversion is required.
    /// </param>
    /// <returns>A new instance of <see cref="IValueConverter"/> implementing the provided conversion logic.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the <paramref name="convert"/> parameter is null.</exception>
    public static IValueConverter Create(ConvertCallback convert, ConvertBackCallback? convertBack = null)
    {
        ArgumentNullException.ThrowIfNull(convert);
        return new AnonymousValueConverter(convert, convertBack);
    }

    /// <summary>
    /// Represents a value converter that allows the user to define custom conversion logic
    /// through delegates for converting data between the source and target bindings.
    /// </summary>
    /// <remarks>
    /// This class is a sealed implementation of the <see cref="IValueConverter"/> interface,
    /// intended for scenarios where custom one-way or two-way conversion logic is needed.
    /// The conversion behavior is defined using the provided <c>ConvertCallback</c> and
    /// optionally <c>ConvertBackCallback</c>.
    /// </remarks>
    private sealed class AnonymousValueConverter : IValueConverter
    {
        /// <summary>
        /// A delegate instance used to define the logic for converting a value from the source to the target
        /// in data binding operations. This conversion is typically determined within the context of
        /// binding transformations where source values must be adapted to match the expected target type.
        /// </summary>
        private readonly ConvertCallback _convert;

        /// <summary>
        /// Represents the delegate used for defining custom logic to convert a value
        /// back from the target to the source in two-way data binding operations.
        /// When provided, it determines how values are transformed during a reverse conversion process.
        /// </summary>
        private readonly ConvertBackCallback? _convertBack;

        /// <summary>
        /// Represents an anonymous implementation of the <see cref="IValueConverter"/> interface,
        /// allowing custom conversion logic to be passed through delegates at runtime.
        /// </summary>
        /// <remarks>
        /// This class is designed for scenarios where conversion logic is defined dynamically
        /// instead of through a concrete implementation of the <see cref="IValueConverter"/> interface.
        /// </remarks>
        public AnonymousValueConverter(ConvertCallback convert, ConvertBackCallback? convertBack = null)
        {
            _convert = convert;
            _convertBack = convertBack;
        }

        /// <inheritdoc/>
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return _convert(value, targetType, parameter, culture);
        }

        /// <inheritdoc/>
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return _convertBack?.Invoke(value, targetType, parameter, culture) ?? UIProperty.UnsetValue;
        }
    }
}