using Frostbar.Core.Dock;

namespace Frostbar.Core.Tests;

public class StackLabelTests
{
    /// <summary>Full-width characters twice as wide as the others.</summary>
    static double W(string s) => s.Sum(c => c >= 0x3000 ? 2.0 : 1.0);

    [Fact]
    public void ANameThatFitsStaysOnOneLine() => Assert.Equal("memo.txt", StackLabel.TwoLines("memo.txt", W, 12));

    [Fact]
    public void ALongNameBreaksAfterASpaceOrBeforeItsExtension()
    {
        Assert.Equal("quarterly\nreport.pdf", StackLabel.TwoLines("quarterly report.pdf", W, 12));
        Assert.Equal("abcdefg\n.hij.pdf", StackLabel.TwoLines("abcdefg.hij.pdf", W, 10));
    }

    [Fact]
    public void ABreakInTheFirstHalfOfTheLineIsNotTaken() =>
        Assert.Equal("a bcdefghij\nklm", StackLabel.TwoLines("a bcdefghijklm", W, 11));

    [Fact]
    public void AHyphenBeforeADigitIsNotABreak()
    {
        Assert.Equal("見積書\n2026-10.pdf", StackLabel.TwoLines("見積書 2026-10.pdf", W, 12));
        Assert.Equal("見積書\n2026-10", StackLabel.TwoLines("見積書 2026-10", W, 12)); // as File Explorer shows it, the extension hidden
    }

    [Fact]
    public void JapaneseBreaksBetweenCharactersButNotBeforeASmallKana() =>
        Assert.Equal("スクリーン\nショット", StackLabel.TwoLines("スクリーンショット", W, 12));

    [Fact]
    public void ATooLongNameIsShortenedInTheMiddleOfItsSecondLineKeepingItsEnd()
    {
        string label = StackLabel.TwoLines("スクリーンショット 2026-10-12 143212.png", W, 14);
        var lines = label.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.True(W(l) <= 14, $"{l} is {W(l)} wide"));
        Assert.StartsWith("スクリーン", lines[0]);
        Assert.Contains("…", lines[1]);
        Assert.EndsWith(".png", lines[1]);
    }
}
