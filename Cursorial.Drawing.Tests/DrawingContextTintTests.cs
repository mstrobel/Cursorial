using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// <see cref="DrawingContext.TintCells"/> takes a <see cref="PartialStyle"/>: the operation is the
/// CALLER's to state, per channel, and the method has no opinion of its own. Before the migration the
/// parameter was a <see cref="CellStyle"/> used as a delta, which hardcoded three decisions —
/// <see cref="TextAttributes.Inverse"/> always cleared, other attributes OR-ed on, and a default
/// background silently meaning "no background stated". These pin the replacements.
/// </summary>
public class DrawingContextTintTests
{
    private static readonly Color Blue = Color.FromRgb(0, 0, 200);
    private static readonly Color Red = Color.FromRgb(200, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);

    /// <summary>
    /// Paints <paramref name="baseStyle"/>-styled "X"s over the whole scene, then runs
    /// <paramref name="tint"/> and hands back the scene's OWN cells — the surface TintCells writes,
    /// read without a compositing pass in between to reinterpret them.
    /// </summary>
    private static Scene Tinted(int columns, int rows, CellStyle baseStyle, Action<DrawingContext> tint)
    {
        var scene = Scene.Create(columns, rows);
        scene.Draw(context =>
        {
            for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
                context.Set(column, row, "X", baseStyle);

            tint(context);
        });
        return scene;
    }

    private static CellStyle Opaque(Color background, TextAttributes attributes = default) =>
        CellStyle.Default.WithForeground(White).WithBackground(background).WithAttributes(attributes);

    // ---- channels the delta carries, and channels it does not ----

    [Fact]
    public void TintCells_ReplacesTheBackgroundWhenTheDeltaCarriesOne()
    {
        using var scene = Tinted(2, 1, Opaque(Blue),
                                 c => c.TintCells(new Rect(0, 0, 1, 1), PartialStyle.WithBackground(Red)));

        Assert.Equal(Red, scene.GetCell(0, 0).Style.Background);
        Assert.Equal(Blue, scene.GetCell(1, 0).Style.Background); // outside bounds: untouched
    }

    [Fact]
    public void TintCells_LeavesTheBackgroundAloneWhenTheDeltaCarriesNone()
    {
        using var scene = Tinted(1, 1, Opaque(Blue),
                                 c => c.TintCells(new Rect(0, 0, 1, 1), PartialStyle.WithSet(TextAttributes.Inverse)));

        Assert.Equal(Blue, scene.GetCell(0, 0).Style.Background);
    }

    /// <summary>
    /// The whole reason the parameter changed type: a <see cref="CellStyle"/> could not say "tint TO
    /// the terminal default", because <c>Color.Default</c> was the encoding of "said nothing". A
    /// present-but-default background is now an ordinary opinion and lands like any other.
    /// </summary>
    [Fact]
    public void TintCells_CanTintToTheTerminalDefaultBackground()
    {
        using var scene = Tinted(1, 1, Opaque(Blue),
                                 c => c.TintCells(new Rect(0, 0, 1, 1), PartialStyle.WithBackground(Color.Default)));

        Assert.True(scene.GetCell(0, 0).Style.Background.IsDefault);
    }

    [Fact]
    public void TintCells_PreservesGraphemes()
    {
        using var scene = Tinted(1, 1, Opaque(Blue),
                                 c => c.TintCells(new Rect(0, 0, 1, 1), PartialStyle.WithBackground(Red)));

        Assert.Equal("X", scene.GetCell(0, 0).Grapheme);
    }

    /// <summary>
    /// The identity delta is inert on EVERY channel, compared against an identically painted twin
    /// rather than against the authored <see cref="CellStyle"/> — the scene's write path normalizes
    /// some channels on the way in, so the twin is the only honest "untouched".
    /// </summary>
    [Fact]
    public void TintCells_LeavesTheCellUntouchedForTheIdentityDelta()
    {
        using var scene = Tinted(2, 1, Opaque(Blue, TextAttributes.Bold | TextAttributes.Inverse),
                                 c => c.TintCells(new Rect(0, 0, 1, 1), PartialStyle.Default));

        Assert.Equal(scene.GetCell(1, 0).Style, scene.GetCell(0, 0).Style);
    }

    // ---- the attribute algebra: set, clear, toggle, and no opinion ----

