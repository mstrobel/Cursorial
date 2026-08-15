// ---------------------------------------------------------------------------------------------------
// MIGRATION SCAFFOLDING — see Characterisation/README-scaffolding.md.
//
// Approval-test plumbing for the text-pipeline characterisation harness. Delete together with the rest
// of Cursorial.Drawing.Tests/Characterisation once the FormattedTextRun style-carrier migration
// (resolved CellStyle -> BrushedStyle + sampling frame) has landed and its own tests cover the
// behaviour. A characterisation harness that outlives its migration is a maintenance tax.
// ---------------------------------------------------------------------------------------------------

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cursorial.Tests.Drawing.Characterisation;

/// <summary>
/// Compares a generated dump against a committed baseline file, and regenerates it on request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Regenerating deliberately.</b> When a change is <em>supposed</em> to move the output, accept it in
/// one step rather than hand-editing baselines — either
/// </para>
/// <list type="bullet">
/// <item>set the environment variable <c>CURSORIAL_TEXT_CHARACTERISATION_REGENERATE=1</c> for one test
///       run (<c>CURSORIAL_TEXT_CHARACTERISATION_REGENERATE=1 dotnet test Cursorial.Drawing.Tests
///       --filter FullyQualifiedName~Characterisation</c>), or</item>
/// <item>flip <see cref="AlwaysRegenerate"/> to <see langword="true"/> locally.</item>
/// </list>
/// <para>
/// Either way the run REWRITES every baseline and then FAILS, so a regeneration can never be mistaken for
/// a pass. Review the resulting <c>git diff</c> — that diff is the whole point of the harness — then flip
/// the switch back and re-run to confirm green.
/// </para>
/// <para>
/// <b>Determinism contract.</b> Everything written through here must be reproducible byte-for-byte across
/// runs and machines: no timestamps, no hash-ordered enumeration, no culture-sensitive formatting, no
/// absolute paths. The baseline directory is located from <see cref="CallerFilePathAttribute"/> at compile
/// time, so the path never reaches the dump.
/// </para>
/// </remarks>
internal static class CharacterisationBaseline
{
    /// <summary>Environment variable that rewrites every baseline for one run (then fails the run).</summary>
    public const string RegenerateVariable = "CURSORIAL_TEXT_CHARACTERISATION_REGENERATE";

    /// <summary>
    /// Local escape hatch equivalent to setting <see cref="RegenerateVariable"/>. Keep this
    /// <see langword="false"/> in source — a committed <see langword="true"/> would silently accept every
    /// future change.
    /// </summary>
    public const bool AlwaysRegenerate = false;

    /// <summary>Maximum diff lines reported per side before the report truncates.</summary>
    private const int MaxHunkLines = 80;

    private static bool Regenerating =>
        AlwaysRegenerate || Environment.GetEnvironmentVariable(RegenerateVariable) is "1" or "true" or "TRUE";

    /// <summary>
    /// Compare <paramref name="actual"/> against <c>Baselines/<paramref name="baselineName"/></c>, failing
    /// with a located diff on mismatch.
    /// </summary>
    public static void Approve(string baselineName, string actual, [CallerFilePath] string callerPath = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(baselineName);
        ArgumentNullException.ThrowIfNull(actual);

        // LF everywhere so a Windows checkout and a macOS checkout produce the same bytes.
        actual = Normalize(actual);

        string directory = Path.Combine(Path.GetDirectoryName(callerPath)!, "Baselines");
        string baselinePath = Path.Combine(directory, baselineName);
        string actualPath = baselinePath + ".actual";

        if (Regenerating)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(baselinePath, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(actualPath)) File.Delete(actualPath);

            Assert.Fail($"""
                         Characterisation baseline '{baselineName}' was REGENERATED, so this run fails by design.

                           1. Review `git diff` on the baseline — that diff is the change you are accepting.
                           2. Clear {RegenerateVariable} (and {nameof(AlwaysRegenerate)}) and re-run to confirm green.
                         """);
        }

        if (!File.Exists(baselinePath))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(actualPath, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Assert.Fail($"""
                         Characterisation baseline '{baselineName}' does not exist.

                         The generated output was written beside it as '{baselineName}.actual'. If it is correct,
                         re-run with {RegenerateVariable}=1 to adopt it as the baseline.
                         """);
        }

        string expected = Normalize(File.ReadAllText(baselinePath));

        if (expected == actual)
        {
            // A stale .actual from an earlier failing run would be confusing to find next to a green tree.
            if (File.Exists(actualPath)) File.Delete(actualPath);
            return;
        }

