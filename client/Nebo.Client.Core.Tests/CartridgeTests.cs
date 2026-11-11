using System.Text.Json.Nodes;
using Nebo.Client.Core.Dtc;

namespace Nebo.Client.Core.Tests;

public class CartridgeTests
{
    [Fact]
    public void Hornet_default_is_dcs_own_empty_cartridge()
    {
        var c = Cartridge.NewDefault(Cartridge.HornetType, "Caucasus");
        Assert.Equal("FA-18C_hornet", c.Type);
        Assert.Equal("Caucasus", c.Terrain);
        Assert.Equal("Caucasus", c.Data["WYPT"]!["terrain"]!.GetValue<string>());
        foreach (var section in new[] { "ALR67", "COMM", "WYPT", "GPS_WYPT", "SA", "TCN", "HARM" })
            Assert.NotNull(c.Data[section]);
        // The tables the avionics read are there, built by DCS's own code.
        Assert.True(c.Data["ALR67"]!["CMDS"]!["CMDS_Threat_table"]!["CMDS_Avionics_Threat_Table"]!.AsArray().Count > 100);
        Assert.Equal(3, c.Data["WYPT"]!["NAV_ROUTE"]!.AsArray().Count);
    }

    [Fact]
    public void Each_default_is_a_fresh_copy()
    {
        var a = Cartridge.NewDefault(Cartridge.HornetType, "Caucasus");
        a.Name = "changed";
        Assert.Equal("", Cartridge.NewDefault(Cartridge.HornetType, "Syria").Name);
    }

    [Fact]
    public void Save_and_load_keep_every_key_and_value()
    {
        var a = Cartridge.NewDefault(Cartridge.HornetType, "Caucasus");
        a.Data["WYPT"]!["NAV_PTS"]!.AsArray().Add(new JsonObject { ["id"] = "STPT1", ["x"] = -281234.5, ["y"] = 612345.25, ["alt"] = 914.4 });
        var b = Cartridge.Parse(a.ToJson());
        Assert.True(JsonNode.DeepEquals(a.Root, b.Root));
        Assert.Equal(a.ToJson(), b.ToJson());
    }

    [Fact]
    public void Definitions_give_dcs_limits_and_threat_rings()
    {
        var d = DtcDefs.For(Cartridge.HornetType);
        Assert.Equal(59, d.Number("NAV_SETTINGS_defs/NAV_SETTINGS_LIMITS/WAYPOINT_MAX"));
        Assert.Equal(25000, d.Number("NAV_SETTINGS_defs/NAV_SETTINGS_LIMITS/ALTITUDE_BARO_MAX"));
        var rings = d["MEZ_THRTS_defs/MEZ_THRTS_defs"]!.AsArray();
        Assert.Contains(rings, r => r!["name"]!.GetValue<string>().StartsWith("SAM SA-11"));
    }

    [Fact]
    public void Unsupported_aircraft_have_no_format()
    {
        Assert.False(Cartridge.IsSupported("Su-25T"));
        Assert.Throws<NotSupportedException>(() => Cartridge.NewDefault("Su-25T", "Caucasus"));
    }

    [Fact]
    public void Not_a_cartridge_is_refused() =>
        Assert.Throws<InvalidDataException>(() => Cartridge.Parse("""{"type":"FA-18C_hornet"}"""));
}
