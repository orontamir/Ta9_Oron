using System.Globalization;
using System.Text;

namespace Ta9.ProcessorService.Domain;

/// <summary>
/// Splits text into lower-cased words.
/// A word is a run of Unicode letters/digits (any script, incl. Hebrew), optionally containing
/// combining marks (e.g. Hebrew niqqud, decomposed accents) and inner apostrophes ("don't", "צה״ל").
/// Everything else (whitespace, punctuation, hyphens, symbols, emoji) separates words.
/// </summary>
public static class Tokenizer
{
    public static IReadOnlyList<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // NFC so that "café" typed as e + combining accent equals the precomposed form.
        var normalized = text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);

        var tokens = new List<string>();
        var i = 0;
        while (i < normalized.Length)
        {
            if (!IsWordStart(normalized[i]))
            {
                i++;
                continue;
            }

            var start = i++;
            while (i < normalized.Length && ContinuesWord(normalized, i))
            {
                i++;
            }

            tokens.Add(normalized[start..i].ToLowerInvariant());
        }

        return tokens;
    }

    private static bool IsWordStart(char c) => char.IsLetterOrDigit(c);

    private static bool ContinuesWord(string text, int index)
    {
        var c = text[index];
        if (char.IsLetterOrDigit(c) || IsCombiningMark(c))
        {
            return true;
        }

        // An apostrophe joins two word characters ("don't") but never starts or ends a word ("'quoted'", "dogs'").
        return IsInnerJoiner(c) && index + 1 < text.Length && char.IsLetterOrDigit(text[index + 1]);
    }

    private static bool IsCombiningMark(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    private static bool IsInnerJoiner(char c) =>
        c is '\'' // ASCII apostrophe
            or '’' // right single quotation mark (typographic apostrophe)
            or '׳' // Hebrew geresh
            or '״'; // Hebrew gershayim
}
