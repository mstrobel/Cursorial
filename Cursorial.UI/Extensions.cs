using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Media;

namespace Cursorial.UI;

public static class Extensions
{
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

    extension(StyleDeltaTemplate)
    {
        /// <summary>
        /// Derives a <see cref="StyleDeltaTemplate"/> from a UI element by reconciling its decomposed text and
        /// brush properties. Values are read at <see cref="UIObject.GetBaseValue">base priority</see> — inherited
        /// included, animated excluded. A theme-resolved default (e.g., <see cref="TextElement.ForegroundProperty"/>)
        /// counts as a stated opinion, so compose this before any delta that should override it.
        /// </summary>
        public static StyleDeltaTemplate FromElement(UIElement element)
        {
            ArgumentNullException.ThrowIfNull(element);

            var t = new StyleDeltaTemplate
                    {
                        Foreground = element.GetBaseValue(TextElement.ForegroundProperty),
                        Background = element.GetBaseValue(Panel.BackgroundProperty),
                        UnderlineColor = element.GetBaseValue(TextElement.UnderlineBrushProperty),
                        Mode = element.GetBaseValue(RenderOptions.BlendingModeProperty)
                    };

            if (element.GetBaseValue(TextElement.UnderlineProperty) is {} underlineShape)
                t = t.Underlining(underlineShape); // Underline is not in BooleanAttributes, so set separately.

            var resolvedAttributes = TextElement.ComposeAttributes(element);

            var booleans = TextAttributes.None;

            foreach (var attribute in BooleanAttributes)
            {
                if (resolvedAttributes.Flags.HasFlag(attribute))
                    booleans |= attribute;
            }

            if (booleans != TextAttributes.None)
                t = t.Applying(booleans);

            return t;
        }
    }
}