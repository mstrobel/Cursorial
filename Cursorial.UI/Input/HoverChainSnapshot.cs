using System.Diagnostics;

namespace Cursorial.UI.Input;

/// <summary>
/// Receives <see cref="InputDispatcher.HoverChanged"/> (design doc §7.4): the elements that left
/// and entered the hover chain in one diff. <paramref name="removed"/> is ordered deepest-first
/// (the <c>MouseLeave</c> raise order); <paramref name="added"/> outermost-first (the
/// <c>MouseEnter</c> raise order). The snapshots are pooled — valid only during the raise; copy
/// elements out, never retain the snapshot (DEBUG-stamped, stale access throws).
/// </summary>
/// <param name="removed">The elements that left the chain, deepest-first.</param>
/// <param name="added">The elements that entered the chain, outermost-first.</param>
public delegate void HoverChangedHandler(HoverChainSnapshot removed, HoverChainSnapshot added);

/// <summary>
/// A pooled, read-only view of one side of a hover-chain diff (design doc §7.4/§7.6). The
/// dispatcher owns two retained instances and re-arms them per diff — handlers iterate during the
/// <see cref="InputDispatcher.HoverChanged"/> raise and copy out what they need; touching a
/// snapshot after the raise completes throws in DEBUG builds (element references are cleared
/// either way so a stale snapshot never pins a detached subtree).
/// </summary>
public sealed class HoverChainSnapshot
{
    private UIElement[] _elements = new UIElement[8];
    private int _count;
#if DEBUG
    private bool _stale = true; // armed by Begin, stamped by Complete
#endif

    internal HoverChainSnapshot()
    {
    }

    /// <summary>The number of elements in this side of the diff.</summary>
    public int Count
    {
        get
        {
            ThrowIfStale();
            return _count;
        }
    }

    /// <summary>The element at <paramref name="index"/> (see the delegate docs for ordering).</summary>
    /// <param name="index">The position within the snapshot.</param>
    public UIElement this[int index]
    {
        get
        {
            ThrowIfStale();
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            return _elements[index];
        }
    }

    /// <summary>Re-arms the snapshot for a new diff (clears the DEBUG stale stamp).</summary>
    internal void Begin()
    {
        _count = 0;
#if DEBUG
        _stale = false;
#endif
    }

    /// <summary>Appends one element (phase-1 capture; the backing array grows and is retained).</summary>
    internal void Add(UIElement element)
    {
        if (_count == _elements.Length)
            Array.Resize(ref _elements, _elements.Length * 2);

        _elements[_count++] = element;
    }

    /// <summary>The count without the stale guard (dispatcher-internal phase-2 iteration).</summary>
    internal int CountUnchecked => _count;

    /// <summary>The element at <paramref name="index"/> without the stale guard (phase-2 iteration).</summary>
    internal UIElement ItemUnchecked(int index) => _elements[index];

    /// <summary>
    /// Stamps the snapshot stale and clears element references — runs on every unwind path so a
    /// retained snapshot neither pins elements nor reads phase-stale data.
    /// </summary>
    internal void Complete()
    {
        Array.Clear(_elements, 0, _count);
        _count = 0;
#if DEBUG
        _stale = true;
#endif
    }

    /// <summary>
    /// DEBUG stale-stamp check (matrix N76): a snapshot captured past its
    /// <see cref="InputDispatcher.HoverChanged"/> raise throws here. Compiled out of release
    /// builds — copy elements out during the raise.
    /// </summary>
    [Conditional("DEBUG")]
    private void ThrowIfStale()
    {
#if DEBUG
        if (_stale)
        {
            throw new InvalidOperationException(
                "This HoverChainSnapshot has been re-armed — it was valid only during its HoverChanged raise. " +
                "Copy the elements out during the raise; never retain the snapshot instance.");
        }
#endif
    }
}
