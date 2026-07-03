using Cursorial.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// The KeyTip overlay controller (keytips-design §5) — the FSM riding <see cref="AccessKeyManager"/>'s Alt cue.
/// It arms on <see cref="AccessKeyManager.CueActivated"/> (in <see cref="AccessKeyMode.AltHeld"/>), shows amber badge
/// levels over the discovered bar surfaces, walks a multi-letter prefix drill (tab → group → control), and tears down
/// on <see cref="AccessKeyManager.CueDeactivated"/> (which subsumes every exit — chorded Alt release, second tap, Esc,
/// pointer focus, terminal focus-out, renegotiate), a window-activation change, a leaf activation, or Esc-at-top.
/// Keys reach it via <see cref="InputDispatcher.PreProcessInput"/> (never the focused element while active).
/// </summary>
public sealed class KeyTipController : IKeyTipController, IKeyTipLayoutHook
{
    private const int MaxParkRetries = 8; // bound the park-retry for a reveal whose subtree never realizes

    // Modifiers that make a letter NOT a KeyTip drill key. Alt is deliberately EXCLUDED: while the user holds Alt,
    // every letter carries the Alt modifier (Alt+H), and that IS the drill gesture — so only Ctrl/Super/Hyper/Meta
    // chords fall through (a global Ctrl+S still fires). Shift is allowed (shifted letters). The Alt-released
    // "sticky" flow sends unmodified letters, which also pass.
    private const KeyModifiers DrillExcludeMask =
        KeyModifiers.Control | KeyModifiers.Super | KeyModifiers.Hyper | KeyModifiers.Meta;

    private readonly UIApplication _app;
    private readonly AccessKeyManager _accessKeys;
    private readonly List<KeyTipLevel> _stack = [];

    private Canvas _layer = new();
    private bool _isActive;
    private UIElement? _restoreFocus;
    private bool _exitViaActivation;
    private int _levelGeneration;

    // A parked next-level build: a drill's reveal triggered a relayout, so the level is built at the post-layout hook.
    private Func<KeyTipLevel?>? _parkedBuild;
    private Action? _parkedRetract;
    private int _parkedGeneration;
    private int _parkedRetries;

    internal KeyTipController(UIApplication app)
    {
        _app = app;
        _accessKeys = app.AccessKeys;

        _accessKeys.CueActivated += OnCueActivated;
        _accessKeys.CueDeactivated += OnCueDeactivated;
        app.InputDispatcher.PreProcessInput += OnPreProcessInput;

        if (app.WindowManager is { } wm)
            wm.ActiveWindowChanged += OnActiveWindowChanged;
    }

    /// <inheritdoc/>
    public bool IsActive => _isActive;

    // ───────────────────────────── triggers (keytips-design §3) ─────────────────────────────

    private void OnCueActivated()
    {
        if (_isActive || _accessKeys.Mode != AccessKeyMode.AltHeld)
            return;

        Enter();
    }

    private void OnCueDeactivated() => Exit();

    private void OnActiveWindowChanged(object? sender, EventArgs e) => Exit();

    // ───────────────────────────── key interception (keytips-design §2) ─────────────────────────────

    private void OnPreProcessInput(object? sender, InputEventArgs e)
    {
        if (!_isActive || e is not KeyEventArgs k || k.Device.Kind != KeyEventKind.Down)
            return;

        // Consume drill letters + digits while active; Ctrl/Super/… chords fall through so global gestures (Ctrl+S)
        // still fire. Escape is handled EARLIER by AccessKeyManager's Alt pre-stage via TryPopLevel (it runs before
        // this seam), so there is no Escape branch here.
        if (TryGetDrillChar(k, out var c))
        {
            TypeChar(c);
            e.Handled = true;
        }
    }

