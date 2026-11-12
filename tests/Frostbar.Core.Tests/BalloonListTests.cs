using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class BalloonListTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 18, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void Balloon_ids_do_not_meet_windows_ids()
    {
        uint id = BalloonList.IdOf(5);
        Assert.True(BalloonList.IsBalloon(id));
        Assert.Equal(5, BalloonList.TrayNumberOf(id));
        Assert.False(BalloonList.IsBalloon(1));
        Assert.False(BalloonList.IsBalloon(123_456));
    }

    [Fact]
    public void Newest_first()
    {
        var list = new BalloonList();
        list.Add(1, "Backup", @"C:\b.exe", T0, "Done", "3 files");
        list.Add(2, "Mail", @"C:\m.exe", T0.AddSeconds(5), "New mail", "From A");
        Assert.Equal(["New mail", "Done"], list.Items.Select(n => n.Title).ToArray());
        Assert.Equal(BalloonList.IdOf(2), list.Items[0].Id);
    }

    /// <summary>An app repeating its status: one card, the latest.</summary>
    [Fact]
    public void The_same_balloon_again_within_a_minute_replaces_the_earlier()
    {
        var list = new BalloonList();
        list.Add(1, "Sync", @"C:\s.exe", T0, "Synced", "All up to date");
        list.Add(2, "Sync", @"C:\s.exe", T0.AddSeconds(30), "Synced", "All up to date");
        Assert.Single(list.Items);
        Assert.Equal(BalloonList.IdOf(2), list.Items[0].Id);
        list.Add(3, "Sync", @"C:\s.exe", T0.AddMinutes(5), "Synced", "All up to date");
        Assert.Equal(2, list.Items.Count); // later than a minute: a card of its own
    }

    [Fact]
    public void Another_text_is_another_card()
    {
        var list = new BalloonList();
        list.Add(1, "Sync", @"C:\s.exe", T0, "Synced", "All up to date");
        list.Add(2, "Sync", @"C:\s.exe", T0.AddSeconds(1), "Synced", "1 file left");
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void At_most_fifty_the_oldest_go()
    {
        var list = new BalloonList();
        for (int i = 0; i < BalloonList.Max + 7; i++) list.Add(i, "App", @"C:\a.exe", T0.AddMinutes(i * 2), "n" + i, "");
        Assert.Equal(BalloonList.Max, list.Items.Count);
        Assert.Equal("n" + (BalloonList.Max + 6), list.Items[0].Title);
        Assert.DoesNotContain(list.Items, n => n.Title == "n0");
    }

    [Fact]
    public void Removed_and_cleared()
    {
        var list = new BalloonList();
        var a = list.Add(1, "A", @"C:\a.exe", T0, "a", "");
        list.Add(2, "B", @"C:\b.exe", T0, "b", "");
        Assert.True(list.Remove(a.Id));
        Assert.False(list.Remove(a.Id));
        Assert.Single(list.Items);
        list.Clear();
        Assert.Empty(list.Items);
    }
}
