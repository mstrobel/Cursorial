using System.Text;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// Read-only introspection over a <see cref="WindowManager"/>'s surface stack (design doc §8) — a debugging
/// aid, not a runtime dependency. <see cref="DumpZOrder"/> renders the z-order bottom→top with each surface's
/// kind, screen rect, and window flags.
/// </summary>
public static class WindowDiagnostics
{
    /// <summary>
    /// Renders the surface stack bottom→top, one line per surface: index, kind (root / window / popup), the
    /// screen rect, and — for windows — active / modal / blocked / clipped flags and the title. The trailing
    /// lines summarize the active window and the modal gate.
    /// </summary>
    public static string DumpZOrder(WindowManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        var builder = new StringBuilder();
        var surfaces = manager.Surfaces;
        builder.Append("WindowManager z-order (").Append(surfaces.Count).Append(" surface(s), screen ")
               .Append(manager.ScreenSize.Columns).Append('×').Append(manager.ScreenSize.Rows).Append("):\n");

        for (var i = 0; i < surfaces.Count; i++)
        {
            var surface = surfaces[i];
            builder.Append("  [").Append(i).Append("] ");

            var rect = $"({surface.Left},{surface.Top} {surface.Size.Columns}×{surface.Size.Rows})";

            if (surface.HostWindow is { } window)
            {
                builder.Append("window ").Append(rect);
                AppendFlag(builder, "active", window.IsActive);
                AppendFlag(builder, "modal", window.IsModal);
                AppendFlag(builder, "blocked", !manager.IsInputEnabled(window));
                AppendFlag(builder, "clipped", window.IsClippedByViewport);
                if (window.Title is { Length: > 0 } title)
                    builder.Append(" \"").Append(title).Append('"');
            }
            else if (surface.IsPopup)
            {
                builder.Append("popup ").Append(rect);
                AppendFlag(builder, "hit-transparent", surface.IsHitTestTransparent);
            }
            else
            {
                builder.Append("root ").Append(rect);
            }

            builder.Append('\n');
        }

        builder.Append("  active: ").Append(manager.ActiveWindow?.Title ?? "(none / root)").Append('\n');
        builder.Append("  modal gate: ").Append(manager.TopmostModal?.Title ?? "(none)");
        return builder.ToString();
    }

    private static void AppendFlag(StringBuilder builder, string name, bool value)
    {
        if (value)
            builder.Append(' ').Append(name);
    }
}
