using System.Runtime.CompilerServices;

using Cursorial.Animation;
using Cursorial.Drawing.Media;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// Auto-registers <c>Cursorial.Drawing</c>'s value-type interpolators with the process-global
/// <see cref="Interpolator"/> registry (design doc §9.11) when the assembly loads — so
/// <c>Interpolator.For&lt;Size&gt;()</c> etc. resolve without any app-side registration. The module
/// initializer runs once, before any consumer can touch these types.
/// </summary>
internal static class DrawingInterpolatorRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void RegisterBuiltIns()
    {
        Interpolator.Register(PointInterpolator.Instance);            // PointD
        Interpolator.Register(SizeInterpolator.Instance);             // Size
        Interpolator.Register(RectInterpolator.Instance);             // Rect
        Interpolator.Register(RelativePointInterpolator.Instance);    // RelativePoint
        Interpolator.Register(MarginsInterpolator.Instance);          // Margins (signed, LD19)
        Interpolator.Register(CompositeParametersInterpolator.Instance);
        Interpolator.Register(BrushInterpolator.Instance);            // IBrush (per-sample allocation)
        Interpolator.Register(PenInterpolator.Instance);              // Pen
    }
#pragma warning restore CA2255
}
