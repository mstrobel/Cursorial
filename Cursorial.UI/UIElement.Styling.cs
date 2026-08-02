namespace Cursorial.UI;

public abstract partial class UIElement
{
    private ClassSet? _classes;
    private PseudoClassSet? _pseudoClasses;
    private Styles? _styles;
    private Style? _style;

    // ───────────────────────────── styling host surface (design doc §3.2) ─────────────────────────────

    /// <summary>
    /// The element's style classes (<c>.class</c> selector targets) — interned strings;
    /// <see cref="ClassSet.Add"/>/<see cref="ClassSet.Remove"/>/<see cref="ClassSet.Replace"/>
    /// notify the styling engine (synchronous re-match at the mutation site, SD12).
    /// <c>':'</c>-prefixed names are rejected (SD11). Lazily allocated.
    /// </summary>
    public ClassSet Classes => _classes ??= new ClassSet(this);

    /// <summary>
    /// The control-author pseudo-class surface (<c>:open</c>-style control-semantic classes —
    /// <see cref="InteractionState"/>-backed names are rejected and flow through
    /// <see cref="SetInteractionState"/> only; ledger B9). Lazily allocated.
    /// </summary>
    protected internal PseudoClassSet PseudoClasses => _pseudoClasses ??= new PseudoClassSet(this);

    /// <summary>
    /// The element-scoped style collection: its rules apply to this element and its subtree at
    /// layer <see cref="StyleLayer.Scoped"/> (nearer scope wins — SD6). Lazily allocated and bound
    /// to this element on first touch; assigning a collection that is attached to another owner
    /// throws (SD19). Assignment re-matches the subtree (S139 — the engine wires in at Y3).
    /// </summary>
    public Styles Styles
    {
        get
        {
            VerifyAccess();

            if (_styles is null)
            {
                _styles = new Styles();
                _styles.AttachTo(this);
            }

            return _styles;
        }
        set
        {
            VerifyAccess();
            ArgumentNullException.ThrowIfNull(value);

            if (ReferenceEquals(_styles, value))
                return;

            value.AttachTo(this); // throws when owned elsewhere — state is untouched on failure
            _styles?.Detach();
            _styles = value;
            OnStylingStylesInvalidated();
        }
    }

    /// <summary>
    /// The explicit single-style attachment (layer <see cref="StyleLayer.Explicit"/> — beats every
    /// selector-matched channel; doc §3.5). The style must be selector-less or <c>^</c>-rooted
    /// (SD17); it is sealed at assignment and arms regardless of <see cref="TemplatedParent"/> —
    /// explicit styles are element-addressed, the template barrier governs selector matching only
    /// (SD8).
    /// </summary>
    /// <exception cref="InvalidOperationException">The style's selector is neither absent nor <c>^</c>-rooted, or sealing fails.</exception>
    public Style? Style
    {
        get => _style;
        set
        {
            VerifyAccess();

            if (ReferenceEquals(_style, value))
                return;

            if (value is not null)
            {
                if (value.Selector is { IsNestingRooted: false })
                {
                    throw new InvalidOperationException(
                        $"Style '{value.IdentityForDiagnostics}' cannot be an explicit UIElement.Style: explicit " +
                        "styles are selector-less or '^'-rooted (SD17); selector-matched styles belong in a " +
                        "Styles collection.");
                }

                value.Seal();
            }

            var oldStyle = _style;
            _style = value;
            OnStylingExplicitStyleChanged(oldStyle, value);
        }
    }

    // ───────────────────────────── styling engine hooks (SD12 — synchronous at the mutation site) ─────────────────────────────

    /// <summary>The per-element styling state (armed frames + interest masks), or null. Engine-owned; dropped on detach (SD15).</summary>
    internal ElementStyleState? StyleStateInternal;

    /// <summary>The application's styling engine on the current (UI) thread, or null outside a running application.</summary>
    private static StyleEngine? Engine => UIApplication.Current?.StyleEngineInternal;

    /// <summary>The class set without lazy allocation (matcher read surface).</summary>
    internal ClassSet? ClassesOrNull => _classes;

