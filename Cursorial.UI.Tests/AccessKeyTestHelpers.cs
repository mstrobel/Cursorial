using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.Tests.UI;

internal static class AccessKeyTestHelpers
{
    internal static void AssertCue(bool active, UIHeadlessHost host, int col, int row, UIElement cueOwner)
    {
        AssertCueMatch(true, active, host, col, row, cueOwner);
        AssertCueMatch(false, !active, host, col, row, cueOwner);
    }

    internal static void AssertNoCue(UIHeadlessHost host, int col, int row, UIElement cueOwner)
    {
        AssertCueMatch(expectMatch: false, active: true, host, col, row, cueOwner);

        var inactiveStyle = ResolveCueStyleCore(host, cueOwner, active: false, FindPresenter(cueOwner));
        if (inactiveStyle.IsIdentity) return;

        AssertCueMatch(expectMatch: false, active: false, host, col, row, cueOwner);
    }

    private static void AssertCueMatch(bool expectMatch, bool active, UIHeadlessHost host, int col, int row,
                                       UIElement? cueOwner = null)
    {
        var root = host.Application.RootElement;

        cueOwner ??= root;

        Assert.NotNull(cueOwner);

        var atp = FindPresenter(cueOwner);

        if (atp is not null)
        {
            if (expectMatch)
                Assert.True(active == AccessKeyManager.GetShowUnderline(atp));
        }

        var expectedStyle = ResolveCueStyle(expectMatch, host, cueOwner, active);
        var otherStyle = ResolveCueStyle(expectMatch, host, cueOwner, !active);

        AssertCueMatchCore(expectMatch, expectedStyle, host, col, row);

        // If matching active AND inactive is identity, skip; we can't prove an identity style wasn't applied.
        if (active && otherStyle.IsIdentity)
            return;

        AssertCueMatchCore(!expectMatch, otherStyle, host, col, row);
    }

    private static void AssertCueMatchCore(bool expectMatch, BrushedStyle effectiveCueStyle, UIHeadlessHost host,
                                           int col, int row)
    {
        var resolved = effectiveCueStyle.Resolve(col, row, new Rect(col, row, 1, 1));

        var actualStyle = host.GetCell(col, row).Style;
        var defaultStyle = host.FrameBuffer.DefaultStyle;
        var expectedStyle = defaultStyle;

        if (expectMatch) expectedStyle = resolved.ApplyTo(expectedStyle);

        if (expectedStyle.Foreground == actualStyle.Foreground ^ expectMatch)
        {
            Fail(nameof(CellStyle.Foreground),
                 expectMatch,
                 expectedStyle.Foreground.ToString(),
                 actualStyle.Foreground.ToString());
        }

        if (expectedStyle.Background == actualStyle.Background ^ expectMatch)
        {
            Fail(nameof(CellStyle.Background),
                 expectMatch,
                 expectedStyle.Background.ToString(),
                 actualStyle.Background.ToString());
        }

        var expectAttr = expectedStyle.Attributes;
        var actualAttr = actualStyle.Attributes;

        if (((expectMatch
                  ? expectAttr.HasFlag(TextAttributes.Underline)
                  : resolved.AppliedAttributes.HasFlag(TextAttributes.Underline)) ||
             actualAttr.HasFlag(TextAttributes.Underline)) &&
            expectedStyle.UnderlineColor != defaultStyle.UnderlineColor &&
            expectedStyle.UnderlineColor == actualStyle.UnderlineColor ^ expectMatch)
        {
            Fail(nameof(CellStyle.UnderlineColor),
                 expectMatch,
                 expectedStyle.UnderlineColor.ToString(),
                 actualStyle.UnderlineColor.ToString());
        }

        if ((expectAttr != TextAttributes.None || actualAttr != TextAttributes.None) &&
            expectAttr == actualAttr ^ expectMatch)
        {
            Fail(nameof(CellStyle.Attributes),
                 expectMatch,
                 expectAttr.ToString(),
                 actualAttr.ToString());
        }

        static void Fail(string attribute, bool expectedMatch, string expected, string actual)
        {
            Assert.Fail($"Expected {attribute} to {(expectedMatch ? "MATCH" : "NOT MATCH")}; " +
                        $"expected <{expected}> but found <{actual}>.");
        }
    }

    private static BrushedStyle ResolveCueStyle(bool expectMatch, UIHeadlessHost host, UIElement? cueOwner,
                                                bool active = true)
    {
        Assert.NotNull(cueOwner ??= host.Application.RootElement);

        var atp = FindPresenter(cueOwner!);
        var cue = ResolveCueStyleCore(host, atp ?? cueOwner, active, atp);

        if (expectMatch is false && active is false && cue.IsIdentity)
            return cue; // we can only prove a mismatch against an identity style if it remains thus; don't compose.

        var baseStyle = BrushedStyle.FromElement(cueOwner!.VisualRoot!);
        var ownerStyle = BrushedStyle.FromElement(cueOwner);

        if (atp is not null && BrushedStyle.FromElement(atp) is { IsIdentity: false } atpStyle)
            ownerStyle = ownerStyle.Then(atpStyle);

        return baseStyle.Then(ownerStyle).Then(cue);
    }

    private static BrushedStyle ResolveCueStyleCore(UIHeadlessHost host, UIElement? cueOwner, bool active,
                                                    AccessTextPresenter? atp)
    {
        Assert.NotNull(cueOwner ??= host.Application.RootElement);

        if (atp is not null) return active ? atp.ActiveCueStyle : atp.InactiveCueStyle;

        var key = active ? ThemeKeys.InteractiveCueActiveStyle : ThemeKeys.InteractiveCueInactiveStyle;

        Assert.True(cueOwner!.TryFindResource(key, out var cs));
        return Assert.IsType<BrushedStyle>(cs);
    }

    private static AccessTextPresenter? FindPresenter(UIElement? cueOwner)
    {
        if (cueOwner is AccessTextPresenter presenter)
            return presenter;

        if (cueOwner?.VisualChildrenList is {} children)
        {
            foreach (var child in children)
            {
                if (FindPresenter(child) is {} found)
                    return found;
            }
        }

        return null;
    }
}