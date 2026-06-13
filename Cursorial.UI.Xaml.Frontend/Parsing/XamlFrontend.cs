using System;
using System.IO;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The public stage-1 entry point of the parser frontend. The loader's <c>XamlLoader.Parse</c>
/// projects its <c>XamlLoaderOptions</c> onto <see cref="XamlParseOptions"/> and calls this; the X4
/// generator uses the same surface. Pure and thread-safe — a fresh parse per call, no shared mutable
/// state (matrix XD20). With a null/stub metadata provider, syntax/grammar/whitespace/diagnostic rows
/// parse with zero <c>Cursorial.UI</c> dependency.
/// </summary>
public static class XamlFrontend
{
    /// <summary>Parses XAML text into an immutable <see cref="XamlDocument"/>.</summary>
    public static XamlDocument Parse(string xml, XamlParseOptions? options = null, Uri? source = null)
    {
        if (xml is null) throw new ArgumentNullException(nameof(xml));
        return XamlParser.Parse(xml, options ?? new XamlParseOptions(), source);
    }

    /// <summary>Parses XAML from a stream into an immutable <see cref="XamlDocument"/>.</summary>
    public static XamlDocument Parse(Stream xml, XamlParseOptions? options = null, Uri? source = null)
    {
        if (xml is null) throw new ArgumentNullException(nameof(xml));
        return XamlParser.Parse(xml, options ?? new XamlParseOptions(), source);
    }

    /// <summary>
    /// Parses XAML from a text reader into an immutable <see cref="XamlDocument"/>. The reader is
    /// consumed incrementally (the <c>XmlReader</c> reads from it directly), not materialized into a
    /// string first — a tool integrator referencing only the frontend can stream a reader (P6 review
    /// P2-14).
    /// </summary>
    public static XamlDocument Parse(TextReader xml, XamlParseOptions? options = null, Uri? source = null)
    {
        if (xml is null) throw new ArgumentNullException(nameof(xml));
        return XamlParser.Parse(xml, options ?? new XamlParseOptions(), source);
    }
}
