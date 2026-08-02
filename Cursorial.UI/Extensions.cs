using Cursorial.UI.Input;

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
}