// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// What happens to an inline application's region when the application exits
/// (<see cref="UIApplicationBuilder.UseInline"/>). Full-screen applications restore the alternate
/// screen (or clear) instead; this choice exists only inline, and can be changed while running via
/// <see cref="UIApplication.InlineExitBehavior"/> — read once, at teardown.
/// </summary>
public enum InlineExitBehavior
{
    /// <summary>
    /// Erase the region on exit: the cursor rewinds to the region's top-left and everything below
    /// is cleared, so the shell prompt resumes exactly where the application started — the inline
    /// analog of leaving the alternate screen.
    /// </summary>
    Clear,

    /// <summary>
    /// Leave the most recent rendered frame standing on exit: the cursor parks on a fresh line
    /// below the region (scrolling one line when the region ends on the terminal's bottom row) and
    /// the shell prompt resumes there, with the final frame preserved in the scrollback above it.
    /// </summary>
    Retain
}