    // The character a key contributes to the prefix, or false to let it fall through. A plain character key (a letter,
    // or a number-row digit — the QAT badges) OR a NUMPAD digit (keyboard-first QAT users press the numpad), with only
    // Alt/Shift allowed (Alt is the held-mode drill modifier; a Ctrl/Super/… chord is a global gesture, not a drill).
    private static bool TryGetDrillChar(KeyEventArgs k, out char c)
    {
        c = '\0';
        if ((k.Modifiers & DrillExcludeMask) != KeyModifiers.None)
            return false;

        if (k is { Key: Key.Character, Text.Length: 1 })
        {
            c = k.Text.Span[0];
            return true;
        }

        if (k.Key is >= Key.Numpad0 and <= Key.Numpad9)
        {
            c = (char)('0' + (k.Key - Key.Numpad0)); // Numpad1 → '1', matching the QAT digit badges
            return true;
        }

        return false;
    }

    // ───────────────────────────── FSM (keytips-design §5) ─────────────────────────────

    /// <inheritdoc/>
    public void Enter()
    {
        if (_isActive)
            return;

        // Build level 0 first; if nothing derived a badge, don't enter (leave the inline cue as-is).
        var level0 = BuildRootLevel();
        if (level0.Entries.Count == 0)
            return;

        _restoreFocus = _app.FocusManager.FocusedElement;
        _exitViaActivation = false;
        _isActive = true;
        _stack.Clear();
        _stack.Add(level0);
        _levelGeneration++;

        _accessKeys.SuspendCue();                // badges are the sole cue while active (drops any inline underline)
        _layer = new Canvas();
        _app.WindowManager?.ShowKeyTipOverlay(_layer);
        ShowLevel(level0);
    }

    /// <inheritdoc/>
    public void Exit()
    {
        if (!_isActive)
            return;

        _isActive = false;
        _parkedBuild = null;
        _parkedRetract = null;
        _levelGeneration++;                      // orphan any parked build/pop in flight

        _app.WindowManager?.HideKeyTipOverlay();
        _layer.Children.Clear();
        _stack.Clear();

        // A leaf activation ENDS Alt/menu mode (dismiss the cue entirely — no lingering inline underlines, and no
        // sticky-cue Esc-consume eating the first Escape a just-opened surface like Backstage should get); any other
        // exit (cue-off, Esc-at-top, window change) just un-suppresses (the cue is already off).
        if (_exitViaActivation)
            _accessKeys.DismissCue();
        else
            _accessKeys.ResumeCue();

        if (!_exitViaActivation)
            RestoreFocus();

        _restoreFocus = null;
        _exitViaActivation = false;
    }

