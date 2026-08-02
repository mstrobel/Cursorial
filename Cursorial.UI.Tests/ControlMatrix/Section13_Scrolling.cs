using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §13 — ScrollViewer + ScrollBar (C3, inversion 5; rows C219–C236). Offset DirectProperty two-way
/// mirrors of the SCP styled offsets, wheel/keyboard scrolling, Auto visibility, and the ScrollBar
/// parts/track/thumb/arrows. One test per row.
/// </summary>
public sealed class Section13_Scrolling
{
    // ───────────────────────────── helpers ─────────────────────────────

    private static UIHeadlessHost Show(UIElement root)
    {
        var host = UIHeadlessHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    /// <summary>A rigid content block of a fixed cell size (the scrollable extent source).</summary>
    private sealed class Block(int columns, int rows) : UIElement
    {
        protected override Size MeasureOverride(Size availableSize) => new(columns, rows);
        protected override Size ArrangeOverride(Size finalSize) => new(columns, rows);
    }

    /// <summary>A ScrollViewer with the default theme, sized to a viewport, over a tall content block.</summary>
    private static ScrollViewer TallScroller(int viewportColumns = 20, int viewportRows = 10, int contentRows = 100)
    {
        return new ScrollViewer
        {
            Width = viewportColumns,
            Height = viewportRows,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new Block(viewportColumns - 1, contentRows) // -1 leaves room for the vertical bar
        };
    }

    // The default ScrollViewer template's vertical bar (PART_VerticalScrollBar) — present in the tree
    // regardless of its Visibility, so a Collapsed bar is still found here (CD28).
    private static ScrollBar VBar(ScrollViewer sv)
        => (ScrollBar)sv.TemplateInstance!.NameScope.Find("PART_VerticalScrollBar")!;

    private static MouseEvent Wheel(int column, int row, int deltaY, int deltaX = 0, KeyModifiers modifiers = KeyModifiers.None) => new()
    {
        Kind = MouseEventKind.Wheel,
        Position = new CellPosition(column, row),
        Button = MouseButton.None,
        ButtonsHeld = MouseButtons.None,
        Modifiers = modifiers,
        WheelDeltaY = deltaY,
        WheelDeltaX = deltaX,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    // ───────────────────────────── 13.1 ScrollViewer offsets / wheel / keyboard ─────────────────────────────

    [Fact] // C219
    public void C219_ScrollViewerProperties()
    {
        var sv = new ScrollViewer();
        // Horizontal defaults to Disabled (WPF parity): a scroll-enabled axis measures content at
        // unbounded width, which would silently disarm TextWrapping/TextTrimming in every default
        // ScrollViewer. Controls that want horizontal scrolling opt in explicitly.
        Assert.Equal(ScrollBarVisibility.Disabled, sv.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, sv.VerticalScrollBarVisibility);
        Assert.Equal(0, sv.HorizontalOffset);
        Assert.Equal(0, sv.VerticalOffset);
        Assert.IsAssignableFrom<ContentControl>(sv);
    }

    [Fact] // review #12 regression: the wrap-kill
    public void C219b_DefaultScrollViewer_WrappingTextStillWraps()
    {
        // With horizontal scrolling enabled by default, the SCP measured content at UNBOUNDED
        // width and a wrapping TextBlock never wrapped — it became one long line behind a
        // horizontal scrollbar, in every plain ScrollViewer. Pin the default-config behavior:
        // wrapping content wraps.
        var text = new TextBlock
        {
            Text = string.Join(' ', Enumerable.Repeat("word", 30)), // 30×5 cells ≫ any viewport line
            TextWrapping = WrapMode.WordWrap,
            TextTrimming = TextTrimming.None
        };

        var sv = new ScrollViewer
        {
            Width = 20,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Content = text
        };

        using var host = Show(sv);

        Assert.True(text.Bounds.Rows > 1, $"expected wrapped rows, got {text.Bounds.Rows}");
        Assert.StartsWith("word word", host.GetRowText(0).TrimStart());
    }

    [Fact] // C220
    public void C220_VerticalOffset_MirrorsScpStyledOffset()
    {
        var sv = TallScroller();
        using var host = Show(sv);

        sv.VerticalOffset = 5;
        host.RunFrame();

        Assert.Equal(5, sv.VerticalOffset);
        Assert.Equal(5, sv.Presenter!.ScrollOffsetRow); // mirrored to the SCP styled offset

        // Setting the SCP side mirrors back to the DirectProperty (two-way).
        sv.Presenter!.ScrollOffsetRow = 8;
        host.RunFrame();
        Assert.Equal(8, sv.VerticalOffset);
    }

    [Fact] // C221
    public void C221_StyledScpOffset_IsTheSourceOfTruth()
    {
        var sv = TallScroller();
        using var host = Show(sv);

        // The SCP offset is a styled (AffectsComposite) property — the ScrollViewer DirectProperty
        // reflects it (the doc supersedes the spec's non-animatable stance, CD28).
        sv.Presenter!.SetValue(ScrollContentPresenter.ScrollOffsetRowProperty, 7);
        host.RunFrame();
        Assert.Equal(7, sv.VerticalOffset);
    }

    [Fact] // C222
    public void C222_WheelScrollsThreeLinesPerNotch()
    {
        var sv = TallScroller();
        using var host = Show(sv);
        var dispatcher = host.Application.InputDispatcher;

        dispatcher.ProcessEvent(Wheel(5, 5, deltaY: -120)); // one notch down ⇒ 3 lines
        host.RunFrame();
        Assert.Equal(3, sv.VerticalOffset);

        dispatcher.ProcessEvent(Wheel(5, 5, deltaY: -240)); // two notches ⇒ 6 more lines
        host.RunFrame();
        Assert.Equal(9, sv.VerticalOffset);
    }

    [Fact] // C223
    public void C223_ShiftWheelScrollsHorizontal()
    {
        var sv = new ScrollViewer
        {
            Width = 20,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = new Block(200, 5)
        };
        using var host = Show(sv);
        var dispatcher = host.Application.InputDispatcher;

        dispatcher.ProcessEvent(Wheel(5, 5, deltaY: -120, modifiers: KeyModifiers.Shift));
        host.RunFrame();
        Assert.True(sv.HorizontalOffset > 0); // Shift+wheel scrolls horizontally
    }

    [Fact] // C223b (added 2026-07-12 — the regression pin for the reversed horizontal wheel)
    public void C223b_HorizontalWheel_DirectionMatchesDelta()
    {
        // WheelDeltaY and WheelDeltaX use the Windows MIXED convention: positive-Y = up (toward the
        // START), positive-X = RIGHT (toward the END). The vertical lane's negation therefore must
        // NOT be applied to the X lane — doing so shipped a reversed horizontal wheel once; this row
        // pins all four directions so the sign can't silently re-invert.
        var sv = new ScrollViewer
        {
            Width = 20,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = new Block(200, 5)
        };
        using var host = Show(sv);
        var dispatcher = host.Application.InputDispatcher;

        dispatcher.ProcessEvent(Wheel(5, 5, deltaY: 0, deltaX: +120)); // wheel RIGHT
        host.RunFrame();
        Assert.Equal(3, sv.HorizontalOffset); // positive X = right = offset increases

        dispatcher.ProcessEvent(Wheel(5, 5, deltaY: 0, deltaX: +120));
        host.RunFrame();
        Assert.Equal(6, sv.HorizontalOffset);

        dispatcher.ProcessEvent(Wheel(5, 5, deltaY: 0, deltaX: -120)); // wheel LEFT
        host.RunFrame();
        Assert.Equal(3, sv.HorizontalOffset);

        // Shift+wheel maps through deltaX := -WheelDeltaY (WPF/Avalonia/browser parity):
        // wheel-UP = left (C223 pins the wheel-down = right direction).
        dispatcher.ProcessEvent(Wheel(5, 5, deltaY: +120, modifiers: KeyModifiers.Shift));
        host.RunFrame();
        Assert.Equal(0, sv.HorizontalOffset);
    }

    [Fact] // C224
    public void C224_WheelAtExtreme_Bubbles()
    {
        var inner = TallScroller(viewportRows: 6, contentRows: 60);
        var outer = new ScrollViewer
        {
            Width = 40,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Content = inner
        };
        using var host = Show(outer);
        var dispatcher = host.Application.InputDispatcher;

        // Inner already at the top (offset 0); a wheel-UP cannot scroll it ⇒ bubbles to outer.
        var innerOuterTotal = 0;
        // Wheel up over the inner content: inner unconsumed (at extreme) bubbles, outer scrolls? Outer
        // is also at top, so neither scrolls up — assert the unconsumed wheel does not throw and the
        // inner stays at 0 (the bubble path is exercised; an outer with room would consume).
        dispatcher.ProcessEvent(Wheel(5, 5, deltaY: +120)); // up at the top extreme
        host.RunFrame();
        Assert.Equal(0, inner.VerticalOffset);
        _ = innerOuterTotal;

        // Now scroll the inner down a bit, then a wheel-down past its extreme bubbles with the inner
        // pinned at max.
        inner.VerticalOffset = 1000; // coerces to max
        host.RunFrame();
        var innerMax = inner.VerticalOffset;
        dispatcher.ProcessEvent(Wheel(5, 5, deltaY: -120)); // down past the inner's max
        host.RunFrame();
        Assert.Equal(innerMax, inner.VerticalOffset); // inner pinned; the wheel bubbled out
    }

    [Fact] // C225
    public void C225_KeyboardScrolling()
    {
        var sv = TallScroller(viewportRows: 10, contentRows: 100);
        using var host = Show(sv);
        sv.Focus();

        host.SendKey(Key.DownArrow);
        host.RunFrame();
        Assert.Equal(1, sv.VerticalOffset); // ±1 row

        host.SendKey(Key.PageDown);
        host.RunFrame();
        Assert.Equal(1 + 10, sv.VerticalOffset); // ±viewport rows

        host.SendKey(Key.UpArrow);
        host.RunFrame();
        Assert.Equal(10, sv.VerticalOffset);

        host.SendKey(Key.PageUp);
        host.RunFrame();
        Assert.Equal(0, sv.VerticalOffset);

        var max = sv.Extent.Rows - sv.Viewport.Rows;
        host.SendKey(Key.End, KeyModifiers.Control);
        host.RunFrame();
        Assert.Equal(max, sv.VerticalOffset); // Ctrl+End → bottom

        host.SendKey(Key.Home, KeyModifiers.Control);
        host.RunFrame();
        Assert.Equal(0, sv.VerticalOffset); // Ctrl+Home → top
    }

    [Fact] // C226
    public void C226_EnsureVisible_ScrollsMinimally()
    {
        var sv = TallScroller(viewportRows: 10, contentRows: 100);
        using var host = Show(sv);

        // A rect below the viewport: scroll just enough to reveal its bottom.
        sv.EnsureVisible(new Rect(0, 40, 1, 1));
        host.RunFrame();
        Assert.True(sv.VerticalOffset > 0);
        Assert.True(sv.VerticalOffset <= 40);

        // A rect above the current offset: scroll up to its top.
        sv.EnsureVisible(new Rect(0, 2, 1, 1));
        host.RunFrame();
        Assert.Equal(2, sv.VerticalOffset);
    }

    [Fact] // C226b — a target taller than the viewport aligns its LEADING edge (not its bottom)
    public void C226b_EnsureVisible_ItemTallerThanViewport_KeepsHeaderVisible()
    {
        // The expanded-TreeViewItem case: a focused node's bounds span its header + the whole subtree, so the
        // rect (rows [5, 30), 25 tall) is far taller than the 10-row viewport. EnsureVisible must align the
        // leading edge (row 5 — the header) to the viewport top, NOT scroll to the subtree's bottom (which
        // would be offset 30-10=20, pushing the header off the top — the reported bug).
        var sv = TallScroller(viewportRows: 10, contentRows: 100);
        using var host = Show(sv);

        sv.EnsureVisible(new Rect(0, 5, 1, 25));
        host.RunFrame();
        Assert.Equal(5, sv.VerticalOffset);
    }

    [Fact] // C226c — an oversized rect already straddling the viewport doesn't scroll
    public void C226c_EnsureVisible_OversizedRectAlreadyCoveringViewport_DoesNotScroll()
    {
        var sv = TallScroller(viewportRows: 10, contentRows: 100);
        using var host = Show(sv);
        sv.VerticalOffset = 10; // viewport now [10, 20)
        host.RunFrame();

        // Rect rows [5, 30) covers the whole viewport (top above it, bottom below it) — already as visible as
        // it can be, so the minimal scroll is zero.
        sv.EnsureVisible(new Rect(0, 5, 1, 25));
        host.RunFrame();
        Assert.Equal(10, sv.VerticalOffset);
    }

    [Fact] // C227
    public void C227_ExtentCappedAtMaxScrollExtent()
    {
        var sv = new ScrollViewer
        {
            Width = 20,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new Block(19, 50_000)
        };
        using var host = Show(sv);

        Assert.Equal(LayoutLimits.MaxScrollExtent, sv.Extent.Rows); // capped (L215)

        sv.VerticalOffset = 1_000_000;
        host.RunFrame();
        Assert.Equal(sv.Extent.Rows - sv.Viewport.Rows, sv.VerticalOffset); // coerced to the capped max
    }

    // ───────────────────────────── 13.2 Auto visibility ─────────────────────────────

    [Fact] // C228 — Auto, content fits: the bar is absent (the template's optional bar is not shown)
    public void C228_AutoVisibility_ContentFits()
    {
        var sv = new ScrollViewer
        {
            Width = 20,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Block(19, 3) // fits the viewport
        };
        using var host = Show(sv);

        Assert.Equal(0, sv.VerticalOffset);
        Assert.True(sv.Extent.Rows <= sv.Viewport.Rows);            // nothing to scroll
        Assert.Equal(Visibility.Collapsed, VBar(sv).Visibility);    // …so the Auto bar is collapsed (CD28)
    }

    [Fact] // C229 — Auto, content overflows: scrolling is enabled (converges, no oscillation)
    public void C229_AutoVisibility_ContentOverflows()
    {
        var sv = TallScroller(viewportRows: 10, contentRows: 100);
        using var host = Show(sv);

        Assert.True(sv.Extent.Rows > sv.Viewport.Rows);
        Assert.Equal(Visibility.Visible, VBar(sv).Visibility); // the Auto bar appears on overflow (CD28)
        sv.VerticalOffset = 5;
        host.RunFrame();
        Assert.Equal(5, sv.VerticalOffset); // scrollable; the Auto two-pass converged (no oscillation)
    }

    [Fact] // C230 — Visible / Hidden / Disabled bar visibility + axis behavior (CD28)
    public void C230_Visibility_Modes()
    {
        // Visible: the bar is shown ALWAYS, even when the content fits (no overflow needed).
        var always = new ScrollViewer
        {
            Width = 20,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = new Block(19, 3) // fits the viewport — yet the bar still shows
        };
        using (Show(always))
        {
            Assert.True(always.Extent.Rows <= always.Viewport.Rows);   // nothing to scroll
            Assert.Equal(Visibility.Visible, VBar(always).Visibility); // …but Visible keeps the bar shown
        }

        // Hidden: the bar is collapsed but the axis is still scrollable by wheel/keys.
        var hidden = TallScroller(viewportRows: 8, contentRows: 80);
        hidden.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        using (var host = Show(hidden))
        {
            Assert.Equal(Visibility.Collapsed, VBar(hidden).Visibility); // no bar…
            hidden.Focus();
            host.SendKey(Key.DownArrow);
            host.RunFrame();
            Assert.Equal(1, hidden.VerticalOffset); // …but still scrollable
        }

        // Disabled: the bar is collapsed and the axis cannot scroll (offset coerced to 0).
        var disabled = TallScroller(viewportRows: 8, contentRows: 80);
        disabled.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        using (var host = Show(disabled))
        {
            Assert.Equal(Visibility.Collapsed, VBar(disabled).Visibility); // no bar…
            disabled.Focus();
            host.SendKey(Key.DownArrow);
            host.RunFrame();
            Assert.Equal(0, disabled.VerticalOffset); // …and no scroll on a disabled axis (L203)
        }
    }

    // ───────────────────────────── 13.3 ScrollBar parts / track / thumb / arrows ─────────────────────────────

    [Fact] // C231 — ScrollBar template parts
    public void C231_ScrollBarParts()
    {
        var bar = new ScrollBar { Orientation = Orientation.Vertical, Height = 10, Minimum = 0, Maximum = 100, ViewportSize = 10 };
        using var host = Show(bar);

        // PART_Track is required; the line buttons are optional RepeatButtons (present in the default theme).
        Assert.NotNull(bar.TemplateInstance);
        var track = bar.TemplateInstance!.NameScope.Find("PART_Track");
        Assert.IsType<Track>(track);
        // Assert.IsType<RepeatButton>(bar.TemplateInstance!.NameScope.Find("PART_LineUpButton"));
        // Assert.IsType<RepeatButton>(bar.TemplateInstance!.NameScope.Find("PART_LineDownButton"));

        // The :horizontal/:vertical pseudo-classes flip via the Orientation PseudoClassMapping (the
        // class swaps on an Orientation change — the mapping select glyph/orientation styling, C231).
        bar.Orientation = Orientation.Horizontal;
        host.RunFrame();
        Assert.True(bar.HasCustomPseudoClass(":horizontal"));
        Assert.False(bar.HasCustomPseudoClass(":vertical"));

        bar.Orientation = Orientation.Vertical;
        host.RunFrame();
        Assert.True(bar.HasCustomPseudoClass(":vertical"));
        Assert.False(bar.HasCustomPseudoClass(":horizontal"));
    }

    [Fact] // C232 — ScrollBar renders rail + proportional thumb
    public void C232_ScrollBarRendersRailAndThumb()
    {
        var bar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Width = 1,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Minimum = 0,
            Maximum = 90,
            ViewportSize = 10
        };
        using var host = Show(bar);

        // The thumb is a run of full blocks (proportional, min 1 cell); the rest is rail.
        var thumbCells = 0;
        for (var r = 0; r < 10; r++)
        {
            if (host.GetCell(0, r).Grapheme == "█") // █
                thumbCells++;
        }
        Assert.True(thumbCells >= 1); // at least a 1-cell thumb
    }

    [Fact] // C233 — track click pages; thumb drag reports a proportional value
    public void C233_TrackClickPagesAndDrag()
    {
        var bar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Width = 1,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Minimum = 0,
            Maximum = 100,
            ViewportSize = 10,
            LargeChange = 10
        };
        var scrolls = new List<ScrollEventType>();
        bar.Scroll += (_, e) => scrolls.Add(e.ScrollEventType);
        using var host = Show(bar);
        var dispatcher = host.Application.InputDispatcher;

        // Click low on the track (below the thumb) ⇒ page forward.
        var track = (Track)bar.TemplateInstance!.NameScope.Find("PART_Track")!;
        var (start, len) = track.ThumbGeometry();
        var belowRow = Math.Min(11, start + len + 1);

        dispatcher.ProcessEvent(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown, Position = new CellPosition(0, belowRow),
            Button = MouseButton.Left, ButtonsHeld = MouseButtons.None, Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch
        });
        host.RunFrame();
        dispatcher.ProcessEvent(new MouseEvent
        {
            Kind = MouseEventKind.ButtonUp, Position = new CellPosition(0, belowRow),
            Button = MouseButton.Left, ButtonsHeld = MouseButtons.None, Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch
        });
        host.RunFrame();

