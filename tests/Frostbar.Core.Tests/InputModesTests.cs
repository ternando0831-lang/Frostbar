using Frostbar.Core.MenuBar;

namespace Frostbar.Core.Tests;

public class InputModesTests
{
    [Theory]
    [InlineData(false, 0x19, "direct")]            // off: whatever the bits say
    [InlineData(true, 0x09, "hiragana")]
    [InlineData(true, 0x19, "hiragana")]           // romaji input: the same mode
    [InlineData(true, 0x0B, "katakana")]
    [InlineData(true, 0x08, "fullAlphanumeric")]
    [InlineData(true, 0x03, "halfKatakana")]
    [InlineData(true, 0x10, "direct")]             // on with half-width letters: typed as directly
    [InlineData(true, 0x01, null)]                 // no mode of the menu's (another IME's)
    public void TheModeTheImeIsIn(bool open, long conversion, string? mode) => Assert.Equal(mode, InputModes.Current(open, conversion));

    [Fact]
    public void ChoosingAModeKeepsRomajiOrKanaInput()
    {
        Assert.Equal((true, 0x1BL), InputModes.For("katakana", 0x19));   // romaji kept
        Assert.Equal((true, 0x0BL), InputModes.For("katakana", 0x09));   // kana input kept
        Assert.Equal((true, 0x18L), InputModes.For("fullAlphanumeric", 0x19));
        Assert.Equal((false, 0x19L), InputModes.For("direct", 0x19));    // off: the bits stay for the next time it is on
        Assert.Null(InputModes.For("bogus", 0x19));
    }

    [Fact]
    public void EveryModeIsReadBackAsItself()
    {
        foreach (var (key, _, _) in InputModes.All)
        {
            var (open, conversion) = InputModes.For(key, InputModes.Roman)!.Value;
            Assert.Equal(key, InputModes.Current(open, conversion));
        }
    }

    [Fact]
    public void TheBarShowsTheModeOrTheLanguage()
    {
        Assert.Equal("あ", InputModes.BarText(true, true, 0x19, "JA"));
        Assert.Equal("カ", InputModes.BarText(true, true, 0x1B, "JA"));
        Assert.Equal(((char)0xFF76).ToString(), InputModes.BarText(true, true, 0x13, "JA"));
        Assert.Equal(((char)0xFF21).ToString(), InputModes.BarText(true, true, 0x18, "JA"));
        Assert.Equal("A", InputModes.BarText(true, false, 0x19, "JA"));
        Assert.Equal("A", InputModes.BarText(true, true, 0x01, "JA")); // a mode of no name: as direct input
        Assert.Equal("EN", InputModes.BarText(false, true, 0x19, "EN"));
    }
}
