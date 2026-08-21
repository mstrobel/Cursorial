using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// Enum member display names (<c>[Display(Name = "…")]</c> on an enum field). When an enum column's
/// members carry <c>[Display]</c>, cells and value pickers show the designated text instead of the
/// raw member name; parsing/filtering still uses the member name. The reflection is cheap and cached
/// per enum type. BCL-only (DataAnnotations ships in the framework), so the shaping engine may use it.
/// </summary>
internal static class EnumDisplay
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string?[]?> Cache = new();

    /// <summary>The per-member display names for an enum type (index-aligned with
    /// <see cref="Enum.GetNames(Type)"/>), or null when NO member carries a <c>[Display(Name)]</c>
    /// (the fast path — cells/pickers use the raw member names).</summary>
    private static string?[]? DisplayNamesOrNull(
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields)] Type enumType)
        => Cache.GetOrAdd(enumType, _ => // capture enumType (DAM-annotated); the key param loses the annotation
        {
            var members = Enum.GetNames(enumType);
            var names = new string?[members.Length];
            bool any = false;
            for (int i = 0; i < members.Length; i++)
            {
                var name = enumType.GetField(members[i])?.GetCustomAttribute<DisplayAttribute>()?.GetName();
                names[i] = name;
                any |= name is not null;
            }
            return any ? names : null;
        });

    /// <summary>Whether the enum type has any member-level <c>[Display(Name)]</c>.</summary>
    public static bool HasDisplayNames([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields)] Type enumType) => enumType.IsEnum && DisplayNamesOrNull(enumType) is not null;

    /// <summary>The display text for one member name (the member's <c>[Display(Name)]</c>, else the member itself).</summary>
    public static string NameOf([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields)] Type enumType, string member)
    {
        if (DisplayNamesOrNull(enumType) is { } names)
        {
            var members = Enum.GetNames(enumType);
            int i = Array.IndexOf(members, member);
            if (i >= 0 && names[i] is { } display)
                return display;
        }
        return member;
    }

    /// <summary>The display text for a boxed enum value (its member's display name, else the raw name).</summary>
    public static string TextOf([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields)] Type enumType, object value)
        => NameOf(enumType, value.ToString() ?? string.Empty);
}