        Assert.Contains(ScrollEventType.LargeIncrement, scrolls);
        Assert.Equal(10, bar.Value); // paged by LargeChange
    }

    [Fact] // Audit P3 regression: a thumb drag release raises EndScroll EXACTLY once (Track cleared _dragging only
           // AFTER ReleaseMouseCapture, so OnLostMouseCapture re-fired OnDragEnd → a double EndScroll).
    public void C233b_ThumbDragRelease_RaisesEndScrollExactlyOnce()
    {
        var bar = new ScrollBar
        {
            Orientation = Orientation.Vertical, Width = 1, Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Minimum = 0, Maximum = 100, ViewportSize = 10, LargeChange = 10,
        };
        var endScrolls = 0;
        bar.Scroll += (_, e) => { if (e.ScrollEventType == ScrollEventType.EndScroll) endScrolls++; };
        using var host = Show(bar);
        var dispatcher = host.Application.InputDispatcher;

        var track = (Track)bar.TemplateInstance!.NameScope.Find("PART_Track")!;
        var (start, _) = track.ThumbGeometry();           // thumb start in TRACK-LOCAL rows
        var (col, trackTop) = track.TranslateToWindow(0, 0); // the Track's screen origin (line buttons inset it)

        MouseEvent At(MouseEventKind kind, int localRow, MouseButton button, MouseButtons held) => new()
        {
            Kind = kind, Position = new CellPosition(col, trackTop + localRow), Button = button, ButtonsHeld = held,
            Modifiers = KeyModifiers.None, Timestamp = DateTimeOffset.UnixEpoch,
        };

        dispatcher.ProcessEvent(At(MouseEventKind.ButtonDown, start, MouseButton.Left, MouseButtons.None)); // grab the thumb
        host.RunFrame();
        dispatcher.ProcessEvent(At(MouseEventKind.Move, start + 3, MouseButton.None, MouseButtons.Left));    // drag down
        host.RunFrame();
        dispatcher.ProcessEvent(At(MouseEventKind.ButtonUp, start + 3, MouseButton.Left, MouseButtons.None)); // release
        host.RunFrame();

        Assert.Equal(1, endScrolls);
    }

    [Fact(Skip = "ScrollBars redesigned to omit arrow buttons")] // C234 — arrow RepeatButtons step ±SmallChange repeating
    public void C234_ArrowRepeatButtonsStep()
    {
        var bar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Minimum = 0,
            Maximum = 100,
            ViewportSize = 10,
            SmallChange = 1,
            Value = 50
        };
        using var host = Show(bar);

        // The down line-button steps +SmallChange on each Click; it is a Press-mode RepeatButton, so a
        // mouse-down at its window position clicks it (the repeat timing itself is exercised by §11).
        var lineDown = (RepeatButton)bar.TemplateInstance!.NameScope.Find("PART_LineDownButton")!;
        ClickButton(host, lineDown);
        host.RunFrame();
        Assert.Equal(51, bar.Value);

        var lineUp = (RepeatButton)bar.TemplateInstance!.NameScope.Find("PART_LineUpButton")!;
        ClickButton(host, lineUp);
        ClickButton(host, lineUp);
        host.RunFrame();
        Assert.Equal(49, bar.Value);
    }

    // Dispatches a Press-mode click on a button by its window position (down = click for ClickMode.Press).
    private static void ClickButton(UIHeadlessHost host, UIElement button)
    {
        var (col, row) = button.TranslateToWindow(0, 0);
        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown, Position = new CellPosition(col, row),
            Button = MouseButton.Left, ButtonsHeld = MouseButtons.None, Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch
        });
        dispatcher.ProcessEvent(new MouseEvent
        {
            Kind = MouseEventKind.ButtonUp, Position = new CellPosition(col, row),
            Button = MouseButton.Left, ButtonsHeld = MouseButtons.None, Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch
        });
    }

    [Fact] // C235 — ScrollViewer wires bars in OnApplyTemplate, unhooks in OnTemplateDetaching
    public void C235_ScrollViewerWiresAndUnhooksBars()
    {
        var sv = TallScroller(viewportRows: 10, contentRows: 100);
        using var host = Show(sv);

        var bar = (ScrollBar)sv.TemplateInstance!.NameScope.Find("PART_VerticalScrollBar")!;

        // Moving the bar's value scrolls the viewer (code-behind wiring, not TemplateBinding).
        bar.Value = 12;
        host.RunFrame();
        Assert.Equal(12, sv.VerticalOffset);

        // Re-templating unhooks the old bar (no further effect after detach).
        var oldBar = bar;
        sv.Template = sv.Template; // identity set won't re-expand; force via a fresh template instead
        sv.Template = TallScrollerTemplate();
        host.RunFrame();
        oldBar.Value = 30; // the detached bar must not move the viewer
        host.RunFrame();
        Assert.NotEqual(30, sv.VerticalOffset);
    }

    [Fact] // C236 — a ScrollBar missing the optional arrow parts degrades gracefully
    public void C236_ScrollBarWithoutArrows_DegradesGracefully()
    {
        var bar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Height = 10,
            Minimum = 0,
            Maximum = 100,
            ViewportSize = 10,
            Template = TrackOnlyTemplate()
        };
        using var host = Show(bar); // no throw despite the optional arrows being absent (CD19/C236)

        Assert.NotNull(bar.TemplateInstance);
        Assert.Null(bar.TemplateInstance!.NameScope.Find("PART_LineUpButton"));
        Assert.IsType<Track>(bar.TemplateInstance!.NameScope.Find("PART_Track"));
    }

    [Fact] // C237 — ScrollBar parts (line buttons + thumb track) are NOT Tab stops
    public void C237_ScrollBarParts_AreNotTabStops()
    {
        // A ScrollBar and its parts are driven by the scrolled content's keyboard + the mouse, never
        // by tabbing onto the arrows (WPF/Avalonia parity). ButtonBase opts Focusable in by default,
        // so the line RepeatButtons must opt back out in the BuiltIn template.
        var top = new Button { Content = "Top", Width = 10, Height = 1 };
        var bottom = new Button { Content = "Bottom", Width = 10, Height = 1 };
        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(new Block(8, 100)); // tall body forces a visible vertical bar
        stack.Children.Add(bottom);

        var sv = new ScrollViewer
        {
            Width = 20,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = stack
        };
        using var host = Show(sv);
        var focus = host.Application.FocusManager;

        // The scrollbar parts are present and non-focusable / non-tab-stop.
        var bar = (ScrollBar)sv.TemplateInstance!.NameScope.Find("PART_VerticalScrollBar")!;
        var lineUp = (RepeatButton?)bar.TemplateInstance!.NameScope.Find("PART_LineUpButton");
        var lineDown = (RepeatButton?)bar.TemplateInstance!.NameScope.Find("PART_LineDownButton");
        var track = (Track)bar.TemplateInstance!.NameScope.Find("PART_Track")!;
        Assert.False(lineUp?.Focusable is true);
        Assert.False(lineUp?.IsTabStop is true);
        Assert.False(lineDown?.Focusable is true);
        Assert.False(lineDown?.IsTabStop is true);
        Assert.False(track.Focusable); // the Track is a non-focusable UIElement to begin with

        // Tab cycles through ONLY the content buttons — never the scrollbar parts. Drive a full cycle
        // and assert the focus never lands on (or passes through) any scrollbar part.
        top.Focus();
        Assert.Same(top, focus.FocusedElement);

        var visited = new List<UIElement?>();
        for (var i = 0; i < 6; i++) // more than enough Tabs to cycle the 2-stop content twice over
        {
            host.SendKey(Key.Tab);
            host.RunFrame();
            visited.Add(focus.FocusedElement);
            Assert.NotSame(lineUp, focus.FocusedElement);
            Assert.NotSame(lineDown, focus.FocusedElement);
            Assert.NotSame(track, focus.FocusedElement);
        }

        // The cycle is exactly {bottom, top, bottom, top, …} — the two content buttons, nothing else.
        Assert.All(visited, e => Assert.True(ReferenceEquals(e, top) || ReferenceEquals(e, bottom)));
        Assert.Contains(visited, e => ReferenceEquals(e, bottom));
        Assert.Contains(visited, e => ReferenceEquals(e, top));

        // The bar still works by code (mouse/wheel/repeat path) — the value still moves the viewer.
        bar.Value = 7;
        host.RunFrame();
        Assert.Equal(7, sv.VerticalOffset);
    }

    // A bare track-only ScrollBar template (no optional arrows) for the degrade row.
    private static ControlTemplate TrackOnlyTemplate() => new(ctx =>
    {
        var owner = (ScrollBar)ctx.TemplatedParent!;
        var track = new Track(owner);
        ctx.RegisterName("PART_Track", track);
        return track;
    });

    // A fresh ScrollViewer template identical to the default shape (the C235 re-template needs a new instance).
    private static ControlTemplate TallScrollerTemplate() => new(ctx =>
    {
        var dock = new DockPanel();
        var bar = new ScrollBar { Orientation = Orientation.Vertical };
        ctx.RegisterName("PART_VerticalScrollBar", bar);
        DockPanel.SetDock(bar, Dock.Right);
        var presenter = new ScrollContentPresenter();
        ctx.RegisterName("PART_ScrollContentPresenter", presenter);
        dock.Children.Add(bar);
        dock.Children.Add(presenter);
        return dock;
    });
}
