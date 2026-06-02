using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Rendering;

/// <summary>
/// Represents an abstract base class for content elements that may use buffer fragments for rendering.
/// </summary>
/// <remarks>
/// A <c>FragmentContent</c> manages the lifecycle of a buffer fragment that can be reused
/// across multiple rendering passes. It provides mechanisms for measuring and painting content
/// within a given space, as well as determining whether a new fragment is needed for rendering.
/// </remarks>
public abstract class FragmentContent : IContent
{
    /// <summary>Construct content from the supplied data.</summary>
    protected FragmentContent(object? fragmentKey = null)
    {
        FragmentKey = fragmentKey ?? this;
    }

    protected internal IContent? RealizedPlaceholder { get; protected set; }

    /// <summary>
    /// Gets the fragment key associated with the content, used to identify and manage buffer fragments.
    /// </summary>
    /// <remarks>
    /// The <c>FragmentKey</c> uniquely identifies a buffer fragment associated with the content.
    /// It is used by the rendering system to reuse existing fragments and optimize rendering performance.
    /// If no specific key is provided during initialization, the content instance itself is used as the key.
    /// </remarks>
    protected internal object FragmentKey { get; private init; }

    /// <summary>
    /// Gets the desired size of the content after the measurement process.
    /// </summary>
    /// <remarks>
    /// The <c>DesiredSize</c> property indicates the dimensions (in columns and rows)
    /// that the content determines it needs during the measurement phase. This value is
    /// calculated by invoking the <c>Measure</c> method, which internally calls
    /// <c>MeasureOverride</c>, a method that subclasses must implement to define their
    /// custom measurement logic.
    /// The value of <c>DesiredSize</c> can be used to assess whether the content fits
    /// within the available space or if adjustments are needed to ensure proper rendering.
    /// </remarks>
    protected Size? DesiredSize { get; private set; }

    /// <summary>
    /// Gets or sets the existing instance of an <see cref="IBufferFragment"/>
    /// associated with the current content.
    /// This property provides a reference to a pre-existing rendered fragment,
    /// allowing the system to reuse or evaluate its status when determining
    /// if rendering updates are necessary.
    /// </summary>
    protected internal IBufferFragment? ExistingFragment { get; protected set; }

    /// <summary>
    /// The available space the cached <see cref="ExistingFragment"/> was built for. A fragment is
    /// re-created when the available space changes (e.g. a layout-driven resize) so its content
    /// is re-measured against the new bounds; an unchanged size lets it be reused as-is.
    /// </summary>
    private Size? _fragmentAvailableSpace;

    // ReSharper disable once UnusedParameter.Global

    /// <summary>
    /// Determines whether a fragment is needed based on the current buffer state,
    /// available rendering space, and output capabilities.
    /// </summary>
    /// <param name="buffer">The buffer that holds rendering content and fragments.</param>
    /// <param name="availableSpace">The available space for rendering defined in rows and columns.</param>
    /// <param name="capabilities">Optional output capabilities to consider during the decision-making process.</param>
    /// <returns>
    /// A boolean value indicating whether a new fragment is required.
    /// Returns true if a fragment is needed, otherwise false.
    /// </returns>
    // ReSharper disable once VirtualMemberNeverOverridden.Global
    protected internal virtual bool IsFragmentNeeded(in CellBufferView buffer, Size availableSpace, OutputCapabilities? capabilities = null)
    {
        // A (re)create is needed when we don't already have a usable fragment registered in the
        // buffer, OR when the available space differs from what the cached fragment was built for.
        //
        // Three prior bugs conspired to re-create the fragment EVERY frame: (1) `ExistingFragment is
        // not null` was inverted — it reported "needed" whenever a fragment was cached; (2) the size
        // test compared `s.Columns` against `availableSpace.Rows` (a dimension mismatch) using `<`,
        // re-creating whenever the bounds were larger than the rendered fragment; and (3) the buffer
        // lookup keyed on the *content's* FragmentKey, but fragments are registered (CellBuffer.
        // AddFragment) and removed (Paint) under the *fragment's* Key — so the lookup never found the
        // entry and always reported "missing". Any one of these drives a per-frame RemoveFragment,
        // which marks the footprint dirty (CellBuffer.RemoveFragment), silently flipping the renderer
        // into dirty-region-only mode and dropping unrelated changed cells (e.g. a ticking clock) —
        // and churns a fresh Kitty image ID per frame. The key now matches the registration key, and
        // the size test is an equality check against the cached available space (the real recreation
        // trigger: a layout/resize).
        return ExistingFragment is not {} existing ||
               buffer.TryGetFragmentAnchor(existing.Key, out var p) is false ||
               buffer.Fragments.TryGetValue(p, out _) is false ||
               _fragmentAvailableSpace != availableSpace;
    }

    /// <summary>Determines the required size to render the content within the specified constraints.</summary>
    /// <param name="availableSpace">The amount of space available for rendering, expressed as a <see cref="Size"/> object.</param>
    /// <param name="capabilities">The output capabilities of the rendering environment.</param>
    /// <returns>The measured size of the content that fits within the given constraints, expressed as a <see cref="Size"/> object.</returns>
    public Size Measure(Size availableSpace, OutputCapabilities capabilities)
    {
        var size = MeasureOverride(availableSpace, capabilities, out var canCreateFragment);

        if (canCreateFragment is false)
        {
            RealizedPlaceholder ??= BuildPlaceholder(availableSpace, capabilities);
        
            if (RealizedPlaceholder is {} placeholder)
                size = placeholder.Measure(availableSpace, capabilities);
            else
                size = Size.Empty;
        }

        DesiredSize = size;
        return size;
    }

