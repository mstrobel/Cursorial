using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Shared parse fixtures for the X0 frontend matrix sections. <see cref="Parse"/> wraps a body in the
/// standard <c>D</c> preamble (default + intrinsics xmlns) so rows show only the body, and injects
/// <see cref="TestMetadata"/> as the metadata provider. <see cref="ParseRaw"/> parses a full document
/// verbatim (for preamble-altering rows). <see cref="ParsePure"/> parses with no provider (pure-syntax
/// rows that need no type resolution).
/// </summary>
public abstract class XamlTestBase
{
    private protected static readonly Uri TestSource = new("cursorial://test/doc.xaml");

    /// <summary>Parses a fragment body with the standard preamble + the test metadata provider.</summary>
    private protected static XamlDocument Parse(string body, XamlDiagnosticMode mode = XamlDiagnosticMode.ThrowOnFirstError, bool fold = true)
    {
        string xml = Wrap(body);
        return XamlFrontend.Parse(xml, Options(mode, fold), TestSource);
    }

    /// <summary>Parses a verbatim document with the test metadata provider.</summary>
    private protected static XamlDocument ParseRaw(string xml, XamlDiagnosticMode mode = XamlDiagnosticMode.ThrowOnFirstError, bool fold = true)
        => XamlFrontend.Parse(xml, Options(mode, fold), TestSource);

    /// <summary>Parses pure syntax with no metadata provider (no type resolution).</summary>
    private protected static XamlDocument ParsePure(string xml, XamlDiagnosticMode mode = XamlDiagnosticMode.ThrowOnFirstError)
        => XamlFrontend.Parse(xml, new XamlParseOptions { MetadataProvider = null, DiagnosticMode = mode }, TestSource);

    private protected static XamlParseOptions Options(XamlDiagnosticMode mode, bool fold)
        => new()
        {
            MetadataProvider = TestMetadata.Instance,
            DiagnosticMode = mode,
            FoldConstants = fold,
        };

    /// <summary>Wraps a body element with the standard xmlns preamble on the root.</summary>
    private protected static string Wrap(string body)
    {
        // Inject the xmlns attributes onto the body's root element. The body is a single element;
        // splice the preamble after the local name + before the first space/'>' /'/>'.
        const string preamble = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";
        int tagStart = body.IndexOf('<');
        if (tagStart < 0)
            return body;
        // find the end of the element's local name
        int i = tagStart + 1;
        while (i < body.Length && !char.IsWhiteSpace(body[i]) && body[i] != '>' && body[i] != '/')
            i++;
        return body.Substring(0, i) + preamble + body.Substring(i);
    }

    /// <summary>Asserts that parsing throws a <see cref="XamlParseException"/> whose first diagnostic has the given code, returning it.</summary>
    private protected static XamlParseException Throws(string code, Func<XamlDocument> parse)
    {
        var ex = Assert.Throws<XamlParseException>(() => parse());
        Assert.Equal(code, ex.Code);
        Assert.True(ex.Line > 0, "Diagnostic line must be 1-based and non-zero.");
        Assert.True(ex.Column > 0, "Diagnostic column must be 1-based and non-zero.");
        return ex;
    }
}
