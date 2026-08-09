# Text-pipeline characterisation harness — MIGRATION SCAFFOLDING

**This directory is temporary. See "When to delete" below.**

## Why it exists

`FormattedTextRun` currently carries a resolved `CellStyle`. The plan is for runs to carry a
`BrushedStyle` plus a sampling frame instead, so the UI layer stops naming the back-buffer
format. That migration touches `FormattedText`, `TextFormatter`, `RichText` and every text path, and
it is supposed to be **purely structural**: not one glyph moves, not one output byte changes.

These baselines pin the behaviour as of `test/formatted-text-characterisation` (branched from
`feature/styling-redesign` @ `453fd9ab`), so "purely structural" is verifiable rather than hopeful.
Precedent: the `DrawText` migration was verified by dumping 14 presenter configurations before and
after — 5614 lines, zero diff — and that is what made it safe to land.

**A baseline is not a specification.** It records what the code *does*, not what it *should* do. Do
not cite one in a review as evidence that behaviour is correct, and do not put behavioural
assertions here — they belong in the ordinary test suites.

## The three tiers

| Tier | What | Baseline | Limit |
|---|---|---|---|
| 1 | `FormattedText.ToPlainText()`, string equality | `Baselines/tier1-geometry.txt` | Runs at `OutputCapabilities.None`, so capability-dependent arms take their fallback route. |
| 2 | Per-cell dump of a painted `CellBuffer` at truecolor and Ansi256 | `Baselines/tier2-styling-truecolor.txt`, `Baselines/tier2-styling-ansi256.txt` | Text sizing is OFF, so sized text shows its fallback arm here, not its fragment. |
| 3 | The emitted VT bytes for OSC 66 fragments — backdrop SGR *and* payload | `Baselines/tier3-fragment-emission.txt` | Only cases marked `EmitsFragments`. |

Tier 1 goes through the **real** paint path (`Paint(view, bounds, OutputCapabilities.None)`), not a
plain-text shortcut, so it exercises run dispatch, wrapping, trimming and the FIGlet arm. It is the
cheapest and most total of the three.

Tier 3 exists because sized text rides an OSC 66 fragment and is *never* painted into cells: Kitty is
the only terminal that implements OSC 66 and there is no headless renderer for it, so a buffer read
cannot see it. What is verifiable is what we emit. The SGR is captured as well as the payload,
because getting the right background onto sized text is the fragile part — it will not take
transparent, and a brush cannot be sampled at normal cell resolution, so `FrameRenderer` hands the
fragment the style of its **anchor cell**. That path was last changed by commit `2137e58b`, and these
baselines were captured fresh afterwards.

## Regenerating a baseline deliberately

When a change is *supposed* to move the output, accept it in one step — never hand-edit a baseline.

```sh
CURSORIAL_TEXT_CHARACTERISATION_REGENERATE=1 \
  dotnet test Cursorial.Drawing.Tests --filter FullyQualifiedName~Characterisation
```

Every baseline is rewritten and **the run then fails by design**, so a regeneration can never be
mistaken for a pass. Review `git diff` on the baselines — that diff is the change you are accepting —
then clear the variable and re-run to confirm green. `CharacterisationBaseline.AlwaysRegenerate` is
the same switch as a compile-time constant; it must stay `false` in source.

On a mismatch the harness writes the generated output beside the baseline as `<name>.txt.actual`
(git-ignored) and fails with a report naming **which corpus cases changed**, followed by the first
divergent hunk with line numbers.

## Determinism

The dumps must be reproducible byte-for-byte, or the harness trains people to ignore diffs:

- the corpus is a hand-ordered array, never a set or dictionary;
- fragment emissions — whose renderer-side order follows a `Dictionary` — are re-sorted by their
  parsed CUP anchor before being dumped, and each DECSC…DECRC bracket opens with an absolute SGR, so
  re-ordering the dump cannot change a bracket's content;
- every number is formatted with `CultureInfo.InvariantCulture`;
- no timestamps, no absolute paths (the baseline directory comes from `[CallerFilePath]`, which never
  reaches the dump), no machine-specific values;
- `TextPipelineCharacterisationTests.EveryTierIsReproducibleWithinAProcess` regenerates each tier
  twice per run and asserts the two are identical.

Documents are rebuilt per tier rather than shared, because `GlyphSource` caches resolved metrics and
`ScaledText` caches a realized placeholder — a shared instance would let one tier's capabilities leak
into the next.

## When to delete

Delete this entire directory once the run-carrier migration has landed **and its own tests cover the
behaviour**. Also remove the `FragmentEmissionSlicer` `<Compile Link>` item from
`Cursorial.Drawing.Tests.csproj`; leave `Cursorial.Rendering.Tests/FragmentEmissionSlicer.cs` in
place, since `FrameRendererDefaultColorTests` uses it.

A characterisation harness that outlives its migration becomes a maintenance tax and a source of
spurious diffs: every legitimate future change to text layout has to be re-blessed through it, and
the blessing is exactly the step people stop reading.