        File.WriteAllText(actualPath, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Assert.Fail(Report(baselineName, expected, actual));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// A mismatch report that points at the damage: which corpus cases moved, then the first divergent
    /// hunk with line numbers. "Not equal" over five thousand lines is useless at 2am.
    /// </summary>
    private static string Report(string baselineName, string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');

        var report = new StringBuilder();
        report.Append("Characterisation mismatch in '").Append(baselineName).Append("'.\n\n");

        var movedCases = ChangedSections(expectedLines, actualLines);
        if (movedCases.Count > 0)
        {
            report.Append("Cases whose dump changed (").Append(movedCases.Count.ToString(CultureInfo.InvariantCulture))
                  .Append("):\n");
            foreach (var id in movedCases)
                report.Append("  - ").Append(id).Append('\n');
            report.Append('\n');
        }
        else
        {
            report.Append("No case section changed — the difference is in the file header or trailer.\n\n");
        }

        // Trim the common prefix / suffix so the hunk lands on the change instead of the file start.
        int prefix = 0;
        while (prefix < expectedLines.Length && prefix < actualLines.Length &&
               expectedLines[prefix] == actualLines[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < expectedLines.Length - prefix && suffix < actualLines.Length - prefix &&
               expectedLines[^(suffix + 1)] == actualLines[^(suffix + 1)])
        {
            suffix++;
        }

        int expectedCount = expectedLines.Length - prefix - suffix;
        int actualCount = actualLines.Length - prefix - suffix;

        report.Append("First divergence at line ").Append((prefix + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" (baseline has ").Append(expectedLines.Length.ToString(CultureInfo.InvariantCulture))
              .Append(" lines, generated ").Append(actualLines.Length.ToString(CultureInfo.InvariantCulture))
              .Append(").\n\n");

        AppendHunk(report, "- baseline", expectedLines, prefix, expectedCount);
        AppendHunk(report, "+ generated", actualLines, prefix, actualCount);

        report.Append("\nThe full generated output was written beside the baseline as '")
              .Append(baselineName).Append(".actual' — diff it with your own tools if the hunk is not enough.\n")
              .Append("If this change is intended, re-run with ").Append(RegenerateVariable)
              .Append("=1 to adopt it, then review `git diff`.\n");

        return report.ToString();
    }

    private static void AppendHunk(StringBuilder report, string label, string[] lines, int start, int count)
    {
        report.Append(label).Append(" (").Append(count.ToString(CultureInfo.InvariantCulture)).Append(" lines):\n");

        int shown = Math.Min(count, MaxHunkLines);
        for (int i = 0; i < shown; i++)
        {
            report.Append("  ")
                  .Append((start + i + 1).ToString("D5", CultureInfo.InvariantCulture))
                  .Append("| ")
                  .Append(lines[start + i])
                  .Append('\n');
        }

        if (count > shown)
        {
            report.Append("  … ").Append((count - shown).ToString(CultureInfo.InvariantCulture))
                  .Append(" more line(s) elided.\n");
        }
    }

    /// <summary>
    /// The <c>== id ==</c> sections whose bodies differ, in baseline order followed by sections that exist
    /// on only one side. Section identity is the header line, so a renamed case reads as removed + added.
    /// </summary>
    private static List<string> ChangedSections(string[] expectedLines, string[] actualLines)
    {
        var expectedSections = SplitSections(expectedLines);
        var actualSections = SplitSections(actualLines);

        var changed = new List<string>();

        foreach (var (id, body) in expectedSections)
        {
            if (!actualSections.TryGetValue(id, out var other))
                changed.Add(id + "  (missing from generated output)");
            else if (other != body)
                changed.Add(id);
        }

        foreach (var (id, _) in actualSections)
        {
            if (!expectedSections.ContainsKey(id))
                changed.Add(id + "  (new in generated output)");
        }

        return changed;
    }

    /// <summary>Section header → body, preserving file order (insertion-ordered, never hash-ordered).</summary>
    private static Dictionary<string, string> SplitSections(string[] lines)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var body = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("== ", StringComparison.Ordinal) && line.EndsWith(" ==", StringComparison.Ordinal))
            {
                if (current is not null) sections[current] = body.ToString();
                current = line[3..^3];
                body.Clear();
                continue;
            }

            if (current is not null) body.Append(line).Append('\n');
        }

        if (current is not null) sections[current] = body.ToString();
        return sections;
    }
}
