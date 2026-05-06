using Memory.Api;

namespace Memory.Api.Tests;

/// <summary>
/// Tests for the slugifier that turns a note's content into the leading part
/// of the .md filename inside backup zips. Filenames need to be filesystem-
/// safe across OSes, deterministic for the same input, and never empty (the
/// Markdown bundle would collapse multiple notes onto the same path
/// otherwise).
/// </summary>
public sealed class SlugifyTests
{
    [Fact]
    public void Slugify_lowercases_and_dashes_simple_text()
    {
        Assert.Equal("hello-world", BackupEndpoints.Slugify("Hello World"));
    }

    [Fact]
    public void Slugify_collapses_runs_of_separators_to_single_dash()
    {
        Assert.Equal("hello-world", BackupEndpoints.Slugify("Hello   World"));
        Assert.Equal("a-b-c", BackupEndpoints.Slugify("A___B   C"));
    }

    [Fact]
    public void Slugify_strips_punctuation()
    {
        Assert.Equal("its-a-test", BackupEndpoints.Slugify("It's a test!"));
    }

    [Fact]
    public void Slugify_truncates_to_first_40_chars_before_processing()
    {
        // The slug only sees first 40 characters of the content.
        var content = "first-40-of-this-very-long-input-and-then" + new string('x', 100);
        var slug = BackupEndpoints.Slugify(content);

        Assert.True(slug.Length <= 40);
        Assert.StartsWith("first-40-of-this-very-long", slug);
    }

    [Fact]
    public void Slugify_returns_note_for_empty_or_punctuation_only_content()
    {
        Assert.Equal("note", BackupEndpoints.Slugify(""));
        Assert.Equal("note", BackupEndpoints.Slugify("!!!"));
        Assert.Equal("note", BackupEndpoints.Slugify("---"));
    }

    [Fact]
    public void Slugify_handles_leading_and_trailing_separators()
    {
        Assert.Equal("middle", BackupEndpoints.Slugify("___middle___"));
    }

    [Fact]
    public void Slugify_drops_non_ascii_letters_outside_unicode_letterordigit()
    {
        // char.IsLetterOrDigit accepts unicode letters (incl. Polish ąćęłńóśźż).
        // The slug should keep them rather than emit empty output.
        var slug = BackupEndpoints.Slugify("Zażółć gęślą jaźń");
        Assert.Contains("zażółć", slug);
    }
}