    // Typing a badge letter: filter the current level by the case-folded prefix.
    private void TypeChar(char c)
    {
        if (_stack.Count == 0)
            return;

        var level = _stack[^1];
        level.Typed.Append(char.ToUpperInvariant(c));
        var prefix = level.Typed.ToString();

        var viableCount = 0;
        KeyTipEntry? complete = null;
        foreach (var entry in level.Entries)
        {
            if (!entry.KeyTip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            viableCount++;
            if (entry.KeyTip.Length == prefix.Length)
                complete = entry;
        }

        if (viableCount == 0)
        {
            Bonk(level);                          // no match: revert the char (never leaks — already consumed)
            return;
        }

        if (viableCount == 1 && complete is not null)
        {
            Commit(complete);
            return;
        }

        // Still ambiguous (multi-char keytips): dim the matched prefix on the viable badges, hide the rest.
        foreach (var entry in level.Entries)
        {
            var isViable = entry.KeyTip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            entry.Hidden = !isViable;
            if (entry.Badge is not { } badge)
                continue;

            badge.Visibility = isViable ? Visibility.Visible : Visibility.Collapsed;
            if (isViable)
                badge.MatchedPrefixLength = prefix.Length;
        }
    }

    private static void Bonk(KeyTipLevel level)
    {
        if (level.Typed.Length > 0)
            level.Typed.Length--;                 // revert the non-matching char (visual flash deferred to v2)
    }

    private void Commit(KeyTipEntry entry)
    {
        if (entry.Kind == KeyTipTargetKind.Activate)
        {
            _exitViaActivation = true;
            entry.Activate?.Invoke();
            Exit();
            return;
        }

        // A drill: perform the reveal now, park the next-level build for the post-layout hook (the revealed subtree /
        // opened surface may realize over ≥1 frames — keytips-design §9).
        entry.Reveal?.Invoke();
        _parkedBuild = entry.BuildNext;
        _parkedRetract = entry.Retract;
        _parkedGeneration = ++_levelGeneration;
        _parkedRetries = 0;
    }

    /// <inheritdoc/>
    public bool TryPopLevel()
    {
        // Esc first-refusal (from the Alt pre-stage): pop one level when there is one to back out of; otherwise let
        // the normal cue-off path exit the whole overlay (return false — the caller does NOT consume the key).
        if (!_isActive || _stack.Count <= 1)
            return false;

        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        top.Retract?.Invoke();

        _parkedBuild = null;                      // cancel any parked deeper build
        _parkedRetract = null;
        _levelGeneration++;

        ShowLevel(_stack[^1]);                    // re-place the parent level's badges
        return true;
    }

    // ───────────────────────────── layout hook (keytips-design §9) ─────────────────────────────

    /// <inheritdoc/>
    public void CompletePendingLayout()
    {
        if (!_isActive)
            return;

        // 1) Build a parked next level now that its reveal's subtree / surface has realized (bounded retry).
        if (_parkedBuild is { } build && _parkedGeneration == _levelGeneration)
        {
            var level = build();
            if (level is { Entries.Count: > 0 })
            {
                level.Retract = _parkedRetract;
                _stack.Add(level);
                _parkedBuild = null;
                _parkedRetract = null;
                ShowLevel(level);
            }
            else if (++_parkedRetries >= MaxParkRetries)
            {
                // Give up: the reveal never realized a target (e.g. a tab with no groups). Undo any reversible
                // reveal and RE-SHOW the drilled-from level so it stays matchable — otherwise its Typed prefix
                // (the drill letter) is left dirty and every sibling letter would bonk (the level would brick).
                _parkedBuild = null;
                _parkedRetract?.Invoke();
                _parkedRetract = null;
                ShowLevel(_stack[^1]);
            }
        }

        // 2) Re-anchor the shown level's badges to their targets' final screen cells — this is what keeps badges glued
        // to a ribbon that MOVES (a panel above it grows) or SCROLLS (a ScrollViewer slide, reflected through
        // TranslateToScreen's ChildScrollOffset walk). Re-arrange the overlay in the SAME frame so a scroll doesn't
        // leave the badges trailing their targets by a frame.
        if (_stack.Count > 0)
        {
            PlaceBadges(_stack[^1]);
            _app.WindowManager?.RunKeyTipOverlayLayout();
        }
    }

    // ───────────────────────────── badge overlay ─────────────────────────────

    // Rebuilds the badge layer for a level: one badge per entry, placed at its target's screen cell. Clears the
    // level's typed prefix so any (re)display — the initial show, an Esc-pop back to a parent, or a give-up re-show —
    // starts from a clean prefix (else a drilled-then-popped level would refuse every sibling letter — the HIGH
    // audit finding: Typed is otherwise only ever appended / bonk-decremented, never reset).
    private void ShowLevel(KeyTipLevel level)
    {
        level.Typed.Clear();
        _layer.Children.Clear();

        foreach (var entry in level.Entries)
        {
            entry.Hidden = false;
            var badge = new KeyTipBadge { KeyTipText = entry.KeyTip };
            entry.Badge = badge;
            _layer.Children.Add(badge);
        }

        PlaceBadges(level);
    }

    // Positions each visible badge at its target's screen cell. A badge is HIDDEN (not stranded at some bogus cell)
    // when: the prefix filter dropped it (entry.Hidden); its target is no longer on a live rendered surface — it
    // detached (a page navigation) or moved into a closed popup (a toolbar-overflowed control), so it has no real
    // position and would otherwise land at the ribbon origin; or its anchor scrolled OFF the viewport (a tab scrolled
    // above a ScrollViewer's top).
    private void PlaceBadges(KeyTipLevel level)
    {
        var viewport = _app.WindowManager?.ScreenSize ?? default;

        foreach (var entry in level.Entries)
        {
            if (entry.Badge is not { } badge)
                continue;

            var onLiveSurface = entry.Target.IsEffectivelyVisible
                                && (_app.WindowManager is not { } wm || wm.SurfaceForElement(entry.Target) is not null);

            var (anchorColumn, anchorRow) = AnchorCell(entry);
            var (column, row) = entry.Target.TranslateToScreen(anchorColumn, anchorRow);
            var onScreen = column >= 0 && row >= 0 && column < viewport.Columns && row < viewport.Rows;

            var show = !entry.Hidden && onLiveSurface && onScreen;
            badge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
                continue;

            // Keep a wide (multi-letter) badge from overflowing the right edge; the anchor itself is on-screen.
            Canvas.SetLeft(badge, Math.Min(column, Math.Max(0, viewport.Columns - 1)));
            Canvas.SetTop(badge, row);
        }
    }

    // The target-local cell a badge anchors to (keytips-design §8). BottomLeading (the v1 default, "at the end of its
    // command" on the bottom row) collapses to (0,0) for a single-row control; TopLeading pins the top-left corner;
    // BottomCenter centers on the bottom edge.
    private static (int Column, int Row) AnchorCell(KeyTipEntry entry)
    {
        var bounds = entry.Target.Bounds;
        var lastRow = Math.Max(0, bounds.Rows - 1);
        return entry.Anchor switch
        {
            KeyTipAnchor.TopLeading => (0, 0),
            KeyTipAnchor.BottomCenter => (Math.Max(0, bounds.Columns / 2), lastRow),
            _ => (0, lastRow), // BottomLeading
        };
    }

    // ───────────────────────────── discovery / activation / focus ─────────────────────────────

    // Level 0: discover the bar-surface hosts under every non-overlay surface root (the app root surface AND any
    // window — a ribbon may live in a top-level root, not only a Window) and aggregate their root entries.
    private KeyTipLevel BuildRootLevel()
    {
        var hosts = new List<IKeyTipHost>();
        foreach (var root in ActiveSurfaceRoots())
            KeyTipTree.CollectHosts(root, hosts);

        var builder = new KeyTipLevelBuilder();
        foreach (var host in hosts)
            host.BuildRootLevel(builder);

        return builder.Build();
    }

    private IEnumerable<UIElement> ActiveSurfaceRoots()
    {
        if (_app.WindowManager is not { } wm)
        {
            if (_app.RootElement is { } root)
                yield return root;
            yield break;
        }

        foreach (var surface in wm.Surfaces)
        {
            if (surface.IsPopup || surface.IsHitTestTransparent)
                continue;                         // skip popups / tooltips / the KeyTip overlay itself

            yield return surface.Root;
        }
    }

    /// <summary>Activates a KeyTip leaf: focus it (if focusable), then invoke its access-key handler — the same
    /// single-match path <see cref="AccessKeyManager"/> uses, so there is no duplicated activation logic.</summary>
    internal static void ActivateLeaf(UIElement target)
    {
        var app = UIApplication.Current;

        if (target.Focusable && app is not null)
            app.FocusManager.SetFocus(target, FocusNavigationMethod.AccessKey);

        if (target is IAccessKeyTarget { IsAccessKeyEligible: true } accessKeyTarget)
            accessKeyTarget.OnAccessKey(new AccessKeyEventArgs('\0', isMultiMatch: false, target));
    }

    /// <summary>The active level-stack depth (test observability of the drill FSM: 0 = inactive, 1 = root level).</summary>
    internal int LevelDepthForTests => _stack.Count;

    /// <summary>The live badge for <paramref name="target"/> in the current level, or null (test observability of
    /// badge placement / scroll tracking).</summary>
    internal KeyTipBadge? BadgeForTargetForTests(UIElement target)
    {
        if (_stack.Count == 0)
            return null;

        foreach (var entry in _stack[^1].Entries)
        {
            if (ReferenceEquals(entry.Target, target))
                return entry.Badge;
        }

        return null;
    }

    private void RestoreFocus()
    {
        // Focus was never moved during a drill (badges are the surrogate), so the snapshot is still valid; restore it.
        if (_restoreFocus is { IsAttachedToTree: true } snapshot)
            _app.FocusManager.SetFocus(snapshot, FocusNavigationMethod.Restore);
    }
}
