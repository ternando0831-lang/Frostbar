using Frostbar.Core.Overview;
using Frostbar.Core.Wm;

namespace Frostbar.Core.Tests;

public sealed class DesktopStripTests
{
    static readonly Guid A = new("4C43B5E8-3219-46CF-9276-05A00B13115B"), B = new("3FA704A7-3F6A-44EB-A24D-520B34DAACCC"), C = new("921B92A4-350B-47B9-8B1D-312DF625CF7D");

    [Fact]
    public void The_registry_order_with_a_just_added_current_desktop_at_its_end()
    {
        Assert.Equal([A, B], DesktopStrip.Order([A, B], B));
        Assert.Equal([A, B, C], DesktopStrip.Order([A, B], C)); // added a moment ago: not listed yet (ADR-014)
        Assert.Equal([A, B], DesktopStrip.Order([A, B, A, Guid.Empty], Guid.Empty));
    }

    [Fact]
    public void Unnamed_desktops_are_numbered_by_their_place()
    {
        var names = DesktopStrip.Names(["", "仕事", null, "  "], n => $"デスクトップ {n}");
        Assert.Equal(["デスクトップ 1", "仕事", "デスクトップ 3", "デスクトップ 4"], names);
    }

    [Fact]
    public void Pictures_keep_the_screens_shape_in_a_centred_row_with_the_add_button_at_the_end()
    {
        // 1536 DIP wide (1920 px at 125%), pictures 63 high at 16:9 (112 wide), 18 apart; the button 40 square, 14 from the right.
        var s = DesktopStrip.Arrange(3, 1536, 16.0 / 9, 63, 18, 14, top: 10, nameGap: 4, nameHeight: 16, addSize: 40, add: true);
        Assert.Equal(3, s.Pictures.Count);
        Assert.Equal(new Rect2(1536 / 2.0 - (3 * 112 + 2 * 18) / 2.0, 10, 112, 63), s.Pictures[0]);
        Assert.Equal(s.Pictures[0].Right + 18, s.Pictures[1].X);
        Assert.Equal((s.Pictures[0].X + s.Pictures[2].Right) / 2, 1536 / 2.0, 6); // centred on the strip
        Assert.Equal(new Rect2(s.Pictures[1].X, 10 + 63 + 4, 112, 16), s.Names[1]); // under its picture
        Assert.Equal(new Rect2(1536 - 14 - 40, 10 + (63 - 40) / 2.0, 40, 40), s.Add);
        Assert.Equal(10 + 63 + 4 + 16, s.Height);
    }

    [Fact]
    public void Many_desktops_get_smaller_to_fit_and_the_button_can_be_left_out()
    {
        var s = DesktopStrip.Arrange(20, 1536, 16.0 / 9, 63, 18, 14, top: 10, nameGap: 4, nameHeight: 16, addSize: 40, add: true);
        Assert.True(s.Pictures[0].X >= 14 && s.Pictures[^1].Right <= 1536 - 14 - 40 - 18 + 1e-6, $"{s.Pictures[0].X}..{s.Pictures[^1].Right}");
        Assert.Equal(16.0 / 9, s.Pictures[0].Width / s.Pictures[0].Height, 6); // still the screen's shape
        Assert.True(s.Pictures[0].Height < 63);
        var none = DesktopStrip.Arrange(2, 1536, 16.0 / 9, 63, 18, 14, top: 10, nameGap: 4, nameHeight: 16, addSize: 40, add: false);
        Assert.Null(none.Add);
    }

    [Fact]
    public void A_removed_desktops_windows_go_to_the_one_on_its_left_or_for_the_first_to_its_right()
    {
        Assert.Equal(A, DesktopStrip.Fallback([A, B, C], B));
        Assert.Equal(B, DesktopStrip.Fallback([A, B, C], C));
        Assert.Equal(B, DesktopStrip.Fallback([A, B, C], A)); // the first: its right neighbour
        Assert.Null(DesktopStrip.Fallback([A], A));             // the only one cannot be removed
        Assert.Null(DesktopStrip.Fallback([A, B], C));          // not among them
    }

    [Fact]
    public void A_window_let_go_on_or_near_a_picture_is_for_that_desktop()
    {
        Rect2[] pictures = [new(100, 10, 112, 63), new(230, 10, 112, 63)];
        Assert.Equal(0, DesktopStrip.PictureAt(pictures, 150, 40, 6));
        Assert.Equal(1, DesktopStrip.PictureAt(pictures, 226, 40, 6));  // just left of the second, within the slack
        Assert.Equal(-1, DesktopStrip.PictureAt(pictures, 221, 40, 6)); // in the gap between them
        Assert.Equal(-1, DesktopStrip.PictureAt(pictures, 150, 90, 6)); // under the pictures (the names)
    }

    [Fact]
    public void A_window_is_the_screen_scaled_into_the_picture()
    {
        var screen = new Rect2(0, 0, 1920, 1080);
        var picture = new Rect2(100, 10, 112, 63);
        Assert.Equal(new Rect2(100 + 56, 10 + 31.5, 56, 31.5), DesktopStrip.InPicture(new Rect2(960, 540, 960, 540), screen, picture));
        // A window partly off the screen is cut at the picture's edges.
        var off = DesktopStrip.InPicture(new Rect2(-200, -100, 400, 300), screen, picture);
        Assert.Equal(100, off.X);
        Assert.Equal(10, off.Y);
        Assert.True(off.Right <= picture.Right && off.Bottom <= picture.Bottom);
    }
}
