using ServerManager.Core.Content;

namespace ServerManager.Core.Tests;

/// <summary>
/// Provider descriptions are written by other people. They are shown as text and never as
/// markup, so nothing in them can render, navigate or run.
/// </summary>
public sealed class ContentTextSanitizerTests
{
    [Fact]
    public void ScriptAndMarkupAreRemovedEntirely()
    {
        var result = ContentTextSanitizer.ToPlainText(
            "<p>Hello</p><script>alert('x')</script><img src=\"https://evil.test/a.png\">World");

        Assert.Contains("Hello", result, StringComparison.Ordinal);
        Assert.Contains("World", result, StringComparison.Ordinal);
        Assert.DoesNotContain("<", result, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLinksKeepTheirWordsAndLoseTheirTargets()
    {
        var result = ContentTextSanitizer.ToPlainText(
            "See [the docs](https://example.test/docs) and ![banner](https://example.test/b.png)");

        Assert.Contains("the docs", result, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", result, StringComparison.Ordinal);
        Assert.DoesNotContain("banner", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptUrlsDoNotSurvive()
    {
        var result = ContentTextSanitizer.ToPlainText(
            "Click javascript:alert(1) or data:text/html;base64,PHNjcmlwdD4=");

        Assert.DoesNotContain("javascript:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhitespaceIsCollapsedAndVeryLongTextIsTrimmed()
    {
        Assert.Equal("one two three", ContentTextSanitizer.ToPlainText("  one \r\n\t two    three  "));

        var long_ = ContentTextSanitizer.ToPlainText(new string('a', 8000));
        Assert.True(long_.Length < 4100);
        Assert.EndsWith("…", long_, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingInMeansNothingOut()
    {
        Assert.Equal(string.Empty, ContentTextSanitizer.ToPlainText(null));
        Assert.Equal(string.Empty, ContentTextSanitizer.ToPlainText("   "));
    }
}
