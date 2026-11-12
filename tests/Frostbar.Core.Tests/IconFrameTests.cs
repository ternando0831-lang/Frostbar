using Frostbar.Core.Icons;
using Xunit;

namespace Frostbar.Core.Tests;

public class IconFrameTests
{
    const int S = 256;

    /// <summary>A 256 px image: a ring of the given alpha at the edge, a square of opaque pixels of the given size in the middle.</summary>
    static byte[] Image(byte ringAlpha, int ringWidth, int middle, byte middleAlpha = 255)
    {
        var px = new byte[S * S * 4];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                int edge = Math.Min(Math.Min(x, y), Math.Min(S - 1 - x, S - 1 - y));
                bool inMiddle = Math.Abs(x - S / 2 + 0.5) < middle / 2.0 && Math.Abs(y - S / 2 + 0.5) < middle / 2.0;
                byte a = inMiddle ? middleAlpha : edge < ringWidth ? ringAlpha : (byte)0;
                px[(y * S + x) * 4 + 3] = a;
                px[(y * S + x) * 4 + 2] = a; // premultiplied grey
            }
        return px;
    }

    [Fact]
    public void ASmallIconInAFaintFrameIsFramed() => Assert.True(IconFrame.IsFramed(Image(0x26, 5, 64), S, S));

    [Fact]
    public void AnIconWithClearEdgesIsNot() => Assert.False(IconFrame.IsFramed(Image(0, 0, 220), S, S));

    [Fact]
    public void ASmallPictureWithClearEdgesIsNot() => Assert.False(IconFrame.IsFramed(Image(0, 0, 64), S, S)); // lots of padding, but no frame

    [Fact]
    public void AFullBleedIconIsNot() => Assert.False(IconFrame.IsFramed(Image(0xFF, 256, 0), S, S)); // a square photo-like icon: opaque to the edges

    [Fact]
    public void AFaintRingAroundALargePictureIsNot() => Assert.False(IconFrame.IsFramed(Image(0x26, 5, 220), S, S)); // its own soft shadow: the band inside is not empty

    [Fact]
    public void SmallImagesAreNeverFramed() => Assert.False(IconFrame.IsFramed(new byte[48 * 48 * 4], 48, 48));
}
