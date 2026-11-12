using Frostbar.Core.Settings;

namespace Frostbar.Core.Tests;

public sealed class WelcomeRuleTests
{
    [Theory]
    [InlineData(false, null, false, true)]  // the first start: no file yet
    [InlineData(true, false, false, true)]  // shown before, not answered (that start crashed or was killed): asked again
    [InlineData(true, true, false, false)]  // answered
    [InlineData(true, null, false, false)]  // a file from before the welcome existed (or a spec's): counts as answered
    [InlineData(true, true, true, true)]    // --welcome
    public void Pending(bool fileExisted, bool? welcomed, bool forced, bool want) =>
        Assert.Equal(want, WelcomeRule.Pending(fileExisted, welcomed, forced));

    [Fact]
    public void An_absent_welcomed_reads_as_null_and_false_stays_false()
    {
        var dir = Path.Combine(Path.GetTempPath(), "frostbar-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, """{ "schemaVersion": 1, "general": { "language": "ja" } }""");
            Assert.Null(SettingsStore.Load(file).Settings.General.Welcomed);
            File.WriteAllText(file, """{ "schemaVersion": 1, "general": { "welcomed": false } }""");
            Assert.False(SettingsStore.Load(file).Settings.General.Welcomed);
        }
        finally { Directory.Delete(dir, true); }
    }
}
