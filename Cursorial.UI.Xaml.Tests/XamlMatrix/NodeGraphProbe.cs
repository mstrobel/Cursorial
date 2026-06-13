using System.Linq;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Test-only convenience reads over a <see cref="XamlDocument"/>'s internal node graph (surfaced via
/// <c>InternalsVisibleTo</c>). The content is the contract; these accessors translate the flat-array
/// shape into row-friendly assertions. They live in the test project, not the production assembly.
/// </summary>
internal static class NodeGraphProbe
{
    public static int ObjectCount(this XamlDocument doc) => doc.Objects.Length;

    public static int MemberCount(this XamlDocument doc) => doc.Members.Length;

    public static ref readonly ObjectRecord ObjectAt(this XamlDocument doc, int index) => ref doc.Objects[index];

    public static ref readonly ObjectRecord Root(this XamlDocument doc) => ref doc.Objects[0];

    public static int RootMemberCount(this XamlDocument doc) => doc.Objects[0].MemberCount;

    public static int RootSubtreeLength(this XamlDocument doc) => doc.Objects[0].SubtreeLength;

    public static string? RootTypeLocalName(this XamlDocument doc)
    {
        ref readonly var root = ref doc.Objects[0];
        return root.TypeId >= 0 ? doc.ResolvedTypes[root.TypeId]?.ClrType.Name : null;
    }

    /// <summary>Returns the members of the object at <paramref name="objectIndex"/>.</summary>
    public static MemberRecord[] MembersOf(this XamlDocument doc, int objectIndex)
    {
        ref readonly var obj = ref doc.Objects[objectIndex];
        return doc.Members.Skip(obj.MemberStart).Take(obj.MemberCount).ToArray();
    }

    /// <summary>Finds the first member of the given resolved local name on an object, or returns false.</summary>
    public static bool TryFindMember(this XamlDocument doc, int objectIndex, string memberName, out MemberRecord record)
    {
        foreach (var m in doc.MembersOf(objectIndex))
        {
            if (m.MemberId >= 0 && doc.ResolvedMembers[m.MemberId]?.Name == memberName)
            {
                record = m;
                return true;
            }
        }
        record = default;
        return false;
    }

    /// <summary>Finds the first directive of the given kind on an object, returning its string value.</summary>
    public static bool TryFindDirective(this XamlDocument doc, int objectIndex, XamlDirectiveKindMirror kind, out string value)
    {
        foreach (var m in doc.MembersOf(objectIndex))
        {
            if (m.Kind == XamlValueKind.Directive && m.DirectiveKind == (int)kind)
            {
                value = doc.Strings[m.ValueIndex];
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    /// <summary>The string value behind a Text/Event/Directive member.</summary>
    public static string StringValue(this XamlDocument doc, in MemberRecord m) => doc.Strings[m.ValueIndex];

    /// <summary>The folded constant behind a Folded member.</summary>
    public static object? FoldedValue(this XamlDocument doc, in MemberRecord m) => doc.Constants[m.ValueIndex];

    /// <summary>The extension behind an Extension member.</summary>
    public static ref readonly ExtensionRecord ExtensionOf(this XamlDocument doc, in MemberRecord m) => ref doc.Extensions[m.ValueIndex];

    /// <summary>The parsed extension node for a Binding/Custom extension.</summary>
    public static MarkupExtensionNode? ParsedExtension(this XamlDocument doc, in ExtensionRecord ext) => doc.ParsedExtensions[ext.Payload];

    /// <summary>The line of a member record (1-based).</summary>
    public static int Line(in MemberRecord m) => LineInfoProbe.Line(m.PackedLineInfo);

    /// <summary>The column of a member record (1-based).</summary>
    public static int Column(in MemberRecord m) => LineInfoProbe.Column(m.PackedLineInfo);

    /// <summary>The line of an object record (1-based).</summary>
    public static int Line(in ObjectRecord o) => LineInfoProbe.Line(o.PackedLineInfo);

    /// <summary>The column of an object record (1-based).</summary>
    public static int Column(in ObjectRecord o) => LineInfoProbe.Column(o.PackedLineInfo);
}

/// <summary>Mirror of the internal <c>LineInfo</c> packing for test reads.</summary>
internal static class LineInfoProbe
{
    private const int LineBits = 20;
    private const int LineMask = (1 << LineBits) - 1;

    public static int Line(int packed) => packed & LineMask;
    public static int Column(int packed) => (int)((uint)packed >> LineBits);
}
