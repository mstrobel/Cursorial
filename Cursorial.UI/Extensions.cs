using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI;

public static class Extensions
{
    extension<THost>(THost host) where THost : UIObject
    {
        public TValue GetDirect<TValue>(DirectProperty<THost, TValue> property) => property.Getter(host);

        public void SetDirect<TValue>(DirectProperty<THost, TValue> property, TValue value)
        {
            if (property.Setter is not {} setter)
            {
                throw new InvalidOperationException($"Direct property {property.HostType.Name}." +
                                                    $"{property.Name} has no setter.");
            }

            setter(host, value);
        }
    }

    extension(Type type)
    {
        internal Type UnwrapNullable()
        {
            return Nullable.GetUnderlyingType(type) ?? type;
        }

        internal bool IsNullableType()
        {
            return type.IsValueType is false ||
                   Nullable.GetUnderlyingType(type) is not null;
        }

        internal bool IsIntegralType()
        {
            return Type.GetTypeCode(type) switch
                   {
                       TypeCode.SByte or
                           TypeCode.Byte or
                           TypeCode.Int16 or
                           TypeCode.UInt16 or
                           TypeCode.Int32 or
                           TypeCode.UInt32 or
                           TypeCode.Int64 or
                           TypeCode.UInt64 => true,
                       _ => false
                   };
        }
    }

    extension(FocusNavigationMethod method)
    {
        /// <summary>
        /// Whether a focus change was driven by the USER (as opposed to a programmatic move or a repair after
        /// the focused element was destroyed) — the test for abandoning a pending drop-down pick.
        /// </summary>
        public bool IsUserInitiated()
            => method is FocusNavigationMethod.Tab or
                         FocusNavigationMethod.Pointer or
                         FocusNavigationMethod.Directional or
                         FocusNavigationMethod.AccessKey;
    }

    private static readonly TextAttributes[] BooleanAttributes
        = Enum.GetValues<TextAttributes>()
              .Where(t => PartialStyle.Booleans.HasFlag(t))
              .ToArray();

    extension(BrushedStyle)
    {
        /// <summary>
        /// Derives a <see cref="BrushedStyle"/> from a UI element by reconciling its decomposed text and
        /// brush properties. Values are read at <see cref="UIObject.GetBaseValue">base priority</see> — inherited
        /// included, animated excluded. A theme-resolved default (e.g., <see cref="TextElement.ForegroundProperty"/>)
        /// counts as a stated opinion, so compose this before any delta that should override it.
        /// </summary>
        /// <remarks>
        /// By default, post-animation values are read. To read the pre-animation value, specify
        /// <c>readBaseValues: true</c>.
        /// </remarks>
        public static BrushedStyle FromElement(UIElement element, bool readBaseValues = false)
        {
            ArgumentNullException.ThrowIfNull(element);

            var t = new BrushedStyle
                    {
                        Foreground = Read(element, TextElement.ForegroundProperty, readBaseValues),
                        Background = Read(element, Panel.BackgroundProperty, readBaseValues),
                        UnderlineColor = Read(element, TextElement.UnderlineBrushProperty, readBaseValues),
                        // RenderOptions.BlendingMode is NOT read here: it composites the element's WHOLE
                        // surface at the render-boundary layer (RenderTree wires it into CompositeParameters,
                        // and a non-default mode promotes a boundary), so folding it per-cell too would blend
                        // the content twice.
                    };

            if (Read(element, TextElement.UnderlineProperty, readBaseValues) is {} underlineShape)
                t = t.Underlining(underlineShape); // Underline is not in BooleanAttributes, so set separately.

            var resolvedAttributes = TextElement.ComposeAttributes(element, readBaseValues);
            var booleans = TextAttributes.None;

            if (resolvedAttributes.Flags.HasFlag(TextAttributes.Bold))
                t = t.Weighing(TextWeight.Bold);
            else if (resolvedAttributes.Flags.HasFlag(TextAttributes.Faint))
                t = t.Weighing(TextWeight.Faint);

            if (resolvedAttributes.Flags.HasFlag(TextAttributes.Italic))
                t = t.Posturing(TextStyle.Italic);

            foreach (var attribute in BooleanAttributes)
            {
                if (resolvedAttributes.Flags.HasFlag(attribute))
                    booleans |= attribute;
            }

            if (booleans != TextAttributes.None)
                t = t.Applying(booleans);

            return t;
        }

        private static T Read<T>(UIElement element, StyledProperty<T> property, bool readBaseValue = false)
            => readBaseValue ? element.GetBaseValue(property) : element.GetValue(property);
    }
}