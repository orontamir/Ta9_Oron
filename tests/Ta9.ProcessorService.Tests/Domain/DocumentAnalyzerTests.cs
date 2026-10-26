using Ta9.ProcessorService.Domain;

namespace Ta9.ProcessorService.Tests.Domain;

public class DocumentAnalyzerTests
{
    private readonly DocumentAnalyzer _analyzer = new();

    [Fact]
    public void Computes_counts_and_top_words()
    {
        var result = _analyzer.Analyze("The cat and the hat. The cat sat.");

        result.WordCount.Should().Be(8);
        result.UniqueWordCount.Should().Be(5);
        result.TopWords.Should().Equal(
            new WordFrequency("the", 3),
            new WordFrequency("cat", 2),
            new WordFrequency("and", 1),
            new WordFrequency("hat", 1),
            new WordFrequency("sat", 1));
    }

    [Fact]
    public void Returns_at_most_five_top_words()
    {
        var result = _analyzer.Analyze("a b c d e f g a");

        result.TopWords.Should().HaveCount(DocumentAnalyzer.TopWordsCount);
        result.TopWords[0].Should().Be(new WordFrequency("a", 2));
    }

    [Fact]
    public void Returns_all_words_when_fewer_than_five_unique()
    {
        var result = _analyzer.Analyze("x y x");

        result.TopWords.Should().Equal(new WordFrequency("x", 2), new WordFrequency("y", 1));
    }

    [Fact]
    public void Breaks_frequency_ties_alphabetically()
    {
        var result = _analyzer.Analyze("pear apple fig pear apple fig kiwi");

        result.TopWords.Select(w => w.Word).Should().Equal("apple", "fig", "pear", "kiwi");
    }

    [Fact]
    public void Handles_empty_document()
    {
        var result = _analyzer.Analyze("");

        result.WordCount.Should().Be(0);
        result.UniqueWordCount.Should().Be(0);
        result.TopWords.Should().BeEmpty();
        result.Sha256.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        result.CharacterCount.Should().Be(0);
        result.LineCount.Should().Be(0);
        result.AverageWordLength.Should().Be(0);
        result.LongestWord.Should().BeNull();
    }

    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("hello world", "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9")]
    public void Computes_sha256_of_utf8_text(string content, string expectedHash) =>
        _analyzer.Analyze(content).Sha256.Should().Be(expectedHash);

    [Fact]
    public void Hashes_original_text_not_normalized_words()
    {
        var upper = _analyzer.Analyze("Hello");
        var lower = _analyzer.Analyze("hello");

        upper.TopWords.Should().Equal(lower.TopWords);
        upper.Sha256.Should().NotBe(lower.Sha256);
    }

    [Theory]
    [InlineData("one line", 1)]
    [InlineData("one line\n", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\r\nb\r\n", 2)]
    [InlineData("a\n\nb", 3)]
    [InlineData("\n", 1)]
    public void Counts_lines(string content, int expectedLines) =>
        _analyzer.Analyze(content).LineCount.Should().Be(expectedLines);

    [Fact]
    public void Computes_average_and_longest_word()
    {
        var result = _analyzer.Analyze("a bb ccc dd");

        result.AverageWordLength.Should().Be(2.0);
        result.LongestWord.Should().Be("ccc");
    }

    [Fact]
    public void Breaks_longest_word_ties_alphabetically() =>
        _analyzer.Analyze("zeta beta alfa").LongestWord.Should().Be("alfa");

    [Fact]
    public void Rounds_average_word_length_to_two_decimals() =>
        _analyzer.Analyze("ab abc abc").AverageWordLength.Should().Be(2.67);

    [Fact]
    public void Counts_characters_of_original_text() =>
        _analyzer.Analyze("Hi, there!").CharacterCount.Should().Be(10);

    [Fact]
    public void Analyzes_hebrew_document()
    {
        var result = _analyzer.Analyze("שלום עולם. שלום לכולם, שלום!");

        result.WordCount.Should().Be(5);
        result.UniqueWordCount.Should().Be(3);
        result.TopWords[0].Should().Be(new WordFrequency("שלום", 3));
    }

    [Fact]
    public void Is_deterministic()
    {
        const string text = "to be or not to be that is the question";

        var first = _analyzer.Analyze(text);
        var second = _analyzer.Analyze(text);

        second.Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Handles_a_large_document_correctly()
    {
        // ~100 KB: 20,000 words cycling through 4 distinct words.
        var words = new[] { "alpha", "beta", "gamma", "delta" };
        var text = string.Join(' ', Enumerable.Range(0, 20_000).Select(i => words[i % words.Length]));

        var result = _analyzer.Analyze(text);

        result.WordCount.Should().Be(20_000);
        result.UniqueWordCount.Should().Be(4);
        result.TopWords.Should().OnlyContain(w => w.Count == 5_000);
        result.TopWords.Select(w => w.Word).Should().Equal("alpha", "beta", "delta", "gamma");
    }

    [Fact]
    public void Throws_on_null() =>
        FluentActions.Invoking(() => _analyzer.Analyze(null!)).Should().Throw<ArgumentNullException>();
}
