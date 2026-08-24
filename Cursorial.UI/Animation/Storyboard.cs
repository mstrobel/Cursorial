using System.Collections.ObjectModel;

using Cursorial.Markup;

// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// A reusable, parallel set of <see cref="AnimationTrack"/>s (design doc §9.3). A storyboard is a
/// <b>description</b>: <see cref="Begin"/> creates a per-<c>(igniter, scope)</c> instance, so two rules
/// sharing one storyboard resource never fight. It <b>seals</b> on first <see cref="Begin"/> (or when a
/// referencing <c>Style</c> seals) — element-independent validation runs then, and <see cref="Children"/>
/// mutation afterwards throws.
/// </summary>
[ContentProperty("Children")]
public sealed class Storyboard
{
    private readonly TrackCollection _children;
    private bool _sealed;

    /// <summary>Creates an empty storyboard.</summary>
    public Storyboard() => _children = new TrackCollection(this);

    /// <summary>The tracks (one per animated property); mutation after seal throws.</summary>
    public IList<AnimationTrack> Children => _children;

    internal IReadOnlyList<AnimationTrack> Tracks => _children;

    /// <summary>
    /// Begins this storyboard on <paramref name="scope"/> (imperative key = the storyboard itself). Seals first.
    /// Throws on an unresolvable target/property (the imperative error policy — §9.3).
    /// </summary>
    public StoryboardHandle Begin(UIElement scope, HandoffBehavior handoff = HandoffBehavior.SnapshotAndReplace)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.VerifyAccess();
        Seal();
        return AnimationScheduler.Current.BeginStoryboard(this, scope, this, handoff, throwOnFailure: true);
    }

    /// <summary>Stops the imperatively-keyed instance on <paramref name="scope"/> (no <c>Completed</c>).</summary>
    public void Stop(UIElement scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        AnimationScheduler.CurrentOrNull?.StopStoryboardByKey(this, scope);
    }

    /// <summary>Seals the storyboard and validates every track (idempotent). Called by <see cref="Begin"/> and by Style seal.</summary>
    internal void Seal()
    {
        if (_sealed)
            return;

        foreach (var track in _children)
            track.Seal();

        _sealed = true;
    }

    private sealed class TrackCollection(Storyboard owner) : Collection<AnimationTrack>
    {
        protected override void InsertItem(int index, AnimationTrack item)
        {
            ArgumentNullException.ThrowIfNull(item);
            ThrowIfSealed();
            base.InsertItem(index, item);
        }

        protected override void SetItem(int index, AnimationTrack item)
        {
            ArgumentNullException.ThrowIfNull(item);
            ThrowIfSealed();
            base.SetItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            ThrowIfSealed();
            base.RemoveItem(index);
        }

        protected override void ClearItems()
        {
            ThrowIfSealed();
            base.ClearItems();
        }

        private void ThrowIfSealed()
        {
            if (owner._sealed)
                throw new InvalidOperationException("The Storyboard is sealed (it has been begun or referenced by a sealed Style); its Children cannot be mutated.");
        }
    }
}
