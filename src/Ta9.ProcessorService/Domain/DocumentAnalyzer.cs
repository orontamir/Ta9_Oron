using System.Security.Cryptography;
using System.Text;

namespace Ta9.ProcessorService.Domain;

public interface IDocumentAnalyzer
{
    DocumentAnalysis Analyze(string content);
}

/// <summary>
/// Pure, deterministic, CPU-bound document statistics. Safe to call concurrently (no shared state).
/// </summary>
public sealed class DocumentAnalyzer : IDocumentAnalyzer
{
    public const int TopWordsCount = 5;

    public DocumentAnalysis Analyze(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var words = Tokenizer.Tokenize(content);

        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        long totalWordLength = 0;
        foreach (var word in words)
        {
            frequencies[word] = frequencies.GetValueOrDefault(word) + 1;
            totalWordLength += word.Length;
        }

        // Ties are broken alphabetically (ordinal) so the result is deterministic.
        var topWords = frequencies
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(TopWordsCount)
            .Select(pair => new WordFrequency(pair.Key, pair.Value))
            .ToList();

        var longestWord = frequencies.Keys
            .OrderByDescending(word => word.Length)
            .ThenBy(word => word, StringComparer.Ordinal)
            .FirstOrDefault();

        return new DocumentAnalysis(
            WordCount: words.Count,
            UniqueWordCount: frequencies.Count,
            TopWords: topWords,
            Sha256: ComputeSha256(content),
            CharacterCount: content.Length,
            LineCount: CountLines(content),
            AverageWordLength: words.Count == 0 ? 0 : Math.Round((double)totalWordLength / words.Count, 2),
            LongestWord: longestWord);
    }

    /// <summary>Lower-case hex SHA-256 of the original (un-normalized) text encoded as UTF-8.</summary>
    public static string ComputeSha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static int CountLines(string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        var newlines = content.AsSpan().Count('\n');
        return content[^1] == '\n' ? newlines : newlines + 1;
    }
}
