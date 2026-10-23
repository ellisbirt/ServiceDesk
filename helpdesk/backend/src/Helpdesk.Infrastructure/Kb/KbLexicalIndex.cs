using Helpdesk.Core.Interfaces;

namespace Helpdesk.Infrastructure.Kb;

public static class KbText
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "if", "of", "to", "in", "on", "at", "for", "with", "from", "by", "as", "is",
        "are", "was", "were", "be", "been", "am", "it", "its", "this", "that", "these", "those", "my", "me", "i", "we",
        "you", "your", "our", "can", "cant", "cannot", "could", "would", "should", "will", "do", "does", "did", "not",
        "no", "so", "than", "then", "there", "when", "what", "why", "how", "which", "who", "has", "have", "had", "get",
        "got", "please", "help", "need", "want", "just", "still", "again", "also", "about", "any", "some", "up", "out"
    };

    /// <summary>Lower-cased alphanumeric terms with stop words removed and a light suffix strip, so "printers" matches "printer".</summary>
    public static List<string> Tokenize(string? text)
    {
        var terms = new List<string>();
        if (string.IsNullOrEmpty(text)) return terms;

        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isTerm = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isTerm && start < 0) start = i;
            if (isTerm || start < 0) continue;

            var raw = text.AsSpan(start, i - start).ToString().ToLowerInvariant();
            start = -1;
            if (raw.Length < 2 || StopWords.Contains(raw)) continue;
            terms.Add(Stem(raw));
        }
        return terms;
    }

    private static string Stem(string term)
    {
        // Identifiers such as error codes and KB numbers contain digits and must match exactly.
        if (term.Any(char.IsDigit)) return term;
        if (term.Length > 5 && term.EndsWith("ing", StringComparison.Ordinal)) return term[..^3];
        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal)) return term[..^3] + "y";
        if (term.Length > 4 && term.EndsWith("ed", StringComparison.Ordinal)) return term[..^2];
        if (term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal)) return term[..^1];
        return term;
    }
}

/// <summary>
/// An in-memory BM25 index over one tenant's articles. Fields are weighted (a title match counts more than a body
/// match). Lexical search is what finds exact identifiers, such as an error code, a product name or a KB number,
/// that a vector search can blur.
/// </summary>
public sealed class KbLexicalIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int MaxQueryTerms = 40;

    private sealed record Entry(KbDocument Doc, Dictionary<string, double> Frequencies, double Length);

    private readonly Dictionary<string, Entry> _entries;
    private readonly Dictionary<string, int> _documentFrequency = [];
    private readonly double _averageLength;

    public static KbLexicalIndex Empty { get; } = new([]);

    public KbLexicalIndex(IEnumerable<KbDocument> documents)
    {
        _entries = [];
        foreach (var doc in documents)
        {
            var frequencies = new Dictionary<string, double>(StringComparer.Ordinal);
            var length = 0.0;
            void Add(string? text, double weight)
            {
                foreach (var term in KbText.Tokenize(text))
                {
                    frequencies[term] = frequencies.GetValueOrDefault(term) + weight;
                    length += weight;
                }
            }

            Add(doc.Number, 3);
            Add(doc.Title, 3);
            Add(string.Join(' ', doc.Tags), 3);
            Add(doc.Summary, 2);
            Add(doc.Symptoms, 2);
            Add(doc.Cause, 1);
            Add(doc.Resolution, 1);
            Add(doc.Body, 1);

            _entries[doc.Id] = new Entry(doc, frequencies, length);
            foreach (var term in frequencies.Keys) _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
        }
        _averageLength = _entries.Count == 0 ? 1 : Math.Max(1, _entries.Values.Average(e => e.Length));
    }

    public int Count => _entries.Count;

    public KbDocument? Find(string id) => _entries.TryGetValue(id, out var entry) ? entry.Doc : null;

    /// <summary>
    /// Best matches first. An article counts only when it contains at least two of the query's distinct terms (or the
    /// one term of a one-word query), so a single common word cannot pull in an unrelated article. A matching
    /// identifier (KB number, error code) is enough on its own.
    /// </summary>
    public IReadOnlyList<(KbDocument Doc, double Score)> Search(string query, int take)
    {
        var terms = KbText.Tokenize(query).Distinct(StringComparer.Ordinal).Take(MaxQueryTerms).ToList();
        if (terms.Count == 0 || _entries.Count == 0) return [];

        var required = Math.Min(2, terms.Count);
        var n = _entries.Count;
        var results = new List<(KbDocument, double)>();
        foreach (var entry in _entries.Values)
        {
            var score = 0.0;
            var matched = 0;
            var identifierMatched = false;
            foreach (var term in terms)
            {
                if (!entry.Frequencies.TryGetValue(term, out var tf)) continue;
                matched++;
                identifierMatched |= IsIdentifier(term);
                var df = _documentFrequency[term];
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * entry.Length / _averageLength));
            }
            if (matched >= required || identifierMatched) results.Add((entry.Doc, score));
        }
        return results.OrderByDescending(r => r.Item2).ThenBy(r => r.Item1.Number, StringComparer.Ordinal).Take(take).ToList();
    }

    /// <summary>A KB number or error code (letters and digits together, such as KB0001002 or 0x80070005) is specific enough to match alone.</summary>
    private static bool IsIdentifier(string term) =>
        term.Length >= 4 && term.Any(char.IsDigit) && term.Any(char.IsLetter);
}

/// <summary>Merges the semantic and lexical rankings with reciprocal rank fusion (RRF).</summary>
public static class HybridRanker
{
    public sealed record Fused(string Id, double Score, string Match);

    /// <summary>
    /// RRF scores an item by <c>1 / (k + rank)</c> in every list it appears in and adds them up. It needs only the ranks, so
    /// the unrelated score scales of cosine similarity and BM25 never have to be normalised against each other.
    /// An article ranked well by both searches beats one ranked first by only one.
    /// </summary>
    public static IReadOnlyList<Fused> Fuse(IReadOnlyList<string> semantic, IReadOnlyList<string> lexical, int k, int take)
    {
        var scores = new Dictionary<string, (double Score, bool Semantic, bool Lexical)>(StringComparer.Ordinal);

        void Add(IReadOnlyList<string> ranking, bool isSemantic)
        {
            for (var i = 0; i < ranking.Count; i++)
            {
                var (score, sem, lex) = scores.GetValueOrDefault(ranking[i]);
                scores[ranking[i]] = (score + 1.0 / (k + i + 1), sem || isSemantic, lex || !isSemantic);
            }
        }
        Add(semantic, true);
        Add(lexical, false);

        return scores
            .OrderByDescending(s => s.Value.Score)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(s => new Fused(s.Key, s.Value.Score,
                s.Value is { Semantic: true, Lexical: true } ? KbMatch.Both : s.Value.Semantic ? KbMatch.Semantic : KbMatch.Lexical))
            .ToList();
    }
}
