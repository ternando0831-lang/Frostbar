using Frostbar.Core.Devices;
using static Frostbar.Core.Devices.BluetoothAudioFilters;

namespace Frostbar.Core.Tests;

public class BluetoothAudioFiltersTests
{
    // What Core Audio listed on the test PC for a pair of earbuds and the PC's own sound (2026-10-13), shortened.
    static readonly Guid Buds = new("c4c709ba-8b4e-5e96-87aa-6ac4dcd30cd2"), Player = new("49c471c1-85e5-59cc-8562-de216620f043"), Realtek = new("ddebb409-e755-56b9-b8f3-a86bdd600ff8");
    const string Stereo = @"{2}.\\?\bthenum#{0000110b-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0f86#7&736e42f&0&581862a8868e_c00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\src";
    const string OldStereo = @"{2}.\\?\bthenum#{0000110b-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0f86#7&736e42f&0&581862a8868e_c00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\topology";
    const string HandsFree = @"{2}.\\?\bthhfenum#bthhfpaudio#8&2a892181&0&97#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\wave";

    static readonly AudioEndpointInfo[] Listed =
    [
        new(Buds, "BTHENUM", NotPresent, OldStereo),
        new(Buds, "BTHENUM", Unplugged, Stereo),
        new(Buds, "BTHHFENUM", Unplugged, HandsFree),
        new(Player, "BTHENUM", Unplugged, @"{2}.\\?\bthenum#...00d6cb2c9a69_c00000000#...\src"),
        new(Realtek, "INTELAUDIO", Unplugged, @"{2}.\\?\intelaudio#func_01...\secondarylineouttopo"),
        new(null, "ROOT", Active, @"{2}.\\?\root#steamstreamingspeakers#0000#...\topology"),
    ];

    [Fact]
    public void BothTheStereoAndTheHandsFreeFiltersOfThatDeviceOnly()
    {
        Assert.Equal(new[] { Stereo, HandsFree }, For(Listed, Buds, connect: true));
        Assert.Equal(new[] { Stereo, HandsFree }, For(Listed, Buds, connect: false));
    }

    [Fact]
    public void AFilterServingTwoEndpointsIsAskedOnce()
    {
        AudioEndpointInfo[] twice = [new(Buds, "BTHENUM", Active, Stereo), new(Buds, "BTHENUM", Active, Stereo.ToUpperInvariant()), new(Buds, "BTHHFENUM", Active, HandsFree)];
        Assert.Equal(2, For(twice, Buds, connect: false).Count);
    }

    [Fact]
    public void ADisabledPartIsNotConnectedButIsDisconnected()
    {
        AudioEndpointInfo[] off = [new(Buds, "BTHENUM", Active, Stereo), new(Buds, "BTHHFENUM", Disabled, HandsFree)];
        Assert.Equal(new[] { Stereo }, For(off, Buds, connect: true));
        Assert.Equal(new[] { Stereo, HandsFree }, For(off, Buds, connect: false));
    }

    [Fact]
    public void OtherBusesAndUnresolvedFiltersAreNotAsked()
    {
        Assert.Empty(For(Listed, Realtek, connect: true));
        Assert.Empty(For([new(Buds, "BTHENUM", Unplugged, null)], Buds, connect: true));
    }

    [Fact]
    public void ConnectableDevicesHaveABluetoothEndpointWindowsStillHas()
    {
        Assert.True(Connectable(Listed).SetEquals([Buds, Player]));
        // A device whose only endpoint is gone (or turned off) has nothing to connect through.
        Assert.Empty(Connectable([new(Buds, "BTHENUM", NotPresent, null), new(Player, "BTHENUM", Disabled, null)]));
    }
}
