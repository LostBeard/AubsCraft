using AubsCraft.Admin.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Chat lines wrapped for a UI that draws one row per line (every character 10 units wide here).</summary>
public class TextWrapTests
{
    private static float Wide(string s) => s.Length * 10f;

    [Test]
    public void ShortTextIsOneLine() =>
        Assert.That(TextWrap.Wrap("hello world", 200, 200, Wide), Is.EqualTo(new[] { "hello world" }));

    [Test]
    public void BreaksAtSpaces_AndTheFirstLineHasItsOwnBudget()
    {
        // first line 5 chars (50 units), the rest 8 chars
        var lines = TextWrap.Wrap("aa bb cc dd ee ff", 50, 80, Wide);
        Assert.That(lines, Is.EqualTo(new[] { "aa bb", "cc dd ee", "ff" }));
        Assert.That(lines[0].Length * 10, Is.LessThanOrEqualTo(50));
        Assert.That(lines.Skip(1).All(l => l.Length * 10 <= 80), Is.True);
    }

    [Test]
    public void AWordWiderThanALineIsCut_AndNothingIsLost()
    {
        var lines = TextWrap.Wrap("abcdefghijklmnopqrstuvwxyz", 100, 100, Wide);
        Assert.That(string.Concat(lines), Is.EqualTo("abcdefghijklmnopqrstuvwxyz"));
        Assert.That(lines.All(l => l.Length * 10 <= 100), Is.True);
        Assert.That(lines, Has.Count.EqualTo(3));
    }

    [Test]
    public void EmptyTextIsOneEmptyLine_NeverZeroLines() =>
        Assert.That(TextWrap.Wrap("", 100, 100, Wide), Is.EqualTo(new[] { "" }));

    [Test]
    public void EveryWordSurvivesWrapping()
    {
        const string text = "the quick brown fox jumps over the lazy dog and keeps running far away";
        var lines = TextWrap.Wrap(text, 130, 150, Wide);
        Assert.That(string.Join(" ", lines), Is.EqualTo(text));
        Assert.That(lines.All(l => l.Length * 10 <= 150), Is.True);
    }
}
