namespace Cursorial.Drawing.Charts;

/// <summary>Per-axis configuration for <see cref="Axes"/>.</summary>
public sealed record Axis
{
    /// <summary>Explicit range; null (default) auto-ranges from the data and rounds to nice ticks.</summary>
    public AxisRange? Range { get; init; }

    /// <summary>Target number of ticks (a "nice" count near this is chosen; default 5).</summary>
    public int TickCount { get; init; } = 5;

    /// <summary>Tick-label formatter; null uses a trailing-zero-trimmed numeric format.</summary>
    public Func<double, string>? Format { get; init; }

    /// <summary>Draw gridlines across the plot at each tick (default false).</summary>
    public bool Gridlines { get; init; }
}