    /// <summary>
    /// Measures the desired size of the content based on the available space and output capabilities.
    /// </summary>
    /// <param name="availableSpace">The available space, represented as a <c>Size</c> object, within which the content can be measured.</param>
    /// <param name="capabilities">The output capabilities of the rendering environment, represented as an <c>OutputCapabilities</c> object.</param>
    /// <param name="canCreateFragment"></param>
    /// <returns>
    /// A <c>Size</c> object representing the desired dimensions of the content after measurement.
    /// </returns>
    protected abstract Size MeasureOverride(Size availableSpace, OutputCapabilities capabilities, out bool canCreateFragment);

    /// <summary>
    /// Renders the content into the given buffer within the specified bounds and style, using the provided output capabilities.
    /// </summary>
    /// <param name="buffer">The buffer where the content will be rendered.</param>
    /// <param name="bounds">The rectangular bounds defining the area for rendering.</param>
    /// <param name="style">The styling information to apply during rendering.</param>
    /// <param name="capabilities">The output capabilities to consider for rendering.</param>
    /// <returns>A rectangle representing the actual area occupied by the rendered content.</returns>
    public Rect Paint(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (buffer.IsEmpty) return bounds.WithSize(Size.Empty);

        if (IsFragmentNeeded(buffer, bounds.Size, capabilities) is false)
        {
            if (ExistingFragment?.GetSize() is {} actualSize)
                return bounds.WithSize(actualSize);

            return bounds;
        }

        if (ExistingFragment is {} existingFragment)
        {
            ExistingFragment = null;
            _fragmentAvailableSpace = null;

            if (buffer.TryGetFragmentAnchor(existingFragment.Key, out var anchor))
                buffer.RemoveFragment(anchor);
        }

        Rect actualBounds;
        IBufferFragment? fragment = CreateFragment(buffer, bounds, style, capabilities);

        if (fragment is not null)
        {
            buffer.AddFragment(bounds.Column, bounds.Row, fragment, style);
            ExistingFragment = fragment;
            _fragmentAvailableSpace = bounds.Size;

            var size = fragment.GetSize();

            actualBounds = new Rect(bounds.Column, bounds.Row,
                                    Math.Min(size.Columns, bounds.Columns),
                                    Math.Min(size.Rows, bounds.Rows));

            PaintOverride(buffer, bounds, style, capabilities);
        }
        else
        {
            actualBounds = PaintPlaceholder(buffer, bounds, style, capabilities);
        }

        return actualBounds;
    }

    protected abstract IContent BuildPlaceholder(Size size, OutputCapabilities capabilities);

    // ReSharper disable UnusedParameter.Global
    /// <summary>
    /// Performs custom rendering of the content within the specified bounds using the provided style and output capabilities.
    /// </summary>
    /// <param name="buffer">The buffer to draw the content into.</param>
    /// <param name="bounds">The rectangular area within which the content should be rendered.</param>
    /// <param name="style">The style to apply while rendering the content.</param>
    /// <param name="capabilities">The output capabilities that define rendering constraints and features.</param>
    protected virtual void PaintOverride(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities) {}

    /// <summary>Renders a placeholder graphic within the specified bounds using the provided style and output capabilities.</summary>
    /// <param name="buffer">The cell buffer where the placeholder will be rendered.</param>
    /// <param name="bounds">The rectangular area defining the bounds of the placeholder graphic.</param>
    /// <param name="style">The style applied to the placeholder during rendering.</param>
    /// <param name="capabilities">The output capabilities to consider during rendering.</param>
    /// <returns>A <see cref="Rect"/> representing the actual area occupied by the rendered placeholder.</returns>
    protected virtual Rect PaintPlaceholder(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (buffer.IsEmpty) return bounds.WithSize(Size.Empty);
        
        var placeholderSize = DesiredSize ?? bounds.Size;

        RealizedPlaceholder ??= BuildPlaceholder(placeholderSize, capabilities);
        
        if (RealizedPlaceholder is {} placeholder)
            return placeholder.Paint(buffer, new Rect(bounds.Position, placeholderSize), style, capabilities);
        
        return bounds;
    }

    /// <summary>
    /// Creates a buffer fragment based on the provided cell buffer, bounds, style, and output capabilities.
    /// </summary>
    /// <param name="buffer">The cell buffer from which the fragment is created.</param>
    /// <param name="bounds">The rectangular bounds defining the area for the fragment.</param>
    /// <param name="style">The style to apply when creating the fragment.</param>
    /// <param name="capabilities">The output capabilities used in fragment creation.</param>
    /// <returns>An instance of <see cref="IBufferFragment"/> representing the created fragment, or null if creation is not possible.</returns>
    protected abstract IBufferFragment? CreateFragment(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities);

    // ReSharper restore UnusedParameter.Global
}
