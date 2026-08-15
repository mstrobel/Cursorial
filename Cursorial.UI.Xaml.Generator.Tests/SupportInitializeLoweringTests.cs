using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// The lowering emitter brackets an ISupportInitialize element's member sets with BeginInit/EndInit — matching
/// the runtime builder — and detects the contract through the full type hierarchy (direct, transitive via
/// ISupportInitializeNotification, and inherited from a base class), never over-emitting for a plain type.
/// </summary>
public class SupportInitializeLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    // Source-defined probes added to the lowering compilation: a direct implementer, a transitive one (via
    // ISupportInitializeNotification, which extends ISupportInitialize), and one inheriting the contract.
    private const string Probes = @"
using System;
using System.ComponentModel;

namespace GenApp
{
    public class DirectInit : ISupportInitialize
    {
        public int Value { get; set; }
        public void BeginInit() { }
        public void EndInit() { }
    }

    public class NotifyInit : ISupportInitializeNotification
    {
        public int Value { get; set; }
        public bool IsInitialized => true;
        public event EventHandler? Initialized;
        public void BeginInit() { }
        public void EndInit() { Initialized?.Invoke(this, EventArgs.Empty); }
    }

    public class DerivedInit : DirectInit
    {
    }
}";

    private static string Lower(string contentElement)
    {
        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
                                          .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Probes));

        // The probe sits in Button.Content (an object slot), so a non-UIElement type is a legal child; the root
        // is `this` (Button) and isn't itself bracketed — the bracket under test is on the content element.
        var xaml =
            $"<Button {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.V\">" +
            contentElement +
            "</Button>";

        return GeneratorHarness.LowerView(compilation, xaml);
    }

    [Theory] // Direct, transitive (ISupportInitializeNotification), and inherited all get the BeginInit/EndInit bracket.
    [InlineData("<g:DirectInit Value=\"5\"/>")]
    [InlineData("<g:NotifyInit Value=\"5\"/>")]
    [InlineData("<g:DerivedInit Value=\"5\"/>")]
    public void ISupportInitialize_EmitsBeginEndInitBracket(string content)
    {
        var src = Lower(content);

        Assert.Contains(".BeginInit();", src);
        Assert.Contains(".EndInit();", src);
    }

    [Fact] // A plain type gets no init bracket — guard against over-emission.
    public void NonISupportInitialize_EmitsNoBracket()
    {
        var src = Lower("<Border/>");

        Assert.DoesNotContain(".BeginInit();", src);
        Assert.DoesNotContain(".EndInit();", src);
    }
}
