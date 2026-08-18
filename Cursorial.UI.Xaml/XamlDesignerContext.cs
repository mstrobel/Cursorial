namespace Cursorial.UI.Xaml;

/// <summary>
/// The ambient designer-context switch (WPF <c>DesignerProperties</c> parity, process-wide). A design
/// host (the Rider previewer) enables it ONCE at startup, before any document loads; runtime
/// applications never touch it. Gates behavior that is correct for design surfaces but wrong for
/// running apps — currently constructor-less activation: a designer materializes a view-model with
/// <see cref="System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject"/> and paints its
/// visible state purely through property assignment, whereas runtime loading keeps the strict
/// "no public parameterless constructor ⇒ not element-activatable" contract.
/// </summary>
/// <remarks>
/// Set it BEFORE the first load: activators are built once per CLR type and cached, so a flip after
/// a type has been touched does not retroactively change that type's activator.
/// </remarks>
public static class XamlDesignerContext
{
    /// <summary>Whether the process is a design host. Default false.</summary>
    public static bool IsDesignMode { get; set; }
}
