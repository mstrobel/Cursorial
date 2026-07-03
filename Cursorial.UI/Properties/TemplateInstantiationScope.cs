// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The ambient template-instantiation scope (precedence matrix §20, PD24). While a scope is open on
/// the current thread, the ordinary local-lane value producers — a literal <c>SetValue</c>, a
/// free-standing binding install (<c>{TemplateBinding}</c>/<c>{Binding}</c>), and a
/// <c>SetResourceReference</c> — reroute to <see cref="BindingPriority.Template"/>, so the values a
/// control or data template <em>authors on its parts</em> sit one rung below <see cref="BindingPriority.Style"/>
/// and are overridable by a page/theme Style. This is the engine half of the deliberate inverse of
/// WPF (where template-property values outrank Style); the rationale is in §20's header and
/// <c>docs/ui-layer-design/judgment-styling-coherence.md</c> (Flaw 1).
/// </summary>
/// <remarks>
/// <para>
/// The framework opens a scope around every template content build —
/// <c>ControlTemplate.Instantiate</c> (covering <c>FuncTemplateContent</c> and the XAML
/// <c>ITemplateContent</c> it nests) and <c>DataTemplate.Build</c>. Outside any scope every producer
/// stays <see cref="BindingPriority.LocalValue"/>: the scope is the <em>only</em> trigger, and it
/// restores on dispose.
/// </para>
/// <para>
/// <b>Capture-at-install.</b> A binding installed inside the scope captures the Template priority into
/// its activation context immediately, even though its store entry may not materialize until a later
/// attach (outside the scope) — the install, not the push, decides the lane.
/// </para>
/// <para>
/// Nesting is re-entrant and last-open-wins: a scope opened while one is already open keeps the lane at
/// Template; disposing pops one level, and the lane returns to LocalValue only when the outermost scope
/// closes. UI-thread only (the whole property stack is single-threaded, invariant 6) — a plain
/// <c>[ThreadStatic]</c> depth counter, no <c>AsyncLocal</c>.
/// </para>
/// </remarks>
public static class TemplateInstantiationScope
{
    [ThreadStatic] private static int _depth;

    /// <summary>Whether a template-instantiation scope is open on the current thread (the lane is <see cref="BindingPriority.Template"/>).</summary>
    public static bool IsActive => _depth > 0;

    /// <summary>
    /// The priority a local-lane producer installs at right now: <see cref="BindingPriority.Template"/>
    /// inside a scope, else <see cref="BindingPriority.LocalValue"/>. The single seam the property /
    /// binding / resource producers consult.
    /// </summary>
    internal static BindingPriority CurrentInstallPriority
        => _depth > 0 ? BindingPriority.Template : BindingPriority.LocalValue;

    /// <summary>Opens a scope; dispose the returned token to close it (LIFO). Use with <c>using</c>.</summary>
    public static IDisposable Enter()
    {
        _depth++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _depth--;
        }
    }
}