    /// <summary>The scoped style collection without lazy allocation (scope-chain gathering).</summary>
    internal Styles? StylesOrNull => _styles;

    /// <summary>The explicit style without surface ceremony (explicit-layer arming).</summary>
    internal Style? ExplicitStyleOrNull => _style;

    /// <summary>
    /// The owning control template's <c>Styles</c> collection, armed at <see cref="StyleLayer.Template"/>
    /// scoped to this (templated-parent) element when its parts re-match (CD30, doc §12.2). Non-null
    /// only on a <c>Control</c> with an expanded template carrying styles; <see langword="null"/>
    /// otherwise. S8's <c>Control</c> overrides this from its <c>TemplateInstance</c>.
    /// </summary>
    internal virtual Styles? TemplateStylesForArming => null;

    /// <summary>The styling-parent chain hop (SD7): <c>UIParent ?? VisualParent</c> — the same chain as the
    /// property-system inheritance parent and the event route. <see cref="UIParent"/> is
    /// <c>LogicalParent ?? TemplatedParent</c> (plus a Popup's placement-target), so this equals the historical
    /// <c>LogicalParent ?? VisualParent</c> for every in-tree/template element (a template part keeps its
    /// in-template LogicalParent; a template root's TemplatedParent == its VisualParent) and additionally
    /// bridges a placement-target-only popup/tooltip to its owner.</summary>
    internal UIElement? StylingParent => UIParent ?? VisualParent;

    /// <summary>Whether <paramref name="pseudoClass"/> (with leading colon) is set in the pseudo set.</summary>
    public bool HasPseudoClass(string pseudoClass)
        => InteractionPseudoClasses.TryGetState(pseudoClass, out var state)
               ? (_interactionState & state) != 0
               : _pseudoClasses?.CustomClasses.Contains(pseudoClass) ?? false;

    /// <summary>Whether <paramref name="pseudoClass"/> (with leading colon) is set in the custom pseudo set.</summary>
    internal bool HasCustomPseudoClass(string pseudoClass) => _pseudoClasses?.ContainsCustom(pseudoClass) ?? false;

    /// <summary>The <see cref="PseudoClassMapping"/> write path — bypasses the B9 sanction's <em>caller</em> check, not its name rules.</summary>
    internal void SetPseudoClassFromMapping(string pseudoClass, bool active)
        => PseudoClasses.SetInternal(pseudoClass, active);

    /// <summary>The per-element S5 transition manager (§9.5) — null until a <see cref="Transition.TransitionsProperty"/> arms it.</summary>
    internal TransitionManager? Transitions { get; set; }

    /// <summary>The attach walk's styling step (B19 — ordered before the element's first measure).</summary>
    internal void OnStylingAttached() => Engine?.OnElementAttached(this);

    /// <summary>The detach walk's styling step (bottom-up; batched cookie retraction + state drop, SD15).</summary>
    internal void OnStylingDetached() => Engine?.OnElementDetached(this);

    /// <summary>Class-set mutation (one call per logical operation; <paramref name="changedClass"/> null = bulk replace).</summary>
    internal void OnStylingClassesChanged(string? changedClass) => Engine?.OnClassesChanged(this, changedClass);

    /// <summary>A custom pseudo-class flip from <see cref="PseudoClasses"/>.</summary>
    internal void OnStylingPseudoClassChanged(string pseudoClass, bool active) => Engine?.OnCustomPseudoClassChanged(this);

    /// <summary>A <see cref="Name"/> change post-construction (S130 — names participate in <c>#name</c> matching).</summary>
    internal void OnStylingNameChanged(string? oldName, string? newName) => Engine?.OnNameChanged(this, oldName, newName);

    /// <summary>An explicit <see cref="Style"/> assignment (old frames retract, new style arms — S133).</summary>
    internal void OnStylingExplicitStyleChanged(Style? oldStyle, Style? newStyle) => Engine?.OnExplicitStyleChanged(this);

    /// <summary>The owning scope's <see cref="Styles"/> collection changed (SD21 — coarse re-match with identity diff).</summary>
    internal void OnStylingStylesInvalidated() => Engine?.OnScopeStylesInvalidated(this);
}