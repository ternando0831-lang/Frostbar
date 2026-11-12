using Frostbar.Core.Settings;

namespace Frostbar.Core.Tests;

/// <summary>settings.json written off the UI thread (2026-10-24): in turn, only the newest of those waiting, waited for.</summary>
public sealed class SettingsSaverTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "frostbar-tests-" + Guid.NewGuid().ToString("N"));
    string File_ => Path.Combine(_dir, "settings.json");
    static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    public SettingsSaverTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static AppSettings WithIcons(int size) { var s = new AppSettings(); s.Dock.IconSize = size; return s; }

    [Fact]
    public void A_save_is_written_by_the_time_the_wait_returns()
    {
        var failed = new List<string>();
        var saver = new SettingsSaver(File_, (what, _) => failed.Add(what));
        saver.Save(WithIcons(56));
        Assert.True(saver.Wait(Long));
        Assert.Equal(56, SettingsStore.Load(File_).Settings.Dock.IconSize);
        Assert.Empty(failed);
    }

    /// <summary>The saves asked for while one is written: only the last of them is written after it, and it is written last.</summary>
    [Fact]
    public void Of_the_saves_waiting_only_the_newest_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var written = new List<int>();
        using var entered = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var saver = new SettingsSaver(File_, (_, _) => { }, write: (path, text) =>
        {
            entered.Set();
            go.Wait(Long, ct);
            lock (written) written.Add(System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("dock").GetProperty("iconSize").GetInt32());
            return SettingsStore.Write(path, text);
        });
        saver.Save(WithIcons(40));
        Assert.True(entered.Wait(Long, ct));
        saver.Save(WithIcons(44));
        saver.Save(WithIcons(48));
        saver.Save(WithIcons(52));
        go.Set();
        Assert.True(saver.Wait(Long));
        Assert.Equal([40, 52], written);
        Assert.Equal(52, SettingsStore.Load(File_).Settings.Dock.IconSize);
    }

    [Fact]
    public void The_wait_says_false_while_a_write_is_held()
    {
        var ct = TestContext.Current.CancellationToken;
        using var go = new ManualResetEventSlim();
        var saver = new SettingsSaver(File_, (_, _) => { }, write: (path, text) => { go.Wait(Long, ct); return SettingsStore.Write(path, text); });
        saver.Save(WithIcons(56));
        Assert.False(saver.Wait(TimeSpan.FromMilliseconds(200)));
        go.Set();
        Assert.True(saver.Wait(Long));
    }

    /// <summary>A reader with the file open (the specs read settings.json) only holds the save off for a moment.</summary>
    [Fact]
    public async Task A_save_held_off_by_a_reader_is_tried_again()
    {
        SettingsStore.Save(File_, WithIcons(40));
        var failed = new List<string>();
        var slow = new List<int>();
        var saver = new SettingsSaver(File_, (what, ex) => failed.Add(what + " " + ex?.Message), (_, tries) => slow.Add(tries));
        var held = new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.Read);
        var letGo = Task.Delay(250, TestContext.Current.CancellationToken).ContinueWith(_ => held.Dispose(), TestContext.Current.CancellationToken);
        saver.Save(WithIcons(64));
        Assert.True(saver.Wait(Long));
        await letGo;
        Assert.Empty(failed);
        Assert.Equal(64, SettingsStore.Load(File_).Settings.Dock.IconSize);
        Assert.True(slow.Count == 0 || slow[0] > 1, "a slow write was told with its tries");
    }

    [Fact]
    public void A_reader_that_does_not_let_go_ends_in_a_failure_told()
    {
        SettingsStore.Save(File_, WithIcons(40));
        var failed = new List<string>();
        var saver = new SettingsSaver(File_, (what, _) => failed.Add(what));
        using (new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            saver.Save(WithIcons(64));
            Assert.True(saver.Wait(Long));
        }
        Assert.Equal(["saving failed"], failed);
        Assert.Equal(40, SettingsStore.Load(File_).Settings.Dock.IconSize);
    }

    [Fact]
    public void A_file_that_could_not_be_read_is_not_written_and_that_is_told()
    {
        File.WriteAllText(File_, """{ "schemaVersion": 1, "dock": { "iconSize": 64 } }""");
        SettingsLoad load;
        using (new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.None)) load = SettingsStore.Load(File_);
        Assert.True(load.Unreadable);
        var failed = new List<string>();
        var saver = new SettingsSaver(File_, (what, _) => failed.Add(what));
        saver.Save(load.Settings);
        Assert.True(saver.Wait(Long));
        Assert.Single(failed);
        Assert.StartsWith("not saved", failed[0]);
        Assert.Equal(64, SettingsStore.Load(File_).Settings.Dock.IconSize); // untouched
    }
}
