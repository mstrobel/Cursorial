using System;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Shared stage-2 (loader) fixtures for the X1+ matrix sections. <see cref="Load"/> wraps a fragment
/// body in the standard <c>D</c> preamble and parses + instantiates through a default
/// <see cref="XamlLoader"/> (the real <see cref="ReflectionXamlMetadata"/> over the actual
/// <c>Cursorial.UI</c> CLR types). <see cref="LoadRaw"/> takes a verbatim document.
/// </summary>
public abstract class LoaderTestBase
{
    private protected const string TestNamespace = "Cursorial.Tests.UI.Xaml.XamlMatrix";

    static LoaderTestBase()
    {
        // Register the test assembly + its fixture namespace with the default schema so the X2/X3
        // custom-extension / test-brush fixtures (TestBrush / RepeatExtension / StatusToBrush) resolve as
        // unprefixed names in the default xmlns.
        XamlSchemaContext.Default.RegisterAssembly(typeof(LoaderTestBase).Assembly);
        XamlSchemaContext.Default.RegisterDefaultNamespace(TestNamespace);
    }

    private protected static readonly Uri TestSource = new("cursorial://test/doc.xaml");

    private protected static readonly XamlLoader Loader = new();

    /// <summary>Parses + instantiates a fragment body with the standard preamble.</summary>
    private protected static object Load(string body) => Loader.Load(Wrap(body), TestSource);

    /// <summary>Parses + instantiates a fragment body, casting to <typeparamref name="T"/>.</summary>
    private protected static T Load<T>(string body) => Loader.Load<T>(Wrap(body), TestSource);

    /// <summary>Parses + instantiates a verbatim document.</summary>
    private protected static object LoadRaw(string xml) => Loader.Load(xml, TestSource);

    /// <summary>Parses a fragment body (stage 1 only).</summary>
    private protected static XamlDocument Parse(string body) => Loader.Parse(Wrap(body), TestSource);

    /// <summary>Wraps a body element with the standard xmlns preamble on its root element.</summary>
    private protected static string Wrap(string body)
    {
        const string preamble = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";
        int tagStart = body.IndexOf('<');
        if (tagStart < 0)
            return body;
        int i = tagStart + 1;
        while (i < body.Length && !char.IsWhiteSpace(body[i]) && body[i] != '>' && body[i] != '/')
            i++;
        return body.Substring(0, i) + preamble + body.Substring(i);
    }

    /// <summary>Asserts that loading throws a <see cref="XamlParseException"/> with the given code (1-based position).</summary>
    private protected static XamlParseException ThrowsLoad(string code, Func<object> load)
    {
        var ex = Assert.Throws<XamlParseException>(load);
        Assert.Equal(code, ex.Code);
        Assert.True(ex.Line > 0, "Diagnostic line must be 1-based and non-zero.");
        Assert.True(ex.Column > 0, "Diagnostic column must be 1-based and non-zero.");
        return ex;
    }
}
