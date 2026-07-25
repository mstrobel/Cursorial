namespace Cursorial.UI;

internal static class Extensions
{
    extension(Type type)
    {
        public bool IsNullableType()
        {
            return type.IsValueType is false ||
                   Nullable.GetUnderlyingType(type) is not null;
        }
        public bool IsIntegralType()
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
}