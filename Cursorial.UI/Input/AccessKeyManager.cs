using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Terminal;

namespace Cursorial.UI.Input;

/// <summary>
/// The access-key engine (design doc §7.8, requirement 6): the flat case-folded registry, the
/// capability gate (<see cref="AccessKeyMode"/>), the Alt-held cue state machine with chord-flash
/// self-correction, activation-time scope resolution, single-match invocation and multi-match
/// focus-only cycling, and the menu-mode entry hooks (Alt tap / F10). P2 ships the manager core;
/// the visual cue (the underscore mnemonic) is wired through the built-in theme rule
/// <c>:access-keys AccessTextPresenter { ShowUnderline: true }</c> — the cue bit drives it via pure
/// styling. The remaining P9 UX (multi-match cycling polish, F10/menu surfaces) is deferred.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capability sourcing (ND23):</b> <see cref="OnCapabilitiesChanged"/> must receive the
/// <b>negotiated, undecorated</b> session snapshot — never the decorated pipeline view. A
/// <c>KeyReleaseSynthesizer</c>-claimed <c>DistinguishesKeyUpDown</c> must not qualify the gate,
/// because no real Alt Down ever arrives to synthesize from. S6 owns the sourcing (the explicit
/// fan-out call in startup/renegotiate sequences).
/// </para>
/// <para>
/// The cue itself is the <see cref="InteractionState.AccessKeyCue"/> bit on the active scope root
/// and window root (every surface root in <see cref="AccessKeyMode.AlwaysVisible"/>); rendering it
/// is pure styling downstream (<c>:access-keys</c>). S3 never touches a glyph. UI-thread only.
/// </para>
/// </remarks>
public sealed class AccessKeyManager
{
    /// <summary>
    /// The access-key cue flag an <c>AccessTextPresenter</c> reads to decide whether to underline its
    /// mnemonic grapheme (design doc §12.5; default <see langword="false"/>). The end-to-end cue
    /// <em>route</em> is live: the built-in theme ships
    /// <c>:access-keys AccessTextPresenter { ShowUnderline: true }</c>, whose <c>:access-keys</c>
    /// pseudo-class binds on the ancestor scope/window root this manager stamps with
    /// <see cref="InteractionState.AccessKeyCue"/>, flipping this flag (<c>AffectsRender</c>) on every
    /// presenter beneath the cue root — permanently in <see cref="AccessKeyMode.AlwaysVisible"/>,
    /// Alt-toggled in <see cref="AccessKeyMode.AltHeld"/>.
    /// </summary>
    public static readonly AttachedProperty<bool> ShowUnderlineProperty =
        UIProperty.RegisterAttached<AccessKeyManager, UIElement, bool>("ShowUnderline");

