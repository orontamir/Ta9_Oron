namespace Ta9.ProcessorService.Domain;

public sealed record WordFrequency(string Word, int Count);

/// <summary>Statistics computed for a single document.</summary>
/// <param name="CharacterCount">Length of the original text in UTF-16 code units.</param>
/// <param name="LineCount">Number of lines; a single trailing newline does not start a new line.</param>
/// <param name="AverageWordLength">Mean word length, rounded to 2 decimals; 0 when there are no words.</param>
/// <param name="LongestWord">Longest word (ties broken alphabetically); null when there are no words.</param>
public sealed record DocumentAnalysis(
    int WordCount,
    int UniqueWordCount,
    IReadOnlyList<WordFrequency> TopWords,
    string Sha256,
    int CharacterCount,
    int LineCount,
    double AverageWordLength,
    string? LongestWord);
