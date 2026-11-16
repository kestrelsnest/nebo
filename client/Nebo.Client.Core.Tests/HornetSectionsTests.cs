using Nebo.Client.Core.Dtc;

namespace Nebo.Client.Core.Tests;

[Trait("Needs", "DCS")]  // tables made from a DCS install (DtcTestData); GitHub's build has none
public class HornetSectionsTests
{
    private static Cartridge New() => Cartridge.NewDefault(Cartridge.HornetType, "Caucasus");

    [Theory]
    [InlineData(251.0, 251.0)]
    [InlineData(500.0, 399.975)]
    [InlineData(10.0, 30.0)]
    [InlineData(200.0, 225.0)]       // in the 173.995–225 gap: nearer 225
    [InlineData(180.0, 173.995)]     // nearer the low edge
    [InlineData(135.998, 136.0)]
    public void Frequencies_snap_like_dcs(double mhz, double tuned) => Assert.Equal(tuned, HornetComm.Snap(mhz));

    [Theory]
    [InlineData(40.0, HornetComm.Am, HornetComm.Fm)]    // FM only
    [InlineData(124.0, HornetComm.Fm, HornetComm.Am)]   // AM only
    [InlineData(251.0, HornetComm.Fm, HornetComm.Fm)]   // either: as wanted
    [InlineData(251.0, HornetComm.Am, HornetComm.Am)]
    public void Modulation_follows_the_band(double mhz, int wanted, int got) => Assert.Equal(got, HornetComm.Allowed(mhz, wanted));

    [Fact]
    public void Comm_presets_are_written_as_dcs_keeps_them()
    {
        var c = New();
        var comm = new HornetComm(c);
        Assert.NotNull(comm.Preset(1, "G"));             // DCS's special presets are there
        comm.Set(2, "7", 251.0, HornetComm.Am, "AWACS DARKSTAR");
        var p = comm.Preset(2, "7")!;
        Assert.Equal(251.0, HornetNav.Number(p["frequency"]));
        Assert.Equal("AWACS DA", (string?)p["name"]);    // 8 letters
        comm.SetGuard(1, true);
        Assert.True(comm.Guard(1));
    }

    [Fact]
    public void Gps_waypoints_like_dcs()
    {
        var gps = new HornetGps(New());
        var a = gps.Add(10, 20, 30)!;
        Assert.Equal("GPS_WYPT1", (string?)a["id"]);
        Assert.Equal("GW1", (string?)a["text"]);
        gps.Add(11, 21, 0);
        gps.Remove(1);
        Assert.Equal(1, HornetNav.Num(gps.Add(0, 0, 0)!));   // first free number again, kept in order
        Assert.Equal([1, 2], gps.All.Select(HornetNav.Num));
        gps.Set(2, "TARGET", "bridge", 30000);
        Assert.Equal("TARGE", (string?)gps.Point(2)!["text"]);
        Assert.Equal(25000, Math.Round((HornetNav.Number(gps.Point(2)!["alt"]) ?? 0) * HornetNav.FeetPerMeter));
    }

    [Fact]
    public void Tacan_stations_at_most_10_no_duplicates()
    {
        var tcn = new HornetTacan(New());
        Beacon B(int i) => new("TACAN", $"T{i}", $"Station {i}", 1.0e9 + i, i, i * 100, 10, i * 200, 0, i);
        Assert.True(tcn.Add(B(1)));
        Assert.False(tcn.Add(B(1)));
        for (var i = 2; i <= 10; i++) Assert.True(tcn.Add(B(i)));
        Assert.False(tcn.Add(B(11)));
        var s = tcn.Stations[0];
        Assert.Equal("Station 1", (string?)s["display_name"]);
        Assert.Equal(100, HornetNav.Number(s["x"]));
        Assert.Equal(200, HornetNav.Number(s["y"]));    // DCS's y is the beacon's z
        tcn.MoveUp(1);
        Assert.Equal("Station 2", (string?)tcn.Stations[0]["display_name"]);
    }
}
