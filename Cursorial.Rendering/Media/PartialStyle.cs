using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Text;

namespace Cursorial.Rendering.Media;

/// <summary>
/// A style DELTA: the channels it carries, and what to do to them. Applying it to a
/// <see cref="CellStyle"/> yields a new one; channels it does not carry pass through.
/// <c>default</c> is the identity — applying it returns the base unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Value channels are NULLABLE, and <see langword="null"/> is the only encoding of "absent". That is
/// deliberate: the defect this type exists to retire is a sentinel doing double duty —
/// <c>Color.Default</c> pressed into service as "no opinion", which makes "set the background to the
/// terminal default" unexpressible. A separate presence bitmask would fix that too, but it puts
/// presence and value in two places the caller has to keep in agreement, and it reads badly at the
/// use site (<c>if (d.Channels.HasFlag(Foreground))</c> versus
/// <c>if (d.Foreground is { } fg)</c>).
/// </para>
/// <para>
/// The size cost is real and is accepted. <see cref="CellStyle"/> keeps non-nullable channels
/// because it is per-cell hot data copied per pixel through compositing; a <see cref="PartialStyle"/>
/// is built per OPERATION and usually hoisted out of the loop entirely, so it should buy
/// expressiveness with the bytes.
/// </para>
/// <para>
/// Attributes are a <c>(Clear, Xor)</c> mask pair rather than a value, which gives three states plus
/// a fourth operation: <c>result = (base &amp; ~Clear) ^ Xor</c>.
/// <list type="table">
///   <item><term>set on</term>   <description><c>Clear = f, Xor = f</c></description></item>
///   <item><term>set off</term>  <description><c>Clear = f, Xor = 0</c></description></item>
///   <item><term>toggle</term>   <description><c>Clear = 0, Xor = f</c></description></item>
///   <item><term>leave</term>    <description><c>Clear = 0, Xor = 0</c></description></item>
/// </list>
/// Toggle is the one the current code cannot express, and the one selection-on-inverse-text needs.
/// </para>
/// </remarks>
public readonly record struct PartialStyle
{
    public static readonly PartialStyle Default = default;

    /// <summary>
    /// The default text shadow: half-alpha black ink that fills nothing, composited MULTIPLY.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A delta rather than a whole <see cref="CellStyle"/>, and it lives here rather than beside
    /// <see cref="CellStyle.Default"/>, for two reasons that are really one. A shadow has an opinion about
    /// three colour channels and about nothing else — a whole style would have had to state the attribute
    /// word, the underline shape and the hyperlink as well, and its one consumer
    /// (<c>ShadowedFont</c>) had to hand-patch those channels back off the base before it could composite,
    /// because a value type cannot say "leave this one alone". Absence says it here.
    /// </para>
    /// <para>
    /// And a shadow's <see cref="Mode"/> is part of what a shadow IS — it darkens what it falls on — so it
    /// belongs to the constant. A <see cref="CellStyle"/> had nowhere to put one, which is why the same
    /// consumer used to recover the mode by comparing the style it held against this constant's old
    /// whole-style form: any caller who happened to pass an equal style silently inherited MULTIPLY too.
    /// </para>
    /// <para>
    /// <see cref="Background"/> is <see cref="Color.Transparent"/> — "a shadow never fills" — which is
    /// weaker than absence would be here: absent means the base's background flows through unchallenged,
    /// transparent means the shadow states one and it composites away to the same thing. The stated form
    /// keeps this usable as a delta over a nonempty background without also boxing it.
    /// </para>
    /// </remarks>
    public static PartialStyle DefaultShadow { get; } = new()
    {
        Foreground     = ShadowInk,
        UnderlineColor = ShadowInk,
        Background     = Color.Transparent,
        Mode           = BlendingModes.Multiply,
    };

    private static Color ShadowInk => Color.FromRgba(0, 0, 0, 127);

    public Color?     Foreground     { get; init; }
    public Color?     Background     { get; init; }
    public Color?     UnderlineColor { get; init; }
    public Hyperlink? Hyperlink      { get; init; }

    /// <summary>
    /// <para>
    /// The underline SHAPE, when this delta turns the underline on. <see langword="null"/> means "no
    /// explicit shape" — either because the delta leaves the underline alone, or because it removes
    /// it. Which of those applies is carried by the underline FLAG, so the shape needs no presence bit of its own:
    /// <list type="table">
    ///   <item><term>ignored</term> <description><c>Underline ∉ Clear</c></description></item>
    ///   <item><term>removed</term> <description><c>Underline ∈ Clear</c>, <c>∉ Xor</c></description></item>
    ///   <item><term>applied</term> <description><c>Underline ∈ Clear</c> and <c>∈ Xor</c></description></item>
    /// </list>
    /// </para>
    /// The interaction between <c>UnderlineShape</c> and the <see cref="TextAttributes.Underline"/> flag
    /// is as follows:
    /// <list type="bullet"> 
    /// <item>
    /// If the flag is <b>PRESENT</b> in <c>RemovedAttributes</c>, no underline will apply. If a shape is
    /// specified, it becomes a <b>REMNANT</b> and will NOT carry over during composition (e.g., via <c>Then()</c>).
    /// </item>
    /// <item>
    /// When the flag is <b>PRESENT</b> in <c>AppliedAttributes</c>, the specified shape will be applied
    /// if present; otherwise, the <see cref="UnderlineStyle.Single">default shape</see> will be applied.
    /// </item>
    /// <item>
    /// When a shape is specified, but the flag is <b>ABSENT</b> from both <c>AppliedAttributes</c> and
    /// <c>RemovedAttributes</c>, the shape will still be applied <b>UNLESS</b> the delta is composed with
    /// another delta which overrides its shape or removes the flag.
    /// </item>
    /// </list>
    /// </summary>
    public UnderlineStyle? UnderlineShape { get; init; }

    // ---- storage: one mask pair over the whole flag word ----

    // The (Clear, Xor) pair is STORAGE, not interface: it is the form that composes associatively
    // (see Then), but it makes a reader do bit algebra to answer "what does this delta do to Bold?".
    // The three properties below are the interface — disjoint, exhaustive, and directly readable.
    // Callers construct through the factories, so the encoding never surfaces.
    internal TextAttributes Clear { get; init; }
    internal TextAttributes Xor   { get; init; }

    /// <summary>Attributes this delta forces ON, whatever the base had.</summary>
    public TextAttributes AppliedAttributes => Clear & Xor;

    /// <summary>Attributes this delta forces OFF, whatever the base had.</summary>
    public TextAttributes RemovedAttributes => Clear & ~Xor;

    /// <summary>Attributes this delta INVERTS — on becomes off, off becomes on.</summary>
    public TextAttributes ToggledAttributes => Xor & ~Clear;

    /// <summary>How this delta's colors combine with the base's. <see langword="null"/> replaces.</summary>
    public IBlendingMode? Mode { get; init; }

    // ---- decomposed axes: PROJECTIONS onto the mask pair, never separate state ----

    private const TextAttributes WeightMask = TextAttributes.Bold | TextAttributes.Faint;

    /// <summary>The weight this delta imposes, or <see langword="null"/> if it leaves weight alone.</summary>
    public TextWeight? Weight => (Clear & WeightMask) != WeightMask
                                     ? null
                                     : (Xor & TextAttributes.Bold)  != 0 ? TextWeight.Bold
                                     : (Xor & TextAttributes.Faint) != 0 ? TextWeight.Faint
                                     : TextWeight.Normal;

    /// <summary>The posture this delta imposes, or <see langword="null"/> if it leaves it alone.</summary>
    public TextStyle? Posture => (Clear & TextAttributes.Italic) is 0
                                     ? null
                                     : (Xor & TextAttributes.Italic) != 0 ? TextStyle.Italic : TextStyle.Normal;

    /// <summary>
    /// Whether this delta REMOVES the underline: it forces the flag off, leaving any provided shape a dead remnant.
    /// </summary>
    private bool RemovesUnderline => (Clear & TextAttributes.Underline) != 0 &&
                                     (Xor & TextAttributes.Underline) is 0;

    /// <summary>
    /// True when this delta is inert in EVERY context — applying it returns the base unchanged, and
    /// composing it changes nothing about what follows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defined as equality with <c>default</c> rather than as a list of per-field checks, so EVERY
    /// field participates automatically. That is deliberate, and it is the second attempt: the
    /// handwritten form needed each field remembered individually, and <see cref="Mode"/> — the one
    /// member with no presence bit, governed only by <see langword="null"/> — was duly forgotten. The
    /// record's generated equality cannot forget one, so a field added later is covered without
    /// anybody reading this comment. It works because §3's encoding makes a zeroed struct inert:
    /// <c>Clear</c> rather than <c>Keep</c> means <c>default</c> IS the identity.
    /// </para>
    /// <para>
    /// Why <see cref="Mode"/> has to count, given it cannot affect <see cref="ApplyTo"/> on its own
    /// (it is read only inside the per-channel color combine, which no absent channel reaches):
    /// <see cref="Then"/> PROPAGATES it, so a delta carrying only a mode is a blend carrier that
    /// changes how subsequent deltas' colors land. Excluding it would leave this property sound for
    /// the direct-apply fast path and a trap for the obvious next use — pruning no-op deltas out of a
    /// chain, which would silently drop the blend.
    /// </para>
    /// </remarks>
    public bool IsIdentity => this == default;

    /// <summary>The axes that are genuinely independent booleans — the only ones the flag-level
    /// <see cref="WithApplied"/>/<see cref="WithRemoved"/>/<see cref="WithToggled"/> factories accept.
    /// Bold, Faint, Italic and Underline have their own axes and are rejected there.</summary>
    public const TextAttributes Booleans =
        TextAttributes.Strikethrough | TextAttributes.Overline | TextAttributes.Inverse |
        TextAttributes.Blink | TextAttributes.Hidden;

    // ---- construction ----

    public static PartialStyle WithForeground(Color c) => new() { Foreground = c };
    public static PartialStyle WithBackground(Color c) => new() { Background = c };
    /// <summary>
    /// Recolor the underline WITHOUT asserting one — "whatever underline exists is this color".
    /// Deliberately weaker than <see cref="WithUnderline"/>, which turns the underline on.
    /// </summary>
    /// <remarks>
    /// The asymmetry is the point, not an oversight. A theme wants to say "error underlines are red"
    /// without also claiming everything is underlined; if the color forced the flag, that would be
    /// unsayable — the same shape of loss as <c>Color.Default</c> meaning "absent", which this type
    /// exists to retire. Pinned by <c>WithUnderlineColor_DoesNotForceAnUnderline</c>.
    /// </remarks>
    public static PartialStyle WithUnderlineColor(Color c) => new() { UnderlineColor = c };
    public static PartialStyle WithHyperlink(Hyperlink link) => new() { Hyperlink = link };
    public static PartialStyle WithBlending(IBlendingMode mode) => new() { Mode = mode };

    /// <summary>Impose a weight — forces Bold ON and Faint OFF, or vice versa, or both off for Normal.
    /// The shared SGR 22 reset is why one mask covers both.</summary>
    public static PartialStyle Weighted(TextWeight w)
        => new()
           {
               Clear = WeightMask,
               Xor = w switch
                     {
                         TextWeight.Bold  => TextAttributes.Bold,
                         TextWeight.Faint => TextAttributes.Faint,
                         _                => 0
                     }
           };

    public static PartialStyle Postured(TextStyle p)
        => new()
           {
               Clear = TextAttributes.Italic,
               Xor = p is TextStyle.Italic ? TextAttributes.Italic : 0
           };

    /// <summary>Force <paramref name="flags"/> ON. Must be within <see cref="Booleans"/>.</summary>
    public static PartialStyle WithApplied(TextAttributes flags) =>
        new() { Clear = Require(flags), Xor = flags };

    /// <summary>Force <paramref name="flags"/> OFF. Must be within <see cref="Booleans"/></summary>
    public static PartialStyle WithRemoved(TextAttributes flags) =>
        new() { Clear = Require(flags) };

    /// <summary>
    /// INVERT <paramref name="flags"/> — on becomes off and off becomes on. Must be within <see cref="Booleans"/>
    /// </summary>
    public static PartialStyle WithToggled(TextAttributes flags) =>
        new() { Xor = Require(flags) };

    // Bold/Faint/Italic/Underline reach the mask only by mistake — they have their own axes, and
    // routing them through the flag word is how `Bold | Faint` gets written.
    //
    // There is deliberately NO unguarded sibling. One existed — `WithAdded`, the flag-word union
    // `base | flags` — justified as the delta form of `CellStyle.AddAttributes` for merging an
    // inherited attribute word onto a run's own. It was `WithSet` with this guard removed, and that
    // was its only distinguishing capability: it existed to do the forbidden thing. The state it
    // bought is not renderable. `Bold | Faint` share the SGR 22 reset, so reaching it emits ESC[1m
    // from a Faint predecessor and ESC[2m from a Bold one — same destination, different bytes, and
    // the terminal keeps whichever arrived last (confirmed in Kitty and Ghostty). `Weight` reports
    // Bold for it regardless, because it tests `Xor & Bold` first, so the accessor and the frame
    // disagree in silence. And no per-call guard could have rescued it: `WithAdded(Bold).Adding(Faint)`
    // equals `WithAdded(Bold | Faint)` exactly, so splitting the call splits the check. Its one
    // caller — the base-attribute leg of `DrawingContext.CreateBrushResolver` — now folds the word
    // through `Weighted` / `Postured` / `WithSet`, one axis at a time.
    internal static TextAttributes Require(TextAttributes flags) =>
        (flags & ~Booleans) is 0
            ? flags
            : throw new ArgumentOutOfRangeException(
                  nameof(flags),
                  $"{flags & ~Booleans} has its own axis; set Weight / Posture / Underline instead.");

    // Every flag the type knows about, assembled from the two vocabularies rather than written as a
    // literal so a flag added to either is covered without anybody remembering this line.
    private const TextAttributes EveryAttribute =
        Booleans | TextAttributes.Bold | TextAttributes.Faint | TextAttributes.Italic | TextAttributes.Underline;

    /// <summary>
    /// The delta a whole <see cref="CellStyle"/> means: EVERY channel stated, so
    /// <c>From(s).ApplyTo(anything) == s</c>. The adapter for code that still holds a
    /// <see cref="CellStyle"/> — and, because a stated background is an OPINION about the cells the
    /// glyph does not ink, the form that selects BOX mode at a glyph paint. Use
    /// <see cref="FromInk"/> for the stamp.
    /// </summary>
    /// <remarks>
    /// One channel does not round-trip, and cannot: a null <see cref="CellStyle.Hyperlink"/> is
    /// indistinguishable from "no opinion" on this side, so the result never REMOVES a hyperlink the
    /// base carries. That is the same absent-versus-empty conflation this type retires for colors,
    /// surviving one layer down in <see cref="CellStyle"/> itself, and it is why this is an adapter
    /// for legacy call sites rather than the way to build a delta.
    /// </remarks>
    public static PartialStyle From(in CellStyle style) => new()
    {
        Foreground     = style.Foreground,
        Background     = style.Background,
        UnderlineColor = style.UnderlineColor,
        Hyperlink      = style.Hyperlink,

        // Shape iff the flag: a shape implies the underline is ON (see ApplyTo), and a style without
        // the flag must record a REMOVAL — flag in Clear, absent from Xor, no shape — which the mask
        // below already spells. Keeps property 11 (flag and shape never disagree) true by construction.
        UnderlineShape = (style.Attributes & TextAttributes.Underline) != 0 ? style.UnderlineStyle : null,

        // Clear EVERY flag and re-set the style's own: a whole CellStyle states the entire word, so
        // the base's attributes must not survive underneath it.
        Clear          = EveryAttribute,
        Xor            = style.Attributes,
    };

    /// <summary>
    /// <see cref="From"/> minus the background: the INK of <paramref name="style"/> and no opinion
    /// about the surrounding cells, which selects STAMP mode at a glyph paint.
    /// </summary>
    public static PartialStyle FromInk(in CellStyle style) => From(style) with { Background = null };

    /// <summary>Underline in <paramref name="shape"/>, coloured <paramref name="color"/>.</summary>
    public static PartialStyle WithUnderline(UnderlineStyle? shape = null, Color? color = null) =>
        new()
        {
            UnderlineShape = shape, UnderlineColor = color,
            Clear = TextAttributes.Underline, Xor = TextAttributes.Underline
        };

    /// <summary>Remove any underline — forces the flag off and leaves no shape.</summary>
    public static PartialStyle WithoutUnderline() =>
        new() { Clear = TextAttributes.Underline, UnderlineShape = null };

    // ---- fluent composition: ONLY where `with` cannot do the job ----
    //
    // The nullable channels (Foreground, Background, UnderlineColor, Hyperlink, Mode) need no fluent
    // setter: `with { Foreground = c }` sets presence and value in one act, and the invariant that
    // once justified them — keeping a presence mask in step with a value — no longer exists.
    //
    // The ATTRIBUTE axes are different. They live in the (Clear, Xor) pair, where the correct edit is
    // an algebra rather than an assignment, and `with { Xor = … }` would let a caller write states the
    // factories exist to forbid. Those keep fluent forms, each defined as `Then` of its static — so
    // there is one implementation of the algebra, not two.

    /// <summary>Force <paramref name="flags"/> ON in addition to whatever this delta already does.</summary>
    public PartialStyle Applying(TextAttributes flags) => Then(WithApplied(flags));

    /// <summary>Force <paramref name="flags"/> IGNORED (no opinion) in addition to whatever this delta already does.</summary>
    public PartialStyle Ignoring(TextAttributes flags)
        => this with { Clear = Clear & ~Require(flags), Xor = Xor & ~flags };

    /// <summary>Force <paramref name="flags"/> OFF in addition to whatever this delta already does.</summary>
    public PartialStyle Removing(TextAttributes flags) => Then(WithRemoved(flags));

    /// <summary>INVERT <paramref name="flags"/> in addition to whatever this delta already does.</summary>
    public PartialStyle Toggling(TextAttributes flags) => Then(WithToggled(flags));

    /// <summary>Impose a weight in addition to whatever this delta already does.</summary>
    public PartialStyle Weighing(TextWeight w) => Then(Weighted(w));

    /// <summary>Impose a posture in addition to whatever this delta already does.</summary>
    public PartialStyle Posturing(TextStyle p) => Then(Postured(p));

    /// <summary>Add an underline in addition to whatever this delta already does.</summary>
    public PartialStyle Underlining(UnderlineStyle? shape = null, Color? color = null) => Then(WithUnderline(shape, color));

    /// <summary>Remove the underline in addition to whatever this delta already does.</summary>
    public PartialStyle RemovingUnderline() => Then(WithoutUnderline());

    // ---- application ----

    public CellStyle ApplyTo(in CellStyle b)
    {
        if (IsIdentity) return b;

        var mode = Mode;   // a local function in a struct cannot capture `this` (CS1673)

        return b with
        {
            Foreground     = Foreground     is { } fg ? Combine(fg, b.Foreground,     mode) : b.Foreground,
            Background     = Background     is { } bg ? Combine(bg, b.Background,     mode) : b.Background,
            UnderlineColor = UnderlineColor is { } uc ? Combine(uc, b.UnderlineColor, mode) : b.UnderlineColor,
            Hyperlink      = Hyperlink      ?? b.Hyperlink,

            // Removal resets the shape as well as the flag. Leaving a stale shape behind would be
            // invisible (a shape means nothing with the flag off) but it breaks the composition law:
            // applying set-then-remove in sequence would leave the SET shape, while the composed
            // equivalent leaves the BASE's, so `a.Then(b)` and `b(a(s))` would disagree on a field
            // nothing reads. Removing it entirely makes them agree.
            UnderlineStyle = RemovesUnderline ? default : UnderlineShape ?? b.UnderlineStyle,

            // A shape implies the flag, STRUCTURALLY rather than by convention: any delta carrying an
            // UnderlineShape turns the underline on, however it was built. That makes a plain
            // `with { UnderlineShape = … }` complete on its own, so the one coupling `with` could
            // otherwise break is not a rule anyone has to remember. Removal is the mask's job
            // (Underline in Clear, absent from Xor), which no shape accompanies.
            Attributes     = UnderlineShape is null || RemovesUnderline
                                 ? (b.Attributes & ~Clear) ^ Xor
                                 : ((b.Attributes & ~Clear) ^ Xor) | TextAttributes.Underline,
        };

        static Color Combine(Color source, Color backdrop, IBlendingMode? mode) =>
            mode is null ? source : Color.Composite(source, backdrop, mode);
    }

    /// <summary>
    /// This delta, then <paramref name="next"/> — one delta equivalent to applying both in order.
    /// The attribute algebra composes exactly: <c>Clear = C₁ | C₂</c>, <c>Xor = (X₁ &amp; ~C₂) ^ X₂</c>.
    /// </summary>
    /// <remarks>
    /// Law: <c>a.Then(b).ApplyTo(s)</c> ≡ <c>b.ApplyTo(a.ApplyTo(s))</c>, for every <c>a</c>,
    /// <c>b</c>, <c>s</c>.
    /// </remarks>
    public PartialStyle Then(in PartialStyle next) =>
        new()
        {
            Foreground = next.Foreground ?? Foreground,
            Background = next.Background ?? Background,
            UnderlineColor = next.UnderlineColor ?? UnderlineColor,
            Hyperlink = next.Hyperlink ?? Hyperlink,

            // NOT strictly `next.UnderlineShape ?? UnderlineShape`. A null shape is ambiguous on its own —
            // it means "no opinion" for a delta that leaves the underline alone, and "definitely no shape"
            // for one that REMOVES it. Check for removal, and if removed, coalesce to null. Otherwise, apply
            // `next.UnderlineShape ?? UnderlineShape`. Makes chains like `Underlining(Curly).Underlining()`
            // coherent without leaving remnants on `Underlining(Curly).RemoveUnderlining()`.
            // ...and the fallback is only THIS delta's shape when this delta did not itself remove the
            // underline. A removal RESETS the shape, so a later shapeless add inherits that reset value
            // rather than the base's — falling back unconditionally would let the base's shape survive an
            // intermediate removal, which application-in-order does not do.
            UnderlineShape = next.RemovesUnderline
                                 ? null
                                 : next.UnderlineShape ?? (RemovesUnderline ? default(UnderlineStyle) : UnderlineShape),
            Clear = Clear | next.Clear,
            Xor = (Xor & ~next.Clear) ^ next.Xor,
            Mode = next.Mode ?? Mode,
        };
}