    static AccessKeyManager()
    {
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, ShowUnderlineProperty);
    }

    /// <summary>Reads the access-key underline cue flag on <paramref name="element"/>.</summary>
    public static bool GetShowUnderline(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ShowUnderlineProperty);
    }

    /// <summary>Sets the access-key underline cue flag on <paramref name="element"/>.</summary>
    public static void SetShowUnderline(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ShowUnderlineProperty, value);
    }

    private readonly UIDispatcher _dispatcher;
    private readonly FocusManager _focus;
    private readonly InteractionStateService _interactions;
    private readonly Dictionary<char, List<UIElement>> _registry = new();
    private readonly List<UIElement> _scopeStack = [];
    private readonly List<UIElement> _cueRoots = [];
    private readonly List<UIElement> _matchScratch = [];
    private readonly List<UIElement> _orderScratch = [];

    private AccessKeyMode _mode = AccessKeyMode.AlwaysVisible;
    private bool _cueActive;
    private bool _leftAltDown;
    private bool _rightAltDown;
    private bool _stickyCue;
    private bool _altWasChordless;
    private bool _sawStandaloneAlt;

    // The chord-flash latch (doc §7.8, N187) — observability-only: it records that the cue was
    // self-corrected ON by a bracket-less Alt chord. The re-flash condition itself keys on
    // !_sawStandaloneAlt (the "ever observed a real bracket" truth), not on this latch.
    private bool _bracketUnobserved;
    private IMainMenu? _mainMenu;

    internal AccessKeyManager(UIDispatcher dispatcher, FocusManager focus, InteractionStateService interactions)
    {
        _dispatcher = dispatcher;
        _focus = focus;
        _interactions = interactions;
    }

    /// <summary>The cue behavior derived from the last capability snapshot (the pinned gate formula).</summary>
    public AccessKeyMode Mode => _mode;

    /// <summary>
    /// Whether cues are showing — mirrors the <see cref="InteractionState.AccessKeyCue"/> bit on
    /// the active scope/window roots. Permanently true in
    /// <see cref="AccessKeyMode.AlwaysVisible"/>.
    /// </summary>
    public bool IsCueActive => _cueActive;

    /// <summary>
    /// Raised on menu-mode entry: a chordless Alt tap (AltHeld terminals) or an unhandled F10
    /// (ND19, both modes). The sticky cue is up when this fires; Esc, a second tap, activation, or
    /// a pointer-driven focus change exits.
    /// </summary>
    public event Action? EnterMenuMode;

    /// <summary>
    /// The installed main menu (S8's menu bar at P9) — notified on menu-mode entry alongside
    /// <see cref="EnterMenuMode"/>. One per application; last installation wins.
    /// </summary>
    public IMainMenu? MainMenu
    {
        get => _mainMenu;
        set
        {
            _dispatcher.VerifyAccess();
            _mainMenu = value;
        }
    }

    // ───────────────────────────── capability gate (doc §7.8) ─────────────────────────────

    /// <summary>
    /// Re-derives <see cref="Mode"/> from the <b>negotiated</b> snapshot —
    /// <c>(DistinguishesKeyUpDown &amp;&amp; ReportsRepeats) || Win32InputMode</c> — and
    /// unconditionally clears the Alt side bits, sticky flag, chord-flash latch, and cue
    /// (renegotiation parks the pump ~500 ms; an Alt Up released during the park vanishes with no
    /// <c>FocusEvent</c>). A mode flip to <see cref="AccessKeyMode.AlwaysVisible"/> sets the cue
    /// permanently; the reverse clears it pending real Alt.
    /// </summary>
    public void OnCapabilitiesChanged(TerminalCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _dispatcher.VerifyAccess();

        var keyboard = capabilities.Input.Keyboard;
        var mode = (keyboard.DistinguishesKeyUpDown && keyboard.ReportsRepeats) || capabilities.Input.Protocol.Win32InputMode
            ? AccessKeyMode.AltHeld
            : AccessKeyMode.AlwaysVisible;

        _leftAltDown = false;
        _rightAltDown = false;
        _stickyCue = false;
        _altWasChordless = false;
        _sawStandaloneAlt = false;
        _bracketUnobserved = false;
        DeactivateCue();

        _mode = mode;
        if (mode == AccessKeyMode.AlwaysVisible)
            ActivateCue(); // requirement 6's fallback: permanently visible (N169)
    }

    // ───────────────────────────── registry (flat, case-folded) ─────────────────────────────

    /// <summary>
    /// Registers <paramref name="target"/> under <paramref name="key"/> (folded lower-invariant).
    /// No scope is captured — scope membership resolves at activation time by walking the target's
    /// ancestor chain against the live scope stack, so attach-vs-<see cref="PushScope"/> ordering
    /// and reparenting are non-issues. Registration is a list: register/unregister pair up.
    /// </summary>
    public void Register(char key, UIElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _dispatcher.VerifyAccess();

        var folded = char.ToLowerInvariant(key);
        if (!_registry.TryGetValue(folded, out var targets))
            _registry[folded] = targets = [];

        if (!targets.Contains(target))
            targets.Add(target);
    }

    /// <summary>Removes <paramref name="target"/>'s registration under <paramref name="key"/> (no-op when absent).</summary>
    public void Unregister(char key, UIElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _dispatcher.VerifyAccess();

        if (_registry.TryGetValue(char.ToLowerInvariant(key), out var targets))
            targets.Remove(target);
    }

    // ───────────────────────────── scopes (activation-time resolution) ─────────────────────────────

    /// <summary>
    /// Pushes a popup/menu scope (LIFO — S4 calls on popup open at P7). While pushed, activation
    /// resolves only targets whose nearest scope is the stack top. The new root is stamped with the
    /// cue when cues are up (always, in <see cref="AccessKeyMode.AlwaysVisible"/>).
    /// </summary>
    public void PushScope(UIElement scopeRoot)
    {
        ArgumentNullException.ThrowIfNull(scopeRoot);
        _dispatcher.VerifyAccess();

        _scopeStack.Add(scopeRoot);
        if (_cueActive)
            StampCueRoot(scopeRoot);
    }

    /// <summary>Pops the topmost matching scope entry and clears its cue stamp.</summary>
    public void PopScope(UIElement scopeRoot)
    {
        ArgumentNullException.ThrowIfNull(scopeRoot);
        _dispatcher.VerifyAccess();

        var index = _scopeStack.LastIndexOf(scopeRoot);
        if (index >= 0)
            _scopeStack.RemoveAt(index);

        UnstampCueRoot(scopeRoot);
    }

    /// <summary>
    /// Window-activation hook (the P2 single-root harness; S4 per window at P7): stamps the newly
    /// active root with the cue when cues are up — in <see cref="AccessKeyMode.AlwaysVisible"/>
    /// that is how every window root acquires its permanent cue bit.
    /// </summary>
    public void OnWindowActivated(UIElement windowRoot)
    {
        ArgumentNullException.ThrowIfNull(windowRoot);
        _dispatcher.VerifyAccess();

        if (_cueActive)
            StampCueRoot(windowRoot);
    }

    // ───────────────────────────── dispatcher hooks (pre-stage + cluster) ─────────────────────────────

    /// <summary>
    /// Pre-stage 1a: a real (non-synthesized) standalone Alt Down — side bit, cue ON, chordless
    /// arm. A <b>repeat</b> Down (Kitty/Win32 repeat held modifiers) refreshes the side-bit/cue
    /// bookkeeping only and never touches the chordless flag (ND32 — a chord followed by held-Alt
    /// repeats then Up must stay chorded, not read as a tap).
    /// </summary>
    internal void OnAltDown(Key side, bool isRepeat = false)
    {
        if (side == Key.LeftAlt)
            _leftAltDown = true;
        else
            _rightAltDown = true;

        if (!isRepeat)
        {
            _sawStandaloneAlt = true;
            _bracketUnobserved = false; // a real bracket clears the chord-flash latch (N187)
            _altWasChordless = true;
        }

        ActivateCue();
    }

    /// <summary>
    /// Pre-stage 1a: a real standalone Alt Up. With both sides released, a chordless bracket is an
    /// <b>Alt tap</b>: sticky cue + menu-mode entry (or the sticky toggle off on a second tap); a
    /// chorded bracket drops the cue unless sticky.
    /// </summary>
    internal void OnAltUp(Key side)
    {
        if (side == Key.LeftAlt)
            _leftAltDown = false;
        else
            _rightAltDown = false;

        if (_leftAltDown || _rightAltDown)
            return; // cue stays until both sides are up (N173)

        if (_altWasChordless)
        {
            if (_stickyCue)
            {
                // Second tap toggles menu mode off (N178).
                _stickyCue = false;
                ClearCueForAltHeld();
            }
            else
            {
                _stickyCue = true; // Alt tap: the cue stays up (N171)
                RaiseEnterMenuMode();
            }
        }
        else if (!_stickyCue)
        {
            ClearCueForAltHeld(); // chorded bracket: cue off at Up (N172)
        }
    }

    /// <summary>
    /// Pre-stage 1b/1c for a non-Alt key Down: chordless tracking, stale-bracket inference (an
    /// event lacking the Alt bit while a side bit is set means the Up was lost — the terminal's
    /// per-event modifier state is ground truth), and the sticky-cue Esc consume (menu mode is
    /// modal to Esc — N177).
    /// </summary>
    /// <returns>Whether the event was consumed (sticky Esc) — the dispatcher returns handled without routing.</returns>
    internal bool OnPreStageKeyDown(KeyEvent key)
    {
        if (_leftAltDown || _rightAltDown)
        {
            if ((key.Modifiers & KeyModifiers.Alt) != 0)
                _altWasChordless = false; // a chord rode the bracket
            else
                OnStaleAltInferred(); // N174
        }

        if (_stickyCue && key.Key == Key.Escape)
        {
            _stickyCue = false;
            if (!_leftAltDown && !_rightAltDown)
                ClearCueForAltHeld();

            return true; // consumed — the focused element never sees it
        }

        return false;
    }

    /// <summary>Pre-stage 1b: the bracket's Up was lost — clear side bits (+ cue unless sticky).</summary>
    internal void OnStaleAltInferred()
    {
        _leftAltDown = false;
        _rightAltDown = false;
        _altWasChordless = false;
        if (!_stickyCue)
            ClearCueForAltHeld();
    }

    /// <summary>FocusManager wire (doc §7.7): a pointer-driven focus change exits menu mode (N179).</summary>
    internal void OnFocusChanged(FocusNavigationMethod method)
    {
        if (method != FocusNavigationMethod.Pointer || !_stickyCue)
            return;

        _stickyCue = false;
        if (!_leftAltDown && !_rightAltDown)
            ClearCueForAltHeld();
    }

    /// <summary>
    /// ND24 ① — terminal focus-out clears side bits, sticky, and cue <b>unconditionally</b>
    /// (Alt+Tab swallows the Up; the bracket can never be trusted across a focus loss).
    /// </summary>
    internal void OnTerminalFocusLost()
    {
        _leftAltDown = false;
        _rightAltDown = false;
        _stickyCue = false;
        _altWasChordless = false;
        ClearCueForAltHeld();
    }

    /// <summary>
    /// Detach backstop (doc §7.10): registrations, scope-stack entries, and cue stamps drop with
    /// the element. O(registry buckets) per detached element — acceptable while registries are
    /// small and flat; revisit (per-element registration index) before P9's menu trees if teardown
    /// of large registered subtrees ever profiles hot.
    /// </summary>
    internal void OnElementDetached(UIElement element)
    {
        if (_registry.Count > 0)
        {
            foreach (var targets in _registry.Values)
                targets.Remove(element);
        }

        if (_scopeStack.Count > 0)
            _scopeStack.Remove(element);

        if (_cueRoots.Count > 0 && _cueRoots.Remove(element))
            element.SetInteractionStateInternal(InteractionState.AccessKeyCue, false);
    }

    // ───────────────────────────── activation (the step-5 unhandled tail) ─────────────────────────────

    /// <summary>
    /// Whether an unmodified <c>Key.Character</c> Down arriving <b>now</b> would enter the
    /// activation path (physical Alt held or sticky menu mode). The dispatcher samples this
    /// <b>before</b> the pre-stage, because the stale-Alt inference may close the bracket on the
    /// very key being dispatched — the user typed the letter while cues were visibly up, so it
    /// must still activate (N182 vs N174).
    /// </summary>
    internal bool IsUnmodifiedActivationWindowOpen => _leftAltDown || _rightAltDown || _stickyCue;

    /// <summary>
    /// The dispatcher's unhandled tail (doc §7.5 step 5). Handles F10 menu-mode entry (ND19),
    /// chord-flash self-correction, the menu-mode unmatched-key swallow, and activation: one match
    /// invokes (<c>IsMultiMatch: false</c>); several cycle focus through the matches in tab order —
    /// <b>multi-match focuses, never invokes</b> (ND18).
    /// </summary>
    /// <param name="key">The unhandled key-down device record.</param>
    /// <param name="cueWindowWasOpen">
    /// <see cref="IsUnmodifiedActivationWindowOpen"/> sampled before the pre-stage ran for this key.
    /// </param>
    /// <returns>Whether the event was handled.</returns>
    internal bool ProcessKeyDown(KeyEvent key, bool cueWindowWasOpen)
    {
        // F10 ≡ Alt tap (ND19) — both modes; a focused element handling F10 in step 4 already won.
        if (key is { Key: Key.F10, Modifiers: KeyModifiers.None })
        {
            _stickyCue = true;
            ActivateCue();
            RaiseEnterMenuMode();
            return true;
        }

        if (key.Key != Key.Character || key.Text.IsEmpty)
            return false;

        var modifiers = key.Modifiers;
        var altChord = modifiers == KeyModifiers.Alt; // exact — AltGr (Ctrl+Alt) stays out
        var anyAltDown = _leftAltDown || _rightAltDown;

        // Eligibility (doc §7.8): the Alt chord works everywhere; unmodified keys only while the
        // cue window is up — physical Alt held or sticky menu mode, including a bracket the
        // pre-stage's stale inference closed on this very key (N182).
        if (!altChord && !((cueWindowWasOpen || anyAltDown || _stickyCue) && modifiers == KeyModifiers.None))
            return false;

        // Chord-flash self-correction (doc §7.8): a family-matched terminal that never delivers
        // the bracket would leave the cue permanently invisible — an Alt chord with no bracket
        // ever observed flips the cue ON (sticky) before processing and latches.
        if (_mode == AccessKeyMode.AltHeld && altChord && !anyAltDown && !_sawStandaloneAlt)
        {
            _bracketUnobserved = true;
            _stickyCue = true;
            ActivateCue();
        }

        var folded = char.ToLowerInvariant(key.Text.Span[0]);
        CollectEligibleMatches(folded);

        if (_matchScratch.Count == 0)
        {
            // Menu-mode bonk (N186): typing must not leak into TextInput while cues are up.
            return _stickyCue;
        }

        if (_matchScratch.Count == 1)
        {
            var target = _matchScratch[0];
            _matchScratch.Clear();

            // Activation clears sticky; the cue drops unless physical Alt is still held.
            _stickyCue = false;
            if (!anyAltDown)
                ClearCueForAltHeld();

            InvokeAccessKey(target, folded, isMultiMatch: false);
            return true;
        }

        // Multi-match: focus the first match after the currently focused element in tab order
        // (wrap-around); the cue stays up; nothing invokes (ND18).
        SortMatchesByTabOrder();
        var focusedIndex = _focus.FocusedElement is { } focused ? _matchScratch.IndexOf(focused) : -1;
        var next = _matchScratch[(focusedIndex + 1) % _matchScratch.Count];
        _matchScratch.Clear();

        _focus.SetFocus(next, FocusNavigationMethod.AccessKey);
        InvokeAccessKey(next, folded, isMultiMatch: true);
        return true;
    }

    // ───────────────────────────── internals ─────────────────────────────

    /// <summary>Test observability for the chord-flash latch (N187).</summary>
    internal bool BracketUnobservedInternal => _bracketUnobserved;

    /// <summary>Test observability for the sticky (menu-mode) flag.</summary>
    internal bool StickyCueInternal => _stickyCue;

    private void RaiseEnterMenuMode()
    {
        _mainMenu?.OnEnterMenuMode();
        EnterMenuMode?.Invoke();
    }

    private static void InvokeAccessKey(UIElement target, char key, bool isMultiMatch)
    {
        if (target is IAccessKeyTarget accessKeyTarget)
            accessKeyTarget.OnAccessKey(new AccessKeyEventArgs(key, isMultiMatch, target));
        else if (!isMultiMatch)
            target.Focus(FocusNavigationMethod.AccessKey); // plain-element fallback: at least move focus
    }

    private void CollectEligibleMatches(char folded)
    {
        _matchScratch.Clear();
        if (!_registry.TryGetValue(folded, out var targets) || targets.Count == 0)
            return;

        // Activation-time scope resolution: the live stack top, else the active window root.
        var scope = _scopeStack.Count > 0 ? _scopeStack[^1] : _focus.ActiveRoot;
        if (scope is null)
            return;

        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (IsEligible(target) && ReferenceEquals(ResolveScope(target), scope))
                _matchScratch.Add(target);
        }
    }

    private static bool IsEligible(UIElement target)
        => target.IsAttachedToTree
           && (target is IAccessKeyTarget accessKeyTarget
               ? accessKeyTarget.IsAccessKeyEligible
               : target is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true });

    /// <summary>
    /// The target's scope: walking its ancestor chain (route walk — <c>VisualParent ??
    /// LogicalParent</c>, self-inclusive), the first node that is a live scope-stack root or the
    /// active window root.
    /// </summary>
    private UIElement? ResolveScope(UIElement target)
    {
        // O(ancestors × scope-stack) per eligible match — fine while the stack is O(1) open
        // popups/menus. If activation ever shows hot in profiles (deep menu trees at P9), convert
        // _scopeStack lookups to a HashSet alongside the list.
        var activeRoot = _focus.ActiveRoot;
        for (var node = target; node is not null; node = node.VisualParent ?? node.LogicalParent)
        {
            if (_scopeStack.Contains(node) || ReferenceEquals(node, activeRoot))
                return node;
        }

        return null;
    }

    /// <summary>Stable (TabIndex, document order) sort of the match scratch via one DFS over the active scope.</summary>
    private void SortMatchesByTabOrder()
    {
        var scope = _scopeStack.Count > 0 ? _scopeStack[^1] : _focus.ActiveRoot;
        if (scope is null || _matchScratch.Count < 2)
            return;

        _orderScratch.Clear();
        AppendMatchesInDocumentOrder(scope, _matchScratch, _orderScratch);
        if (_orderScratch.Count != _matchScratch.Count)
            return; // defensive — keep registration order when the DFS missed targets

        // Stable insertion sort by TabIndex over the document-ordered list (n is tiny).
        for (var i = 1; i < _orderScratch.Count; i++)
        {
            var element = _orderScratch[i];
            var tabIndex = element.GetValue(UIElement.TabIndexProperty);
            var j = i - 1;
            while (j >= 0 && _orderScratch[j].GetValue(UIElement.TabIndexProperty) > tabIndex)
            {
                _orderScratch[j + 1] = _orderScratch[j];
                j--;
            }

            _orderScratch[j + 1] = element;
        }

        _matchScratch.Clear();
        _matchScratch.AddRange(_orderScratch);
        _orderScratch.Clear();
    }

    private static void AppendMatchesInDocumentOrder(UIElement parent, List<UIElement> matches, List<UIElement> into)
    {
        if (matches.Contains(parent))
            into.Add(parent);

        if (parent.VisualChildrenList is not { } children)
            return;

        for (var i = 0; i < children.Count; i++)
            AppendMatchesInDocumentOrder(children[i], matches, into);
    }

    // ───────────────────────────── cue writes ─────────────────────────────

    /// <summary>
    /// Turns the cue on: <see cref="InteractionState.AccessKeyCue"/> on the window root and the
    /// active scope root (every known surface root in <see cref="AccessKeyMode.AlwaysVisible"/>),
    /// in one interaction batch.
    /// </summary>
    private void ActivateCue()
    {
        _cueActive = true;

        using var batch = _interactions.BeginUpdate();
        if (_focus.ActiveRoot is { } windowRoot)
            StampCueRoot(windowRoot);

        if (_mode == AccessKeyMode.AlwaysVisible)
        {
            for (var i = 0; i < _scopeStack.Count; i++)
                StampCueRoot(_scopeStack[i]);
        }
        else if (_scopeStack.Count > 0)
        {
            StampCueRoot(_scopeStack[^1]);
        }
    }

    /// <summary>Turns the cue off everywhere it was stamped (never called while AlwaysVisible holds the cue).</summary>
    private void DeactivateCue()
    {
        _cueActive = false;
        if (_cueRoots.Count == 0)
            return;

        using var batch = _interactions.BeginUpdate();
        for (var i = 0; i < _cueRoots.Count; i++)
            _cueRoots[i].SetInteractionStateInternal(InteractionState.AccessKeyCue, false);

        _cueRoots.Clear();
    }

    private void ClearCueForAltHeld()
    {
        if (_mode == AccessKeyMode.AltHeld)
            DeactivateCue(); // AlwaysVisible never clears
    }

    private void StampCueRoot(UIElement root)
    {
        if (_cueRoots.Contains(root))
            return;

        _cueRoots.Add(root);
        root.SetInteractionStateInternal(InteractionState.AccessKeyCue, true);
    }

    private void UnstampCueRoot(UIElement root)
    {
        if (_cueRoots.Remove(root))
            root.SetInteractionStateInternal(InteractionState.AccessKeyCue, false);
    }
}
