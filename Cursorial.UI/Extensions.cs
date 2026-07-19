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
    }
}