using System.Text.Json;
using Nebo.Client.Core;

namespace Nebo.Client.Core.Tests;

public class RadarScanTests
{
    private static Unit Contact(double? age) =>
        SymbologyTests.Make(coalition: "red", detected: true, type: null) with { Age = age };

    [Fact]
    public void Plot_age_is_read_from_the_picture()
    {
        var u = JsonSerializer.Deserialize<Unit>("""{"id":5,"category":"airplane","coalition":"red","detected":true,"age":7}""")!;
        Assert.Equal(7, u.Age);
    }

    [Fact]
    public void Fresh_plot_shows_no_age() =>
        Assert.Equal("250  450 M0.75", Symbology.DataBlock(Contact(10), LabelLevel.Full)[1]);

    [Fact]
    public void Old_plot_shows_its_age() =>
        Assert.Equal("250  450 M0.75 18s", Symbology.DataBlock(Contact(18), LabelLevel.Full)[1]);

    [Theory]
    [InlineData(0, 340.3, 1.00)]       // sea level
    [InlineData(11000, 295.1, 1.00)]   // tropopause
    [InlineData(15000, 147.5, 0.50)]   // stratosphere: as cold as at 11 km
    public void Mach_uses_the_isa_speed_of_sound(double altM, double spdMps, double mach) =>
        Assert.Equal(mach, Symbology.MachOf(Contact(10) with { Alt = altM, Spd = spdMps })!.Value, 2);

    [Fact]
    public void Frozen_contact_shows_frz_not_age() =>
        Assert.EndsWith("FRZ", Symbology.DataBlock(Contact(30) with { Frozen = true }, LabelLevel.Full)[1]);

    [Fact]
    public void Flying_refusal_is_explained()
    {
        Assert.Contains("cockpit", NeboConnection.RefusalText("flying"));
        Assert.Null(NeboConnection.RefusalText(null));
    }
}
