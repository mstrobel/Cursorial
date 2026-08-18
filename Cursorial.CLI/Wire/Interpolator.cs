using System;
using System.Text;

namespace Cursorial.CLI.Wire;

/// <summary>
/// In-process argv interpolation (docs/cli-design.md §4.2): replaces <c>{accessor}</c> occurrences
/// inside each token with values from a <see cref="VariableBag"/>. Substitution happens on the argv
/// array — values are never re-parsed by a shell, so there is no quoting or injection hazard.
/// </summary>
public static class Interpolator
{
    /// <summary>
    /// Return a new argv with every <c>{accessor}</c> replaced via
    /// <see cref="VariableBag.TryResolve"/> (unresolved accessors become empty), <c>{{</c> as a
    /// literal <c>{</c>, and <c>}}</c> as a literal <c>}</c>. Substitution is a single pass:
    /// braces inside a substituted value are literal, never re-interpolated. A brace with no mate
    /// passes through literally. The input array is never mutated.
    /// </summary>
    public static string[] Apply(string[] stepArgv, VariableBag vars)
    {
        ArgumentNullException.ThrowIfNull(stepArgv);
        ArgumentNullException.ThrowIfNull(vars);

        var result = new string[stepArgv.Length];
        for (var i = 0; i < stepArgv.Length; i++)
            result[i] = ApplyToken(stepArgv[i], vars);

        return result;
    }

    private static string ApplyToken(string token, VariableBag vars)
    {
        if (token.AsSpan().IndexOfAny('{', '}') < 0)
            return token;

        var result = new StringBuilder(token.Length);

        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];

            if (c == '{')
            {
                if (i + 1 < token.Length && token[i + 1] == '{')
                {
                    result.Append('{');
                    i++;
                    continue;
                }

                var close = token.IndexOf('}', i + 1);
                if (close < 0)
                {
                    result.Append('{'); // unmatched — literal
                    continue;
                }

                if (vars.TryResolve(token[(i + 1)..close], out var value))
                    result.Append(value);

                i = close;
                continue;
            }

            if (c == '}' && i + 1 < token.Length && token[i + 1] == '}')
            {
                result.Append('}');
                i++;
                continue;
            }

            result.Append(c); // includes an unmatched '}' — literal
        }

        return result.ToString();
    }
}
