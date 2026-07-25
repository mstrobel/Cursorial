using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A mutually-exclusive option in a group (design doc §12.7, CD27): a <see cref="ToggleButton"/>
/// whose check unchecks every peer in its group via <see cref="UIObject.SetCurrentValue{T}"/> (so a
/// peer's two-way binding survives — C216). The group is the same logical parent when
/// <see cref="GroupName"/> is null, else every same-named radio under the surface root. A checked
/// radio can't uncheck itself by clicking (C214); arrow keys move + check within the group (C215).
/// The default theme draws ASCII <c>( ) (*)</c> glyphs.
/// </summary>
public class RadioButton : ToggleButton
{
    private bool _focusedByAccessKey;

    /// <summary>The named radio group (default empty ⇒ logical-parent grouping). All same-named radios under the surface root are peers (CD27).</summary>
    public static readonly StyledProperty<string?> GroupNameProperty =
        UIProperty.Register<RadioButton, string?>(nameof(GroupName));

    /// <inheritdoc cref="GroupNameProperty"/>
    public string? GroupName { get => GetValue(GroupNameProperty); set => SetValue(GroupNameProperty, value); }

    /// <summary>
    /// A radio can't uncheck itself by clicking (CD27/C214): a click on a checked radio leaves it
    /// checked; a click on an unchecked radio checks it (which unchecks the group). Radios never cycle
    /// to indeterminate by click (C218) — only an explicit <see cref="ToggleButton.IsChecked"/> set
    /// can place the indeterminate state.
    /// </summary>
    protected override void OnToggle(InvokeMethod method = InvokeMethod.Programmatic)
    {
        if (IsChecked == true)
            return; // re-clicking a checked radio is a no-op (WPF)

        IsChecked = true;
    }

    /// <summary>Unchecks the group peers when this radio becomes checked (CD27).</summary>
    private protected override void OnIsCheckedChangedCore(bool? oldValue, bool? newValue)
    {
        base.OnIsCheckedChangedCore(oldValue, newValue);
        if (newValue == true)
            UncheckGroupPeers();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Modifiers != KeyModifiers.None)
            return;

        // Arrow keys move + check the next/previous radio in the group, consuming the event (C215).
        var forward = e.Key is Key.DownArrow or Key.RightArrow;
        var backward = e.Key is Key.UpArrow or Key.LeftArrow;
        if (!forward && !backward)
            return;

        if (FindGroupNeighbor(forward) is { } neighbor)
        {
            e.Handled = true;
            neighbor.Focus(FocusNavigationMethod.Directional);
            neighbor.IsChecked = true;
        }
    }

    private void UncheckGroupPeers()
    {
        foreach (var peer in EnumerateGroup())
        {
            if (!ReferenceEquals(peer, this) && peer.IsChecked == true)
                peer.SetCurrentValue(IsCheckedProperty, false); // SetCurrentValue preserves the peer's binding (C216)
        }
    }

    private RadioButton? FindGroupNeighbor(bool forward)
    {
        RadioButton? first = null;
        RadioButton? previous = null;
        var seenSelf = false;

        foreach (var peer in EnumerateGroup())
        {
            first ??= peer;

            if (seenSelf && forward)
                return peer; // the radio after self

            if (ReferenceEquals(peer, this))
            {
                if (!forward)
                    return previous; // the radio before self (null wraps below)
                seenSelf = true;
            }

            previous = peer;
        }

        // Wrap: forward from the last → first; backward from the first → last (previous holds the last).
        return forward ? first : previous;
    }

    // The group membership (CD27): same GroupName under the surface root, or same logical parent when
    // GroupName is null/empty. Document order (a stable depth-first logical walk).
    private IEnumerable<RadioButton> EnumerateGroup()
    {
        var groupName = GroupName;

        if (string.IsNullOrEmpty(groupName))
        {
            // Logical-parent grouping: peers are this radio's logical-parent's radio children.
            if (LogicalParent?.LogicalChildrenList is { } siblings)
            {
                foreach (var sibling in siblings)
                {
                    if (sibling is RadioButton radio && string.IsNullOrEmpty(radio.GroupName))
                        yield return radio;
                }
            }
            else
            {
                yield return this;
            }

            yield break;
        }

        // Named grouping spans the surface root (logical tree, depth-first, document order).
        var root = ResourceServices.LogicalRoot(this);
        foreach (var descendant in EnumerateLogicalDepthFirst(root))
        {
            if (descendant is RadioButton radio && string.Equals(radio.GroupName, groupName, StringComparison.Ordinal))
                yield return radio;
        }
    }

    private static IEnumerable<UIElement> EnumerateLogicalDepthFirst(UIElement node)
    {
        yield return node;
        if (node.LogicalChildrenList is { } children)
        {
            foreach (var child in children)
            {
                foreach (var descendant in EnumerateLogicalDepthFirst(child))
                    yield return descendant;
            }
        }
    }
    
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        _focusedByAccessKey = false;
        base.OnLostFocus(e);
    }

    protected override void OnAccessKey(AccessKeyEventArgs e)
    {
        if (_focusedByAccessKey)
        {
            _focusedByAccessKey = false;
            return;
        }

        base.OnAccessKey(e);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        if (e.Method is FocusNavigationMethod.AccessKey)
            _focusedByAccessKey = true;
    }
}