    [Theory]
    [InlineData(TextAttributes.None, true)]      // set on a cell that lacks it
    [InlineData(TextAttributes.Inverse, true)]   // set on a cell that already has it
    public void TintCells_ForcesSetAttributesOn(TextAttributes existing, bool expected)
    {
        using var scene = Tinted(1, 1, Opaque(Blue, existing),
                                 c => c.TintCells(new Rect(0, 0, 1, 1), PartialStyle.WithSet(TextAttributes.Inverse)));

        Assert.Equal(expected, scene.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Inverse));
    }

    /// <summary>
    /// The operation the old signature could not express at all: an OR can only ever turn a flag ON.
    /// TintCells cleared Inverse for its one caller by hand; every other flag was un-clearable.
    /// </summary>
    [Theory]
    [InlineData(TextAttributes.Inverse)]
    [InlineData(TextAttributes.Strikethrough)]
    public void TintCells_ForcesClearedAttributesOff(TextAttributes flag)
    {
        using var scene = Tinted(1, 1, Opaque(Blue, flag),
                                 c => c.TintCells(new Rect(0, 0, 1, 1), PartialStyle.WithCleared(flag)));

        Assert.False(scene.GetCell(0, 0).Style.Attributes.HasFlag(flag));
    }

    /// <summary>
    /// Also new: one delta whose effect DIFFERS per cell. Selection over partly-inverse text is the
    /// motivating case — the old form had to pick one answer for the whole rectangle.
    /// </summary>
    [Fact]
    public void TintCells_TogglesPerCell()
    {
        var scene = Scene.Create(2, 1);
        scene.Draw(context =>
        {
            context.Set(0, 0, "X", Opaque(Blue));
            context.Set(1, 0, "X", Opaque(Blue, TextAttributes.Inverse));
            context.TintCells(new Rect(0, 0, 2, 1), PartialStyle.WithToggled(TextAttributes.Inverse));
        });

        using (scene)
        {
            Assert.True(scene.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Inverse));
            Assert.False(scene.GetCell(1, 0).Style.Attributes.HasFlag(TextAttributes.Inverse));
        }
    }

    [Fact]
    public void TintCells_LeavesAttributesTheDeltaSaysNothingAboutAlone()
    {
        using var scene = Tinted(1, 1, Opaque(Blue, TextAttributes.Bold | TextAttributes.Underline),
                                 c => c.TintCells(new Rect(0, 0, 1, 1), PartialStyle.WithSet(TextAttributes.Inverse)));

        var attributes = scene.GetCell(0, 0).Style.Attributes;
        Assert.True(attributes.HasFlag(TextAttributes.Bold));
        Assert.True(attributes.HasFlag(TextAttributes.Underline));
        Assert.True(attributes.HasFlag(TextAttributes.Inverse));
    }

    /// <summary>
    /// The colour selection path, exactly as <c>TextPresenter</c> now spells it. Its Inverse clear used
    /// to be invisible — the caller passed attributes of <c>None</c> and TintCells cleared Inverse
    /// anyway — so a naive migration to a bare <c>WithBackground</c> would silently leave selected
    /// inverse text inverted under its new background.
    /// </summary>
    [Fact]
    public void TintCells_ColorSelectionSpellingClearsInverseAndPaintsTheBackground()
    {
        using var scene = Tinted(1, 1, Opaque(Blue, TextAttributes.Inverse | TextAttributes.Bold),
                                 c => c.TintCells(new Rect(0, 0, 1, 1),
                                                  PartialStyle.WithBackground(Red).Clearing(TextAttributes.Inverse)));

        var style = scene.GetCell(0, 0).Style;
        Assert.Equal(Red, style.Background);
        Assert.False(style.Attributes.HasFlag(TextAttributes.Inverse));
        Assert.True(style.Attributes.HasFlag(TextAttributes.Bold));
    }

    // ---- geometry: the same per-cell translate + clip the scalar write path uses ----

    [Fact]
    public void TintCells_HonorsTheAmbientTranslate()
    {
        using var scene = Tinted(4, 1, Opaque(Blue), c =>
        {
            using var _ = c.PushTranslate(2, 0);
            c.TintCells(new Rect(0, 0, 2, 1), PartialStyle.WithBackground(Red));
        });

        Assert.Equal(Blue, scene.GetCell(0, 0).Style.Background);
        Assert.Equal(Blue, scene.GetCell(1, 0).Style.Background);
        Assert.Equal(Red, scene.GetCell(2, 0).Style.Background);
        Assert.Equal(Red, scene.GetCell(3, 0).Style.Background);
    }

    [Fact]
    public void TintCells_HonorsTheAmbientClip()
    {
        using var scene = Tinted(4, 1, Opaque(Blue), c =>
        {
            using var _ = c.PushClip(new Rect(1, 0, 2, 1));
            c.TintCells(new Rect(0, 0, 4, 1), PartialStyle.WithBackground(Red));
        });

        Assert.Equal(Blue, scene.GetCell(0, 0).Style.Background);
        Assert.Equal(Red, scene.GetCell(1, 0).Style.Background);
        Assert.Equal(Red, scene.GetCell(2, 0).Style.Background);
        Assert.Equal(Blue, scene.GetCell(3, 0).Style.Background);
    }

    /// <summary>
    /// The pair compose PER CELL, which is why the mapping cannot be an up-front rectangle
    /// intersection: bounds are local and the clip is in scene space, so the two only agree when no
    /// translate is active.
    /// </summary>
    [Fact]
    public void TintCells_TranslateAndClipCompose()
    {
        using var scene = Tinted(5, 1, Opaque(Blue), c =>
        {
            using var _ = c.PushTranslate(2, 0);
            using var __ = c.PushClip(new Rect(0, 0, 2, 1)); // local, post-translate: scene columns 2..3
            c.TintCells(new Rect(0, 0, 3, 1), PartialStyle.WithBackground(Red)); // scene columns 2..4
        });

        Assert.Equal(Blue, scene.GetCell(0, 0).Style.Background);
        Assert.Equal(Blue, scene.GetCell(1, 0).Style.Background);
        Assert.Equal(Red, scene.GetCell(2, 0).Style.Background);
        Assert.Equal(Red, scene.GetCell(3, 0).Style.Background);
        Assert.Equal(Blue, scene.GetCell(4, 0).Style.Background); // clipped away
    }

    [Fact]
    public void TintCells_ClipsToTheSurfaceEdge()
    {
        using var scene = Tinted(2, 1, Opaque(Blue),
                                 c => c.TintCells(new Rect(-2, 0, 6, 1), PartialStyle.WithBackground(Red)));

        Assert.Equal(Red, scene.GetCell(0, 0).Style.Background);
        Assert.Equal(Red, scene.GetCell(1, 0).Style.Background);
    }
}
