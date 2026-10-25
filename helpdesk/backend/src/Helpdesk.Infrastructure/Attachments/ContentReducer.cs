using System.Text;
using System.Text.RegularExpressions;
using Helpdesk.Core;

namespace Helpdesk.Infrastructure.Attachments;

/// <summary>
/// Cuts a long file down to a token budget without losing the lines that matter. A log is mostly noise around a few
/// failures, so it keeps the start (how it began), the end (how it finished) and, from the middle, the lines that look
/// like errors, with a line of context each, and says how much was left out.
/// </summary>
public static partial class ContentReducer
{
    private const int MaxLineChars = 400;

    public static (string Text, bool Reduced) Reduce(string text, int maxTokens)
    {
        var maxChars = Math.Max(0, maxTokens) * 4;
        if (text.Length <= maxChars) return (text, false);

        var lines = text.Split('\n');
        if (lines.Length < 8) return (HeadAndTail(text, maxChars), true);

        var headBudget = maxChars / 4;
        var tailBudget = maxChars * 35 / 100;
        var middleBudget = maxChars - headBudget - tailBudget;

        var head = TakeFromStart(lines, headBudget, out var headEnd);
        var tail = TakeFromEnd(lines, tailBudget, Math.Max(headEnd, 0), out var tailStart);

        var kept = new List<int>();
        var used = 0;
        for (var i = headEnd; i < tailStart && used < middleBudget; i++)
        {
            if (!Interesting().IsMatch(lines[i])) continue;
            foreach (var j in new[] { i - 1, i, i + 1 })
            {
                if (j < headEnd || j >= tailStart || (kept.Count > 0 && kept[^1] >= j)) continue;
                kept.Add(j);
                used += Clip(lines[j]).Length + 1;
            }
        }

        var result = new StringBuilder(head);
        var previous = headEnd - 1;
        string? lastLine = null;
        foreach (var index in kept)
        {
            var line = Clip(lines[index]);
            if (index > previous + 1) result.Append($"[... {index - previous - 1} lines omitted ...]\n");
            if (line != lastLine) result.Append(line).Append('\n');
            lastLine = line;
            previous = index;
        }
        if (tailStart > previous + 1) result.Append($"[... {tailStart - previous - 1} lines omitted ...]\n");
        result.Append(tail);
        return (result.ToString().TrimEnd('\n'), true);
    }

    /// <summary>The number of tokens, estimated, that <paramref name="text"/> would add to a prompt.</summary>
    public static int Tokens(string text) => InferenceRoutePolicy.EstimateTokens([text]);

    private static string TakeFromStart(string[] lines, int budget, out int endExclusive)
    {
        var sb = new StringBuilder();
        endExclusive = 0;
        while (endExclusive < lines.Length)
        {
            var line = Clip(lines[endExclusive]);
            if (sb.Length + line.Length + 1 > budget && endExclusive > 0) break;
            sb.Append(line).Append('\n');
            endExclusive++;
        }
        return sb.ToString();
    }

    private static string TakeFromEnd(string[] lines, int budget, int notBefore, out int startInclusive)
    {
        var taken = new List<string>();
        var size = 0;
        startInclusive = lines.Length;
        while (startInclusive > notBefore)
        {
            var line = Clip(lines[startInclusive - 1]);
            if (size + line.Length + 1 > budget && taken.Count > 0) break;
            taken.Add(line);
            size += line.Length + 1;
            startInclusive--;
        }
        taken.Reverse();
        return string.Join('\n', taken);
    }

    private static string HeadAndTail(string text, int maxChars)
    {
        var half = maxChars / 2;
        return $"{text[..half]}\n[... {text.Length - maxChars} characters omitted ...]\n{text[^half..]}";
    }

    private static string Clip(string line)
    {
        line = line.TrimEnd('\r');
        return line.Length <= MaxLineChars ? line : line[..MaxLineChars] + "…";
    }

    [GeneratedRegex(
        @"\b(error|exception|fatal|fail(ed|ure|s)?|panic|critical|denied|refused|reset|timeout|timed\s+out|unable|cannot|can't|warn(ing)?|crash(ed)?|abort(ed)?|traceback|unhandled)\b|0x[0-9a-f]{4,}|^\s+at\s|^\s*File "".+"", line \d+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Interesting();
}
