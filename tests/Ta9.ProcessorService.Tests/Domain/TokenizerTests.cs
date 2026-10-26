using Ta9.ProcessorService.Domain;

namespace Ta9.ProcessorService.Tests.Domain;

public class TokenizerTests
{
    [Theory]
    [InlineData("Hello, world! Hello.", new[] { "hello", "world", "hello" })]
    [InlineData("Apple APPLE apple", new[] { "apple", "apple", "apple" })]
    [InlineData("one\ttwo\nthree\r\nfour five", new[] { "one", "two", "three", "four", "five" })]
    [InlineData("version 2 of 10", new[] { "version", "2", "of", "10" })]
    [InlineData("well-known e-mail", new[] { "well", "known", "e", "mail" })]
    [InlineData("(a) [b] {c} \"d\" <e>", new[] { "a", "b", "c", "d", "e" })]
    public void Splits_on_non_word_characters_and_lower_cases(string text, string[] expected) =>
        Tokenizer.Tokenize(text).Should().Equal(expected);

    [Theory]
    [InlineData("don't stop", new[] { "don't", "stop" })]
    [InlineData("rock’n’roll", new[] { "rock’n’roll" })]
    [InlineData("'quoted'", new[] { "quoted" })]
    [InlineData("dogs' bones", new[] { "dogs", "bones" })]
    [InlineData("it''s", new[] { "it", "s" })]
    public void Keeps_only_inner_apostrophes(string text, string[] expected) =>
        Tokenizer.Tokenize(text).Should().Equal(expected);

    [Fact]
    public void Supports_hebrew()
    {
        Tokenizer.Tokenize("שלום עולם, שלום!").Should().Equal("שלום", "עולם", "שלום");
    }

    [Fact]
    public void Keeps_hebrew_niqqud_inside_the_word()
    {
        Tokenizer.Tokenize("שָׁלוֹם לְךָ").Should().HaveCount(2);
    }

    [Fact]
    public void Keeps_hebrew_gershayim_inside_the_word()
    {
        Tokenizer.Tokenize("צה״ל").Should().Equal("צה״ל");
    }

    [Fact]
    public void Treats_composed_and_decomposed_accents_as_the_same_word()
    {
        var precomposed = Tokenizer.Tokenize("café");
        var decomposed = Tokenizer.Tokenize("café");

        decomposed.Should().Equal(precomposed);
        precomposed.Should().Equal("café");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \t\r\n ")]
    [InlineData("!?.,;:-— …")]
    public void Returns_no_words_for_text_without_letters_or_digits(string text) =>
        Tokenizer.Tokenize(text).Should().BeEmpty();

    [Fact]
    public void Ignores_emoji_and_symbols()
    {
        Tokenizer.Tokenize("good 👍 job ★").Should().Equal("good", "job");
    }

    [Fact]
    public void Throws_on_null() =>
        FluentActions.Invoking(() => Tokenizer.Tokenize(null!)).Should().Throw<ArgumentNullException>();
}
